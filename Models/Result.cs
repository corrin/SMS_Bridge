using SMS_Bridge.SmsProviders;

namespace SMS_Bridge.Models
{
    public class Result
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string MessageID { get; set; }
    }

    public class ApiResponse<T>
    {
        public List<T> Results { get; set; } = new();
    }

    public class MessageStatusResponse
    {
        public string MessageID { get; set; }
        public MessageStatus Status { get; set; }
        public string StatusDisplay => Status.ToString();

    }


}
