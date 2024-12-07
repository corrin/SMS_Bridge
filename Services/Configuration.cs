using System;
using System.Collections.Generic;
using System.IO;

namespace SMS_Bridge.Services
{
    public class Configuration
    {
        private const string ConfigFilePath = @"\\OPENDENTAL\OD Letters\odsms.txt";
        private readonly Dictionary<string, string> _settings;

        public Configuration()
        {
            _settings = LoadSettings();

            // Ensure API_KEY is mandatory
            if (!_settings.ContainsKey("API_KEY"))
            {
                throw new InvalidOperationException($"API_KEY must be configured in {ConfigFilePath}");
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

            foreach (var line in lines)
            {
                // Skip comments or empty lines
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                    continue;

                var parts = line.Split(':', 2); // Split on the first colon
                if (parts.Length == 2)
                {
                    var key = parts[0].Trim();
                    var value = parts[1].Trim();
                    settings[key] = value;
                }
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

        public string GetApiKey()
        {
            return _settings["API_KEY"]; // API_KEY is guaranteed to be present
        }
    }
}
