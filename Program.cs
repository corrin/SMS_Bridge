using System.Text.Json.Serialization;
using JustRemotePhone.RemotePhoneService;

var builder = WebApplication.CreateSlimBuilder(args);

// Use source generation for JSON serialization, as per the original todo example
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

var app = builder.Build();

// Define the endpoints with `/api/smsgateway` prefix
var smsGatewayApi = app.MapGroup("/api/smsgateway");

// 1. Send SMS
smsGatewayApi.MapPost("/send-sms", (SendSmsRequest request) =>
{
    try
    {
        // Instantiate the JustRemotePhone application
        Application justRemoteApp = new Application("SMS Bridge Service");

        // Connect to JustRemotePhone and send the SMS
        justRemoteApp.BeginConnect(true);
        justRemoteApp.Phone.Call(request.PhoneNumber);

        return Results.Ok($"SMS sent successfully to {request.PhoneNumber}");
    }
    catch (Exception ex)
    {
        return Results.Problem($"An error occurred while sending SMS: {ex.Message}");
    }
});

// 2. Receive SMS
smsGatewayApi.MapPost("/receive-sms", (ReceiveSmsRequest request) =>
{
    // Logic for handling incoming SMS (placeholder for now)
    return Results.Ok($"Received SMS from {request.From} to {request.To} with message: {request.MessageBody}");
});

// 3. SMS Status
smsGatewayApi.MapGet("/sms-status/{messageId}", (string messageId) =>
{
    // Placeholder logic to check the status of a specific message
    return Results.Ok($"Status for message {messageId}: Delivered (example)");
});

// 4. Gateway Status
smsGatewayApi.MapGet("/gateway-status", () =>
{
    return Results.Ok("Gateway is up and running");
});

app.Run();

// Define the records for request payloads
public record SendSmsRequest(string PhoneNumber, string Message);

public record ReceiveSmsRequest(string From, string To, string MessageBody);

[JsonSerializable(typeof(SendSmsRequest[]))]
[JsonSerializable(typeof(ReceiveSmsRequest))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}
