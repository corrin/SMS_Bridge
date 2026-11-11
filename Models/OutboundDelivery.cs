namespace SMS_Bridge.Models
{
    public enum OutboundSmsStatus
    {
        Pending,         // Waiting to be handed to the phone
        AwaitingResult,  // Handed to the phone; its send result hasn't arrived
        Sent,
        Failed           // Given up on; listed by /smsgateway/failed-sms until a later text to the number goes out
    }

    public record OutboundDelivery(
        OutboundSmsStatus Status,
        int Attempts = 0,
        int Timeouts = 0,
        DateTimeOffset? NextAttemptAt = null,
        DateTimeOffset? FinishedAt = null,
        string? LastError = null,
        bool PrincipleReported = false,
        DateTimeOffset? SentAt = null
    );

    public record FailedSms(
        string PrincipleMessageId,
        string? PatientId,
        string PatientPhoneNumber,
        DateTime ReceivedAt,
        DateTime FailedAt,
        int Attempts,
        string Reason
    );

    public record FailedSmsResponse(List<FailedSms> Failed);
}
