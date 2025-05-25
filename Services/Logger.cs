﻿﻿﻿﻿﻿﻿﻿using System.Diagnostics;
using System.Text.Json;
using SMS_Bridge.Models;
using SMS_Bridge.SmsProviders;

namespace SMS_Bridge.Services
{
    public static class Logger
    {
        private const string LOG_PATH = @"\\OPENDENTAL\OD Letters\od_logs";


        public static void Initialize()
        {
            try
            {
                if (!Directory.Exists(LOG_PATH))
                {
                    throw new InvalidOperationException($"Log directory does not exist: {LOG_PATH}");
                }

                var testFile = Path.Combine(LOG_PATH, "test.log");
                File.WriteAllText(testFile, "Logging test");
                File.Delete(testFile);
            }
            catch (UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"The application does not have write permissions for the log directory: {LOG_PATH}");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to initialize logging: {ex.Message}");
            }
        }


        public static void Log(string level, SmsProviderType provider,  string eventType, string messageID, string details)
        {
            try
            {
                string providerString = provider.ToString();

                var logEntry = new LogEntry(
                    Timestamp: DateTime.Now.ToString("o"),
                    Level: level,
                    Provider: providerString,
                    EventType: eventType,
                    MessageId: messageID,
                    Details: details
                );

                string jsonLog = JsonSerializer.Serialize(logEntry, AppJsonSerializerContext.Default.LogEntry);
                string logFilePath = Path.Combine(LOG_PATH, $"SMS_Log_{DateTime.Now:yyyyMMdd}.log");

                File.AppendAllText(logFilePath, jsonLog + Environment.NewLine);

                // Simplified console log
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} | {level} | {providerString} | {eventType} | {details}");

            }
            catch (Exception ex)
            {
                string errorMessage = $"{level}|{provider}|{eventType}|{messageID}|{details}";
                Console.WriteLine($"Failed to write to log file: {ex.Message}");


                // Last resort - console
                Console.WriteLine($"CRITICAL: All logging mechanisms failed. Original message: {errorMessage}");
            }
        }

        public static void LogCritical(SmsProviderType provider, string eventType, string messageID, string details)
        {
            Log("CRITICAL", provider, eventType, messageID, details);
        }

        public static void LogInfo(SmsProviderType provider, string eventType, string messageID, string details) =>
            Log("INFO", provider, eventType, messageID, details);

        public static void LogError(SmsProviderType provider, string eventType, string messageID, string details) =>
            Log("ERROR", provider, eventType, messageID, details);

        public static void LogWarning(SmsProviderType provider, string eventType, string messageID, string details) =>
            Log("WARNING", provider, eventType, messageID, details);
    }
}