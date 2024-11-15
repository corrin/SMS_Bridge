using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SMS_Bridge.Models;
using static SMS_Bridge.Testing;

namespace SMS_Bridge
{
    [JsonSerializable(typeof(SendSmsRequest))]
    [JsonSerializable(typeof(ReceiveSmsRequest))]
    [JsonSerializable(typeof(IEnumerable<ReceiveSmsRequest>))]
    [JsonSerializable(typeof(ProblemDetails))]
    [JsonSerializable(typeof(LogEntry))]
    [JsonSerializable(typeof(Result))]
    [JsonSerializable(typeof(ApiResponse<IResult>))]
    [JsonSerializable(typeof((IResult Result, Guid MessageId)))]
    [JsonSerializable(typeof(MessageStatusResponse))]
    [JsonSerializable(typeof(CheckSendSmsResponse))]
    [JsonSerializable(typeof(BulkSmsResponse))]
    [JsonSerializable(typeof(DebugStatusResponse))]

    internal partial class AppJsonSerializerContext : JsonSerializerContext
    {
    }


}
