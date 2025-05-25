namespace SMS_Bridge.Models
{
    public record LogEntry(
        string Timestamp,
        string Level,
        string Provider,
        string EventType,
        string Details,
        string SMSBridgeID, 
        string ProviderMessageID
    );
}
