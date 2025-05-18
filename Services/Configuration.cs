using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SMS_Bridge.Services
{
    public class Configuration
    {
        private const string ConfigFilePath = @"\\OPENDENTAL\OD Letters\odsms.txt";
        private readonly Dictionary<string, string> _settings;

        // Cache of provider settings for faster lookup
        private readonly Dictionary<string, Dictionary<string, string>> _providerSettings = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public Configuration()
        {
            _settings = LoadSettings();

            // Ensure API_KEY is mandatory
            if (!_settings.ContainsKey("API_KEY"))
            {
                throw new InvalidOperationException($"API_KEY must be configured in {ConfigFilePath}");
            }

            // Pre-parse provider settings
            foreach (var key in _settings.Keys.ToList())
            {
                // Only process keys that match our provider_setting pattern (contain underscore)
                if (key.Contains('_'))
                {
                    var parts = key.Split('_', 2);
                    if (parts.Length == 2)
                    {
                        var provider = parts[0].ToLower();
                        var settingName = parts[1];

                        if (!_providerSettings.TryGetValue(provider, out var providerDict))
                        {
                            providerDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            _providerSettings[provider] = providerDict;
                        }

                        providerDict[settingName] = _settings[key];
                    }
                }
                // Other settings (like RECEIVER:RECEPTION-AIO) are left in the main _settings dictionary
            }
        }

        private Dictionary<string, string> LoadSettings()
        {
            if (!File.Exists(ConfigFilePath))
            {
                throw new InvalidOperationException($"Configuration file not found: {ConfigFilePath}");
            }

            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lines = File.ReadAllLines(ConfigFilePath);

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                // Remove comments
                var commentIndex = line.IndexOf('#');
                if (commentIndex >= 0)
                {
                    line = line.Substring(0, commentIndex);
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var parts = line.Split(':', 2);
                if (parts.Length != 2)
                {
                    throw new InvalidOperationException(
                        $"Malformed configuration line {i + 1}: '{lines[i]}'. Ensure the file uses 'key: value' pairs.");
                }

                var key = parts[0].Trim();
                var value = parts[1].Trim();

                if (settings.ContainsKey(key))
                {
                    throw new InvalidOperationException($"Duplicate key '{key}' found in configuration file: {ConfigFilePath}");
                }

                settings[key] = value;
            }

            return settings;
        }

        public string GetSetting(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (_settings.TryGetValue(key, out var value))
            {
                return value;
            }

            // Return null for optional settings
            return null;
        }

        // Get a setting that is required - throws exception if missing
        public string GetRequiredSetting(string key)
        {
            var value = GetSetting(key);
            if (string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException($"Required configuration setting '{key}' not found in {ConfigFilePath}");
            }
            return value;
        }

        public string GetApiKey()
        {
            return _settings["API_KEY"]; // API_KEY is guaranteed to be present
        }

        // Get all settings for a specific provider
        public Dictionary<string, string> GetProviderSettings(string provider)
        {
            if (string.IsNullOrWhiteSpace(provider))
            {
                throw new ArgumentNullException(nameof(provider));
            }

            provider = provider.ToLower();

            if (_providerSettings.TryGetValue(provider, out var settings))
            {
                return new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase);
            }

            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        // Get a specific setting for a provider
        public string GetProviderSetting(string provider, string setting)
        {
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(setting))
            {
                throw new ArgumentNullException(provider == null ? nameof(provider) : nameof(setting));
            }

            provider = provider.ToLower();

            if (_providerSettings.TryGetValue(provider, out var settings) &&
                settings.TryGetValue(setting, out var value))
            {
                return value;
            }

            return null;
        }

        // Get a required setting for a provider - throws exception if missing
        public string GetRequiredProviderSetting(string provider, string setting)
        {
            var value = GetProviderSetting(provider, setting);
            if (string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException(
                    $"Required provider setting '{provider}_{setting}' not found in {ConfigFilePath}");
            }
            return value;
        }

        // Get all settings as a dictionary (including all non-provider specific settings)
        public Dictionary<string, string> GetAllSettings()
        {
            return new Dictionary<string, string>(_settings, StringComparer.OrdinalIgnoreCase);
        }
    }
}