namespace SMS_Bridge
{
    using Microsoft.AspNetCore.Builder;
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

        public static void RegisterTestingEndpoints(RouteGroupBuilder smsGatewayApi, IConfiguration configuration)
        {
            smsGatewayApi.MapGet("/test/send-sms", async (IServiceProvider services) =>
            {
                var smsQueueService = services.GetRequiredService<SmsQueueService>();
                var configuration = services.GetRequiredService<IConfiguration>();
                var defaultPhoneNumber = configuration["SmsSettings:TestingPhoneNumber"] ?? "+6421467784";

                var testRequest = new SendSmsRequest(defaultPhoneNumber, "This is a test message during development");

                // Use the queue service like the main endpoint does
                var messageId = await smsQueueService.QueueSms(testRequest);

                return Results.Ok(new Result
                (
                    Success: true,
                    Message: "SMS queued for sending",
                    MessageID: messageId.ToString()
                ));
            });

            smsGatewayApi.MapGet("/test/send-bulk-sms", async (IServiceProvider services) =>
            {
                var provider = services.GetRequiredService<ISmsProvider>();
                var configuration = services.GetRequiredService<IConfiguration>();
                var defaultPhoneNumber = configuration["SmsSettings:TestingPhoneNumber"] ?? "+6421467784";

                var responses = new List<(IResult Result, Guid MessageId)>();
                var totalMessages = 20;

                for (int i = 0; i < totalMessages; i++)
                {
                    var testRequest = new SendSmsRequest(defaultPhoneNumber, $"Bulk test message #{i + 1} of {totalMessages}");
                    var response = await provider.SendSms(testRequest);
                    responses.Add(response);
                }

                // Create a response that includes both results and message IDs
                return Results.Ok(new BulkSmsResponse(
                    Results: responses.Select(r => r.Result),
                    MessageIds: responses.Select(r => r.MessageId.ToString())
                ));
            });

            smsGatewayApi.MapGet("/test/check-send-sms", async (IServiceProvider services) =>
            {
                var smsQueueService = services.GetRequiredService<SmsQueueService>();
                var smsProvider = services.GetRequiredService<ISmsProvider>();
                var configuration = services.GetRequiredService<IConfiguration>();
                var defaultPhoneNumber = configuration["SmsSettings:TestingPhoneNumber"] ?? "+6421467784";

                var testRequest = new SendSmsRequest(defaultPhoneNumber, "This is a test message during development");

                // Queue the SMS
                var messageId = await smsQueueService.QueueSms(testRequest);
                MessageStatus status = MessageStatus.Pending;
                                    await Task.Delay(1000); // Wait for 1 second before the next check

                // Check up to 20 times with a 1-second delay between each check
                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(1000); // Wait for 1 second before the next check

                    status = await smsProvider.GetMessageStatus(messageId);

                    if (status != MessageStatus.Pending) // Assuming Pending is the initial status
                    {
                        break; // Exit the loop if status changes from Pending
                    }

                }
                // Get the status of the SMS


                return Results.Ok(new CheckSendSmsResponse
                (
                    MessageID: messageId,
                    Status: status.ToString()
                ));
            });

        }
    }
}
