using SMS_Bridge.Models;

namespace SMS_Bridge.SmsProviders
{

    public enum MessageStatus
    {
        Pending,   // Still in progress (enroute, submitted, or just sent)
        Delivered, // Successfully delivered, read, etc.
        Failed     // Any kind of failure (expired, rejected, failed, timeout)
    }

    public interface ISmsProvider
    {
        Task<(IResult Result, Guid MessageId)> SendSms(SendSmsRequest request);
        Task<MessageStatus> GetMessageStatus(Guid messageId);

    }

}
