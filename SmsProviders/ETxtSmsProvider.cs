using SMS_Bridge.Models;
using SMS_Bridge.Services;

namespace SMS_Bridge.SmsProviders
{
    public class ETxtSmsProvider : ISmsProvider
    {
        public Task<(IResult Result, Guid MessageId)> SendSms(SendSmsRequest request)
        {
            var result = Results.Problem(
                detail: "eTXT SMS provider is not implemented yet.",
                statusCode: 501,
                title: "Not Implemented"
            );

            return Task.FromResult((Result: result, MessageId: Guid.Empty));
        }

        public Task<MessageStatus> GetMessageStatus(Guid messageId)
        {
            Logger.LogWarning(
                provider: "eTXT",
                eventType: "NotImplemented",
                messageID: messageId.ToString(),
                details: "Status check attempted but eTXT provider is not implemented"
            );

            return Task.FromResult(MessageStatus.Failed);  // Stub - not implemented yet
        }
    }
}
