using Microsoft.Extensions.Configuration;

namespace SMS_Bridge.Services
{
    public class Configuration
    {
        private readonly IConfiguration _config;

        public Configuration(IConfiguration config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            if (string.IsNullOrEmpty(_config["BRIDGE_API_KEY"]))
            {
                throw new InvalidOperationException("BRIDGE_API_KEY must be configured in install-settings.json");
            }
            else
            {
                // Happy case handled below
            }
        }

        public string GetSetting(string key)
        {
            return _config[key]!;
        }

        public string GetRequiredSetting(string key)
        {
            var value = _config[key];
            if (string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException($"Required configuration setting '{key}' not found");
            }
            else
            {
                // Happy case handled below
            }
            return value;
        }

        public string GetApiKey()
        {
            return _config["BRIDGE_API_KEY"]!;
        }

        public string GetProviderSetting(string provider, string setting)
        {
            var key = $"Providers:{provider}:{setting}";
            return _config[key] ?? throw new Exception($"Provider setting '{key}' not found");
        }

        public string GetRequiredProviderSetting(string provider, string setting)
        {
            var key = $"Providers:{provider}:{setting}";
            var value = _config[key];
            if (string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException($"Required provider setting '{key}' not found");
            }
            else
            {
                // Happy case handled below
            }
            return value;
        }

        public Dictionary<string, string> GetProviderSettings(string provider)
        {
            var prefix = $"Providers:{provider}:";
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _config.AsEnumerable())
            {
                if (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && kvp.Value != null)
                {
                    result[kvp.Key.Substring(prefix.Length)] = kvp.Value;
                }
            }
            return result;
        }

        public Dictionary<string, string> GetAllSettings()
        {
            return _config.AsEnumerable()
                .Where(kvp => kvp.Value != null)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!, StringComparer.OrdinalIgnoreCase);
        }
    }
}
