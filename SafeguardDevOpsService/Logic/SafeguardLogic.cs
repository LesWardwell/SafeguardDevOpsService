﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OneIdentity.DevOps.ConfigDb;
using OneIdentity.DevOps.Data;
using OneIdentity.DevOps.Data.Spp;
using OneIdentity.SafeguardDotNet;
using Microsoft.AspNetCore.WebUtilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OneIdentity.DevOps.Common;
using OneIdentity.DevOps.Exceptions;
using OneIdentity.DevOps.Extensions;
using OneIdentity.SafeguardDotNet.A2A;
using A2ARetrievableAccount = OneIdentity.DevOps.Data.Spp.A2ARetrievableAccount;
// ReSharper disable InconsistentNaming

namespace OneIdentity.DevOps.Logic
{
    internal class SafeguardLogic : ISafeguardLogic, IDisposable
    {
        private readonly Serilog.ILogger _logger;
        private readonly IConfigurationRepository _configDb;
        private readonly Func<IPluginsLogic> _pluginsLogic;
        private readonly Func<IMonitoringLogic> _monitoringLogic;
        private readonly Func<IAddonLogic> _addonLogic;
        private readonly Func<IAddonManager> _addonManager;

        private ApplianceAvailability _applianceAvailabilityCache;
        private DateTime _applianceAvailabilityLastCheck = DateTime.MinValue;

        [ThreadStatic] private static string _threadToken;

        private ServiceConfiguration _serviceConfiguration => AuthorizedCache.Instance.FindByToken(_threadToken);
        private DevOpsSecretsBroker _devOpsSecretsBrokerCache;

        public DevOpsSecretsBroker DevOpsSecretsBrokerCache
        {
            get => _devOpsSecretsBrokerCache;
            private set
            {
                _devOpsSecretsBrokerCache = value;
                _configDb.AssetId = _devOpsSecretsBrokerCache?.Asset?.Id;
                _configDb.A2aUserId = _devOpsSecretsBrokerCache?.A2AUser?.Id;
            }
        }

        public bool PauseBackgroundMaintenance { get; private set; }
        public bool? ApplianceSupportsDevOps { get; private set; } = null;

        public SafeguardLogic(IConfigurationRepository configDb, Func<IPluginsLogic> pluginsLogic, 
            Func<IMonitoringLogic> monitoringLogic, Func<IAddonLogic> addonLogic, Func<IAddonManager> addonManager)
        {
            _configDb = configDb;
            _pluginsLogic = pluginsLogic;
            _monitoringLogic = monitoringLogic;
            _addonLogic = addonLogic;
            _addonManager = addonManager;
            _logger = Serilog.Log.Logger;
        }

        bool CertificateValidationCallback(object sender, X509Certificate certificate, X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            return CertificateHelper.CertificateValidation(sender, certificate, chain, sslPolicyErrors, _logger,
                _configDb);
        }

        private DevOpsException LogAndException(string msg, Exception ex = null)
        {
            _logger.Error(ex, msg);
            return new DevOpsException(msg, ex);
        }

        private IDictionary<string, string> AddDevOpsHeader(string devOpsInstanceId,
            IDictionary<string, string> additionalHeaders)
        {
            if (ApplianceSupportsDevOps ?? false)
            {
                if (additionalHeaders == null)
                    return new Dictionary<string, string> {{"devOpsInstanceId", devOpsInstanceId}};

                additionalHeaders.Add("devOpsInstanceId", devOpsInstanceId);
            }

            return additionalHeaders;
        }

        private string DevOpsInvokeMethod(string devOpsInstanceId, ISafeguardConnection sgConnection,
            Service service, Method method, string relativeUrl, string body = null,
            IDictionary<string, string> parameters = null,
            IDictionary<string, string> additionalHeaders = null, TimeSpan? timeout = null)
        {
            return sgConnection.InvokeMethod(service, method, relativeUrl, body, parameters,
                AddDevOpsHeader(devOpsInstanceId, additionalHeaders), timeout);
        }

        private FullResponse DevOpsInvokeMethodFull(string devOpsInstanceId, ISafeguardConnection sgConnection,
            Service service, Method method, string relativeUrl, string body = null,
            IDictionary<string, string> parameters = null,
            IDictionary<string, string> additionalHeaders = null, TimeSpan? timeout = null)
        {
            return sgConnection.InvokeMethodFull(service, method, relativeUrl, body, parameters,
                AddDevOpsHeader(devOpsInstanceId, additionalHeaders), timeout);
        }

        public bool SetThreadData(string token)
        {
            var threadData = AuthorizedCache.Instance.FindByToken(token);

            if (threadData == null)
                return false;

            _threadToken = token;
            return true;

        }

        public bool ValidateLicense()
        {
            var sg = _configDb.IgnoreSsl ?? true
                ? Safeguard.Connect(_configDb.SafeguardAddress, _configDb.ApiVersion ?? WellKnownData.DefaultApiVersion, true)
                : Safeguard.Connect(_configDb.SafeguardAddress, CertificateValidationCallback, _configDb.ApiVersion ?? WellKnownData.DefaultApiVersion);

            try
            {
                var licensesJson = DevOpsInvokeMethod(_configDb.SvcId, sg, Service.Core, Method.Get,
                    "Licenses/Summary");
                var _licenses = JsonHelper.DeserializeObject<IEnumerable<LicenseSummary>>(licensesJson).ToArray();

                return
                    _licenses.Any(x => x.Module == LicensableModule.PasswordManagement && x.IsValid) &&
                    _licenses.Any(x => x.Module == LicensableModule.SecretsBroker && x.IsValid);
            }
            catch (SafeguardDotNetException ex)
            {
                throw new DevOpsException($"Failed to validate license: {ex.Message}");
            }
            finally
            {
                sg.Dispose();
            }
        }

        private bool CheckSslConnection(IEnumerable<TrustedCertificate> customCertificateList = null)
        {
            ISafeguardConnection sg = null;
            var certChainOk = false;

            try
            {
                sg = Safeguard.Connect(_configDb.SafeguardAddress, 
                    (object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) =>
                    {
                        certChainOk = CertificateHelper.CertificateValidation(sender, certificate, chain, sslPolicyErrors, _logger, _configDb, customCertificateList);
                        return certChainOk;
                    }, _configDb.ApiVersion ?? WellKnownData.DefaultApiVersion);

                return certChainOk;
            }
            catch (SafeguardDotNetException ex)
            {
                _logger.Error(ex, "Invalid certificate chain.  Please add or import the Safeguard trusted certificates.");
            }
            finally
            {
                sg?.Dispose();
            }

            return certChainOk;
        }

        private SafeguardDevOpsConnection GetSafeguardAppliance(ISafeguardConnection sgConnection, string address = null)
        {
            var sg = sgConnection ?? Connect();

            try
            {
                if (_applianceAvailabilityCache == null ||
                    DateTime.Now - _applianceAvailabilityLastCheck > TimeSpan.FromMinutes(5))
                {
                    var availabilityJson = DevOpsInvokeMethod(_configDb.SvcId, sg, Service.Notification, Method.Get,
                        "Status/Availability");
                    _applianceAvailabilityCache = JsonHelper.DeserializeObject<ApplianceAvailability>(availabilityJson);
                    _applianceAvailabilityLastCheck = DateTime.Now;
                }

                var servConfig = _serviceConfiguration;
                return new SafeguardDevOpsConnection()
                {
                    ApplianceAddress = address ?? _configDb.SafeguardAddress,
                    ApplianceId = _applianceAvailabilityCache.ApplianceId,
                    ApplianceName = _applianceAvailabilityCache.ApplianceName,
                    ApplianceVersion = _applianceAvailabilityCache.ApplianceVersion,
                    ApplianceState = _applianceAvailabilityCache.ApplianceCurrentState,
                    // TODO: Hardcoded to false for now. This should be changed back to call the following when devops support it added to Safeguard
                    // ApplianceSupportsDevOps = ApplianceSupportsDevOps ?? GetSafeguardDevOpsSupport(sg, address ?? _configDb.SafeguardAddress),
                    ApplianceSupportsDevOps = false,  
                    DevOpsInstanceId = _configDb.SvcId,
                    UserName = servConfig?.User?.UserName,
                    UserDisplayName = servConfig?.User?.DisplayName,
                    AdminRoles = servConfig?.User?.AdminRoles,
                    Version = WellKnownData.DevOpsServiceVersion()
                };
            }
            catch (SafeguardDotNetException ex)
            {
                throw new DevOpsException($"Failed to get the appliance information: {ex.Message}");
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }
        }

        private bool GetSafeguardDevOpsSupport(ISafeguardConnection sgConnection, string address = null)
        {
            var sg = sgConnection ?? Connect();

            var handler = new HttpClientHandler();
            handler.ClientCertificateOptions = ClientCertificateOption.Manual;
            handler.ServerCertificateCustomValidationCallback = (sender, certificate, chain, errors) => true;
            var client = new HttpClient(handler);

            try
            {
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var response = client
                    .GetAsync(string.Format(WellKnownData.SwaggerUrl, address ?? _configDb.SafeguardAddress, _configDb.ApiVersion)).Result;
                if (response.IsSuccessStatusCode)
                {
                    var data = (JObject) JsonConvert.DeserializeObject(response.Content.ReadAsStringAsync().Result);
                    if (data != null)
                    {
                        var apis = data.SelectToken("paths")!.Where(x => x.Type == JTokenType.Property && ((JProperty) x).Name.StartsWith(WellKnownData.DevOpsSecretsBrokerEndPoints));
                        ApplianceSupportsDevOps = apis.Any();
                        return ApplianceSupportsDevOps.Value;
                    }
                }
                else
                {
                    _logger.Error(
                        $"Unable to get the Safeguard endpoints to check for DevOps support. {response.ReasonPhrase}");
                }
            }
            catch (SafeguardDotNetException ex)
            {
                throw new DevOpsException($"Failed to get the appliance information: {ex.Message}");
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
                client.Dispose();
            }

            return false;
        }


        private SafeguardDevOpsConnection GetSafeguardAvailability(ISafeguardConnection sgConnection,
            SafeguardDevOpsConnection safeguardConnection)
        {
            var safeguard = GetSafeguardAppliance(sgConnection, safeguardConnection.ApplianceAddress);
            safeguardConnection.ApplianceId = safeguard.ApplianceId;
            safeguardConnection.ApplianceName = safeguard.ApplianceName;
            safeguardConnection.ApplianceVersion = safeguard.ApplianceVersion;
            safeguardConnection.ApplianceState = safeguard.ApplianceState;
            safeguardConnection.ApplianceSupportsDevOps = safeguard.ApplianceSupportsDevOps;
            safeguardConnection.DevOpsInstanceId = _configDb.SvcId;
            safeguardConnection.UserName = safeguard.UserName;
            safeguardConnection.UserDisplayName = safeguard.UserDisplayName;
            safeguardConnection.AdminRoles = safeguard.AdminRoles;
            safeguardConnection.Version = safeguard.Version;

            return safeguardConnection;
        }

        private bool FetchAndStoreSignatureCertificate(string token, SafeguardDevOpsConnection safeguardConnection)
        {
            var signatureCert = FetchSignatureCertificate(safeguardConnection.ApplianceAddress);

            if (signatureCert != null)
            {
                if (ValidateLogin(token, safeguardConnection, signatureCert))
                {
                    _configDb.SigningCertificate = signatureCert;
                    return true;
                }
            }

            return false;
        }

        private string FetchSignatureCertificate(string applianceAddress)
        {
            // Chicken and egg problem here. Fetching and storing the signature certificate is the first
            //  thing that has to happen on a new system.  We can't check the SSL certificate unless a certificate
            //  chain has been provided.  However a certificate chain can't be provided until after login but
            //  login can't happen until we have the signature certificate.  So we will just ignore the
            //  the validation of the SSL certificate.
            HttpClientHandler handler = new HttpClientHandler
            {
                ClientCertificateOptions = ClientCertificateOption.Manual,
                ServerCertificateCustomValidationCallback =
                    (httpRequestMessage, cert, certChain, policyErrors) => true
            };

            using var client = new HttpClient(handler) {BaseAddress = new Uri($"https://{applianceAddress}")};
            var response = client.GetAsync("RSTS/SigningCertificate").Result;
            response.EnsureSuccessStatusCode();

            return CertificateHelper.ConvertPemToBase64(response.Content.ReadAsStringAsync().Result);
        }

        private SafeguardDevOpsConnection ConnectAnonymous(string safeguardAddress, int apiVersion, bool ignoreSsl)
        {
            ISafeguardConnection sg = null;
            try
            {
                var safeguardConnection = new SafeguardDevOpsConnection
                {
                    ApplianceAddress = safeguardAddress,
                    IgnoreSsl = _configDb.IgnoreSsl ?? ignoreSsl,
                    ApiVersion = apiVersion
                };

                sg = ignoreSsl
                    ? Safeguard.Connect(safeguardAddress, apiVersion, true)
                    : Safeguard.Connect(safeguardAddress, CertificateValidationCallback, apiVersion);
                return GetSafeguardAvailability(sg, safeguardConnection);
            }
            catch (SafeguardDotNetException ex)
            {
                throw LogAndException($"Failed to contact Safeguard at '{safeguardAddress}': {ex.Message}", ex);
            }
            finally
            {
                sg?.Dispose();
            }
        }

        private ISafeguardConnection ConnectWithAccessToken(string token, SafeguardDevOpsConnection safeguardConnection)
        {
            if (_serviceConfiguration != null)
            {
                DisconnectWithAccessToken();
            }

            if (string.IsNullOrEmpty(safeguardConnection.ApplianceAddress))
                throw new DevOpsException("Missing safeguard appliance configuration.");
            if (string.IsNullOrEmpty(token))
                throw new DevOpsException("Missing safeguard access token.");

            return Connect(safeguardConnection.ApplianceAddress, token.ToSecureString(), safeguardConnection.ApiVersion,
                safeguardConnection.IgnoreSsl);
        }

        private void DisconnectWithAccessToken()
        {
            var sc = _serviceConfiguration;
            if (sc != null)
            {
                sc.AccessToken = null;
            }
        }

        private bool GetAndValidateUserPermissions(string token, SafeguardDevOpsConnection safeguardConnection)
        {
            ISafeguardConnection sg = null;
            try
            {
                sg = ConnectWithAccessToken(token, safeguardConnection);

                var meJson = sg.InvokeMethod(Service.Core, Method.Get, "Me");
                var loggedInUser = JsonHelper.DeserializeObject<LoggedInUser>(meJson);

                var valid = loggedInUser.AdminRoles.Any(x => x.Equals("PolicyAdmin"));
                if (valid)
                {
                    var appliance = GetSafeguardAvailability(sg, safeguardConnection);

                    AuthorizedCache.Instance.Add(new ServiceConfiguration(loggedInUser)
                    {
                        AccessToken = token.ToSecureString(),
                        Appliance = appliance
                    });
                }

                return valid;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to get the user information: {ex.Message}");
            }
            finally
            {
                sg?.Dispose();
            }

            return false;
        }

