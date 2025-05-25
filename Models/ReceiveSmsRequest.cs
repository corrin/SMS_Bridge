namespace SMS_Bridge.Models
{
    public record ReceiveSmsRequest(
        SmsBridgeId MessageID,
        string FromNumber,
        string MessageText,
        DateTime ReceivedAt
    );
}
