﻿using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using OneIdentity.DevOps.Common;
using Serilog;

namespace OneIdentity.DevOps.AzureKeyVault
{
    public class PluginDescriptor : ILoadablePlugin
    {
        private SecretClient _secretsClient;
        private Dictionary<string,string> _configuration;
        private Regex _rgx;
        private ILogger _logger;

        private const string ApplicationIdName = "applicationId";
        private const string VaultUriName = "vaultUri";
        private const string TenantIdName = "tenantId";

        public string Name => "AzureKeyVault";
        public string DisplayName => "Azure Key Vault";
        public string Description => "This is the Azure Key Vault plugin for updating passwords";
        public CredentialType[] SupportedCredentialTypes => new[] {CredentialType.Password, CredentialType.SshKey, CredentialType.ApiKey};
        public CredentialType AssignedCredentialType { get; set; } = CredentialType.Password;

        public Dictionary<string,string> GetPluginInitialConfiguration()
        {
            return _configuration ??= new Dictionary<string, string>
            {
                { ApplicationIdName, "" },
                { VaultUriName, "" },
                { TenantIdName, "" }
            };
        }

        public void SetPluginConfiguration(Dictionary<string,string> configuration)
        {
            // Make sure that the new configuration key is added to the configuration.
            if (!configuration.ContainsKey(TenantIdName))
            {
                configuration.Add(TenantIdName, "");
            }
            if (configuration != null && configuration.ContainsKey(ApplicationIdName) &&
                configuration.ContainsKey(VaultUriName) && configuration.ContainsKey(TenantIdName))
            {
                _configuration = configuration;
                _logger.Information($"Plugin {Name} has been successfully configured.");
                _rgx = new Regex("[^a-zA-Z0-9-]");
            }
            else
            {
                _logger.Error("Some parameters are missing from the configuration.");
            }
        }

        public void SetVaultCredential(string credential)
        {
            if (_configuration != null)
            {
                _secretsClient = new SecretClient(new Uri(_configuration[VaultUriName]),
                    new ClientSecretCredential(_configuration[TenantIdName], _configuration[ApplicationIdName], credential));
                _logger.Information($"Plugin {Name} has been successfully authenticated to the Azure vault.");
            }
            else
            {
                _logger.Error("The plugin is missing the configuration.");
            }
        }

        public bool TestVaultConnection()
        {
            if (_secretsClient == null)
                return false;

            try
            {
                var result = _secretsClient.GetDeletedSecrets();
                _logger.Information($"Test vault connection for {DisplayName}: Result = {result != null}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed the connection test for {DisplayName}: {ex.Message}.");
                return false;
            }
        }

        public bool SetPassword(string asset, string account, string password, string altAccountName = null)
        {
            if (AssignedCredentialType != CredentialType.Password)
            {
                _logger.Error("This plugin instance does not handle the Password credential type.");
                return false;
            }

            if (_secretsClient == null || _configuration == null || !_configuration.ContainsKey(VaultUriName))
            {
                _logger.Error("No vault connection. Make sure that the plugin has been configured.");
                return false;
            }

            var name = _rgx.Replace(altAccountName ?? $"{asset}-{account}", "-");
            return StoreCredential(name, password);
        }

        public bool SetSshKey(string asset, string account, string sshKey, string altAccountName = null)
        {
            if (AssignedCredentialType != CredentialType.SshKey)
            {
                _logger.Error("This plugin instance does not handle the SshKey credential type.");
                return false;
            }

            if (_secretsClient == null || _configuration == null || !_configuration.ContainsKey(VaultUriName))
            {
                _logger.Error("No vault connection. Make sure that the plugin has been configured.");
                return false;
            }

            var name = _rgx.Replace(altAccountName ?? $"{asset}-{account}", "-");
            return StoreCredential(name, sshKey);
        }

        public bool SetApiKey(string asset, string account, string[] apiKeys, string altAccountName = null)
        {
            if (AssignedCredentialType != CredentialType.ApiKey)
            {
                _logger.Error("This plugin instance does not handle the ApiKey credential type.");
                return false;
            }

            if (_secretsClient == null || _configuration == null || !_configuration.ContainsKey(VaultUriName))
            {
                _logger.Error("No vault connection. Make sure that the plugin has been configured.");
                return false;
            }

            var name = _rgx.Replace(altAccountName ?? $"{asset}-{account}", "-");
            var retval = true;

            foreach (var apiKeyJson in apiKeys)
            {
                var apiKey = JsonHelper.DeserializeObject<ApiKey>(apiKeyJson);
                if (apiKey != null)
                {
                    StoreCredential($"{name}-{apiKey.Name}", $"{apiKey.ClientId}.{apiKey.ClientSecret}");
                }
                else
                {
                    _logger.Error($"The ApiKey {name} {apiKey.ClientId} failed to save to the {this.DisplayName} vault.");
                    retval = false;
                }
            }

            return retval;
        }

        public void SetLogger(ILogger logger)
        {
            _logger = logger;
        }

        public void Unload()
        {
        }

        private bool StoreCredential(string name, string payload)
        {
            try
            {
                Task.Run(async () => await _secretsClient.SetSecretAsync(name, payload));

                _logger.Information($"The secret for {name} has been successfully stored in the vault.");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to set the secret for {name}: {ex.Message}.");
                return false;
            }
        }
    }
}