        private LoggedInUser GetLoggedInUser(ISafeguardConnection sgConnection)
        {
            try
            {
                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sgConnection, Service.Core, Method.Get, "Me");
                if (result.StatusCode == HttpStatusCode.OK)
                {
                    var loggedInUser = JsonHelper.DeserializeObject<LoggedInUser>(result.Body);
                    if (loggedInUser != null)
                        return loggedInUser;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to get the user information: {ex.Message}");
            }

            return null;
        }

        private void CreateA2AUser(ISafeguardConnection sg)
        {
            string thumbprint = _configDb.UserCertificate?.Thumbprint;

            if (thumbprint == null)
                throw new DevOpsException("Failed to create A2A user due to missing client certificate");

            var a2aUser = GetA2AUser(sg);
            if (a2aUser == null)
            {
                a2aUser = new A2AUser()
                {
                    UserName = WellKnownData.DevOpsUserName(_configDb.SvcId),
                    PrimaryAuthenticationIdentity = thumbprint
                };

                var a2aUserStr = JsonHelper.SerializeObject(a2aUser);
                try
                {
                    var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Post, "Users", a2aUserStr);
                    if (result.StatusCode == HttpStatusCode.Created)
                    {
                        a2aUser = JsonHelper.DeserializeObject<A2AUser>(result.Body);
                        if (DevOpsSecretsBrokerCache?.A2AUser == null ||
                            DevOpsSecretsBrokerCache.A2AUser.Id != a2aUser.Id)
                        {
                            if (DevOpsSecretsBrokerCache != null)
                            {
                                _configDb.A2aUserId = a2aUser.Id;
                                DevOpsSecretsBrokerCache.A2AUser = a2aUser;
                                UpdateSecretsBrokerInstance(sg, DevOpsSecretsBrokerCache, false);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw LogAndException($"Failed to create the A2A user: {ex.Message}", ex);
                }
            }
            else
            {
                if (!a2aUser.PrimaryAuthenticationIdentity.Equals(thumbprint,
                    StringComparison.InvariantCultureIgnoreCase))
                {
                    try
                    {
                        a2aUser.PrimaryAuthenticationIdentity = thumbprint;
                        var a2aUserStr = JsonHelper.SerializeObject(a2aUser);
                        var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Put, $"Users/{a2aUser.Id}", a2aUserStr);
                        if (result.StatusCode == HttpStatusCode.OK)
                        {
                            a2aUser = JsonHelper.DeserializeObject<A2AUser>(result.Body);
                        }
                    }
                    catch (Exception ex)
                    {
                        throw LogAndException($"Failed to update the A2A user: {ex.Message}", ex);
                    }
                }

                if (DevOpsSecretsBrokerCache?.A2AUser == null ||
                    DevOpsSecretsBrokerCache.A2AUser.Id != a2aUser.Id)
                {
                    if (DevOpsSecretsBrokerCache != null)
                    {
                        _configDb.A2aUserId = a2aUser.Id;
                        DevOpsSecretsBrokerCache.A2AUser = a2aUser;
                        UpdateSecretsBrokerInstance(sg, DevOpsSecretsBrokerCache, false);
                    }
                }
            }
        }

        private A2AUser GetA2AUser(ISafeguardConnection sgConnection)
        {
            var sg = sgConnection ?? Connect();

            try
            {
                // If we don't have a user Id then try to find the user by name
                if (_configDb.A2aUserId == null || _configDb.A2aUserId == 0)
                {
                    var p = new Dictionary<string, string>
                        {{"filter", $"UserName eq '{WellKnownData.DevOpsUserName(_configDb.SvcId)}'"}};

                    try
                    {
                        var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get,
                            "Users", null, p);
                        if (result.StatusCode == HttpStatusCode.OK)
                        {
                            var foundUsers = JsonHelper.DeserializeObject<List<A2AUser>>(result.Body);

                            if (foundUsers.Count > 0)
                            {
                                var a2aUser = foundUsers.FirstOrDefault();
                                if (a2aUser != null && _configDb.A2aUserId != a2aUser.Id)
                                {
                                    _configDb.A2aUserId = a2aUser.Id;
                                    DevOpsSecretsBrokerCache.A2AUser = a2aUser;
                                }

                                return a2aUser;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"Failed to get the A2A user by name: {ex.Message}");
                    }
                }
                else // Otherwise just get the user by id
                {
                    try
                    {
                        var a2aUser = GetA2AUser(sg, _configDb.A2aUserId);
                        if (a2aUser != null)
                        {
                            return a2aUser;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"Failed to get the A2A user by id {_configDb.A2aUserId}: {ex.Message}");
                    }

                    // Apparently the user id we have is wrong so get rid of it.
                    _configDb.A2aUserId = null;
                }

                return null;
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }
        }

        private A2AUser GetA2AUser(ISafeguardConnection sg, int? id)
        {
            if (id != null)
            {
                try
                {
                    var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get,
                        $"Users/{id.Value}");
                    if (result.StatusCode == HttpStatusCode.OK)
                    {
                        return JsonHelper.DeserializeObject<A2AUser>(result.Body);
                    }
                }
                catch (Exception ex)
                {
                    throw LogAndException($"Failed to get the A2A user by id {id.Value}: {ex.Message}");
                }
            }

            return null;
        }

        private bool DeleteA2AUser(ISafeguardConnection sgConnection, int userId)
        {
            if (sgConnection == null)
            {
                _logger.Error($"Failed to delete the A2A client user. Invalid safeguard connection.");
                return false;
            }
            if (userId == 0)
            {
                _logger.Error("Failed to delete the A2A client user. No user found.");
                return false;
            }

            try
            {
                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sgConnection, Service.Core, Method.Delete, $"Users/{userId}");
                if (result.StatusCode == HttpStatusCode.NoContent)
                {
                    _configDb.A2aUserId = null;
                    _logger.Information("Successfully deleted the A2A client user from safeguard.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                throw LogAndException($"Failed to delete the A2A client user from safeguard: {ex.Message}", ex);
            }

            return false;
        }

        private void EnableA2AService(ISafeguardConnection sg)
        {
            try
            {
                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Appliance, Method.Post,
                    "A2AService/Enable");
                if (result.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Error("Failed to start the A2A service.");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to start the A2A service: {ex.Message}");
            }
        }

        private void CreateA2ARegistration(ISafeguardConnection sgConnection, A2ARegistrationType registrationType)
        {
            if (_configDb.A2aUserId == null)
                throw new DevOpsException("Failed to create A2A registration due to missing A2A user");

            var sg = sgConnection ?? Connect();

            try
            {
                var a2aRegistration = GetA2ARegistration(sg, registrationType);
                if (a2aRegistration == null)
                {
                    var registration = new A2ARegistration()
                    {
                        AppName = registrationType == A2ARegistrationType.Account
                            ? WellKnownData.DevOpsRegistrationName(_configDb.SvcId)
                            : WellKnownData.DevOpsVaultRegistrationName(_configDb.SvcId),
                        CertificateUserId = _configDb.A2aUserId.Value,
                        VisibleToCertificateUsers = true,
                        DevOpsInstanceId = _configDb.SvcId
                    };

                    var registrationStr = JsonHelper.SerializeObject(registration);

                    try
                    {
                        var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Post,
                            "A2ARegistrations", registrationStr);
                        if (result.StatusCode == HttpStatusCode.Created)
                        {
                            registration = JsonHelper.DeserializeObject<A2ARegistration>(result.Body);
                            if (registrationType == A2ARegistrationType.Account)
                            {
                                _configDb.A2aRegistrationId = registration.Id;
                                DevOpsSecretsBrokerCache.A2ARegistration = registration;
                            }
                            else
                            {
                                _configDb.A2aVaultRegistrationId = registration.Id;
                                DevOpsSecretsBrokerCache.A2AVaultRegistration = registration;
                            }

                            UpdateSecretsBrokerInstance(sg, DevOpsSecretsBrokerCache, false);
                        }
                    }
                    catch (Exception ex)
                    {
                        throw LogAndException($"Failed to create the A2A registration: {ex.Message}", ex);
                    }
                }
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }
        }

        public A2ARegistration GetA2ARegistration(ISafeguardConnection sgConnection, A2ARegistrationType registrationType)
        {
            var sg = sgConnection ?? Connect();

            try
            {
                // If we don't have a registration Id then try to find the registration by name
                if ((registrationType == A2ARegistrationType.Account && (_configDb.A2aRegistrationId == null || _configDb.A2aRegistrationId == 0)) ||
                    (registrationType == A2ARegistrationType.Vault && (_configDb.A2aVaultRegistrationId == null || _configDb.A2aVaultRegistrationId == 0)))
                {
                    var knownRegistrationName = (registrationType == A2ARegistrationType.Account)
                        ? WellKnownData.DevOpsRegistrationName(_configDb.SvcId)
                        : WellKnownData.DevOpsVaultRegistrationName(_configDb.SvcId);
                    try
                    {
                        var p = new Dictionary<string, string>
                            {{"filter", $"AppName eq '{knownRegistrationName}'"}};

                        var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get,
                            "A2ARegistrations", null, p);
                        if (result.StatusCode == HttpStatusCode.OK)
                        {
                            var foundRegistrations = JsonHelper.DeserializeObject<List<A2ARegistration>>(result.Body);

                            if (foundRegistrations.Count > 0)
                            {
                                var registration = foundRegistrations.FirstOrDefault();
                                if (registration != null)
                                {
                                    if (registrationType == A2ARegistrationType.Account)
                                    {
                                        _configDb.A2aRegistrationId = registration.Id;
                                    }
                                    else
                                    {
                                        _configDb.A2aVaultRegistrationId = registration.Id;
                                    }
                                }

                                return registration;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"Failed to get the A2A user by name {knownRegistrationName}: {ex.Message}");
                    }
                }
                else // Otherwise just get the registration by id
                {
                    var registrationId = (registrationType == A2ARegistrationType.Account)
                        ? _configDb.A2aRegistrationId
                        : _configDb.A2aVaultRegistrationId;
                    try
                    {
                        var a2aRegistration = GetA2ARegistration(sg, registrationId);
                        if (a2aRegistration != null)
                        {
                            return a2aRegistration;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Just log the exception and set the registration id in the databsae to null.  If there is a
                        //  registration that matches, it will find it the next time.
                        _logger.Error(ex, $"Failed to get the registration for id '{registrationId}': {ex.Message}");
                    }
                }

                // Apparently the registration id we have is wrong so get rid of it.
                if (registrationType == A2ARegistrationType.Account)
                {
                    _configDb.A2aRegistrationId = null;
                }
                else
                {
                    _configDb.A2aVaultRegistrationId = null;
                }
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }

            return null;
        }

        private A2ARegistration GetA2ARegistration(ISafeguardConnection sg, int? id)
        {
            FullResponse result;

            if (id != null)
            {
                try
                {
                    result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get, $"A2ARegistrations/{id}");
                    if (result.StatusCode == HttpStatusCode.OK)
                    {
                        return JsonHelper.DeserializeObject<A2ARegistration>(result.Body);
                    }
                }
                catch (SafeguardDotNetException ex)
                {
                    if (ex.HttpStatusCode == HttpStatusCode.NotFound)
                    {
                        _logger.Error($"Registration not found for id '{id}'", ex);
                    }
                    else
                    {
                        throw LogAndException($"Failed to get the registration for id '{id}'", ex);
                    }
                }
                catch (Exception ex)
                {
                    throw LogAndException($"Failed to get the registration for id '{id}'", ex);
                }
            }

            return null;
        }

        private bool DeleteA2ARegistration(ISafeguardConnection sgConnection, int a2aRegistrationId, A2ARegistrationType registrationType)
        {
            if (sgConnection == null)
            {
                _logger.Error($"Failed to delete the A2A {nameof(registrationType)} registration. Invalid safeguard connection.");
                return false;
            }
            if (a2aRegistrationId == 0)
            {
                _logger.Error($"Failed to delete the A2A {nameof(registrationType)} registration. No A2A registration found.");
                return false;
            }

            try
            {
                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sgConnection, Service.Core, Method.Delete, $"A2aRegistrations/{a2aRegistrationId}");
                if (result.StatusCode == HttpStatusCode.NoContent)
                {
                    if (registrationType == A2ARegistrationType.Account)
                        _configDb.A2aRegistrationId = null;
                    if (registrationType == A2ARegistrationType.Vault)
                        _configDb.A2aVaultRegistrationId = null;

                    _logger.Information($"Successfully deleted the A2A {nameof(registrationType)} registration from safeguard.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                throw LogAndException($"Failed to delete the A2A {nameof(registrationType)} registration from safeguard: {ex.Message}", ex);
            }

            return false;
        }

        public Asset CreateAsset(ISafeguardConnection sgConnection, AssetPartition assetPartition)
        {
            if (assetPartition == null || assetPartition.Id == 0)
                throw LogAndException("Failed to create the asset. Missing the asset partition.");

            var sg = sgConnection ?? Connect();

            try
            {
                var asset = GetAsset(sg);
                if (asset == null)
                {
                    asset = new Asset()
                    {
                        Id = 0,
                        Name =  WellKnownData.DevOpsAssetName(_configDb.SvcId),
                        Description = "DevOps Secrets Broker Asset",
                        PlatformId = 501, //Other Managed
                        NetworkAddress = LocalIPAddress(),
                        AssetPartitionId = assetPartition.Id
                    };

                    var assetStr = JsonHelper.SerializeObject(asset);
                    try
                    {
                        var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Post, "Assets", assetStr);
                        if (result.StatusCode == HttpStatusCode.Created)
                        {
                            asset = JsonHelper.DeserializeObject<Asset>(result.Body);
                            if (DevOpsSecretsBrokerCache?.Asset == null ||
                                DevOpsSecretsBrokerCache.Asset.Id != asset.Id)
                            {
                                _configDb.AssetId = asset.Id;
                                if (DevOpsSecretsBrokerCache != null)
                                {
                                    DevOpsSecretsBrokerCache.Asset = asset;
                                    UpdateSecretsBrokerInstance(sg, DevOpsSecretsBrokerCache, false);
                                }
                            }

                            return asset;
                        }
                    }
                    catch (Exception ex)
                    {
                        throw LogAndException($"Failed to create the asset: {ex.Message}", ex);
                    }
                }
                else
                {
                    if (DevOpsSecretsBrokerCache?.Asset == null ||
                        DevOpsSecretsBrokerCache.Asset.Id != asset.Id)
                    {
                        _configDb.AssetId = asset.Id;
                        if (DevOpsSecretsBrokerCache != null)
                        {
                            DevOpsSecretsBrokerCache.Asset = asset;
                            UpdateSecretsBrokerInstance(sg, DevOpsSecretsBrokerCache, false);
                        }
                    }

                    return asset;
                }
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }

            return null;
        }

