using Microsoft.Extensions.DependencyInjection;
using SMS_Bridge.SmsProviders;

namespace SMS_Bridge.Services
{
    public class MessageCleanupService : BackgroundService
    {
        private readonly IConfiguration _configuration;
        private readonly ISmsProvider _smsProvider;
        private readonly TimeSpan _interval;
        private readonly int _retentionDays;
        private readonly bool _cleanupEnabled;
        private DateTime _lastRun = DateTime.MinValue;

        public MessageCleanupService(
            IConfiguration configuration,
            ISmsProvider smsProvider)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _smsProvider = smsProvider ?? throw new ArgumentNullException(nameof(smsProvider));

            // Get configuration with defaults
            _interval = TimeSpan.FromMilliseconds(
                configuration.GetValue<int>("SmsSettings:CleanupInterval", 86400000)); // Default 24 hours
            _retentionDays = configuration.GetValue<int>("SmsSettings:RetentionDays", 30);
            _cleanupEnabled = configuration.GetValue<bool>("SmsSettings:EnableCleanup", true);

            // Log initialization
            Logger.LogInfo(
                provider: "MessageCleanup",
                eventType: "Initialization",
                messageID: "",
                details: $"Message cleanup service initialized with interval {_interval.TotalHours:F1} hours and retention period {_retentionDays} days. Cleanup is {(_cleanupEnabled ? "enabled" : "disabled")}."
            );
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_cleanupEnabled)
            {
                Logger.LogInfo(
                    provider: "MessageCleanup",
                    eventType: "Disabled",
                    messageID: "",
                    details: "Message cleanup service is disabled in configuration"
                );
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.UtcNow;

                    // Only run cleanup if it hasn't run in the last interval period
                    if (now - _lastRun < _interval)
                    {
                        var nextRun = _lastRun + _interval;
                        var delay = nextRun - now;
                        if (delay > TimeSpan.Zero)
                        {
                            await Task.Delay(delay, stoppingToken);
                        }
                        continue;
                    }

                    _lastRun = now;
                    var threshold = now.AddDays(-_retentionDays);

                    Logger.LogInfo(
                        provider: "MessageCleanup",
                        eventType: "CleanupStarted",
                        messageID: "",
                        details: $"Starting cleanup of messages older than {threshold:yyyy-MM-dd HH:mm:ss UTC}"
                    );

                    int deletedCount = 0;
                    if (_smsProvider is JustRemotePhoneSmsProvider provider)
                    {
                        deletedCount = JustRemotePhoneSmsProvider.CleanupOldEntries(threshold);
                    }

                    Logger.LogInfo(
                        provider: "MessageCleanup",
                        eventType: "CleanupCompleted",
                        messageID: "",
                        details: $"Cleanup completed. Removed {deletedCount} old messages"
                    );
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Normal shutdown, no need to log
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogError(
                        provider: "MessageCleanup",
                        eventType: "CleanupError",
                        messageID: "",
                        details: $"Error during message cleanup: {ex.Message}"
                    );

                    // Add delay after error to prevent rapid retry loops
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            Logger.LogInfo(
                provider: "MessageCleanup",
                eventType: "Shutdown",
                messageID: "",
                details: "Message cleanup service is shutting down"
            );

            await base.StopAsync(cancellationToken);
        }
    }
}