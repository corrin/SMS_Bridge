﻿using SMS_Bridge.Models;

namespace SMS_Bridge.SmsProviders
{


    public interface ISmsProvider
    {
        Task<(IResult Result, Guid MessageId)> SendSms(SendSmsRequest request);
        Task<SmsStatus> GetMessageStatus(Guid messageId);
        Task<IEnumerable<ReceiveSmsRequest>> GetReceivedMessages();
        Task<DeleteMessageResponse> DeleteReceivedMessage(Guid messageId);
        Task<IEnumerable<MessageStatusRecord>> GetRecentMessageStatuses();
    }

}