        public Asset GetAsset(ISafeguardConnection sgConnection)
        {
            var sg = sgConnection ?? Connect();

            try
            {
                FullResponse result;

                // If we don't have an asset Id then try to find the asset by name
                if (_configDb.AssetId == null || _configDb.AssetId == 0)
                {
                    var knownAssetName = WellKnownData.DevOpsAssetName(_configDb.SvcId);
                    try
                    {
                        var p = new Dictionary<string, string>
                            {{"filter", $"Name eq '{knownAssetName}'"}};

                        result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get, "Assets",
                            null, p);
                        if (result.StatusCode == HttpStatusCode.OK)
                        {
                            var foundAssets = JsonHelper.DeserializeObject<List<Asset>>(result.Body);

                            if (foundAssets.Count > 0)
                            {
                                var asset = foundAssets.FirstOrDefault();
                                if (asset != null)
                                {
                                    _configDb.AssetId = asset.Id;
                                    if (DevOpsSecretsBrokerCache != null)
                                        DevOpsSecretsBrokerCache.Asset = asset;
                                }

                                return asset;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"Failed to get the asset by name {knownAssetName}: {ex.Message}");
                    }
                }
                else // Otherwise just get the asset by id
                {
                    try
                    {
                        var asset = GetAsset(sg, _configDb.AssetId);
                        if (asset != null)
                        {
                            DevOpsSecretsBrokerCache.Asset = asset;
                            return asset;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Just log the error and move on.  This will set the asset id in the database to null.
                        //  If there is actually an asset that matches, it will find it the next time.
                        _logger.Error(ex,
                            $"Failed to get the asset for id '{_configDb.AssetId}': {ex.Message}");
                    }
                }

                // Apparently the asset id we have is wrong so get rid of it.
                _configDb.AssetId = null;
                if (DevOpsSecretsBrokerCache != null)
                    DevOpsSecretsBrokerCache.Asset = null;
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }

            return null;
        }

        private Asset GetAsset(ISafeguardConnection sg, int? id)
        {
            if ((id ?? 0) > 0)
            {
                try
                {
                    var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get, $"Assets/{id}");
                    if (result.StatusCode == HttpStatusCode.OK)
                    {
                        return JsonHelper.DeserializeObject<Asset>(result.Body);
                    }
                }
                catch (SafeguardDotNetException ex)
                {
                    if (ex.HttpStatusCode == HttpStatusCode.NotFound)
                    {
                        _logger.Error(ex, $"Asset not found for id '{id}'");
                    }
                    else
                    {
                        throw LogAndException($"Failed to get the asset for id '{id}'", ex);
                    }
                }
                catch (Exception ex)
                {
                    throw LogAndException($"Failed to get the asset for id '{id}'", ex);
                }
            }

            return null;
        }

        public AssetPartition CreateAssetPartition(ISafeguardConnection sgConnection)
        {
            var sg = sgConnection ?? Connect();

            try
            {
                var assetPartition = GetAssetPartition(sg);
                if (assetPartition == null)
                {
                    var user = GetLoggedInUser(sg);
                    assetPartition = new AssetPartition()
                    {
                        Id = 0,
                        Description = "DevOps Secrets Broker Asset Partition",
                        Name = WellKnownData.DevOpsAssetPartitionName(_configDb.SvcId),
                        ManagedBy = new[] {new Identity
                        {
                            Name = user.UserName,
                            Id = user.Id
                        }}
                    };

                    var assetStr = JsonHelper.SerializeObject(assetPartition);
                    try
                    {
                        var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Post, "AssetPartitions", assetStr);
                        if (result.StatusCode == HttpStatusCode.Created)
                        {
                            assetPartition = JsonHelper.DeserializeObject<AssetPartition>(result.Body);
                            if (DevOpsSecretsBrokerCache?.Asset == null ||
                                DevOpsSecretsBrokerCache.Asset.Id != assetPartition.Id)
                            {
                                _configDb.AssetPartitionId = assetPartition.Id;
                                if (DevOpsSecretsBrokerCache != null)
                                {
                                    DevOpsSecretsBrokerCache.AssetPartition = assetPartition;
                                    UpdateSecretsBrokerInstance(sg, DevOpsSecretsBrokerCache, false);
                                }
                            }

                            return assetPartition;
                        }
                    }
                    catch (Exception ex)
                    {
                        throw LogAndException($"Failed to create the asset partition: {ex.Message}", ex);
                    }
                }
                else
                {
                    if (DevOpsSecretsBrokerCache?.AssetPartition == null ||
                         DevOpsSecretsBrokerCache.AssetPartition.Id != assetPartition.Id)
                    {
                        _configDb.AssetPartitionId = assetPartition.Id;
                        if (DevOpsSecretsBrokerCache != null)
                        {
                            DevOpsSecretsBrokerCache.AssetPartition = assetPartition;
                            UpdateSecretsBrokerInstance(sg, DevOpsSecretsBrokerCache, false);
                        }
                    }

                    return assetPartition;
                }
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }

            return null;
        }

        private AssetPartition GetAssetPartition(ISafeguardConnection sgConnection)
        {
            var sg = sgConnection ?? Connect();

            try
            {
                // If we don't have an asset partition Id then try to find the asset partition by name
                if (_configDb.AssetPartitionId == null || _configDb.AssetPartitionId == 0)
                {
                    var knownAssetPartitionName = WellKnownData.DevOpsAssetPartitionName(_configDb.SvcId);
                    try
                    {
                        var p = new Dictionary<string, string>
                            {{"filter", $"Name eq '{knownAssetPartitionName}'"}};

                        var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get,
                            "AssetPartitions", null, p);
                        if (result.StatusCode == HttpStatusCode.OK)
                        {
                            var foundAssetPartitions =
                                JsonHelper.DeserializeObject<List<AssetPartition>>(result.Body);

                            if (foundAssetPartitions.Count > 0)
                            {
                                var assetPartition = foundAssetPartitions.FirstOrDefault();
                                if (assetPartition != null)
                                {
                                    _configDb.AssetPartitionId = assetPartition.Id;
                                    if (DevOpsSecretsBrokerCache != null)
                                        DevOpsSecretsBrokerCache.AssetPartition = assetPartition;
                                }

                                return assetPartition;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex,
                            $"Failed to get the asset partition by name {knownAssetPartitionName}: {ex.Message}");
                    }
                }
                else // Otherwise just get the registration by id
                {
                    try
                    {
                        var assetPartition = GetAssetPartition(sg, _configDb.AssetPartitionId);
                        if (assetPartition != null)
                        {
                            return assetPartition;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Just log the error and move on. If there is an asset partition that matches, Secrets Broker will find it
                        //  the next time.
                        _logger.Error(ex,
                            $"Failed to get the asset partition for id '{_configDb.AssetPartitionId}': {ex.Message}");
                    }
                }

                // Apparently the asset partition id we have is wrong so get rid of it.
                _configDb.AssetPartitionId = null;
                if (DevOpsSecretsBrokerCache != null)
                    DevOpsSecretsBrokerCache.AssetPartition = null;
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }

            return null;
        }

        private AssetPartition GetAssetPartition(ISafeguardConnection sg, int? id)
        {
            try
            {
                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get, $"AssetPartitions/{id}");
                if (result.StatusCode == HttpStatusCode.OK)
                {
                    return JsonHelper.DeserializeObject<AssetPartition>(result.Body);
                }
            }
            catch (SafeguardDotNetException ex)
            {
                if (ex.HttpStatusCode == HttpStatusCode.NotFound)
                {
                    _logger.Error($"Asset partition not found for id '{id}'", ex);
                }
                else
                {
                    throw LogAndException($"Failed to get the asset partition for id '{id}'", ex);
                }
            }
            catch (Exception ex)
            {
                throw LogAndException($"Failed to get the asset partition for id '{id}'", ex);
            }

            return null;
        }

        private bool DeleteAssetPartition(ISafeguardConnection sgConnection, int assetPartitionId)
        {
            if (sgConnection == null)
            {
                _logger.Error("Failed to delete the asset partition. Invalid safeguard connection.");
                return false;
            }
            if (assetPartitionId == 0)
            {
                _logger.Error("Failed to delete the asset partition. No asset partition found.");
                return false;
            }

            try
            {
                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sgConnection, Service.Core, Method.Delete, $"AssetPartitions/{assetPartitionId}");
                if (result.StatusCode == HttpStatusCode.NoContent)
                {
                    _configDb.AssetPartitionId = null;
                    _logger.Information("Successfully deleted the asset partition from safeguard.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                throw LogAndException($"Failed to delete the asset partition from safeguard: {ex.Message}", ex);
            }

            return false;
        }

        public AssetAccountGroup CreateAssetAccountGroup(ISafeguardConnection sgConnection, Addon addon)
        {
            if (addon?.VaultAccountName == null)
            {
                _logger.Error("Failed to create the asset account group. Invalid add-on provided.");
                return null;
            }

            var sg = sgConnection ?? Connect();

            try
            {
                var assetAccountGroup = GetAssetAccountGroup(sg);
                if (assetAccountGroup == null)
                {
                    assetAccountGroup = new AssetAccountGroup
                    {
                        Id = 0,
                        Description = "DevOps Secrets Broker Asset Account Group",
                        Name = WellKnownData.DevOpsAssetAccountGroupName(_configDb.SvcId),
                        IsDynamic = true,
                        GroupingRule = new TaggingGroupingRule
                        {
                            Description = "DevOps Secrets Broker Dynamic Group",
                            Enabled = true,
                            RuleConditionGroup = new TaggingGroupingConditionGroup()
                            {
                                LogicalJoinType = ConditionJoinType.And,
                                Children = new[]
                                {
                                    new TaggingGroupingConditionOrConditionGroup()
                                    {
                                        TaggingGroupingCondition = new TaggingGroupingCondition
                                        {
                                            ObjectAttribute = TaggingGroupingObjectAttributes.Name,
                                            CompareType = ComparisonOperator.StartsWith,
                                            CompareValue = addon.Name
                                        }
                                    } 
                                }
                            }
                        }
                    };

                    var assetAccountGroupStr = JsonHelper.SerializeObject(assetAccountGroup);
                    try
                    {
                        var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Post, "AccountGroups", assetAccountGroupStr);
                        if (result.StatusCode == HttpStatusCode.Created)
                        {
                            assetAccountGroup = JsonHelper.DeserializeObject<AssetAccountGroup>(result.Body);
                            if (DevOpsSecretsBrokerCache?.AssetAccountGroup == null ||
                                DevOpsSecretsBrokerCache.AssetAccountGroup.Id != assetAccountGroup.Id)
                            {
                                _configDb.AssetAccountGroupId = assetAccountGroup.Id;
                                if (DevOpsSecretsBrokerCache != null)
                                {
                                    DevOpsSecretsBrokerCache.AssetAccountGroup = assetAccountGroup;
                                    UpdateSecretsBrokerInstance(sg, DevOpsSecretsBrokerCache, false);
                                }
                            }

                            return assetAccountGroup;
                        }
                    }
                    catch (Exception ex)
                    {
                        throw LogAndException($"Failed to create the asset account group: {ex.Message}", ex);
                    }
                }
                else
                {
                    if (DevOpsSecretsBrokerCache?.AssetAccountGroup == null ||
                         DevOpsSecretsBrokerCache.AssetAccountGroup.Id != assetAccountGroup.Id)
                    {
                        _configDb.AssetAccountGroupId = assetAccountGroup.Id;
                        if (DevOpsSecretsBrokerCache != null)
                        {
                            DevOpsSecretsBrokerCache.AssetAccountGroup = assetAccountGroup;
                            UpdateSecretsBrokerInstance(sg, DevOpsSecretsBrokerCache, false);
                        }
                    }

                    return assetAccountGroup;
                }
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }

            return null;
        }

        private AssetAccountGroup GetAssetAccountGroup(ISafeguardConnection sgConnection)
        {
            var sg = sgConnection ?? Connect();

            try
            {
                // If we don't have an asset account group Id then try to find the asset account group by name
                if ((_configDb.AssetAccountGroupId ?? 0) == 0)
                {
                    var knownAssetAccountGroupName = WellKnownData.DevOpsAssetAccountGroupName(_configDb.SvcId);
                    try
                    {
                        var p = new Dictionary<string, string>
                            {{"filter", $"Name eq '{knownAssetAccountGroupName}'"}};

                        var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get,
                            "AccountGroups", null, p);
                        if (result.StatusCode == HttpStatusCode.OK)
                        {
                            var foundAssetAccountGroups =
                                JsonHelper.DeserializeObject<List<AssetAccountGroup>>(result.Body);

                            if (foundAssetAccountGroups.Count > 0)
                            {
                                var assetAccountGroup = foundAssetAccountGroups.FirstOrDefault();
                                if (assetAccountGroup != null)
                                {
                                    _configDb.AssetAccountGroupId = assetAccountGroup.Id;
                                    if (DevOpsSecretsBrokerCache != null)
                                        DevOpsSecretsBrokerCache.AssetAccountGroup = assetAccountGroup;
                                }

                                return assetAccountGroup;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex,
                            $"Failed to get the asset account group by name {knownAssetAccountGroupName}: {ex.Message}");
                    }
                }
                else // Otherwise just get the account group by id
                {
                    try
                    {
                        var assetAccountGroup = GetAssetAccountGroup(sg, _configDb.AssetAccountGroupId);
                        if (assetAccountGroup != null)
                        {
                            return assetAccountGroup;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Just log the error and move on. If there is an asset account group that matches, Secrets Broker will find it
                        //  the next time.
                        _logger.Error(ex,
                            $"Failed to get the asset account group for id '{_configDb.AssetAccountGroupId}': {ex.Message}");
                    }
                }

                // Apparently the asset account group id we have is wrong so get rid of it.
                _configDb.AssetAccountGroupId = null;
                if (DevOpsSecretsBrokerCache != null)
                    DevOpsSecretsBrokerCache.AssetAccountGroup = null;
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }

            return null;
        }

        private AssetAccountGroup GetAssetAccountGroup(ISafeguardConnection sg, int? id)
        {
            try
            {
                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get, $"AccountGroups/{id}");
                if (result.StatusCode == HttpStatusCode.OK)
                {
                    return JsonHelper.DeserializeObject<AssetAccountGroup>(result.Body);
                }
            }
            catch (SafeguardDotNetException ex)
            {
                if (ex.HttpStatusCode == HttpStatusCode.NotFound)
                {
                    _logger.Error($"Asset account group not found for id '{id}'", ex);
                }
                else
                {
                    throw LogAndException($"Failed to get the asset account group for id '{id}'", ex);
                }
            }
            catch (Exception ex)
            {
                throw LogAndException($"Failed to get the asset account group for id '{id}'", ex);
            }

            return null;
        }

