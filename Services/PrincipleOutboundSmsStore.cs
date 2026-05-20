using System.Text.Json;
using SMS_Bridge.Models;

namespace SMS_Bridge.Services
{
    public class PrincipleOutboundSmsStore
    {
        private static readonly SemaphoreSlim SaveLock = new(1, 1);
        private static readonly string DirectoryPath = @"\\OPENDENTAL\OD Letters\msg_guids\";
        private static readonly string FilePath = Path.Combine(
            DirectoryPath,
            $"{Environment.MachineName}_principle_sms_map.json"
        );

        public async Task<bool> ExistsAsync(string principleMessageId)
        {
            var records = await LoadAsync();
            return records.Any(record => record.PrincipleMessageId == principleMessageId);
        }

        public async Task AddAsync(PrincipleOutboundSmsMapRecord record)
        {
            await SaveLock.WaitAsync();
            try
            {
                var records = await LoadUnsafeAsync();
                if (records.Any(existing => existing.PrincipleMessageId == record.PrincipleMessageId))
                {
                    return;
                }
                records.Add(record);
                Directory.CreateDirectory(DirectoryPath);
                var json = JsonSerializer.Serialize(
                    records,
                    AppJsonSerializerContext.Default.ListPrincipleOutboundSmsMapRecord
                );
                await File.WriteAllTextAsync(FilePath, json);
            }
            finally
            {
                SaveLock.Release();
            }
        }

        public async Task<PrincipleOutboundSmsMapRecord?> FindByPhoneNumberAsync(string phoneNumber)
        {
            var records = await LoadAsync();
            return records
                .LastOrDefault(record => NormalisePhoneNumber(record.PatientPhoneNumber) == NormalisePhoneNumber(phoneNumber));
        }

        private static async Task<List<PrincipleOutboundSmsMapRecord>> LoadAsync()
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

        private static async Task<List<PrincipleOutboundSmsMapRecord>> LoadUnsafeAsync()
        {
            if (!File.Exists(FilePath))
            {
                return new List<PrincipleOutboundSmsMapRecord>();
            }

            var json = await File.ReadAllTextAsync(FilePath);
            return JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListPrincipleOutboundSmsMapRecord)
                ?? new List<PrincipleOutboundSmsMapRecord>();
        }

        private static string NormalisePhoneNumber(string phoneNumber)
        {
            return new string(phoneNumber.Where(char.IsDigit).ToArray());
        }
    }
}
