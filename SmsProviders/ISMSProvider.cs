﻿using SMS_Bridge.Models;

namespace SMS_Bridge.SmsProviders
{


    public enum SmsProviderType
    {
        JustRemotePhone,
        Diafaan,
        ETxt,
        BuggyCodeNeedsFixing
    }

    public interface ISmsProvider
    {
        Task<(IResult Result, SmsBridgeId smsBridgeId)> SendSms(SendSmsRequest request, SmsBridgeId smsBridgeId);
        Task<SmsStatus> GetMessageStatus(SmsBridgeId smsBridgeId);
        ProviderMessageId? GetProviderMessageID(SmsBridgeId smsBridgeId);
        Task<IEnumerable<ReceiveSmsRequest>> GetReceivedMessages();
        Task<DeleteMessageResponse> DeleteReceivedMessage(SmsBridgeId smsBridgeId);
        Task<IEnumerable<MessageStatusRecord>> GetRecentMessageStatuses();

        // Why texts can't go out right now, or null when they can.
        string? SendBlockedReason();

        // True when a send accepted by SendSms is confirmed later through SendOutcome:
        // Delivered when it went out, Failed when it didn't, TimedOut when nothing came back.
        // False when SendSms accepting the text is the last word on it.
        bool ReportsSendOutcome { get; }
        event Action<SmsBridgeId, SmsStatus> SendOutcome;
    }

}
