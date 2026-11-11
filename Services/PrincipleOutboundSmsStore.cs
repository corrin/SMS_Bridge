using Microsoft.Extensions.Configuration;
using System.Text.Json;
using SMS_Bridge.Models;
using SMS_Bridge.SmsProviders;

namespace SMS_Bridge.Services
{
    public class PrincipleOutboundSmsStore
    {
        private readonly SemaphoreSlim SaveLock = new(1, 1);
        private readonly string DirectoryPath;
        private readonly string FilePath;
        private readonly int _cacheTtlDays;

        public PrincipleOutboundSmsStore(IConfiguration configuration, string? directoryPath = null)
        {
            var options = configuration.GetSection("Principle").Get<PrincipleOptions>()!;
            _cacheTtlDays = options.CacheTtlDays;
            DirectoryPath = directoryPath ?? Path.Combine(AppData.BasePath, "msg_guids");
            FilePath = Path.Combine(DirectoryPath, $"{Environment.MachineName}_principle_sms_map.json");
        }

        // Adds the record unless one with the same Principle message id exists. The check and the
        // write share one lock, so concurrent webhook retries of a message claim it exactly once.
        public async Task<bool> AddAsync(PrincipleOutboundSmsMapRecord record)
        {
            await SaveLock.WaitAsync();
            try
            {
                var records = await LoadUnsafeAsync();
                if (records.Any(existing => existing.PrincipleMessageId == record.PrincipleMessageId))
                {
                    return false;
                }
                else
                {
                    // Happy case handled below
                }
                var cutoff = DateTimeOffset.Now.AddDays(-_cacheTtlDays);
                records.RemoveAll(r => r.ReceivedAt < cutoff && !MustKeep(r, records));
                records.Add(record);
                await SaveUnsafeAsync(records);
                return true;
            }
            finally
            {
                SaveLock.Release();
            }
        }

        // Replaces the stored record with the same Principle message id.
        public async Task UpdateAsync(PrincipleOutboundSmsMapRecord record)
        {
            await SaveLock.WaitAsync();
            try
            {
                var records = await LoadUnsafeAsync();
                var index = records.FindIndex(r => r.PrincipleMessageId == record.PrincipleMessageId);
                if (index < 0)
                {
                    throw new InvalidOperationException($"Principle message {record.PrincipleMessageId} is not in the store");
                }
                else
                {
                    // Happy case handled below
                }
                records[index] = record;
                await SaveUnsafeAsync(records);
            }
            finally
            {
                SaveLock.Release();
            }
        }

        // Replaces each stored record with the same Principle message id, in one write.
        public async Task UpdateManyAsync(List<PrincipleOutboundSmsMapRecord> updated)
        {
            if (updated.Count == 0)
                return;
            else
            {
                // Happy case handled below
            }

            await SaveLock.WaitAsync();
            try
            {
                var records = await LoadUnsafeAsync();
                foreach (var record in updated)
                {
                    var index = records.FindIndex(r => r.PrincipleMessageId == record.PrincipleMessageId);
                    if (index < 0)
                    {
                        throw new InvalidOperationException($"Principle message {record.PrincipleMessageId} is not in the store");
                    }
                    else
                    {
                        records[index] = record;
                    }
                }
                await SaveUnsafeAsync(records);
            }
            finally
            {
                SaveLock.Release();
            }
        }

        // Records still to be sent or awaiting the phone's result, and finished ones Principle hasn't been told about.
        public async Task<List<PrincipleOutboundSmsMapRecord>> UnfinishedAsync()
        {
            var records = await LoadAsync();
            return records
                .Where(r => r.Delivery != null
                         && (r.Delivery.Status is OutboundSmsStatus.Pending or OutboundSmsStatus.AwaitingResult
                             || !r.Delivery.PrincipleReported))
                .ToList();
        }

