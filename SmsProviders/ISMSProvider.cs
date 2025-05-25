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
    }

}
