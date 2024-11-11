namespace SMS_Bridge.Models
{
    public record ReceiveSmsRequest(string From, string To, string MessageBody);
}
