using System.Diagnostics;
using System.Text.Json;
using SMS_Bridge.Models;

namespace SMS_Bridge.Services
{
    public static class Logger
    {
        private static readonly bool _eventLogAvailable;
        private const string LOG_PATH = @"\\OPENDENTAL\OD Letters\od_logs";


        public static void Initialize()
        {
            // Verify immediately that we can write logs
            var testFile = Path.Combine(LOG_PATH, "test.log");
            File.WriteAllText(testFile, "Logging test");
            File.Delete(testFile);
        }


        public static void Log(string level, string provider,  string eventType, string messageID, string details)
        {
            try
            {
                var logEntry = new LogEntry(
                    Timestamp: DateTime.UtcNow.ToString("o"),
                    Level: level,
                    Provider: provider,
                    EventType: eventType,
                    MessageId: messageID,
                    Details: details
                );

                string jsonLog = JsonSerializer.Serialize(logEntry, AppJsonSerializerContext.Default.LogEntry);
                string logFilePath = Path.Combine(LOG_PATH, $"SMS_Log_{DateTime.UtcNow:yyyyMMdd}.log");

                File.AppendAllText(logFilePath, jsonLog + Environment.NewLine);

                // Simplified console log
                Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} | {level} | {provider} | {eventType} | {details}");

            }
            catch (Exception ex)
            {
                string errorMessage = $"{level}|{provider}|{eventType}|{messageID}|{details}";
                Console.WriteLine($"Failed to write to log file: {ex.Message}");


                // Last resort - console
                Console.WriteLine($"CRITICAL: All logging mechanisms failed. Original message: {errorMessage}");
            }
        }

        public static void LogCritical(string provider, string eventType, string messageID, string details)
        {
            Log("CRITICAL", provider, eventType, messageID, details);
        }

        public static void LogInfo(string provider, string eventType, string messageID, string details) =>
            Log("INFO", provider, eventType, messageID, details);

        public static void LogError(string provider, string eventType, string messageID, string details) =>
            Log("ERROR", provider, eventType, messageID, details);

        public static void LogWarning(string provider, string eventType, string messageID, string details) =>
            Log("WARNING", provider, eventType, messageID, details);
    }
}