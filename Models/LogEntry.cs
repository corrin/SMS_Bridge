namespace SMS_Bridge.Models
{
    public class LogEntry
    {
        public string Timestamp { get; set; }
        public string Level { get; set; }
        public string Provider { get; set; }
        public string EventType { get; set; }
        public string MessageId { get; set; } 
        public string Details { get; set; }
    }
}
