using SMS_Bridge.SmsProviders;

namespace SMS_Bridge.Models
{

    public enum SmsStatus
    {
        Pending,     // Just sent, no updates yet
        Delivered,   // Got confirmation of delivery
        Failed,      // Got confirmation of failure
        Unknown,     // Generic/unexpected status
        TimedOut     // No update received in time
    }

    public record Result(
        bool Success,
        string Message,
        string SMSBridgeID = "" 
    );

    public class ApiResponse<T>
    {
        public List<T> Results { get; set; } = new();
    }

    public record BulkSmsResponse(
        bool Success,
        string Message,
        IEnumerable<IResult> Results,
        IEnumerable<SmsBridgeId> SMSBridgeIDs,
        IEnumerable<ProviderMessageId> ProviderMessageIDs
    );

    public record MessageStatusResponse(
        string MessageID,
        SmsStatus Status)
    {
        public string StatusDisplay => Status.ToString();
    }

    public record DeleteMessageResponse(
        SmsBridgeId SMSBridgeID,
        bool Deleted,
        string DeleteFeedback
    );

    public record DebugStatusResponse(
        bool IsDebugMode,
        string TestingPhoneNumber,
        string[] AllowedTestNumbers
    );

    public record MessageStatusRecord(
        SmsBridgeId SMSBridgeID,
        ProviderMessageId ProviderMessageID,
        SmsStatus Status,
        DateTime SentAt,
        DateTime StatusAt
    );


}
