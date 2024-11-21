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

        public Task<SmsStatus> GetMessageStatus(Guid messageId)
        {
            Logger.LogWarning(
                provider: "eTXT",
                eventType: "NotImplemented",
                messageID: messageId.ToString(),
                details: "Status check attempted but eTXT provider is not implemented"
            );

            return Task.FromResult(SmsStatus.Failed);  // Stub - not implemented yet
        }

        public Task<IEnumerable<ReceiveSmsRequest>> GetReceivedMessages()
        {
            Logger.LogWarning(
                provider: "eTXT",
                eventType: "NotImplemented",
                messageID: "",
                details: "Receive Messages attempted but eTXT provider is not implemented"
            );
            return Task.FromResult(Enumerable.Empty<ReceiveSmsRequest>());
        }
        public Task<DeleteMessageResponse> DeleteReceivedMessage(Guid messageId)
        {
            Logger.LogWarning(
                provider: "eTXT",
                eventType: "NotImplemented",
                messageID: "",
                details: "Delete Message attempted but eTXT provider is not implemented"
            );
            return Task.FromResult(new DeleteMessageResponse(
                MessageID: messageId.ToString(),
                Deleted: false,
                DeleteFeedback: "Delete operation not implemented for eTXT provider"
            ));
        }

        public Task<IEnumerable<MessageStatusRecord>> GetRecentMessageStatuses()
        {
            Logger.LogWarning(
                provider: "eTXT",
                eventType: "NotImplemented",
                messageID: "",
                details: "Get Recent Statuses attempted but eTXT provider is not implemented"
            );
            return Task.FromResult(Enumerable.Empty<MessageStatusRecord>());
        }


    }
}
