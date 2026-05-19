using System.Text.Json.Serialization;

namespace SMS_Bridge.Models
{
    public record PrincipleBridgeInboundSmsNotification(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("providerMessageId")] string ProviderMessageId,
        [property: JsonPropertyName("smsBridgeId")] string SmsBridgeId,
        [property: JsonPropertyName("receivedAt")] DateTime ReceivedAt
    );
}