        private bool DeleteAssetAccountGroup(ISafeguardConnection sgConnection, int assetAccountGroupId)
        {
            if (sgConnection == null)
            {
                _logger.Error("Failed to delete the asset account group. Invalid safeguard connection.");
                return false;
            }
            if (assetAccountGroupId == 0)
            {
                _logger.Error("Failed to delete the asset account group. No asset account group found.");
                return false;
            }

            try
            {
                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sgConnection, Service.Core, Method.Delete, $"AccountGroups/{assetAccountGroupId}");
                if (result.StatusCode == HttpStatusCode.NoContent)
                {
                    _configDb.AssetAccountGroupId = null;
                    _logger.Information("Successfully deleted the asset account group from safeguard.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                throw LogAndException($"Failed to delete the asset account group from safeguard: {ex.Message}", ex);
            }

            return false;
        }


        private string LocalIPAddress()
        {

            var addresses = GetLocalIPAddresses();
            if (addresses != null)
            {
                var hostAddresses = string.Join(",", addresses.Select(x => $"\"{x}\""));
                _logger.Debug($"Found host IP address(s) {hostAddresses}");

                return hostAddresses;
            }

            return null;
        }

        private List<IPAddress> GetLocalIPAddresses()
        {
            if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
            {
                return null;
            }

            if (bool.Parse(Environment.GetEnvironmentVariable("DOCKER_RUNNING") ?? "false"))
            {
                _logger.Debug("Running in Docker and searching for local IP address");
                var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST_IP") ?? "";
                _logger.Debug($"Found host IP address {dockerHost}");

                try
                {
                    var address = IPAddress.Parse(dockerHost);
                    return new List<IPAddress>()
                    {
                        address
                    };
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Failed to find or convert the IP address from the environment.", ex);
                    return null;
                }
            }

            _logger.Debug("Running as a service and searching for local IP address");
            var host = Dns.GetHostEntry(Dns.GetHostName());

            var addresses = host.AddressList.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork);
            return addresses.ToList();
        }

        public void RemoveClientCertificate()
        {
            _configDb.UserCertificateBase64Data = null;
            _configDb.UserCertificatePassphrase = null;
            _configDb.UserCertificateThumbprint = null;
        }

        public void RemoveWebServerCertificate()
        {
            _configDb.WebSslCertificateBase64Data = null;
            _configDb.WebSslCertificatePassphrase = null;
            _configDb.WebSslCertificate = CertificateHelper.CreateDefaultSslCertificate();
        }

        public ISafeguardConnection Connect()
        {
            var sc = _serviceConfiguration;
            if (sc != null)
            {
                return Connect(sc.Appliance.ApplianceAddress, sc.AccessToken, sc.Appliance.ApiVersion,
                    sc.Appliance.IgnoreSsl);
            }

            throw LogAndException("Failed to connect to Safeguard. The service configuration is not set yet.");
        }

        private ISafeguardConnection Connect(string address, SecureString token, int? version, bool? ignoreSsl)
        {
            try
            {
                _logger.Debug("Connecting to Safeguard as user: {address}", address);
                return (ignoreSsl.HasValue && ignoreSsl.Value)
                    ? Safeguard.Connect(address, token, version ?? WellKnownData.DefaultApiVersion, true)
                    : Safeguard.Connect(address, token, CertificateValidationCallback, version ?? WellKnownData.DefaultApiVersion);
            }
            catch (SafeguardDotNetException ex)
            {
                throw LogAndException($"Failed to connect to Safeguard at '{address}': {ex.Message}", ex);
            }
        }

        public ISafeguardConnection CertConnect()
        {
            var sppAddress = _configDb.SafeguardAddress;
            var userCertificate = _configDb.UserCertificateBase64Data;
            var passPhrase = _configDb.UserCertificatePassphrase?.ToSecureString();
            var apiVersion = _configDb.ApiVersion ?? WellKnownData.DefaultApiVersion;
            var ignoreSsl = _configDb.IgnoreSsl ?? true;

            if (sppAddress != null && userCertificate != null)
            {
                try
                {
                    _logger.Debug("Connecting to Safeguard as cert user: {address}", sppAddress);
                    var connection = Safeguard.Connect(sppAddress, Convert.FromBase64String(userCertificate),
                        passPhrase, apiVersion, ignoreSsl);
                    return connection;
                }
                catch (SafeguardDotNetException ex)
                {
                    _logger.Error(ex, $"Failed to connect to Safeguard at '{sppAddress}': {ex.Message}");
                }
            }

            return null;
        }

        public bool ValidateLogin(string token, bool tokenOnly = false)
        {

            return ValidateLogin(token, tokenOnly
                ? null
                : new SafeguardDevOpsConnection()
                {
                    ApiVersion = _configDb.ApiVersion ?? WellKnownData.DefaultApiVersion,
                    ApplianceAddress = _configDb.SafeguardAddress,
                    IgnoreSsl = _configDb.IgnoreSsl ?? false
                });
        }

        private bool ValidateLogin(string token, SafeguardDevOpsConnection safeguardConnection = null, string tempKey = null)
        {
            if (token == null)
                return false;

            try
            {
                var key = tempKey ?? _configDb.SigningCertificate;
                var bytes = Convert.FromBase64String(key);
                var cert = new X509Certificate2(bytes);

                var parts = token.Split('.');

                var header = parts[0];
                var payload = parts[1];
                var signature = Base64UrlTextEncoder.Decode(parts[2]);
                var data = Encoding.UTF8.GetBytes(header + '.' + payload);

                var validToken = cert.GetRSAPublicKey()
                    .VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                if (validToken && safeguardConnection != null)
                {
                    return GetAndValidateUserPermissions(token, safeguardConnection);
                }

                return validToken;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, ex.Message);
            }

