﻿using System.Diagnostics;
using System.Text.Json;
using SMS_Bridge.Models;

namespace SMS_Bridge.Services
{
    public static class Logger
    {
        private static readonly string LogDirectory = @"L:\od_sms\";
        private const string EventSource = "ODSMS";
        private static readonly bool _eventLogAvailable;

        static Logger()
        {
            Directory.CreateDirectory(LogDirectory);

            // Check if ODSMS source exists
            try
            {
                _eventLogAvailable = EventLog.SourceExists(EventSource);
            }
            catch
            {
                _eventLogAvailable = false;
            }
        }

        private static void Log(string level, string provider, string eventType, string messageID, string details)
        {
            try
            {
                var logEntry = new LogEntry
                {
                    Timestamp = DateTime.UtcNow.ToString("o"),
                    Level = level,
                    Provider = provider,
                    EventType = eventType,
                    MessageId = messageID,
                    Details = details
                };

                string jsonLog = JsonSerializer.Serialize(logEntry, AppJsonSerializerContext.Default.LogEntry);
                string logFilePath = Path.Combine(LogDirectory, $"SMS_Log_{DateTime.UtcNow:yyyyMMdd}.log");

                File.AppendAllText(logFilePath, jsonLog + Environment.NewLine);
            }
            catch
            {
                // Swallow file logging errors
            }
        }

        public static void LogCritical(string provider, string eventType, string messageID, string details)
        {
            if (_eventLogAvailable)
            {
                try
                {
                    EventLog.WriteEntry(EventSource,
                        $"SMS Bridge: {provider}, Event: {eventType}, ID: {messageID}, Details: {details}",
                        EventLogEntryType.Error);
                }
                catch
                {
                    // If even this fails, we're in real trouble
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