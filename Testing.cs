namespace SMS_Bridge
{
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Configuration;
    using SMS_Bridge.Models;
    using SMS_Bridge.Services;
    using SMS_Bridge.SmsProviders;

    public static class Testing
    {

        public record CheckSendSmsResponse
        (
            Guid MessageID,
            string Status
        );

        public static void RegisterTestingEndpoints(RouteGroupBuilder testingGatewayAPI, IConfiguration configuration)
        {
            testingGatewayAPI.MapGet("/send-sms", (IServiceProvider services) =>
            {
                try
                {
                    var smsQueueService = services.GetRequiredService<SmsQueueService>();
                    var configuration = services.GetRequiredService<IConfiguration>();
                    var defaultPhoneNumber = configuration["SmsSettings:TestingPhoneNumber"] ?? "+6421467784";
                    var testRequest = new SendSmsRequest(defaultPhoneNumber, "This is a test message during development");

                    var messageId = smsQueueService.QueueSms(testRequest);

                    return Results.Ok(new Result(
                        Success: true,
                        Message: "SMS queued for sending",
                        MessageID: messageId.ToString()
                    ));
                }
                catch (Exception ex)
                {
                    return Results.Problem(
                        title: "SMS Gateway Error",
                        detail: ex.Message,
                        statusCode: 503
                    );
                }
            });

            testingGatewayAPI.MapGet("/send-bulk-sms", async (IServiceProvider services) =>
            {
                try
                {
                    var provider = services.GetRequiredService<ISmsProvider>();
                    var configuration = services.GetRequiredService<IConfiguration>();
                    var defaultPhoneNumber = configuration["SmsSettings:TestingPhoneNumber"] ?? "+64211626986";
                    var responses = new List<(IResult Result, Guid MessageId)>();
                    var totalMessages = 50;

                    for (int i = 0; i < totalMessages; i++)
                    {
                        var testRequest = new SendSmsRequest(defaultPhoneNumber, $"Bulk test message #{i + 1} of {totalMessages}");
                        var response = await provider.SendSms(testRequest);
                        responses.Add(response);
                    }

                    var successCount = responses.Count(r => r.Result is ObjectResult && ((ObjectResult)r.Result).StatusCode == 200);
                    var failureCount = responses.Count - successCount;

                    return Results.Ok(new BulkSmsResponse(
                        Success: failureCount == 0,
                        Message: $"Bulk send completed: {successCount} succeeded, {failureCount} failed",
                        Results: responses.Select(r => r.Result),
                        MessageIds: responses.Select(r => r.MessageId.ToString())
                    ));
                }
                catch (Exception ex)
                {
                    return Results.Problem(
                        title: "Bulk SMS Gateway Error",
                        detail: ex.Message,
                        statusCode: 503
                    );
                }
            });
            testingGatewayAPI.MapGet("/check-send-sms", async (IServiceProvider services) =>
            {
                try
                {
                    var smsQueueService = services.GetRequiredService<SmsQueueService>();
                    var smsProvider = services.GetRequiredService<ISmsProvider>();
                    var configuration = services.GetRequiredService<IConfiguration>();
                    var defaultPhoneNumber = configuration["SmsSettings:TestingPhoneNumber"] ?? "+6421467784";
                    var testRequest = new SendSmsRequest(defaultPhoneNumber, "This is a test message during development");

                    var messageId = smsQueueService.QueueSms(testRequest);
                    var status = SmsStatus.Pending;

                    await Task.Delay(1000); // Initial delay

                    // Check up to 20 times with a 1-second delay between each check
                    for (int i = 0; i < 20; i++)
                    {
                        await Task.Delay(1000);
                        status = await smsProvider.GetMessageStatus(messageId);
                        if (status != SmsStatus.Pending)
                        {
                            break;
                        }
                    }

                    return Results.Ok(new CheckSendSmsResponse(
                        MessageID: messageId,
                        Status: status.ToString()
                    ));
                }
                catch (Exception ex)
                {
                    return Results.Problem(
                        title: "SMS Check Failed",
                        detail: ex.Message,
                        statusCode: 503
                    );
                }
            });
        }
    }
}
