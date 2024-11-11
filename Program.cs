using Microsoft.Extensions.DependencyInjection;
using SMS_Bridge;
using SMS_Bridge.Models;
using Microsoft.Extensions.Configuration;
using SMS_Bridge.SmsProviders;
using SMS_Bridge.Services;


var builder = WebApplication.CreateSlimBuilder(args);

// Load configuration from appsettings.json
var configuration = builder.Configuration;
configuration.AddJsonFile("appsettings.json");

// Use source generation for JSON serialization with the custom context
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

// Read the configured provider from settings
var providerName = configuration["SmsSettings:Provider"]?.ToLower();

// Register the configured SMS provider based on the appsettings.json configuration
switch (providerName)
{
    case "justremotephone":
        builder.Services.AddSingleton<ISmsProvider, JustRemotePhoneSmsProvider>(); // Register as Singleton
        break;
    case "diafaan":
        builder.Services.AddSingleton<ISmsProvider, DiafaanSmsProvider>(); // Register as Singleton
        break;
    case "etxt":
        builder.Services.AddSingleton<ISmsProvider, ETxtSmsProvider>(); // Register as Singleton
        break;
    default:
        throw new InvalidOperationException($"Unsupported SMS provider: {providerName}");
}

builder.Services.AddSingleton<SmsQueueService>();


var app = builder.Build();

// Define the endpoints with `/api/smsgateway` prefix
var smsGatewayApi = app.MapGroup("/smsgateway");

// Send SMS using the configured provider
app.MapPost("/send-sms", async (SendSmsRequest request, SmsQueueService smsQueueService) =>
{
    try
    {
        var messageID = await smsQueueService.QueueSms(request);
        return Results.Ok(new Result
        (
            Success: true,
            Message: "SMS queued for sending",
            MessageID: messageID.ToString()
        ));
    }
    catch (Exception ex)
    {
        return Results.Problem("Failed to queue SMS for sending: " + ex.Message);
    }
});

smsGatewayApi.MapGet("/sms-status/{messageId}", async (string messageId, ISmsProvider smsProvider) =>
{
    if (!Guid.TryParse(messageId, out var guid))
    {
        return Results.BadRequest("Invalid message ID format");
    }

    var status = await smsProvider.GetMessageStatus(guid);
    return Results.Ok(new MessageStatusResponse
    (
        MessageID: messageId,
        Status: status
    ));

});

smsGatewayApi.MapGet("/received-sms", async (ISmsProvider smsProvider) =>
{
    if (smsProvider is not JustRemotePhoneSmsProvider provider)
    {
        return Results.BadRequest("Unsupported SMS provider");
    }

    var messages = await provider.GetReceivedMessages();
    return Results.Ok(messages);
});

// Note: Using MapGet instead of MapDelete for SMS gateway compatibility.
// While DELETE would be more RESTful, existing SMS gateways (eTXT, JustRemote) 
// commonly implement message deletion via GET endpoints for broader compatibility.
smsGatewayApi.MapGet("/delete-received-sms/{messageId}", async (string messageId, ISmsProvider smsProvider) =>
{
    if (!Guid.TryParse(messageId, out var guid))
    {
        return Results.BadRequest("Invalid message ID format");
    }

    if (smsProvider is not JustRemotePhoneSmsProvider provider)
    {
        return Results.BadRequest("Unsupported SMS provider");
    }

    var result = await provider.DeleteReceivedMessage(guid);
    if (result.Deleted)
    {
        return Results.Ok(result);
    }
    return Results.NotFound(result);
});



// Gateway Status Endpoint
smsGatewayApi.MapGet("/gateway-status", () =>
{
    return Results.Ok("Gateway is up and running");
});

Testing.RegisterTestingEndpoints(smsGatewayApi, configuration);

app.Run();