            return false;
        }

        public CertificateInfo GetCertificateInfo(CertificateType certificateType)
        {
            var cert = certificateType == CertificateType.A2AClient
                ? _configDb.UserCertificate
                : _configDb.WebSslCertificate;

            if (cert != null)
            {
                var result = new CertificateInfo()
                {
                    Thumbprint = cert.Thumbprint,
                    IssuedBy = cert.Issuer,
                    Subject = cert.Subject,
                    NotAfter = cert.NotAfter,
                    NotBefore = cert.NotBefore,
                    Base64CertificateData = cert.ToPemFormat(),
                    PassedTrustChainValidation = certificateType != CertificateType.A2AClient || CertificateHelper.ValidateTrustChain(cert, _configDb, _logger)
                };

                return result;
            }

            var msg = $"{Enum.GetName(typeof(CertificateType), certificateType)} certificate not found.";
            _logger.Error(msg);
            throw new DevOpsException(msg, HttpStatusCode.NotFound);
        }

        public void InstallCertificate(CertificateInfo certificate, CertificateType certificateType)
        {
            var certificateBytes = CertificateHelper.ConvertPemToData(certificate.Base64CertificateData);
            X509Certificate2 cert;
            try
            {
                cert = certificate.Passphrase == null
                    ? new X509Certificate2(certificateBytes)
                    : new X509Certificate2(certificateBytes, certificate.Passphrase);
                _logger.Debug(
                    $"Parsed certificate for installation: subject={cert.SubjectName.Name}, thumbprint={cert.Thumbprint}");
            }
            catch (Exception ex)
            {
                throw LogAndException($"Failed to convert the provided certificate: {ex.Message}", ex);
            }

            if (cert.HasPrivateKey)
            {
                _logger.Debug("Parsed certificate contains private key");
                if (!CertificateHelper.ValidateCertificate(cert, certificateType))
                    throw new DevOpsException("Invalid certificate");

                switch (certificateType)
                {
                    case CertificateType.A2AClient:
                    {
                        _configDb.UserCertificatePassphrase = certificate.Passphrase;
                        _configDb.UserCertificateBase64Data = certificate.Base64CertificateData;
                        break;
                    }
                    case CertificateType.WebSsl:
                    {
                        _configDb.WebSslCertificatePassphrase = certificate.Passphrase;
                        _configDb.WebSslCertificateBase64Data = certificate.Base64CertificateData;
                        break;
                    }
                    default:
                    {
                        throw new DevOpsException("Invalid certificate type");
                    }
                }
            }
            else
            {
                try
                {
                    using var rsa = RSA.Create();
                    var privateKeyBytesBase64 = certificateType == CertificateType.A2AClient
                        ? _configDb.UserCsrPrivateKeyBase64Data
                        : _configDb.WebSslCsrPrivateKeyBase64Data;
                    if (privateKeyBytesBase64 == null)
                    {
                        throw LogAndException(
                            "Failed to find a matching private key. Possibly a mismatched CSR type was selected.");
                    }

                    var privateKeyBytes = Convert.FromBase64String(privateKeyBytesBase64);
                    rsa.ImportRSAPrivateKey(privateKeyBytes, out _);

                    using (X509Certificate2 pubOnly = cert)
                    using (X509Certificate2 pubPrivEphemeral = pubOnly.CopyWithPrivateKey(rsa))
                    {
                        if (!CertificateHelper.ValidateCertificate(pubPrivEphemeral, certificateType))
                            throw new DevOpsException("Invalid certificate");

                        switch (certificateType)
                        {
                            case CertificateType.A2AClient:
                            {
                                _configDb.UserCertificatePassphrase = null;
                                _configDb.UserCertificateBase64Data =
                                    Convert.ToBase64String(pubPrivEphemeral.Export(X509ContentType.Pfx));
                                break;
                            }
                            case CertificateType.WebSsl:
                            {
                                _configDb.WebSslCertificatePassphrase = null;
                                _configDb.WebSslCertificateBase64Data =
                                    Convert.ToBase64String(pubPrivEphemeral.Export(X509ContentType.Pfx));
                                break;
                            }
                            default:
                            {
                                throw new DevOpsException("Invalid certificate type");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw LogAndException($"Failed to import the certificate: {ex.Message}", ex);
                }
            }
        }

        public string GetCSR(int? size, string subjectName, string sanDns, string sanIp, CertificateType certificateType)
        {
            var certSize = 2048;
            var certSubjectName = certificateType == CertificateType.A2AClient
                ? WellKnownData.DevOpsServiceClientCertificate(_configDb.SvcId)
                : WellKnownData.DevOpsServiceWebSslCertificate(_configDb.SvcId);

            if (size != null)
                certSize = size.Value;
            if (subjectName != null)
            {
                try
                {
                    subjectName = subjectName.Trim('"').Trim('\'');
                    var validCnName = new X500DistinguishedName(subjectName);
                    certSubjectName = validCnName.Name;
                }
                catch (Exception ex)
                {
                    throw LogAndException("Invalid subject name.", ex);
                }
            }

            using (RSA rsa = RSA.Create(certSize))
            {
                var certificateRequest = new CertificateRequest(certSubjectName, rsa,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

                certificateRequest.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(false, false, 0, false));

                switch (certificateType)
                {
                    case CertificateType.A2AClient:
                    {
                        certificateRequest.CertificateExtensions.Add(
                            new X509KeyUsageExtension(
                                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement | X509KeyUsageFlags.KeyEncipherment,
                                true));
                        certificateRequest.CertificateExtensions.Add(
                            new X509EnhancedKeyUsageExtension(
                                new OidCollection
                                {
                                    new Oid("1.3.6.1.5.5.7.3.2")
                                },
                                false));
                        break;
                    }
                    case CertificateType.WebSsl:
                    {
                        certificateRequest.CertificateExtensions.Add(
                            new X509KeyUsageExtension(
                                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement | X509KeyUsageFlags.KeyEncipherment,
                                true));
                        certificateRequest.CertificateExtensions.Add(
                            new X509EnhancedKeyUsageExtension(
                                new OidCollection
                                {
                                    new Oid("1.3.6.1.5.5.7.3.1")
                                },
                                false));
                        break;
                    }
                    default:
                    {
                        throw new DevOpsException("Invalid certificate type");
                    }
                }

                certificateRequest.CertificateExtensions.Add(
                    new X509SubjectKeyIdentifierExtension(certificateRequest.PublicKey, false));

                var sanBuilder = new SubjectAlternativeNameBuilder();
                if (sanIp != null)
                {
                    try
                    {
                        var addresses = sanIp.Split(',').ToList();
                        addresses.ForEach(x => sanBuilder.AddIpAddress(IPAddress.Parse(x)));
                    }
                    catch (Exception ex)
                    {
                        throw LogAndException("Invalid SAN IP address list.", ex);
                    }
                }

                if (sanDns != null)
                {
                    try
                    {
                        var dnsNames = sanDns.Split(',').ToList();
                        dnsNames.ForEach(x => sanBuilder.AddDnsName(x.Trim()));
                    }
                    catch (Exception ex)
                    {
                        throw LogAndException("Invalid SAN DNS list.", ex);
                    }
                }

                certificateRequest.CertificateExtensions.Add(sanBuilder.Build());

                var csr = certificateRequest.CreateSigningRequest();
                switch (certificateType)
                {
                    case CertificateType.A2AClient:
                    {
                        _configDb.UserCsrBase64Data = Convert.ToBase64String(csr);
                        _configDb.UserCsrPrivateKeyBase64Data = Convert.ToBase64String(rsa.ExportRSAPrivateKey());
                        break;
                    }
                    case CertificateType.WebSsl:
                    {
                        _configDb.WebSslCsrBase64Data = Convert.ToBase64String(csr);
                        _configDb.WebSslCsrPrivateKeyBase64Data = Convert.ToBase64String(rsa.ExportRSAPrivateKey());
                        break;
                    }
                    // ReSharper disable once UnreachableSwitchCaseDueToIntegerAnalysis
                    default:
                    {
                        throw new DevOpsException("Invalid certificate type");
                    }
                }

                var builder = new StringBuilder();
                builder.Append("-----BEGIN CERTIFICATE REQUEST-----\r\n");
                builder.Append(PemExtensions.AddPemLineBreaks(Convert.ToBase64String(csr)));
                builder.Append("\r\n-----END CERTIFICATE REQUEST-----");

                return builder.ToString();
            }
        }

        public DevOpsSecretsBroker ConfigureDevOpsService()
        {
            using var sg = Connect();
            CreateA2AUser(sg);
            CreateA2ARegistration(sg, A2ARegistrationType.Account);
            CreateA2ARegistration(sg, A2ARegistrationType.Vault);
            EnableA2AService(sg);

            CheckAndSyncSecretsBrokerInstance(null);

            return GetDevOpsConfiguration(sg);
        }

        public SafeguardDevOpsConnection GetAnonymousSafeguardConnection()
        {
            if (string.IsNullOrEmpty(_configDb.SafeguardAddress))
                return new SafeguardDevOpsConnection();

            ISafeguardConnection sg = null;
            try
            {
                var safeguardConnection = new SafeguardDevOpsConnection
                {
                    ApplianceAddress = _configDb.SafeguardAddress,
                    IgnoreSsl = _configDb.IgnoreSsl,
                    ApiVersion = _configDb.ApiVersion ?? WellKnownData.DefaultApiVersion
                };

                sg = Safeguard.Connect(safeguardConnection.ApplianceAddress,
                    safeguardConnection.ApiVersion ?? WellKnownData.DefaultApiVersion, true);
                return GetSafeguardAvailability(sg, safeguardConnection);
            }
            catch (SafeguardDotNetException ex)
            {
                throw LogAndException($"Failed to contact Safeguard at '{_configDb.SafeguardAddress}': {ex.Message}",
                    ex);
            }
            finally
            {
                sg?.Dispose();
            }
        }

        public SafeguardDevOpsConnection GetSafeguardConnection()
        {
            if (string.IsNullOrEmpty(_configDb.SafeguardAddress))
                return new SafeguardDevOpsConnection();

            return ConnectAnonymous(_configDb.SafeguardAddress, _configDb.ApiVersion ?? WellKnownData.DefaultApiVersion, true);
        }

        public SafeguardDevOpsLogon GetSafeguardLogon()
        {
            var safeguardConnection = GetSafeguardConnection();
            if (safeguardConnection == null)
                return null;

            var availableA2aRegistrationIds = GetAvailableA2ARegistrationIds(null);
            var webSslCertificate = _configDb.WebSslCertificate;
            var userCertificate = _configDb.UserCertificate;

            var safeguardLogon = new SafeguardDevOpsLogon()
            {
                SafeguardDevOpsConnection = safeguardConnection,
                HasAvailableA2ARegistrations = availableA2aRegistrationIds.Any(),
                HasMissingPlugins = _pluginsLogic().GetAllPlugins().Any(x => !x.IsLoaded),
                NeedsClientCertificate = userCertificate == null,
                NeedsSSLEnabled = safeguardConnection.IgnoreSsl ?? true,
                NeedsTrustedCertificates = !_configDb.GetAllTrustedCertificates().Any() || !CheckSslConnection(),
                NeedsWebCertificate = webSslCertificate?.SubjectName.Name != null && webSslCertificate.SubjectName.Name.Equals(WellKnownData.DevOpsServiceDefaultWebSslCertificateSubject),
                PassedTrustChainValidation = userCertificate != null ? CertificateHelper.ValidateTrustChain(userCertificate, _configDb, _logger) : false
            };

            return safeguardLogon;
        }

        public SafeguardDevOpsConnection SetSafeguardData(string token, SafeguardData safeguardData)
        {
            if (token == null)
                throw new DevOpsException("Invalid authorization token.", HttpStatusCode.Unauthorized);

            if (_configDb.SafeguardAddress != null)
            {
                if (!string.IsNullOrEmpty(safeguardData.ApplianceAddress) 
                    && !_configDb.SafeguardAddress.Equals(safeguardData.ApplianceAddress, StringComparison.OrdinalIgnoreCase))
                {
                    var newSignatureCert = FetchSignatureCertificate(safeguardData.ApplianceAddress);
                    if (!_configDb.SigningCertificate.Equals(newSignatureCert))
                    {
                        throw LogAndException(
                            "Invalid operation. The previously configured Secrets Broker cannot be repurposed until the configuration is deleted.");
                    }
                }

                var signatureCert = FetchSignatureCertificate(_configDb.SafeguardAddress);
                if (!ValidateLogin(token, null, signatureCert))
                {
                    throw LogAndException("Invalid login token. Authentication failed.");
                }
            }

            var enablingSsl = safeguardData.IgnoreSsl.HasValue && !safeguardData.IgnoreSsl.Value;
            if (enablingSsl && !_configDb.GetAllTrustedCertificates().Any())
            {
                throw LogAndException("Cannot enable TLS before adding trusted certificates.");
            }

            if (enablingSsl && !CheckSslConnection())
            {
                throw LogAndException("Invalid certificate chain. Unable to validate the Safeguard certificate without a complete trusted certificate chain.");
            }

            var safeguardConnection = ConnectAnonymous(safeguardData.ApplianceAddress,
                safeguardData.ApiVersion ?? WellKnownData.DefaultApiVersion, safeguardData.IgnoreSsl ?? false);

            // If the user is trying to change the state of the ignoreSsl flag back to true, then use the new value.
            //  If the current value of the flag in the database or the new value is null, then assume true or the current value.
            safeguardConnection.IgnoreSsl = (safeguardConnection.IgnoreSsl ?? true) || (safeguardData.IgnoreSsl ?? (safeguardConnection.IgnoreSsl ?? true));

            _applianceAvailabilityCache = null;
            if (FetchAndStoreSignatureCertificate(token, safeguardConnection))
            {
                _configDb.SafeguardAddress = safeguardData.ApplianceAddress;
                _configDb.ApiVersion = safeguardData.ApiVersion ?? WellKnownData.DefaultApiVersion;
                _configDb.IgnoreSsl = safeguardData.IgnoreSsl ?? true;

                safeguardConnection.ApplianceAddress = _configDb.SafeguardAddress;
                safeguardConnection.ApiVersion = _configDb.ApiVersion;
                safeguardConnection.IgnoreSsl = _configDb.IgnoreSsl;
                return safeguardConnection;
            }

            throw LogAndException(
                $"Invalid authorization token or SPP appliance {safeguardData.ApplianceAddress} is unavailable.");
        }

        public void DeleteSecretsBrokerData()
        {
            _applianceAvailabilityCache = null;
            // Since the database is about to be dropped, we can't store deleted plugins in the database.
            // Write an empty file in the plugins dir to indicate on startup that the plugins need to be deleted.
            if (Directory.Exists(WellKnownData.PluginDirPath))
            {
                File.Create(WellKnownData.DeleteAllPlugins).Dispose();
            }

            _configDb.DropDatabase();
            _configDb.SvcId = WellKnownData.ServiceIdentitifierRegenerate;
            RestartService();
        }

        private void RestoreDefaultPort()
        {
            if (File.Exists(WellKnownData.AppSettingsFile))
            {
                File.Delete(WellKnownData.AppSettingsFile);
            }
        }

        private void DeleteSafeguardData(ISafeguardConnection sgConnection)
        {
            var tasks = new[]
            {
                Task.Run(() =>
                {
                    if ((_configDb.AssetAccountGroupId ?? 0) > 0) 
                        DeleteAssetAccountGroup(sgConnection, _configDb.AssetAccountGroupId ?? 0);
                }),
                Task.Run(() =>
                {
                    if ((_configDb.AssetId ?? 0) > 0) 
                        DeleteAssetAndAccounts(sgConnection, _configDb.AssetId ?? 0);
                }),
                Task.Run(() =>
                {
                    if ((_configDb.AssetPartitionId ?? 0) > 0) 
                        DeleteAssetPartition(sgConnection, _configDb.AssetPartitionId ?? 0);
                }),
                Task.Run(() =>
                {
                    if ((_configDb.A2aRegistrationId ?? 0) > 0) 
                        DeleteA2ARegistration(sgConnection, _configDb.A2aRegistrationId ?? 0, A2ARegistrationType.Account);
                }),
                Task.Run(() =>
                {
                    if ((_configDb.A2aVaultRegistrationId ?? 0) > 0) 
                        DeleteA2ARegistration(sgConnection, _configDb.A2aVaultRegistrationId ?? 0, A2ARegistrationType.Vault);
                }),
                Task.Run(() =>
                {
                    if ((_configDb.A2aUserId ?? 0) > 0) 
                        DeleteA2AUser(sgConnection, _configDb.A2aUserId ?? 0);
                })
            };
            
            if (tasks.Any())
                Task.WaitAll(tasks.ToArray());

        }

        public void DeleteDevOpsConfiguration(ISafeguardConnection sgConnection, bool secretsBrokerOnly)
        {
            var sg = sgConnection ?? Connect();

            foreach (var addon in _configDb.GetAllAddons())
            {
                _addonLogic().RemoveAddon(addon.Name);
            }

            if (secretsBrokerOnly)
            {
                // If not just deleting the DevOpsSecretsBroker instance from
                // SPP, at least update it before wiping out the local database.
                CheckAndSyncSecretsBrokerInstance(sg);
            }
            else
            {
                if (ApplianceSupportsDevOps ?? false)
                {
                    DeleteDevOpsInstance(sg);
                }
                else
                {
                    DeleteSafeguardData(sg);
                }
            }

            RestoreDefaultPort();
            DeleteSecretsBrokerData();
            DevOpsSecretsBrokerCache = null;
            DisconnectWithAccessToken();
        }

        public void RestartService()
        {
            // Try to shutdown any services that are owned by the addons before
            //  shutting down Secrets Broker.
            foreach (var addon in _configDb.GetAllAddons())
            {
                _addonManager().ShutdownAddon(addon);
            }

            // Sleep just for a second to give the caller time to respond before we exit.
            Thread.Sleep(1000);
            Task.Run(() => Environment.Exit(54));
        }

        public IEnumerable<CertificateInfo> GetTrustedCertificates()
        {
            var trustedCertificates = _configDb.GetAllTrustedCertificates().ToArray();
            if (!trustedCertificates.Any())
                return new List<CertificateInfo>();

            return trustedCertificates.Select(x => x.GetCertificateInfo());
        }

        public CertificateInfo GetTrustedCertificate(string thumbPrint)
        {
            if (string.IsNullOrEmpty(thumbPrint))
                throw LogAndException("Invalid thumbprint");

            var certificate = _configDb.GetTrustedCertificateByThumbPrint(thumbPrint);

            return certificate?.GetCertificateInfo();
        }

        private CertificateInfo AddTrustedCertificate(string base64CertificateData, string passPhrase = null)
        {
            if (base64CertificateData == null)
                throw LogAndException("Certificate cannot be null");

            try
            {
                var certificateBytes = CertificateHelper.ConvertPemToData(base64CertificateData);
                var cert = passPhrase != null ? new X509Certificate2(certificateBytes, passPhrase) : new X509Certificate2(certificateBytes);
                _logger.Debug(
                    $"Parsed new trusted certificate: subject={cert.SubjectName}, thumbprint={cert.Thumbprint}.");

                // Check of the certificate already exists and just return it if it does.
                var existingCert = _configDb.GetTrustedCertificateByThumbPrint(cert.Thumbprint);
                if (existingCert != null)
                {
                    _logger.Debug("New trusted certificate already exists.");
                    return existingCert.GetCertificateInfo(false);
                }

                if (!CertificateHelper.ValidateCertificate(cert, CertificateType.Trusted))
                    throw new DevOpsException("Invalid certificate");

                var trustedCertificate = new TrustedCertificate()
                {
                    Thumbprint = cert.Thumbprint,
                    Subject = cert.Subject,
                    Base64CertificateData = cert.ToPemFormat()
                };

                _configDb.SaveTrustedCertificate(trustedCertificate);
                return trustedCertificate.GetCertificateInfo();
            }
            catch (Exception ex)
            {
                throw LogAndException($"Failed to add the certificate: {ex.Message}", ex);
            }
        }

        public CertificateInfo AddTrustedCertificate(CertificateInfo certificate)
        {
            return AddTrustedCertificate(certificate.Base64CertificateData, certificate.Passphrase);
        }

        public void DeleteTrustedCertificate(string thumbPrint)
        {
            if (string.IsNullOrEmpty(thumbPrint))
                throw LogAndException("Invalid thumbprint");

            if (_configDb.IgnoreSsl == false)
            {
                var trustedCertificates = _configDb.GetAllTrustedCertificates().Where(x => !x.Thumbprint.Equals(thumbPrint));
                if (!CheckSslConnection(trustedCertificates))
                {
                    var cert = _configDb.GetTrustedCertificateByThumbPrint(thumbPrint);
                    var subject = cert == null ? thumbPrint : cert.GetCertificate().SubjectName.Name;
                    throw LogAndException($"Removing the trusted certificate {subject} would break the trust chain.");
                }
            }

            _configDb.DeleteTrustedCertificateByThumbPrint(thumbPrint);
        }

        public IEnumerable<CertificateInfo> ImportTrustedCertificates(ISafeguardConnection sgConnection)
        {
            var sg = sgConnection ?? Connect();

            try
            {
                IList<ServerCertificate> serverCertificates = null;
                var trustedCertificates = new List<CertificateInfo>();

                try
                {

                    var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get, "TrustedCertificates");
                    if (result.StatusCode == HttpStatusCode.OK)
                    {
                        serverCertificates = JsonHelper.DeserializeObject<IList<ServerCertificate>>(result.Body);
                        _logger.Debug($"Received {serverCertificates.Count()} certificates from Safeguard.");
                    }
                }
                catch (Exception ex)
                {
                    throw LogAndException("Failed to get the Safeguard trusted certificates.", ex);
                }

                if (serverCertificates != null)
                {
                    foreach (var cert in serverCertificates)
                    {
                        try
                        {
                            _logger.Debug($"Importing trusted certificate {cert.Subject} {cert.Thumbprint}.");
                            var certificateInfo = AddTrustedCertificate(cert.Base64CertificateData);
                            trustedCertificates.Add(certificateInfo);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex,
                                $"Failed to import certificate {cert.Subject} {cert.Thumbprint} from Safeguard. {ex.Message}");
                        }
                    }
                }

                try
                {

                    var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get, "SslCertificates");
                    if (result.StatusCode == HttpStatusCode.OK)
                    {
                        serverCertificates = JsonHelper.DeserializeObject<IList<ServerCertificate>>(result.Body);
                        _logger.Debug($"Received {serverCertificates.Count()} SSL certificates from Safeguard.");
                    }
                }
                catch (Exception ex)
                {
                    throw LogAndException("Failed to get the Safeguard ssl certificates.", ex);
                }

                if (serverCertificates != null)
                {
                    foreach (var cert in serverCertificates)
                    {
                        try
                        {
                            var certificateBytes = CertificateHelper.ConvertPemToData(cert.Base64CertificateData);
                            var certificate2 = new X509Certificate2(certificateBytes);

                            // Only import self-signed CA certificates.
                            if (certificate2.Subject.Equals(certificate2.IssuerName.Name) && CertificateHelper.IsCa(certificate2))
                            {
                                _logger.Debug($"Importing ssl certificate {cert.Subject} {cert.Thumbprint}.");
                                var certificateInfo = AddTrustedCertificate(cert.Base64CertificateData);
                                trustedCertificates.Add(certificateInfo);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex,
                                $"Failed to import certificate {cert.Subject} {cert.Thumbprint} from Safeguard. {ex.Message}");
                        }
                    }
                }

                return trustedCertificates;
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }
        }

        public void DeleteAllTrustedCertificates()
        {
            if (_configDb.IgnoreSsl == false)
            {
                if (!CheckSslConnection(new List<TrustedCertificate>()))
                {
                    throw LogAndException("Removing all of the trusted certificate would break the trust chain.");
                }
            }
            _configDb.DeleteAllTrustedCertificates();
        }

        public A2ARetrievableAccount GetA2ARetrievableAccount(ISafeguardConnection sgConnection, int id, A2ARegistrationType registrationType)
        {
            if ((registrationType == A2ARegistrationType.Account && _configDb.A2aRegistrationId == null) ||
                (registrationType == A2ARegistrationType.Vault && _configDb.A2aVaultRegistrationId == null))
            {
                throw LogAndException("A2A registration not configured");
            }

            var registrationId = (registrationType == A2ARegistrationType.Account)
                ? _configDb.A2aRegistrationId
                : _configDb.A2aVaultRegistrationId;

            var sg = sgConnection ?? Connect();

            try
            {
                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get,
                    $"A2ARegistrations/{registrationId}/RetrievableAccounts/{id}");
                if (result.StatusCode == HttpStatusCode.OK)
                {
                    return JsonHelper.DeserializeObject<A2ARetrievableAccount>(result.Body);
                }
            }
            catch (Exception ex)
            {
                throw LogAndException($"Get retrievable account failed for account {id}", ex);
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }

            return null;
        }

        public void DeleteA2ARetrievableAccount(ISafeguardConnection sgConnection, int id, A2ARegistrationType registrationType)
        {
            if ((registrationType == A2ARegistrationType.Account && _configDb.A2aRegistrationId == null) ||
                (registrationType == A2ARegistrationType.Vault && _configDb.A2aVaultRegistrationId == null))
            {
                throw LogAndException("A2A registration not configured");
            }

            var registrationId = (registrationType == A2ARegistrationType.Account)
                ? _configDb.A2aRegistrationId
                : _configDb.A2aVaultRegistrationId;

            var sg = sgConnection ?? Connect();

            try
            {
                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Delete,
                    $"A2ARegistrations/{registrationId}/RetrievableAccounts/{id}");
                if (result.StatusCode != HttpStatusCode.NoContent)
                {
                    _logger.Error($"(Failed to remove A2A retrievable account {id}");
                }
            }
            catch (Exception ex)
            {
                throw LogAndException($"Failed to remove A2A retrievable account {id}", ex);
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }
        }

        public IEnumerable<A2ARetrievableAccount> GetA2ARetrievableAccounts(ISafeguardConnection sgConnection, A2ARegistrationType registrationType)
        {
            if ((registrationType == A2ARegistrationType.Account && _configDb.A2aRegistrationId == null) ||
                (registrationType == A2ARegistrationType.Vault && _configDb.A2aVaultRegistrationId == null))
            {
                throw LogAndException("A2A registration not configured");
            }

            var registrationId = (registrationType == A2ARegistrationType.Account)
                ? _configDb.A2aRegistrationId
                : _configDb.A2aVaultRegistrationId;

            var sg = sgConnection ?? Connect();

            try
            {
                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get,
                    $"A2ARegistrations/{registrationId}/RetrievableAccounts");
                if (result.StatusCode == HttpStatusCode.OK)
                {
                    return JsonHelper.DeserializeObject<IEnumerable<A2ARetrievableAccount>>(result.Body);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Get retrievable accounts failed: {ex.Message}");
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }

            return new List<A2ARetrievableAccount>();
        }

        public A2ARetrievableAccount GetA2ARetrievableAccountById(ISafeguardConnection sgConnection, A2ARegistrationType registrationType, int accountId)
        {
            if ((registrationType == A2ARegistrationType.Account && _configDb.A2aRegistrationId == null) ||
                (registrationType == A2ARegistrationType.Vault && _configDb.A2aVaultRegistrationId == null))
            {
                throw LogAndException("A2A registration not configured");
            }

            var registrationId = (registrationType == A2ARegistrationType.Account)
                ? _configDb.A2aRegistrationId
                : _configDb.A2aVaultRegistrationId;

            var sg = sgConnection ?? Connect();

            try
            {
                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get,
                    $"A2ARegistrations/{registrationId}/RetrievableAccounts/{accountId}");
                if (result.StatusCode == HttpStatusCode.OK)
                {
                    return JsonHelper.DeserializeObject<A2ARetrievableAccount>(result.Body);
                }
            }
            catch (SafeguardDotNetException ex)
            {
                if (ex.HttpStatusCode == HttpStatusCode.NotFound)
                {
                    _logger.Error($"Retrievable account {accountId} not found: {ex.Message}");
                    return null;
                }

                throw LogAndException($"Get retrievable account failed for account id {accountId}", ex);
            }
            catch (Exception ex)
            {
                throw LogAndException($"Get retrievable account failed for account id {accountId}", ex);
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }

            return null;
        }

        public IEnumerable<A2ARetrievableAccount> AddA2ARetrievableAccounts(ISafeguardConnection sgConnection, IEnumerable<SppAccount> accounts, A2ARegistrationType registrationType)
        {
            if (_configDb.A2aRegistrationId == null)
            {
                throw LogAndException("A2A registration not configured");
            }

            var ipRestrictions = LocalIPAddress();
            var registrationId = (registrationType == A2ARegistrationType.Account)
                ? _configDb.A2aRegistrationId
                : _configDb.A2aVaultRegistrationId;

            var sg = sgConnection ?? Connect();

            try
            {
                foreach (var account in accounts)
                {
                    try
                    {
                        DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Post,
                            $"A2ARegistrations/{registrationId}/RetrievableAccounts",
                            $"{{\"AccountId\":{account.Id}, \"IpRestrictions\":[{ipRestrictions}]}}");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"Failed to add account {account.Id} - {account.Name}: {ex.Message}");
                    }
                }

                return GetA2ARetrievableAccounts(sg, registrationType);
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }
        }

        public void RemoveA2ARetrievableAccounts(ISafeguardConnection sgConnection, IEnumerable<A2ARetrievableAccount> accounts, A2ARegistrationType registrationType)
        {
            if (_configDb.A2aRegistrationId == null)
            {
                throw LogAndException("A2A registration not configured");
            }

            var retrievableAccounts = accounts.ToArray();
            if (retrievableAccounts.All(x => x.AccountId == 0))
            {
                var msg = "Invalid list of accounts. Expecting a list of retrievable accounts.";
                _logger.Error(msg);
                throw new DevOpsException(msg);
            }

            var registrationId = (registrationType == A2ARegistrationType.Account)
                ? _configDb.A2aRegistrationId
                : _configDb.A2aVaultRegistrationId;

            var sg = sgConnection ?? Connect();

            try
            {
                foreach (var account in retrievableAccounts)
                {
                    try
                    {
                        DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Delete,
                            $"A2ARegistrations/{registrationId}/RetrievableAccounts/{account.AccountId}");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex,
                            $"Failed to remove account {account.AccountId} - {account.AccountName}: {ex.Message}");
                    }
                }
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }
        }

        public DevOpsSecretsBroker GetDevOpsConfiguration(ISafeguardConnection sgConnection)
        {
            var sg = sgConnection ?? Connect();

            try
            {
                _serviceConfiguration.Appliance = GetSafeguardAvailability(sg,
                    new SafeguardDevOpsConnection()
                    {
                        ApplianceAddress = _configDb.SafeguardAddress,
                        IgnoreSsl = _configDb.IgnoreSsl != null && _configDb.IgnoreSsl.Value,
                        ApiVersion = _configDb.ApiVersion ?? WellKnownData.DefaultApiVersion
                    });

                return GetSecretsBrokerInstance(sg, true);
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }
        }

        public void CheckAndConfigureAddonPlugins(ISafeguardConnection sgConnection)
        {
            var notLicensed = !ValidateLicense();

            var addons = _configDb.GetAllAddons();
            foreach (var addon in addons)
            {
                var plugin = _configDb.GetPluginByName(addon.Manifest.PluginName);
                if (plugin != null)
                {
                    if (plugin.IsDisabled != notLicensed)
                    {
                        plugin.IsDisabled = notLicensed;
                        _configDb.SavePluginConfiguration(plugin);
                    }
                }

                if ((ApplianceSupportsDevOps ?? false)
                    && !string.IsNullOrEmpty(addon.VaultAccountName)
                    && addon.VaultAccountId.HasValue
                    && addon.VaultAccountId > 0
                    && !string.IsNullOrEmpty(addon.Manifest?.PluginName))
                {
                    plugin = _configDb.GetPluginByName(addon.Manifest.PluginName);
                    if (plugin != null && plugin.VaultAccountId != addon.VaultAccountId)
                    {
                        plugin.VaultAccountId = addon.VaultAccountId;
                        _pluginsLogic().SavePluginVaultAccount(sgConnection, plugin.Name, new AssetAccount(){Id = addon.VaultAccountId.Value});
                    }
                }
            }
        }

        public void Dispose()
        {
            DisconnectWithAccessToken();
        }


        // Everything after this point requires that Safeguard supports the /DevOps/SecretsBrokers endpoints
        #region DevOpsSecretsBrokerCode

        public void PingSpp(ISafeguardConnection sgConnection)
        {
            var sg = sgConnection ?? CertConnect();

            try
            {
                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core,
                    Method.Post, "DevOps/SecretsBrokers/Ping");
                if (result.StatusCode != HttpStatusCode.OK && result.StatusCode != HttpStatusCode.NoContent)
                {
                    _logger.Information($"Pinging Safeguard at {_configDb.SafeguardAddress} failed.");
                }
            }
            catch (Exception ex) //Throw away any exception
            {
                _logger.Error(ex, $"Pinging Safeguard at {_configDb.SafeguardAddress} failed.");
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }
        }

        public object GetAvailableAccounts(ISafeguardConnection sgConnection, string filter, int? page, bool? count, int? limit, string orderby, string q)
        {
            var sg = sgConnection ?? Connect();

            try
            {
                var p = new Dictionary<string, string>();
                JsonHelper.AddQueryParameter(p, nameof(filter), filter);
                JsonHelper.AddQueryParameter(p, nameof(page), page?.ToString());
                JsonHelper.AddQueryParameter(p, nameof(limit), limit?.ToString());
                JsonHelper.AddQueryParameter(p, nameof(count), count?.ToString());
                JsonHelper.AddQueryParameter(p, nameof(orderby), orderby);
                JsonHelper.AddQueryParameter(p, nameof(q), q);

                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get, "PolicyAccounts", null, p);
                if (result.StatusCode == HttpStatusCode.OK)
                {
                    if (count == null || count.Value == false)
                    {
                        var accounts = JsonHelper.DeserializeObject<IEnumerable<SppAccount>>(result.Body);
                        if (accounts != null)
                        {
                            return accounts;
                        }
                    }
                    else
                    {
                        return result.Body;
                    }
                }
            }
            catch (Exception ex)
            {
                throw LogAndException($"Get available accounts failed. {ex.Message}", ex);
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }

            return new List<SppAccount>();
        }

        public IEnumerable<AssetAccount> GetAssetAccounts(ISafeguardConnection sgConnection, int assetId)
        {
            if (assetId > 0)
            {
                var sg = sgConnection ?? Connect();

                try
                {
                    var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get, $"Assets/{assetId}/Accounts");
                    if (result.StatusCode == HttpStatusCode.OK)
                    {
                        var accounts = JsonHelper.DeserializeObject<IEnumerable<AssetAccount>>(result.Body);
                        if (accounts != null)
                        {
                            return accounts;
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw LogAndException($"Failed to get the account for id '{assetId}': {ex.Message}", ex);
                }
                finally
                {
                    if (sgConnection == null)
                        sg.Dispose();
                }
            }

            return null;
        }

        public AssetAccount GetAssetAccount(ISafeguardConnection sgConnection, int accountId)
        {
            var sg = sgConnection ?? Connect();

            try
            {
                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get, $"AssetAccounts/{accountId}");
                if (result.StatusCode == HttpStatusCode.OK)
                {
                    var account = JsonHelper.DeserializeObject<AssetAccount>(result.Body);
                    if (account != null)
                    {

                        return account;
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex is SafeguardDotNetException &&
                    ((SafeguardDotNetException) ex).HttpStatusCode == HttpStatusCode.NotFound)
                {
                    _logger.Error($"Account not found for id '{accountId}': {ex.Message}");
                }
                else
                {
                    throw LogAndException($"Failed to get the account for id '{accountId}': {ex.Message}", ex);
                }
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }

            return null;
        }

        public void SetAssetAccountPassword(ISafeguardConnection sgConnection, AssetAccount account, string password)
        {
            if (account == null || account.Id == 0 || string.IsNullOrEmpty(password))
                return;

            try
            {
                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sgConnection, Service.Core,
                    Method.Put, $"AssetAccounts/{account.Id}/Password", $"\"{password}\"");
                if (result.StatusCode != HttpStatusCode.NoContent)
                {
                    _logger.Error(
                        $"Failed to sync the password for account {account.Name} ");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex,
                    $"Failed to sync the password for account {account.Name} ");
            }
        }

        public AssetAccount AddAssetAccount(ISafeguardConnection sgConnection, AssetAccount account)
        {
            if (sgConnection == null)
            {
                _logger.Error("Failed to add the asset accounts. Invalid safeguard connection.");
                return null;
            }

            try
            {
                var accountStr = JsonHelper.SerializeObject(account);

                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sgConnection, Service.Core, Method.Post, "AssetAccounts", accountStr);
                if (result.StatusCode == HttpStatusCode.Created)
                {
                    _logger.Information($"Successfully added asset account {account.Name} to safeguard.");
                    return JsonHelper.DeserializeObject<AssetAccount>(result.Body);
                }
            }
            catch (Exception ex)
            {
                throw LogAndException($"Failed to add the account {account.Name} to safeguard for '{account.AssetName}': {ex.Message}", ex);
            }

            return null;
        }

        private bool DeleteAssetAndAccounts(ISafeguardConnection sgConnection, int assetId)
        {
            if (sgConnection == null)
            {
                _logger.Error("Failed to delete the asset and accounts. Invalid safeguard connection.");
                return false;
            }
            if (assetId == 0)
            {
                _logger.Error("Failed to delete the asset and accounts. No asset found.");
                return false;
            }

            if (DeleteAssetAccounts(sgConnection, assetId))
            {
                try
                {
                    var p = new Dictionary<string, string>
                        {{"forceDelete", "true"}};

                    var result = DevOpsInvokeMethodFull(_configDb.SvcId, sgConnection, Service.Core, Method.Delete, $"Assets/{assetId}", null, p);
                    if (result.StatusCode == HttpStatusCode.NoContent)
                    {
                        _configDb.AssetId = null;
                        _logger.Information("Successfully removed the asset and accounts from safeguard.");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to remove the asset and accounts from safeguard: {ex.Message}", ex);
                }
            }

            return false;
        }

        public bool DeleteAssetAccounts(ISafeguardConnection sgConnection, int assetId)
        {
            if (assetId == 0)
            {
                _logger.Error("Failed to delete the asset accounts. Invalid asset.");
                return false;
            }

            var sg = sgConnection ?? Connect();

            try
            {
                var accounts = GetAssetAccounts(null, assetId).ToArray();
                var tasks = accounts.Select(account => Task.Run(() => { DeleteAssetAccount(sg, account); })).ToList();

                if (tasks.Any())
                    Task.WaitAll(tasks.ToArray());

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to delete the asset accounts in safeguard.: {ex.Message}", ex);
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }

            return false;
        }

        private bool DeleteAssetAccount(ISafeguardConnection sgConnection, AssetAccount account)
        {
            if (sgConnection == null)
            {
                _logger.Error($"Failed to delete the asset account {account.Name}. Invalid safeguard connection.");
                return false;
            }
            if (account == null || account.Id == 0)
            {
                _logger.Error($"Failed to delete the asset account {account.Name}. Invalid account.");
                return false;
            }

            try
            {
                var p = new Dictionary<string, string>
                    {{"forceDelete", "true"}};

                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sgConnection, Service.Core, Method.Delete, $"AssetAccounts/{account.Id}", null, p);
                if (result.StatusCode == HttpStatusCode.NoContent)
                {
                    _logger.Information($"Successfully deleted the asset account {account.Name} from safeguard.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to delete the asset account {account.Name} from safeguard: {ex.Message}", ex);
            }

            return false;
        }


        public void RetrieveDevOpsSecretsBrokerInstance(ISafeguardConnection sgConnection)
        {
            var sg = sgConnection ?? Connect();

            try {
                if (ApplianceSupportsDevOps ?? false)
                {
                    var secretsBroker = GetSecretsBrokerInstanceByName(sg);
                    if (secretsBroker != null)
                    {
                        DevOpsSecretsBrokerCache = secretsBroker;
                    }
                }
                else
                {
                    DevOpsSecretsBrokerCache = GetSecretsBrokerInstance(sg, true);
                }
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }
        }

        public object GetAvailableA2ARegistrations(ISafeguardConnection sgConnection, string filter, int? page, bool? count, int? limit, string @orderby, string q)
        {
            var sg = sgConnection ?? Connect();

            try
            {
                if (ApplianceSupportsDevOps ?? false)
                {
                    var registrationIds = GetAvailableA2ARegistrationIds(sg);

                    if (registrationIds.Any())
                    {
                        var p = new Dictionary<string, string>();
                        JsonHelper.AddQueryParameter(p, nameof(filter), filter);
                        JsonHelper.AddQueryParameter(p, nameof(page), page?.ToString());
                        JsonHelper.AddQueryParameter(p, nameof(limit), limit?.ToString());
                        JsonHelper.AddQueryParameter(p, nameof(count), count?.ToString());
                        JsonHelper.AddQueryParameter(p, nameof(orderby), orderby);
                        JsonHelper.AddQueryParameter(p, nameof(q), q);

                        var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get,
                            "A2ARegistrations", null, p);
                        if (result.StatusCode == HttpStatusCode.OK)
                        {
                            if (count == null || count.Value == false)
                            {
                                var registrations =
                                    JsonHelper.DeserializeObject<IEnumerable<A2ARegistration>>(result.Body);
                                if (registrations != null)
                                {
                                    registrations = registrations.Where(x =>
                                        !string.IsNullOrEmpty(x.DevOpsInstanceId) && registrationIds.Contains(x.Id));
                                    return registrations;
                                }
                            }
                            else
                            {
                                return result.Body;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw LogAndException($"Get available accounts failed. {ex.Message}", ex);
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }

            return new List<A2ARegistration>();
        }

        public A2ARegistration SetA2ARegistration(ISafeguardConnection sgConnection, int id)
        {
            //Turn off the monitor if it is running
            //Remove all addons
            //Remove all of the mapped accounts to plugins
            //Remove all of the mapped vault accounts to plugins

            //Look up the new A2A registration to make sure that it exists in Safeguard
            //Update the configdb.svcId with the DevOpsInstanceId from the A2A registration;

            var sg = sgConnection ?? Connect();

            try
            {
                if (ApplianceSupportsDevOps ?? false)
                {
                    var a2aRegistration = GetA2ARegistration(sg, id);
                    if (a2aRegistration == null || string.IsNullOrEmpty(a2aRegistration.DevOpsInstanceId))
                        throw LogAndException(
                            "Failed to find the A2A registration or the associated Safeguard Secrets Broker instance.");

                    var devOpsSecretsBroker = GetSecretsBrokerInstanceByName(sg, a2aRegistration.DevOpsInstanceId);
                    if (devOpsSecretsBroker == null)
                        throw LogAndException(
                            $"Failed to find the associated Safeguard Secrets Broker instance {a2aRegistration.DevOpsInstanceId}.");

                    //Determine if we need to remove an unused DevOpsInstance from SPP.  An A2A registration has not been
                    //  created yet, then the devops instance is useless so delete it.
                    int devOpsSecretsBrokerToDelete = 0;
                    if (devOpsSecretsBroker.A2AVaultRegistration != null)
                    {
                        devOpsSecretsBrokerToDelete = DevOpsSecretsBrokerCache?.Id ?? 0;
                    }

                    PauseBackgroundMaintenance = true;
                    _monitoringLogic().EnableMonitoring(false);
                    foreach (var addon in _configDb.GetAllAddons())
                    {
                        _addonLogic().RemoveAddon(addon.Name);
                    }

                    _configDb.SvcId = devOpsSecretsBroker.DevOpsInstanceId;
                    _configDb.A2aUserId = devOpsSecretsBroker.A2AUser?.Id;
                    _configDb.AssetId = devOpsSecretsBroker.Asset?.Id;
                    _configDb.AssetPartitionId = devOpsSecretsBroker.AssetPartition?.Id;

                    //Just making these calls with the config ids set to null will realign everything.
                    _configDb.A2aRegistrationId = devOpsSecretsBroker.A2ARegistration?.Id ?? 0;
                    _configDb.A2aVaultRegistrationId = devOpsSecretsBroker.A2AVaultRegistration?.Id ?? 0;

                    _pluginsLogic().DeleteAccountMappings();
                    _pluginsLogic().ClearMappedPluginVaultAccounts();

                    foreach (var p in devOpsSecretsBroker.Plugins)
                    {
                        var plugin = new Plugin(p);
                        _configDb.SavePluginConfiguration(plugin);
                        if (p.MappedAccounts != null)
                        {
                            var accountMappings =
                                JsonHelper.DeserializeObject<IEnumerable<AccountMapping>>(p.MappedAccounts);
                            _configDb.SaveAccountMappings(accountMappings);
                        }
                    }

                    //Cache the new secrets broker instance.
                    DevOpsSecretsBrokerCache = devOpsSecretsBroker;

                    try
                    {
                        if (devOpsSecretsBrokerToDelete > 0)
                            DeleteDevOpsInstance(sg, devOpsSecretsBrokerToDelete);
                    }
                    catch (Exception ex)
                    {
                        _logger.Information(ex, ex.Message);
                    }
                    return a2aRegistration;
                }
            }
            finally
            {
                PauseBackgroundMaintenance = false;
                if (sgConnection == null)
                    sg.Dispose();
            }

            throw LogAndException("Setting an A2A registration is not currently supported by the associated Safeguard appliance.");
        }

        private static volatile object _secretsBrokerInstanceLock = new object();
        public void AddSecretsBrokerInstance(ISafeguardConnection sgConnection)
        {
            if (ApplianceSupportsDevOps ?? false)
            {
                lock (_secretsBrokerInstanceLock)
                {
                    var sg = sgConnection ?? Connect();

                    try
                    {
                        var secretsBroker = GetSecretsBrokerInstanceByName(sg);
                        if (secretsBroker != null)
                        {
                            DevOpsSecretsBrokerCache = secretsBroker;
                            return;
                        }

                        var addresses = GetLocalIPAddresses();
                        if (addresses != null && addresses.Any())
                        {
                            var ipAddress = addresses.FirstOrDefault();

                            secretsBroker = new DevOpsSecretsBroker()
                            {
                                Host = ipAddress?.ToString(),
                                DevOpsInstanceId = _configDb.SvcId,
                                Asset = new Asset() {Name = WellKnownData.DevOpsAssetName(_configDb.SvcId)},
                                AssetPartition = new AssetPartition() {Name = WellKnownData.DevOpsAssetPartitionName(_configDb.SvcId)}
                            };

                            var secretsBrokerStr = JsonHelper.SerializeObject(secretsBroker);
                            try
                            {
                                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Post, "DevOps/SecretsBrokers",
                                    secretsBrokerStr);
                                if (result.StatusCode == HttpStatusCode.Created)
                                {
                                    DevOpsSecretsBrokerCache = JsonHelper.DeserializeObject<DevOpsSecretsBroker>(result.Body);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Error(ex,
                                    $"Failed to create the DevOps Secrets Broker instance in Safeguard: {ex.Message}");
                            }
                        }
                        else
                        {
                            _logger.Error(
                                "Failed to create the DevOps Secrets Broker instance in Safeguard.  Unable to get the local IP address");
                        }
                    }
                    finally
                    {
                        if (sgConnection == null)
                            sg.Dispose();
                    }
                }
            }
        }

        public void CheckAndSyncSecretsBrokerInstance(ISafeguardConnection sgConnection)
        {
            if (ApplianceSupportsDevOps ?? false)
            {
                lock (_secretsBrokerInstanceLock)
                {
                    var sg = sgConnection ?? Connect();

                    try
                    {
                        if (DevOpsSecretsBrokerCache == null)
                        {
                            // This call just gets the latest Secrets Broker instance from SPP and caches it.
                            RetrieveDevOpsSecretsBrokerInstance(sg);
                        }

                        if (DevOpsSecretsBrokerCache != null)
                        {
                            var needsUpdate = false;
                            var devOpsInstance = DevOpsSecretsBrokerCache;
                            var devOpsAsset = DevOpsSecretsBrokerCache.Asset ?? GetAsset(sg);
                            var devOpsAssetPartition = DevOpsSecretsBrokerCache.AssetPartition ?? GetAssetPartition(sg);
                            var plugins = _configDb.GetAllPlugins();

                            if (devOpsAsset == null)
                            {
                                // By setting the asset id to 0 and updating the devops instance, Safeguard will regenerate the asset.
                                devOpsInstance.Asset.Id = 0;
                                needsUpdate = true;
                            }
                            else if (DevOpsSecretsBrokerCache.Asset == null || DevOpsSecretsBrokerCache.Asset.Id != devOpsAsset.Id)
                            {
                                devOpsInstance.Asset = devOpsAsset;
                                needsUpdate = true;
                            }

                            if (devOpsAssetPartition == null)
                            {
                                // By setting the asset partition id to 0 and updating the devops instance, Safeguard will regenerate the asset partition.
                                devOpsInstance.AssetPartition.Id = 0;
                                needsUpdate = true;
                            }
                            else if (DevOpsSecretsBrokerCache.AssetPartition == null ||
                                     DevOpsSecretsBrokerCache.AssetPartition.Id != devOpsAssetPartition.Id)
                            {
                                devOpsInstance.AssetPartition = devOpsAssetPartition;
                                needsUpdate = true;
                            }

                            if (plugins != null)
                            {
                                var devOpsPlugins = plugins.Select(x => x.ToDevOpsSecretsBrokerPlugin(_configDb)).ToList();
                                if (!devOpsPlugins.SequenceEqual(devOpsInstance.Plugins))
                                {
                                    devOpsInstance.Plugins = devOpsPlugins;
                                    needsUpdate = true;
                                }
                            }

                            if (needsUpdate)
                                UpdateSecretsBrokerInstance(sg, devOpsInstance, true);
                        }
                    }
                    finally
                    {
                        if (sgConnection == null)
                            sg.Dispose();
                    }
                }
            }
        }

        public void CheckAndPushAddOnCredentials(ISafeguardConnection sgConnection)
        {
            if (ApplianceSupportsDevOps ?? false)
            {
                if (DevOpsSecretsBrokerCache?.Asset == null)
                    return;

                var addons = _configDb.GetAllAddons().ToList();
                if (!addons.Any())
                    return;

                var secretsBrokerAccounts = GetSecretsBrokerAccounts(sgConnection);
                if (secretsBrokerAccounts != null)
                {
                    foreach (var addon in addons)
                    {
                        // Determine if there are any accounts that need to be pushed to Safeguard.
                        var accounts = new List<AssetAccount>();
                        foreach (var credential in addon.VaultCredentials)
                        {
                            if (secretsBrokerAccounts.All(x => x.Name != credential.Key))
                            {
                                accounts.Add(new AssetAccount()
                                {
                                    Id = 0,
                                    Name = credential.Key,
                                    Description = addon.Manifest.DisplayName + " account",
                                    AssetId = DevOpsSecretsBrokerCache.Asset.Id,
                                    Password = credential.Value
                                });
                            }
                        }

                        // Add any missing accounts to Safeguard through the DevOps/SecretsBroker APIs which will also create an asset to tie them together.
                        if (accounts.Any())
                        {
                            var secretsBrokerAccountsStr = JsonHelper.SerializeObject(accounts);
                            try
                            {
                                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sgConnection, Service.Core,
                                    Method.Post,
                                    $"DevOps/SecretsBrokers/{DevOpsSecretsBrokerCache.Id}/Accounts/Add",
                                    secretsBrokerAccountsStr);
                                if (result.StatusCode == HttpStatusCode.OK)
                                {
                                    // Refresh the secrets broker account list after the additions
                                    secretsBrokerAccounts = GetSecretsBrokerAccounts(sgConnection);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Error(ex,
                                    $"Failed to sync the credentials for the Add-On {addon.Name}: {ex.Message}");
                            }
                        }

                        // Make sure that the vault account and asset information has been saved to the AddOn object in the database.
                        if (!string.IsNullOrEmpty(addon.VaultAccountName))
                        {
                            var vaultAccount = secretsBrokerAccounts.FirstOrDefault(x =>
                                x.Name.StartsWith(addon.VaultAccountName));
                            if (vaultAccount != null && addon.VaultAccountId != vaultAccount.Id)
                            {
                                addon.VaultAccountId = vaultAccount.Id;
                                addon.VaultAccountName = vaultAccount.Name;
                                addon.VaultAssetId = vaultAccount.AssetId;
                                addon.VaultAssetName = vaultAccount.AssetName;
                                _configDb.SaveAddon(addon);
                            }
                        }

                        // Make sure that all vault accounts have been added to the assigned vault A2A registration.
                        if (_configDb.A2aVaultRegistrationId != null && secretsBrokerAccounts.Any())
                        {
                            var a2aAccounts = GetA2ARetrievableAccounts(sgConnection, A2ARegistrationType.Vault);

                            var accountsToPush = secretsBrokerAccounts.Where(x =>
                                    a2aAccounts.All(y =>
                                        !y.AccountName.Equals(x.Name,
                                            StringComparison.InvariantCultureIgnoreCase)))
                                .Select(x => new SppAccount() {Id = x.Id, Name = x.Name});

                            AddA2ARetrievableAccounts(sgConnection, accountsToPush, A2ARegistrationType.Vault);
                        }
                    }
                }
            }
        }

        public void CheckAndSyncVaultCredentials(ISafeguardConnection sgConnection)
        {
            if (ApplianceSupportsDevOps ?? false)
            {
                var addons = _configDb.GetAllAddons().ToList();
                if (!addons.Any())
                    return;

                var a2aRegistration = GetA2ARegistration(sgConnection, A2ARegistrationType.Vault);

                var accounts = new List<A2ARetrievableAccount>();

                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sgConnection, Service.Core, Method.Get,
                    $"A2ARegistrations/{a2aRegistration.Id}/RetrievableAccounts");
                if (result.StatusCode == HttpStatusCode.OK)
                {
                    accounts = JsonHelper.DeserializeObject<List<A2ARetrievableAccount>>(result.Body);
                }

                if (accounts.Any())
                {
                    using var a2aContext = GetA2aContext();
                    foreach (var account in accounts)
                    {
                        string pp;
                        try
                        {
                            var p = a2aContext.RetrievePassword(account.ApiKey.ToSecureString());
                            pp = p.ToInsecureString();
                            if (string.IsNullOrEmpty(pp))
                                continue;
                        }
                        catch (Exception ex)
                        {
                            _logger.Information(ex, $"Failed to check the password for account {account.AccountName} ");
                            continue;
                        }

                        foreach (var addon in addons)
                        {
                            var addonAccount =
                                addon.VaultCredentials.FirstOrDefault(x =>
                                    account.AccountName.StartsWith(x.Key) && !pp.Equals(x.Value));
                            if (!string.IsNullOrEmpty(addonAccount.Value))
                            {
                                try
                                {
                                    result = DevOpsInvokeMethodFull(_configDb.SvcId, sgConnection,
                                        Service.Core, Method.Put, $"AssetAccounts/{account.AccountId}/Password",
                                        $"\"{addonAccount.Value}\"");
                                    if (result.StatusCode != HttpStatusCode.OK)
                                    {
                                        _logger.Error(
                                            $"Failed to sync the password for account {account.AccountName} ");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.Error(ex,
                                        $"Failed to sync the password for account {account.AccountName} ");
                                }
                            }
                        }
                    }
                }
            }
        }

        private DevOpsSecretsBroker GetSecretsBrokerInstance(ISafeguardConnection sg, bool expand)
        {
            if (ApplianceSupportsDevOps ?? false)
            {
                if (DevOpsSecretsBrokerCache == null)
                {
                    return GetSecretsBrokerInstanceByName(sg);
                }

                return GetSecretsBrokerInstance(sg, DevOpsSecretsBrokerCache.Id);
            }

            var devOpsSecretsBroker = _configDb.DevOpsSecretsBroker;
            devOpsSecretsBroker.IsLicensed = ValidateLicense();
            devOpsSecretsBroker.Appliance = _serviceConfiguration.Appliance;
            if (expand)
            {
                // Don't worry about getting the asset or the assetPartition since those are only used when
                //  Safeguard supports devops.
                var tasks = new Task[]
                {
                    Task.Run(() =>
                    {
                        try
                        {
                            if (devOpsSecretsBroker.A2AUser?.Id > 0)
                                devOpsSecretsBroker.A2AUser = GetA2AUser(sg, devOpsSecretsBroker.A2AUser?.Id);
                        }
                        catch { devOpsSecretsBroker.A2AUser = null; }
                    }),
                    Task.Run(() =>
                    {
                        try
                        {
                            if (devOpsSecretsBroker.A2ARegistration?.Id > 0)
                                devOpsSecretsBroker.A2ARegistration =
                                    GetA2ARegistration(sg, devOpsSecretsBroker.A2ARegistration?.Id);
                        }
                        catch { devOpsSecretsBroker.A2ARegistration = null; }
                    }),
                    Task.Run(() =>
                    {
                        try
                        {
                            if (devOpsSecretsBroker.A2AVaultRegistration?.Id > 0)
                                devOpsSecretsBroker.A2AVaultRegistration =
                                    GetA2ARegistration(sg, devOpsSecretsBroker.A2AVaultRegistration?.Id);
                        }catch { devOpsSecretsBroker.A2AVaultRegistration = null; }
                    }),
                    Task.Run(() =>
                    {
                        try
                        {
                            var assetId = devOpsSecretsBroker.Asset?.Id ?? 0;
                            if (assetId > 0)
                            {
                                devOpsSecretsBroker.Asset = GetAsset(sg, assetId);
                                devOpsSecretsBroker.Accounts = GetAssetAccounts(sg, assetId);
                            }
                        }catch { 
                            devOpsSecretsBroker.Asset = null; 
                            devOpsSecretsBroker.Accounts = null;
                        }
                    }),
                    Task.Run(() =>
                    {
                        try
                        {
                            if (devOpsSecretsBroker.AssetPartition?.Id > 0)
                                devOpsSecretsBroker.AssetPartition =
                                    GetAssetPartition(sg, devOpsSecretsBroker.AssetPartition?.Id);
                        }catch { devOpsSecretsBroker.AssetPartition = null; }
                    }),
                    Task.Run(() =>
                    {
                        try
                        {
                            if (devOpsSecretsBroker.AssetAccountGroup?.Id > 0)
                                devOpsSecretsBroker.AssetAccountGroup =
                                    GetAssetAccountGroup(sg, devOpsSecretsBroker.AssetAccountGroup?.Id);
                        }catch { devOpsSecretsBroker.AssetAccountGroup = null; }
                    })
                };

                Task.WaitAll(tasks);
            }
            else
            {
                devOpsSecretsBroker.A2AUser = DevOpsSecretsBrokerCache.A2AUser?.Id == devOpsSecretsBroker.A2AUser?.Id
                    ? DevOpsSecretsBrokerCache.A2AUser
                    : devOpsSecretsBroker.A2AUser;
                devOpsSecretsBroker.A2ARegistration = DevOpsSecretsBrokerCache.A2ARegistration?.Id == devOpsSecretsBroker.A2ARegistration?.Id
                    ? DevOpsSecretsBrokerCache.A2ARegistration
                    : devOpsSecretsBroker.A2ARegistration;
                devOpsSecretsBroker.A2AVaultRegistration = DevOpsSecretsBrokerCache.A2AVaultRegistration?.Id == devOpsSecretsBroker.A2AVaultRegistration?.Id
                    ? DevOpsSecretsBrokerCache.A2AVaultRegistration
                    : devOpsSecretsBroker.A2AVaultRegistration;
                devOpsSecretsBroker.Asset = DevOpsSecretsBrokerCache.Asset?.Id == devOpsSecretsBroker.Asset?.Id
                    ? DevOpsSecretsBrokerCache.Asset
                    : devOpsSecretsBroker.Asset;
                devOpsSecretsBroker.Accounts = DevOpsSecretsBrokerCache.Accounts ?? devOpsSecretsBroker.Accounts;
                devOpsSecretsBroker.AssetPartition = DevOpsSecretsBrokerCache.AssetPartition?.Id == devOpsSecretsBroker.AssetPartition?.Id
                    ? DevOpsSecretsBrokerCache.AssetPartition
                    : devOpsSecretsBroker.AssetPartition;
                devOpsSecretsBroker.AssetAccountGroup = DevOpsSecretsBrokerCache.AssetAccountGroup?.Id == devOpsSecretsBroker.AssetAccountGroup?.Id
                    ? DevOpsSecretsBrokerCache.AssetAccountGroup
                    : devOpsSecretsBroker.AssetAccountGroup;
            }

            return devOpsSecretsBroker;
        }

        private void UpdateSecretsBrokerInstance(ISafeguardConnection sgConnection, DevOpsSecretsBroker devOpsSecretsBroker, bool expand)
        {
            if (ApplianceSupportsDevOps ?? false)
            {
                lock (_secretsBrokerInstanceLock)
                {
                    if (devOpsSecretsBroker == null)
                        throw LogAndException(
                            "Unable to update the devOps secrets broker instance.  The devOpsSecretsBroker cannot be null.");

                    if (devOpsSecretsBroker.Host == null)
                        throw LogAndException("Invalid devOps secrets broker instance.  The host cannot be null.");

                    if (devOpsSecretsBroker.Asset == null)
                        devOpsSecretsBroker.Asset = new Asset();

                    if (devOpsSecretsBroker.Asset.Id == 0)
                        devOpsSecretsBroker.Asset.Name = WellKnownData.DevOpsAssetName(_configDb.SvcId);

                    if (devOpsSecretsBroker.AssetPartition == null)
                        devOpsSecretsBroker.AssetPartition = new AssetPartition();

                    if (devOpsSecretsBroker.AssetPartition.Id == 0)
                        devOpsSecretsBroker.AssetPartition.Name =
                            WellKnownData.DevOpsAssetPartitionName(_configDb.SvcId);

                    var devopsSecretsBrokerStr = JsonHelper.SerializeObject(devOpsSecretsBroker);
                    try
                    {
                        var result = DevOpsInvokeMethodFull(_configDb.SvcId, sgConnection, Service.Core, Method.Put,
                            $"DevOps/SecretsBrokers/{devOpsSecretsBroker.Id}", devopsSecretsBrokerStr);
                        if (result.StatusCode == HttpStatusCode.OK)
                        {
                            DevOpsSecretsBrokerCache = JsonHelper.DeserializeObject<DevOpsSecretsBroker>(result.Body);
                            PingSpp(sgConnection);
                        }
                    }
                    catch (Exception ex)
                    {
                        throw LogAndException("Failed to update the devops instance.", ex);
                    }
                }
            }
            else
            {
                DevOpsSecretsBrokerCache = GetSecretsBrokerInstance(sgConnection, expand);
            }
        }

        private ISafeguardA2AContext GetA2aContext()
        {
            var sppAddress = _configDb.SafeguardAddress;
            var userCertificate = _configDb.UserCertificateBase64Data;
            var passPhrase = _configDb.UserCertificatePassphrase?.ToSecureString();
            var apiVersion = _configDb.ApiVersion ?? WellKnownData.DefaultApiVersion;
            var ignoreSsl = _configDb.IgnoreSsl ?? true;

            if (sppAddress != null && userCertificate != null)
            {
                try
                {
                    _logger.Debug("Connecting to Safeguard A2A context: {address}", sppAddress);
                    var a2AContext = Safeguard.A2A.GetContext(sppAddress, Convert.FromBase64String(userCertificate), passPhrase, apiVersion, ignoreSsl);
                    return a2AContext;
                }
                catch (SafeguardDotNetException ex)
                {
                    _logger.Error(ex, $"Failed to connect to Safeguard A2A context at '{sppAddress}': {ex.Message}");
                }
            }

            return null;
        }

        private List<int> GetAvailableA2ARegistrationIds(ISafeguardConnection sgConnection)
        {
            var sg = sgConnection ?? Connect();

            try
            {
                var devOpsInstances = GetAvailableSecretsBrokerInstances(sg).ToList();

                if (devOpsInstances.Any())
                {
                    var registrationIds = devOpsInstances.Where(x => x.A2ARegistration != null)
                        .Select(x => x.A2ARegistration.Id).ToList();
                    if (registrationIds.Any())
                        return registrationIds;
                }
            }
            catch (Exception ex)
            {
                throw LogAndException($"Get available A2A registration Ids failed. {ex.Message}", ex);
            }
            finally
            {
                if (sgConnection == null)
                    sg.Dispose();
            }

            return new List<int>();
        }

        private DevOpsSecretsBroker GetSecretsBrokerInstanceByName(ISafeguardConnection sg, string devOpsInstanceId = null)
        {
            if (ApplianceSupportsDevOps ?? false)
            {
                try
                {
                    var svcId = devOpsInstanceId ?? _configDb.SvcId;
                    var filter = $"DevOpsInstanceId eq \"{svcId}\"";

                    var p = new Dictionary<string, string>();
                    JsonHelper.AddQueryParameter(p, nameof(filter), filter);

                    var result =
                        DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get, "DevOps/SecretsBrokers",
                            null, p);
                    if (result.StatusCode == HttpStatusCode.OK)
                    {
                        var secretsBroker =
                            (JsonHelper.DeserializeObject<IEnumerable<DevOpsSecretsBroker>>(result.Body))
                            .FirstOrDefault();
                        if (secretsBroker != null)
                        {
                            // Only ping SPP for the devops instance that is currently in use. Not one that is about to be used.
                            if (devOpsInstanceId == null)
                                PingSpp(sg);
                            return secretsBroker;
                        }
                    }
                }
                catch (SafeguardDotNetException ex)
                {
                    if (ex.HttpStatusCode != HttpStatusCode.NotFound)
                        _logger.Error(ex, "Failed to get the DevOps Secrets Broker instance from Safeguard. ");
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to get the DevOps Secrets Broker instance from Safeguard. ");
                }
            }

            return null;
        }

        private DevOpsSecretsBroker GetSecretsBrokerInstance(ISafeguardConnection sg, int id)
        {
            if (ApplianceSupportsDevOps ?? false)
            {
                try
                {
                    var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get,
                        $"DevOps/SecretsBrokers/{id}");

                    if (result.StatusCode == HttpStatusCode.OK)
                    {
                        var secretsBroker = JsonHelper.DeserializeObject<DevOpsSecretsBroker>(result.Body);
                        if (secretsBroker != null)
                        {
                            PingSpp(sg);
                            return secretsBroker;
                        }
                    }

                    _logger.Error("Failed to get the DevOps Secrets Broker instance from Safeguard.");
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to get the DevOps Secrets Broker instance from Safeguard. ");
                }
            }

            return null;
        }

        private IEnumerable<DevOpsSecretsBroker> GetAvailableSecretsBrokerInstances(ISafeguardConnection sg)
        {
            if (ApplianceSupportsDevOps ?? false)
            {
                try
                {
                    var filter = "InUse eq \"false\"";

                    var p = new Dictionary<string, string>();
                    JsonHelper.AddQueryParameter(p, nameof(filter), filter);

                    var result =
                        DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get, "DevOps/SecretsBrokers", null, p);
                    if (result.StatusCode == HttpStatusCode.OK)
                    {
                        var secretsBrokers =
                            (JsonHelper.DeserializeObject<IEnumerable<DevOpsSecretsBroker>>(result.Body));
                        if (secretsBrokers != null)
                        {
                            return secretsBrokers;
                        }
                    }
                }
                catch (SafeguardDotNetException ex)
                {
                    if (ex.HttpStatusCode != HttpStatusCode.NotFound)
                        _logger.Error(ex, "Failed to get the available DevOps Secrets Broker instances from Safeguard. ");
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to get the available DevOps Secrets Broker instances from Safeguard. ");
                }
            }

            return new List<DevOpsSecretsBroker>();
        }

        private void DeleteDevOpsInstance(ISafeguardConnection sgConnection, int id = 0)
        {
            if (ApplianceSupportsDevOps ?? false)
            {
                try
                {
                    var devOpsInstance = id == 0
                        ? GetSecretsBrokerInstance(sgConnection, false)
                        : GetSecretsBrokerInstance(sgConnection, id);
                    if (devOpsInstance != null)
                    {
                        var result = DevOpsInvokeMethodFull(_configDb.SvcId, sgConnection, Service.Core, Method.Delete,
                            $"DevOps/SecretsBrokers/{devOpsInstance.Id}");
                        if (result.StatusCode != HttpStatusCode.NoContent)
                        {
                            var errorMessage = JsonHelper.DeserializeObject<ErrorMessage>(result.Body);
                            throw LogAndException(
                                $"Failed to delete the DevOps Secrets Broker Instance. {errorMessage.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw LogAndException("Failed to delete the DevOps Secrets Broker Instance.", ex);
                }
            }
        }

        private List<AssetAccount> GetSecretsBrokerAccounts(ISafeguardConnection sg)
        {
            try
            {
                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get, $"DevOps/SecretsBrokers/{DevOpsSecretsBrokerCache.Id}/Accounts");
                if (result.StatusCode == HttpStatusCode.OK)
                {
                    var secretsBrokerAccounts = JsonHelper.DeserializeObject<IEnumerable<AssetAccount>>(result.Body).ToList();
                    return secretsBrokerAccounts;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get the asset accounts from Safeguard. ");
            }

            return new List<AssetAccount>();
        }

        #endregion


        //TODO: Delete me when done testing the authorization scheme.
        public void TestCertConnection()
        {
            var sg = CertConnect();

            try
            {
                if (sg == null)
                    return;

                var h = new Dictionary<string, string>
                    {{"devOpsInstanceId", _configDb.SvcId}};

                var result = DevOpsInvokeMethodFull(_configDb.SvcId, sg, Service.Core, Method.Get, "DevOps/SecretsBrokers/TestEndPoint", null, null, h);
                return;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to call the test endpoint: {ex.Message}");
            }
            finally
            {
                sg?.Dispose();
            }
        }
    }
}
