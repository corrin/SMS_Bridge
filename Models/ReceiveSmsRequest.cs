namespace SMS_Bridge.Models
{
    public record ReceiveSmsRequest(
        Guid MessageID,
        string FromNumber,
        string MessageText,
        DateTime ReceivedAt
    );
}
