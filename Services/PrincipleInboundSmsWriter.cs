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
            else
            {
                // Happy case handled below
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
                else
                {
                    // Happy case handled below
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
                    details: $"Principle API error for inbound SMS from {sms.FromNumber}: {ex.Message}"
                );
                return false;
            }
        }

        public async Task<PrincipleReplyTarget?> ResolveTargetAsync(string fromNumber)
        {
            var mapped = await _store.FindByPhoneNumberAsync(fromNumber);
            Logger.LogInfo(
                provider: SmsProviderType.JustRemotePhone,
                eventType: "PrincipleInboundResolve",
                details: $"Store lookup for {fromNumber}: found={mapped != null}, practiceId={mapped?.PracticeId}, patientId={mapped?.PatientId}"
            );

            if (mapped is { PatientId: not null })
            {
                return new PrincipleReplyTarget(mapped.PatientId, mapped.PracticeId);
            }
            else
            {
                // Happy case handled below
            }

            var practiceIds = mapped != null
                ? new[] { mapped.PracticeId }
                : _principleApi.PracticeIds;

            Logger.LogInfo(
                provider: SmsProviderType.JustRemotePhone,
                eventType: "PrincipleInboundResolve",
                details: $"Searching Principle API for {fromNumber} in practices: [{string.Join(", ", practiceIds)}]"
            );

            foreach (var practiceId in practiceIds)
            {
                var matches = await _principleApi.SearchPatientsByPhoneAsync(practiceId, fromNumber);
                Logger.LogInfo(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "PrincipleInboundResolve",
                    details: $"Search practice={practiceId} phone={fromNumber}: {matches.Count} matches"
                );
                if (matches.Count == 0)
                    continue;
                return new PrincipleReplyTarget(matches[0].Id, practiceId);
            }

            return null;
        }

        public record PrincipleReplyTarget(string PatientId, string PracticeId);
    }
}
