using System.Diagnostics;
using System.Text.Json;
using SMS_Bridge.Models;

namespace SMS_Bridge.Services
{
    public static class Logger
    {
        private static readonly string LogDirectory;
        private const string EventSource = "ODSMS";
        private static readonly bool _eventLogAvailable;
        private const string DEFAULT_LOG_PATH = @"L:\od_logs\";

        static Logger()
        {
            // Try to get config, fallback to default if not available
            try
            {
                var config = new ConfigurationBuilder()
                    .AddJsonFile("appsettings.json", optional: true)
                    .Build();

                LogDirectory = config["Logging:Directory"] ?? DEFAULT_LOG_PATH;
            }
            catch
            {
                LogDirectory = DEFAULT_LOG_PATH;
            }

            // Try primary directory, fallback to temp if needed
            try
            {
                Directory.CreateDirectory(LogDirectory);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to create log directory at {LogDirectory}: {ex.Message}");

                // Only fallback if primary was not the default
                if (LogDirectory != DEFAULT_LOG_PATH)
                {
                    Console.WriteLine($"Attempting to use default path: {DEFAULT_LOG_PATH}");
                    try
                    {
                        Directory.CreateDirectory(DEFAULT_LOG_PATH);
                        LogDirectory = DEFAULT_LOG_PATH;
                    }
                    catch (Exception defaultEx)
                    {
                        Console.WriteLine($"Failed to create default log directory: {defaultEx.Message}");
                        LogDirectory = Path.Combine(Path.GetTempPath(), "sms_bridge_logs");
                        Directory.CreateDirectory(LogDirectory);
                        Console.WriteLine($"Using temporary directory for logs: {LogDirectory}");
                    }
                }
            }

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    _eventLogAvailable = EventLog.SourceExists(EventSource);
                    if (!_eventLogAvailable)
                    {
                        Console.WriteLine("Event log source ODSMS is not available");
                    }
                }
                catch (Exception ex)
                {
                    _eventLogAvailable = false;
                    Console.WriteLine($"Failed to check event log source: {ex.Message}");
                }
            }
        }

        private static void Log(string level, string provider, string eventType, string messageID, string details)
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
                string logFilePath = Path.Combine(LogDirectory, $"SMS_Log_{DateTime.UtcNow:yyyyMMdd}.log");

                File.AppendAllText(logFilePath, jsonLog + Environment.NewLine);
            }
            catch (Exception ex)
            {
                string errorMessage = $"{level}|{provider}|{eventType}|{messageID}|{details}";
                Console.WriteLine($"Failed to write to log file: {ex.Message}");

                // Try event log if available
                if (OperatingSystem.IsWindows() && _eventLogAvailable)
                {
                    try
                    {
                        EventLog.WriteEntry(EventSource,
                            $"Logging failed: {errorMessage}",
                            EventLogEntryType.Error);
                        return;
                    }
                    catch (Exception eventLogEx)
                    {
                        Console.WriteLine($"Failed to write to event log: {eventLogEx.Message}");
                    }
                }

                // Last resort - console
                Console.WriteLine($"CRITICAL: All logging mechanisms failed. Original message: {errorMessage}");
            }
        }

        public static void LogCritical(string provider, string eventType, string messageID, string details)
        {
            if (OperatingSystem.IsWindows() && _eventLogAvailable)
            {
                try
                {
                    EventLog.WriteEntry(EventSource,
                        $"SMS Bridge: {provider}, Event: {eventType}, ID: {messageID}, Details: {details}",
                        EventLogEntryType.Error);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to write critical event to event log: {ex.Message}");
                }
            }

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