namespace SMS_Bridge.Models
{
    public record SendSmsRequest(string PhoneNumber, string Message, string? CallbackUrl = null, string? SenderId = null);
}
