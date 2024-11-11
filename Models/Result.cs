using SMS_Bridge.SmsProviders;

namespace SMS_Bridge.Models
{
    public record Result(
        bool Success,
        string Message,
        string MessageID = ""  // Default value for MessageID since it might not always be needed
    );

    public class ApiResponse<T>
    {
        public List<T> Results { get; set; } = new();
    }

    public record BulkSmsResponse(
        IEnumerable<IResult> Results,
        IEnumerable<string> MessageIds
    );


    public record MessageStatusResponse(
        string MessageID,
        MessageStatus Status)
    {
        public string StatusDisplay => Status.ToString();
    }

    public record DeleteMessageResponse(
        string MessageID,
        bool Deleted,
        string DeleteFeedback
    );

}
