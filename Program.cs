using Microsoft.Extensions.DependencyInjection;
using SMS_Bridge;
using SMS_Bridge.Models;
using Microsoft.Extensions.Configuration;
using SMS_Bridge.SmsProviders;
using SMS_Bridge.Services;
using System.Diagnostics;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Net.Http;
using System.Reflection.PortableExecutable;
using Microsoft.AspNetCore.DataProtection;
using System.Net;
using System.Linq;
using System.Text.Json.Serialization;

// TODO: Enhancement.
// On QUIT: Log all received messages in the dictionary
// And reload that dictionary on dictionary on startup

var appPort = 5170;
var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(opts =>
    opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase
);

builder.WebHost.UseUrls($"http://*:{appPort}");  // Explicitly sets the port so it can be deployed

Console.WriteLine("Starting SMS_Bridge application...");

if (Environment.UserInteractive == false) // Checks if running as a service
{
    builder.Host.UseWindowsService();
}
else
{
    Console.WriteLine("Running as a console application...");
}

Console.WriteLine("About to start the main try/catch...");

// Define configuredProviderType at a higher scope so it's available in the catch block
SmsProviderType configuredProviderType = SmsProviderType.BuggyCodeNeedsFixing;

try
{
    Logger.Initialize();

    // Configuration setup and validation
    var configuration = builder.Configuration;
    configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

    var smsProvider = configuration["SmsSettings:Provider"]?.ToLower() ??
        throw new InvalidOperationException("SMS provider must be configured in appsettings.json");

    if (!Enum.TryParse(smsProvider, true, out configuredProviderType))
    {
        configuredProviderType = SmsProviderType.BuggyCodeNeedsFixing;
    }

    Configuration fileConfiguration = new Configuration();
    var apiKey = fileConfiguration.GetApiKey();

    var productionMachines = configuration.GetSection("SmsSettings:ProductionMachines")
        .Get<string[]>() ?? Array.Empty<string>();
    var isDebugMode = !productionMachines.Contains(Environment.MachineName);

    if (isDebugMode && string.IsNullOrEmpty(configuration["SmsSettings:TestingPhoneNumber"]))
    {
        Logger.LogWarning(
            provider: configuredProviderType,
            eventType: "Configuration",
            details: "Debug mode is enabled but no testing phone number is configured",
            SMSBridgeID: default
        );
    }

    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
    });

    // Initialize the appropriate SMS provider based on configuration
    builder.Services.AddSingleton<ISmsProvider>(services =>
    {
        var httpClient = new HttpClient();
        var apiKey = configuration["SmsSettings:Providers:etxt:ApiKey"]!;
        var apiSecret = configuration["SmsSettings:Providers:etxt:ApiSecret"]!;

        // We already parsed the provider type earlier, use it here
        ISmsProvider provider = configuredProviderType switch
        {
            SmsProviderType.JustRemotePhone => new JustRemotePhoneSmsProvider(),
            SmsProviderType.Diafaan => new DiafaanSmsProvider(),
            SmsProviderType.ETxt => CreateETxtProvider(configuration, fileConfiguration, httpClient, apiKey, apiSecret),
            _ => throw new InvalidOperationException($"Unsupported SMS provider: {smsProvider}") // This case should not be reached if parsing is successful
        };

        Logger.LogInfo(
            provider: configuredProviderType,
            eventType: "Configuration",
            details: $"Initialized SMS provider: {smsProvider}",
            SMSBridgeID: default
        );

        return provider;
    });

    builder.Services.AddSingleton<SmsQueueService>();

    var app = builder.Build();

    // Security middleware to validate API keys for non-localhost requests
    var smsGatewayApi = app.MapGroup("/smsgateway")
        .AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;

            if (http.Request.Host.Host.ToLower() is not ("localhost" or "127.0.0.1"))
            {
                if (!http.Request.Headers.TryGetValue("X-API-Key", out var sent)
                 || sent != apiKey)
                {
                    Logger.LogWarning(
                        provider: configuredProviderType,
                        eventType: "UnauthorizedAccess",
                        details: $"Missing/invalid global API key from {http.Connection.RemoteIpAddress}",
                        SMSBridgeID: default
                    );
                    return Results.Unauthorized();
                }
            }

            return await next(context);
        });
    
    // Endpoint for sending SMS messages
    smsGatewayApi.MapPost("/send-sms", (SendSmsRequest request, SmsQueueService smsQueueService) =>
    {
        try
        {
            var destinationNumber = request.PhoneNumber;

            if (isDebugMode)
            {
                var allowedNumbers = configuration.GetSection("SmsSettings:AllowedTestNumbers")
                    .Get<string[]>() ?? Array.Empty<string>();

                if (!allowedNumbers.Select(n => n.TrimStart('+')).Contains(destinationNumber.TrimStart('+')))
                {
                    var testNumber = configuration["SmsSettings:TestingPhoneNumber"];
                    if (!string.IsNullOrEmpty(testNumber))
                    {
                        Logger.LogInfo(
                            provider: configuredProviderType,
                            eventType: "DebugRedirect",
                            details: $"Redirecting SMS from {destinationNumber} to {testNumber}"
                        );
                        destinationNumber = testNumber;
                    }
                }
            }

            var modifiedRequest = request with { PhoneNumber = destinationNumber };

            var smsBridgeID = smsQueueService.QueueSms(modifiedRequest);
            return Results.Json(
                new Result(
                    Success: true,
                    Message: "SMS queued for sending",
                    SMSBridgeID: smsBridgeID.Value.ToString()
                ),
                AppJsonSerializerContext.Default.Result
            );
        }
        catch (Exception ex)
        {
            return Results.Problem("Failed to queue SMS for sending: " + ex.Message);
        }
    });

    // Note SMSBridgeID is the SMSBridgeID - it's the ID we use when talking to external systems (OD mostly)
    // and providerMessageID is the ID used internally by providers (e.g. eTXT, Diafaan, etc.)
    smsGatewayApi.MapGet("/sms-status/{smsBridgeId:guid}", 
    async (Guid smsBridgeId, ISmsProvider smsProvider) =>
    {
        if (smsProvider is not JustRemotePhoneSmsProvider provider)
            return Results.BadRequest("Unsupported SMS provider");

        var status = await provider.GetMessageStatus(new SmsBridgeId(smsBridgeId));

        return Results.Json(
            new MessageStatusResponse(
                MessageID: smsBridgeId.ToString(),
                Status:    status
            ),
            AppJsonSerializerContext.Default.MessageStatusResponse
        );
    });

    smsGatewayApi.MapGet("/received-sms", async (ISmsProvider smsProvider) =>
    {
        try
        {
            if (smsProvider is not JustRemotePhoneSmsProvider provider)
                return Results.BadRequest("Unsupported SMS provider");

            var messages = await provider.GetReceivedMessages();
            var flat = messages.Select(m => new {
                messageID         = m.MessageID.Value,
                providerMessageID = m.ProviderMessageID.Value,
                fromNumber        = m.FromNumber,
                messageText       = m.MessageText,
                receivedAt        = m.ReceivedAt
            });

            return Results.Json(flat);
        }
        catch (Exception ex)
        {
            Logger.LogError(
                provider: configuredProviderType,
                eventType: "GetReceivedMessagesEndpoint",
                details: $"Error in received-sms endpoint: {ex.Message}"
            );
            return Results.Problem(
                detail: "An error occurred while retrieving messages",
                statusCode: 500,
                title: "Internal Server Error"
            );
        }
    });

    smsGatewayApi.MapGet("/recent-status-values", async (ISmsProvider smsProvider) =>
    {
        SmsProviderType CurrentSMSProvider = smsProvider switch
        {
            JustRemotePhoneSmsProvider => SmsProviderType.JustRemotePhone,
            ETxtSmsProvider => SmsProviderType.ETxt,
            DiafaanSmsProvider => SmsProviderType.Diafaan,
            _ => SmsProviderType.BuggyCodeNeedsFixing // Fallback for unsupported providers
        };
        try
        {
            if (smsProvider is not JustRemotePhoneSmsProvider provider)
            {
                return Results.BadRequest("Unsupported SMS provider");
            }

            var statuses = await provider.GetRecentMessageStatuses();
            return Results.Json(
                statuses,
                AppJsonSerializerContext.Default.IEnumerableMessageStatusRecord
            );

        }
        catch (Exception ex)
        {
            Logger.LogError(
                provider: CurrentSMSProvider,
                eventType: "GetReceivedMessagesEndpoint",
                details: $"Error in received-sms endpoint: {ex.Message}",
                SMSBridgeID: default
            );
            return Results.Problem(
                detail: "An error occurred while retrieving messages",
                statusCode: 500,
                title: "Internal Server Error"
            );
        }
    });

    smsGatewayApi.MapGet("/delete-received-sms/{messageId}", async (string messageId, ISmsProvider smsProvider) =>
    {
        if (!Guid.TryParse(messageId, out var smsBridgeIdGuid))
        {
            return Results.BadRequest("Invalid message ID format");
        }
        var smsBridgeId = new SmsBridgeId(smsBridgeIdGuid);

        if (smsProvider is not JustRemotePhoneSmsProvider provider)
        {
            return Results.BadRequest("Unsupported SMS provider");
        }

        // BUGGY.  Work out the call
        var result = await provider.DeleteReceivedMessage(smsBridgeId);
        if (result.Deleted)
        {
            return Results.Ok(result);
        }
        return Results.NotFound(result);
    });

    smsGatewayApi.MapGet("/gateway-status", () =>
    {
        return Results.Ok("Gateway is up and running");
    });

    smsGatewayApi.MapGet("/debug-status", () =>
    {
        var resp = new DebugStatusResponse(
            IsDebugMode: isDebugMode,
            TestingPhoneNumber: configuration["SmsSettings:TestingPhoneNumber"] ?? "",
            AllowedTestNumbers: configuration.GetSection("SmsSettings:AllowedTestNumbers")
                .Get<string[]>() ?? Array.Empty<string>()
        );
        return Results.Ok(resp);
    });

    if (true)  // We used to restrict to Debug Mode but it complicated PVT
    {
        var testingApi = app.MapGroup("/smsgateway/test");

        Testing.RegisterTestingEndpoints(testingApi, configuration);

        Logger.LogInfo(
            provider: configuredProviderType,
            eventType: "Configuration",
            details: "Debug mode enabled - test endpoints registered"
        );
    }

    if (true) // both dev and prod have webhook support
    {
        var webhooksApi = app.MapGroup("/smsgateway/webhooks");
        Webhooks.RegisterWebhookEndpoints(webhooksApi, builder.Configuration);

    }

    Logger.LogInfo(
        provider: configuredProviderType,
        eventType: "Configuration",
        details: $"SMS Gateway initialized in {(isDebugMode ? "debug" : "production")} mode"
    );

    app.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"Application failed to start. About to log: {ex}");

    Logger.LogCritical(
        provider: configuredProviderType,
        eventType: "StartupFailure",
        details: $"Application failed to start: {ex.Message}"
    );
    throw;
}


// Factory method to create ETxtSmsProvider with required dependencies
static ETxtSmsProvider CreateETxtProvider(IConfiguration configuration, Configuration fileConfiguration, HttpClient httpClient, string apiKey, string apiSecret)
{
    return new ETxtSmsProvider(httpClient, apiKey, apiSecret, configuration, fileConfiguration);
}