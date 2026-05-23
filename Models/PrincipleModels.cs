using System.Text.Json.Serialization;

namespace SMS_Bridge.Models
{
    public class PrincipleOptions
    {
        public bool Enabled { get; set; } = false;
        public string ApiBaseUrl { get; set; } = "https://api.principle.dental";
        public string[] PracticeIds { get; set; } = Array.Empty<string>();
        public int CacheTtlDays { get; set; } = 7;
    }

    public record PrincipleWebhookPayload(
        [property: JsonPropertyName("eventType")] string EventType,
        [property: JsonPropertyName("timestamp")] DateTime Timestamp,
        [property: JsonPropertyName("data")] PrincipleSmsMessageData? Data
    );

    public record PrincipleSmsMessageData(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("direction")] string? Direction,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("patientId")] string? PatientId,
        [property: JsonPropertyName("practiceId")] string? PracticeId,
        [property: JsonPropertyName("patientPhoneNumber")] string? PatientPhoneNumber,
        [property: JsonPropertyName("practicePhoneNumber")] string? PracticePhoneNumber
    );

    public record PrincipleSmsMessageCreate(
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("direction")] string Direction,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("practiceId")] string PracticeId
    );

    public record PrinciplePatientSearchResponse(
        [property: JsonPropertyName("data")] List<PrinciplePatient> Data
    );

    public record PrinciplePatient(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("practiceId")] string? PracticeId
    );

    public record PrincipleOutboundSmsMapRecord(
        string PrincipleMessageId,
        string SmsBridgeId,
        string PatientPhoneNumber,
        string? PatientId,
        string PracticeId,
        string Body,
        DateTime ReceivedAt
    );
}
