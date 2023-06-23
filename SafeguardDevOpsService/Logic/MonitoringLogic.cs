﻿
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OneIdentity.DevOps.Common;
using OneIdentity.DevOps.ConfigDb;
using OneIdentity.DevOps.Data;
using OneIdentity.DevOps.Data.Spp;
using OneIdentity.DevOps.Exceptions;
using OneIdentity.SafeguardDotNet;
using OneIdentity.SafeguardDotNet.A2A;
using OneIdentity.SafeguardDotNet.Event;
using Safeguard = OneIdentity.SafeguardDotNet.Safeguard;

namespace OneIdentity.DevOps.Logic
{
    internal class MonitoringLogic : IMonitoringLogic
    {
        private readonly Serilog.ILogger _logger;
        private readonly IConfigurationRepository _configDb;
        private readonly IPluginManager _pluginManager;
        private readonly ICredentialManager _credentialManager;
        private readonly ISafeguardLogic _safeguardLogic;

        private static ISafeguardEventListener _eventListener;
        private static ISafeguardA2AContext _a2AContext;
        private static List<AccountMapping> _retrievableAccounts;
        private static FixedSizeQueue<MonitorEvent> _lastEventsQueue = new FixedSizeQueue<MonitorEvent>(10000);
        private static bool _reverseFlowEnabled = false;

        public MonitoringLogic(IConfigurationRepository configDb, IPluginManager pluginManager, 
            ICredentialManager credentialManager, ISafeguardLogic safeguardLogic)
        {
            _configDb = configDb;
            _pluginManager = pluginManager;
            _logger = Serilog.Log.Logger;
            _credentialManager = credentialManager;
            _safeguardLogic = safeguardLogic;
        }

        bool CertificateValidationCallback(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            return CertificateHelper.CertificateValidation(sender, certificate, chain, sslPolicyErrors, _logger, _configDb);
        }

        public void EnableMonitoring(bool enable)
        {
            if (enable)
                StartMonitoring();
            else
                StopMonitoring();

            _configDb.LastKnownMonitorState = GetMonitorState().Enabled ? WellKnownData.MonitorEnabled : WellKnownData.MonitorDisabled;
        }

        public MonitorState GetMonitorState()
        {
            return new MonitorState()
            {
                Enabled = _eventListener != null && _a2AContext != null,
                ReverseFlowEnabled = _reverseFlowEnabled
            };
        }

        public IEnumerable<MonitorEvent> GetMonitorEvents(int size)
        {
            if (size <= 0)
                size = 25;
            if (size > _lastEventsQueue.Count)
                size = _lastEventsQueue.Count;
            return _lastEventsQueue.TakeLast(size).Reverse();
        }

        public bool PollReverseFlow()
        {
            if (ReverseFlowMonitoringAvailable())
            {

                // If monitoring is running then we can assume that the plugins have
                // proper vault credentials.  If not then we need to refresh the
                // vault credentials.
                if (!GetMonitorState().Enabled)
                {
                    _pluginManager.RefreshPluginCredentials();
                }

                Task.Run(() => PollReverseFlowInternal());
                return true;
            }

            _logger.Information("Reverse flow monitoring is not available. Check 'Allow Setting Credentials' flag in the A2A registration. ");

            return false;
        }

