﻿using SMS_Bridge.Models;
using SMS_Bridge.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static Microsoft.AspNetCore.Http.Results;

namespace SMS_Bridge.SmsProviders
{
    public class DiafaanSmsProvider : ISmsProvider
    {
        private readonly ConcurrentDictionary<SmsBridgeId, ProviderMessageId> _smsBridgeToProviderId = new();

        public Task<(IResult Result, SmsBridgeId smsBridgeId)> SendSms(SendSmsRequest request, SmsBridgeId smsBridgeId)
        {
            var result = Results.Problem(
                detail: "Diafaan SMS provider is not implemented yet.",
                statusCode: 501,
                title: "Not Implemented"
            );

            // Since this is a stub implementation, we don't need to create a real mapping
            return Task.FromResult((Result: result, smsBridgeId));
        }

        public Task<SmsStatus> GetMessageStatus(SmsBridgeId smsBridgeId)
        {
            Logger.LogWarning(
                provider: SmsProviderType.Diafaan,
                eventType: "NotImplemented",
                SMSBridgeID: smsBridgeId,
                providerMessageID: default,
                details: "Status check attempted but eTXT provider is not implemented"
            );
            return Task.FromResult(SmsStatus.Failed);
        }

        public Task<IEnumerable<ReceiveSmsRequest>> GetReceivedMessages()
        {
            Logger.LogWarning(
                provider: SmsProviderType.Diafaan,
                eventType: "NotImplemented",
                SMSBridgeID: default,
                providerMessageID: default,
                details: "Receive Messages attempted but Diafaan provider is not implemented"
            );
            return Task.FromResult(Enumerable.Empty<ReceiveSmsRequest>());
        }

        public ProviderMessageId? GetProviderMessageID(SmsBridgeId smsBridgeId)
        {
            // Since this is a stub implementation, we'll just throw an exception
            throw new NotImplementedException("Diafaan SMS provider is not implemented yet.");
        }

        public Task<DeleteMessageResponse> DeleteReceivedMessage(SmsBridgeId smsBridgeId)
        {
            Logger.LogWarning(
                provider: SmsProviderType.Diafaan,
                eventType: "NotImplemented",
                SMSBridgeID: smsBridgeId,
                providerMessageID: default,
                details: "Delete Message attempted but Diafaan provider is not implemented"
            );
            return Task.FromResult(new DeleteMessageResponse(
                SMSBridgeID: smsBridgeId,
                Deleted: false,
                DeleteFeedback: "Delete operation not implemented for Diafaan provider"
            ));
        }

        public Task<IEnumerable<MessageStatusRecord>> GetRecentMessageStatuses()
        {
            Logger.LogWarning(
                provider: SmsProviderType.Diafaan,
                eventType: "NotImplemented",
                SMSBridgeID: default,
                providerMessageID: default,
                details: "Get Recent Statuses attempted but Diafaan provider is not implemented"
            );
            return Task.FromResult(Enumerable.Empty<MessageStatusRecord>());
        }

    }
}
