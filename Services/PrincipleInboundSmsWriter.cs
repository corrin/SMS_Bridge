using SMS_Bridge.Models;
using SMS_Bridge.SmsProviders;

namespace SMS_Bridge.Services
{
    public class PrincipleInboundSmsWriter
    {
        private readonly PrincipleApiClient _principleApi;
        private readonly PrincipleOutboundSmsStore _store;

        public PrincipleInboundSmsWriter(PrincipleApiClient principleApi, PrincipleOutboundSmsStore store)
        {
            _principleApi = principleApi;
            _store = store;
        }

        public async Task<bool> TryWriteInboundReplyAsync(SmsProviderType provider, ReceiveSmsRequest sms)
        {
            if (!_principleApi.Enabled)
            {
                return false;
            }

            try
            {
                var target = await ResolveTargetAsync(sms.FromNumber);
                if (target == null)
                {
                    Logger.LogWarning(
                        provider: provider,
                        eventType: "PrincipleInboundSmsUnmatched",
                        SMSBridgeID: sms.MessageID,
                        providerMessageID: sms.ProviderMessageID,
                        details: $"Could not match inbound SMS sender {sms.FromNumber} to a Principle patient"
                    );
                    return false;
                }

                await _principleApi.CreateInboundSmsMessageAsync(
                    patientId: target.PatientId,
                    body: sms.MessageText,
                    practiceId: target.PracticeId
                );

                Logger.LogInfo(
                    provider: provider,
                    eventType: "PrincipleInboundSmsCreated",
                    SMSBridgeID: sms.MessageID,
                    providerMessageID: sms.ProviderMessageID,
                    details: $"Created Principle inbound SMS for patient {target.PatientId}"
                );
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    provider: provider,
                    eventType: "PrincipleInboundSmsFailed",
                    SMSBridgeID: sms.MessageID,
                    providerMessageID: sms.ProviderMessageID,
                    details: $"Failed to create Principle inbound SMS: {ex.Message}"
                );
                return false;
            }
        }

        private async Task<PrincipleReplyTarget?> ResolveTargetAsync(string fromNumber)
        {
            var mapped = await _store.FindByPhoneNumberAsync(fromNumber);
            if (mapped is { PatientId: not null })
            {
                return new PrincipleReplyTarget(mapped.PatientId, mapped.PracticeId);
            }

            var practiceIds = mapped != null
                ? new[] { mapped.PracticeId }
                : _principleApi.PracticeIds;

            foreach (var practiceId in practiceIds)
            {
                var matches = await _principleApi.SearchPatientsByPhoneAsync(practiceId, fromNumber);
                if (matches.Count == 1)
                {
                    return new PrincipleReplyTarget(matches[0].Id, practiceId);
                }
            }

            return null;
        }

        private record PrincipleReplyTarget(string PatientId, string PracticeId);
    }
}