        public void Run()
        {
            try
            {
                if (_configDb.LastKnownMonitorState != null &&
                    _configDb.LastKnownMonitorState.Equals(WellKnownData.MonitorEnabled))
                {
                    StartMonitoring();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Could not restore the last known running state of the monitor. {ex.Message}");
            }
        }

        private void StartMonitoring()
        {
            if (_eventListener != null)
                throw new DevOpsException("Listener is already running.");

            var sppAddress = _configDb.SafeguardAddress;
            var userCertificate = _configDb.UserCertificateBase64Data;
            var passPhrase = _configDb.UserCertificatePassphrase?.ToSecureString();
            var apiVersion = _configDb.ApiVersion;
            var ignoreSsl = _configDb.IgnoreSsl;

            if (sppAddress == null || userCertificate == null || !apiVersion.HasValue || !ignoreSsl.HasValue)
            {
                _logger.Error("No safeguardConnection was found.  Safeguard Secrets Broker for DevOps must be configured first");
                return;
            }

            if (ignoreSsl.Value)
                throw new DevOpsException("Monitoring cannot be enabled until a secure connection has been established. Trusted certificates may be missing.");

            // This call will fail if the monitor is being started as part of the service start up.
            //  The reason why is because at service startup, the user has not logged into Secrets Broker yet
            //  so Secrets Broker does not have the SPP credentials that are required to query the current vault account credentials.
            //  However, the monitor can still be started using the existing vault credentials. If syncing doesn't appear to be working
            //  the monitor can be stopped and restarted which will cause a refresh of the vault credentials.
            _pluginManager.RefreshPluginCredentials();

            // Make sure that the credentialManager cache is empty.
            _credentialManager.Clear();

            // connect to Safeguard
            _a2AContext = Safeguard.A2A.GetContext(sppAddress, Convert.FromBase64String(userCertificate), passPhrase, CertificateValidationCallback, apiVersion.Value);
            // figure out what API keys to monitor
            _retrievableAccounts = _configDb.GetAccountMappings().ToList();
            if (_retrievableAccounts.Count == 0)
            {
                var msg = "No accounts have been mapped to plugins.  Nothing to do.";
                _logger.Error(msg);
                throw new DevOpsException(msg);
            }

            var apiKeys = new List<SecureString>();
            foreach (var account in _retrievableAccounts)
            {
                apiKeys.Add(account.ApiKey.ToSecureString());
            }

            _eventListener = _a2AContext.GetPersistentA2AEventListener(apiKeys, PasswordChangeHandler);
            _eventListener.Start();

            InitialPasswordPull();

            _logger.Information("Password change monitoring has been started.");

            StartReverseFlowMonitor();
            _logger.Information("Reverse flow monitoring has been started.");

        }

        private void StopMonitoring()
        {
            try
            {
                try
                {
                    _eventListener?.Stop();
                } catch { }

                StopReverseFlowMonitor();

                _a2AContext?.Dispose();
                _logger.Information("Password change monitoring has been stopped.");
            }
            finally
            {
                _eventListener = null;
                _a2AContext = null;
                _retrievableAccounts = null;
                _credentialManager.Clear();
            }
        }

        private void PasswordChangeHandler(string eventName, string eventBody)
        {
            var eventInfo = JsonHelper.DeserializeObject<EventInfo>(eventBody);

            try
            {
                var apiKeys = _retrievableAccounts.Where(mp => mp.AssetName == eventInfo.AssetName && mp.AccountName == eventInfo.AccountName).ToArray();

                // Make sure that we have at least one plugin mapped to the account
                if (!apiKeys.Any())
                    _logger.Error("No API keys were found by the password change handler.");

                // Make sure that if there are more than one mapped plugin, all of the API key match for the same account
                var apiKey = apiKeys.FirstOrDefault()?.ApiKey;
                if (!apiKeys.All(x => x.ApiKey.Equals(apiKey)))
                    _logger.Error("Mismatched API keys for the same account were found by the password change handler.");

                var selectedAccounts = _configDb.GetAccountMappings().Where(a => a.ApiKey.Equals(apiKey)).ToList();

                // At this point we should have one API key to retrieve.
                PullAndPushPasswordByApiKey(apiKey, selectedAccounts);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Password change handler failed: {ex.Message}.");
            }
        }

        private void InitialPasswordPull()
        {
            try
            {
                var apiKeys = _retrievableAccounts.GroupBy(x => x.ApiKey).Select(x => x.First().ApiKey).ToArray();

                // Make sure that we have at least one plugin mapped to the account
                if (!apiKeys.Any())
                    return;

                var accounts = _configDb.GetAccountMappings().ToList();
                foreach (var apiKey in apiKeys)
                {
                    var selectedAccounts = accounts.Where(a => a.ApiKey.Equals(apiKey));
                    PullAndPushPasswordByApiKey(apiKey, selectedAccounts);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Password change handler failed: {ex.Message}.");
            }
        }

        private void PullAndPushPasswordByApiKey(string a2AApiKey, IEnumerable<AccountMapping> selectedAccounts)
        {
            var credentialCache = new Dictionary<CredentialType, string[]>();

            foreach (var account in selectedAccounts)
            {
                var pluginInfo = _configDb.GetPluginByName(account.VaultName);
                var credentialType = Enum.GetName(typeof(CredentialType), pluginInfo.AssignedCredentialType);

                var monitorEvent = new MonitorEvent()
                {
                    Event = $"Sending {credentialType} for account {account.AccountName} to {account.VaultName}.",
                    Result = WellKnownData.SentPasswordSuccess,
                    Date = DateTime.UtcNow
                };

                if (!_pluginManager.IsLoadedPlugin(account.VaultName) || pluginInfo.IsDisabled)
                {
                    monitorEvent.Event = $"{account.VaultName} is disabled or not loaded. No {credentialType} sent for account {account.AccountName}.";
                    monitorEvent.Result = WellKnownData.SentPasswordFailure;
                }
                else
                {
                    if (!credentialCache.ContainsKey(pluginInfo.AssignedCredentialType))
                    {
                        var credential = _pluginManager.GetAccountCredential(pluginInfo.Name, a2AApiKey, pluginInfo.AssignedCredentialType);
                        if (credential == null || credential.Length <= 0)
                        {
                            monitorEvent.Event = $"Failed to get the {credentialType} from Safeguard for plugin {account.VaultName}. No {credentialType} sent for account {account.AccountName}.";
                            monitorEvent.Result = WellKnownData.SentPasswordFailure;
                            _lastEventsQueue.Enqueue(monitorEvent);
                            _logger.Error(monitorEvent.Event);
                            continue;
                        }

                        credentialCache.Add(pluginInfo.AssignedCredentialType, credential);
                    }

                    try
                    {
                        if (pluginInfo.SupportsReverseFlow && pluginInfo.ReverseFlowEnabled)
                        {
                            // Only store passwords and ssh keys in the credential manager for reverse flow comparison. API keys are not supported yet.
                            if (pluginInfo.AssignedCredentialType != CredentialType.ApiKey)
                            {
                                _credentialManager.Insert(credentialCache[pluginInfo.AssignedCredentialType][0], account, pluginInfo.AssignedCredentialType);
                            }
                        }
                        else
                        {
                            _logger.Information(monitorEvent.Event);
                            if (!_pluginManager.SendCredential(account, credentialCache[pluginInfo.AssignedCredentialType], pluginInfo.AssignedCredentialType))
                            {
                                monitorEvent.Event = $"Unable to set the {credentialType} for {account.AccountName} to {account.VaultName}.";
                                monitorEvent.Result = WellKnownData.SentPasswordFailure;
                                _logger.Error(monitorEvent.Event);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        monitorEvent.Event = $"Unable to set the {credentialType} for {account.AccountName} to {account.VaultName}: {ex.Message}.";
                        monitorEvent.Result = WellKnownData.SentPasswordFailure;
                        _logger.Error(ex, monitorEvent.Event);
                    }
                }

                _lastEventsQueue.Enqueue(monitorEvent);
            }
        }

        private CancellationTokenSource _cts = null;

        private void StartReverseFlowMonitor()
        {
            if (ReverseFlowMonitoringAvailable())
            {
                if (_cts == null)
                {
                    _cts = new CancellationTokenSource();

                    Task.Run(() => ReverseFlowMonitorThread(_cts.Token), _cts.Token);
                }
                else
                {
                    _logger.Information("Reverse monitor thread shutting down.");
                }
            }
        }

        private void StopReverseFlowMonitor()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts = null;
            }
        }

        private bool ReverseFlowMonitoringAvailable()
        {
            using var sg = _safeguardLogic.Connect();

            try
            {
                var result = sg.InvokeMethodFull(Service.Core, Method.Get, $"A2ARegistrations/{_configDb.A2aRegistrationId}");
                if (result.StatusCode == HttpStatusCode.OK)
                {
                    var registration =  JsonHelper.DeserializeObject<A2ARegistration>(result.Body);
                    if (registration != null && registration.BidirectionalEnabled.HasValue && registration.BidirectionalEnabled.Value)
                    {
                        return true;
                    }
                }
            }
            catch (SafeguardDotNetException ex)
            {
                if (ex.HttpStatusCode == HttpStatusCode.NotFound)
                {
                    _logger.Error(ex, $"Registration not found for id '{_configDb.A2aRegistrationId}'");
                }
                else
                {
                    var msg = $"Failed to get the registration for id '{_configDb.A2aRegistrationId}'";
                    _logger.Error(ex, msg);
                }
            }
            catch (Exception ex)
            {
                var msg = $"Failed to get the registration for id '{_configDb.A2aRegistrationId}'";
                _logger.Error(ex, msg);
            }

            return false;
        }

        private async Task ReverseFlowMonitorThread(CancellationToken token)
        {
            try
            {
                _reverseFlowEnabled = true;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(WellKnownData.ReverseFlowMonitorPollingInterval), token);
                    }
                    catch (OperationCanceledException e)
                    {
                        _logger.Information("Reverse flow monitor thread shutting down.");
                    }

                    if (token.IsCancellationRequested || !GetMonitorState().Enabled)
                        break;

                    PollReverseFlowInternal();
                }
            }
            finally
            {
                _reverseFlowEnabled = false;
            }
        }

