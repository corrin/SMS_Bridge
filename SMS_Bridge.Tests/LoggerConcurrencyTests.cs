using SMS_Bridge.Services;
using SMS_Bridge.SmsProviders;
using Xunit;

namespace SMS_Bridge.Tests;

public class LoggerConcurrencyTests
{
    // Writes to the machine's real od_logs folder; each run's lines carry a unique marker.
    [Fact]
    public void Concurrent_log_calls_all_reach_the_file()
    {
        Logger.Initialize();
        var marker = $"concurrency-test-{Guid.NewGuid():N}";
        const int count = 200;

        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
            Logger.LogInfo(SmsProviderType.JustRemotePhone, "LoggerConcurrencyTest", $"{marker} {i}"));

        var logFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SMS_Bridge", "od_logs", $"SMS_Log_{DateTime.Now:yyyyMMdd}.log");
        using var reader = new StreamReader(new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        var written = reader.ReadToEnd().Split('\n').Count(line => line.Contains(marker));
        Assert.Equal(count, written);
    }
}