        // Texts given up on with no later text to the same number sent after the failure.
        public async Task<List<FailedSms>> FailedAsync()
        {
            var records = await LoadAsync();
            return records
                .Where(r => r.Delivery?.Status == OutboundSmsStatus.Failed && !ClearedByLaterSend(r, records))
                .Select(r => new FailedSms(
                    PrincipleMessageId: r.PrincipleMessageId,
                    PatientId: r.PatientId,
                    PatientPhoneNumber: r.PatientPhoneNumber,
                    ReceivedAt: r.ReceivedAt.UtcDateTime,
                    FailedAt: r.Delivery!.FinishedAt!.Value.UtcDateTime,
                    Attempts: r.Delivery.Attempts,
                    Reason: r.Delivery.LastError ?? "unknown"))
                .OrderBy(f => f.FailedAt)
                .ToList();
        }

        public async Task<PrincipleOutboundSmsMapRecord?> FindByPhoneNumberAsync(string phoneNumber)
        {
            var records = await LoadAsync();
            var normalised = NormalisePhoneNumber(phoneNumber);
            var cutoff = DateTimeOffset.Now.AddDays(-_cacheTtlDays);
            var matching = records
                .Where(record => NormalisePhoneNumber(record.PatientPhoneNumber) == normalised
                              && record.ReceivedAt > cutoff)
                .ToList();
            var match = matching.LastOrDefault(r => r.PatientId != null)
                ?? matching.LastOrDefault();
            if (match == null)
            {
                // Distinguishes an empty/expired store from a number-format mismatch
                Logger.LogInfo(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "PrincipleStoreLookupMiss",
                    details: $"No store match for {phoneNumber} (normalised: {normalised}): store holds {records.Count} records, {records.Count(r => r.ReceivedAt > cutoff)} within TTL"
                );
            }
            else
            {
                // Happy case handled below
            }
            return match;
        }

        // Unfinished work, and failures nobody has cleared, outlive the TTL.
        private static bool MustKeep(PrincipleOutboundSmsMapRecord record, List<PrincipleOutboundSmsMapRecord> records) =>
            record.Delivery?.Status switch
            {
                OutboundSmsStatus.Pending or OutboundSmsStatus.AwaitingResult => true,
                OutboundSmsStatus.Failed => !ClearedByLaterSend(record, records),
                _ => record.Delivery?.PrincipleReported == false
            };

        private static bool ClearedByLaterSend(PrincipleOutboundSmsMapRecord failed, List<PrincipleOutboundSmsMapRecord> records)
        {
            var number = NormalisePhoneNumber(failed.PatientPhoneNumber);
            return records.Any(r => r.Delivery?.Status == OutboundSmsStatus.Sent
                                 && r.Delivery.FinishedAt > failed.Delivery!.FinishedAt
                                 && NormalisePhoneNumber(r.PatientPhoneNumber) == number);
        }

        private async Task<List<PrincipleOutboundSmsMapRecord>> LoadAsync()
        {
            await SaveLock.WaitAsync();
            try
            {
                return await LoadUnsafeAsync();
            }
            finally
            {
                SaveLock.Release();
            }
        }

        private async Task<List<PrincipleOutboundSmsMapRecord>> LoadUnsafeAsync()
        {
            if (!File.Exists(FilePath))
            {
                return new List<PrincipleOutboundSmsMapRecord>();
            }
            else
            {
                // Happy case handled below
            }

            var json = await File.ReadAllTextAsync(FilePath);
            return JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListPrincipleOutboundSmsMapRecord)
                ?? new List<PrincipleOutboundSmsMapRecord>();
        }

        // Written beside the store and moved over it, so a crash mid-write leaves the previous
        // version rather than a truncated file that would lose waiting texts and webhook dedupe.
        private async Task SaveUnsafeAsync(List<PrincipleOutboundSmsMapRecord> records)
        {
            Directory.CreateDirectory(DirectoryPath);
            var json = JsonSerializer.Serialize(
                records,
                AppJsonSerializerContext.Default.ListPrincipleOutboundSmsMapRecord
            );
            var temp = FilePath + ".tmp";
            await File.WriteAllTextAsync(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }

        private static string NormalisePhoneNumber(string phoneNumber)
        {
            return new string(phoneNumber.Where(char.IsDigit).ToArray());
        }
    }
}