        private static object _lockReverseFlow = new object();

        private void PollReverseFlowInternal()
        {
            lock (_lockReverseFlow)
            {
                var reverseFlowInstances = _configDb.GetAllReverseFlowPluginInstances().ToList();
                foreach (var pluginInstance in reverseFlowInstances)
                {
                    if (_pluginManager.IsLoadedPlugin(pluginInstance.Name) && !pluginInstance.IsDisabled)
                    {
                        var accounts = _configDb.GetAccountMappings(pluginInstance.Name);
                        foreach (var account in accounts)
                        {
                            var monitorEvent = new MonitorEvent()
                            {
                                Event =
                                    $"Getting {pluginInstance.AssignedCredentialType} for account {account.AccountName} to {account.VaultName}.",
                                Result = WellKnownData.GetPasswordSuccess,
                                Date = DateTime.UtcNow
                            };

                            try
                            {
                                _logger.Information(monitorEvent.Event);
                                if (!_pluginManager.GetCredential(account, pluginInstance.AssignedCredentialType))
                                {
                                    monitorEvent.Event =
                                        $"Unable to get the {pluginInstance.AssignedCredentialType} for {account.AccountName} to {account.VaultName}.";
                                    monitorEvent.Result = WellKnownData.GetPasswordFailure;
                                    _logger.Error(monitorEvent.Event);
                                }
                            }
                            catch (Exception ex)
                            {
                                monitorEvent.Event =
                                    $"Unable to get the {pluginInstance.AssignedCredentialType} for {account.AccountName} to {account.VaultName}: {ex.Message}.";
                                monitorEvent.Result = WellKnownData.GetPasswordFailure;
                                _logger.Error(ex, monitorEvent.Event);
                            }

                            _lastEventsQueue.Enqueue(monitorEvent);
                        }
                    }
                }
            }
        }
    }
}
