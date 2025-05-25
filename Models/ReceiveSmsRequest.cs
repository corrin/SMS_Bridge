namespace SMS_Bridge.Models
{
    public record ReceiveSmsRequest(
        SmsBridgeId MessageID,
        ProviderMessageId ProviderMessageID,
        string FromNumber,
        string MessageText,
        DateTime ReceivedAt
    );
}
