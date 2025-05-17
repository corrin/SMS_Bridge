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

// TODO: Enhancement.
// On QUIT: Log all received messages in the dictionary
// And reload that dictionary on startup

var appPort = 5170;
var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls($"http://*:{appPort}");  // Explicitly sets the port so it can be deployed

// Your existing code starts here
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

try
{
    Logger.Initialize();

    // Load and validate critical configuration
    var configuration = builder.Configuration;
    configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

    var smsProvider = configuration["SmsSettings:Provider"]?.ToLower() ??
        throw new InvalidOperationException("SMS provider must be configured in appsettings.json");

    Configuration fileConfiguration = new Configuration();
    var apiKey = fileConfiguration.GetApiKey();

    // Validate production machines configuration
    var productionMachines = configuration.GetSection("SmsSettings:ProductionMachines")
        .Get<string[]>() ?? Array.Empty<string>();
    var isDebugMode = !productionMachines.Contains(Environment.MachineName);

    // Validate test mode configuration if in debug mode
    if (isDebugMode && string.IsNullOrEmpty(configuration["SmsSettings:TestingPhoneNumber"]))
    {
        Logger.LogWarning(
            provider: "Startup",
            eventType: "Configuration",
            messageID: "",
            details: "Debug mode is enabled but no testing phone number is configured"
        );
    }

    // Configure JSON serialization
    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
    });

    // Register SMS provider
    builder.Services.AddSingleton<ISmsProvider>(services =>
    {
        var httpClient = new HttpClient();
        var etxtApiKey = configuration["ETxt:ApiKey"]!;
        var etxtApiSecret = configuration["ETxt:ApiSecret"]!;

        ISmsProvider provider = smsProvider switch
        {
            "justremotephone" => new JustRemotePhoneSmsProvider(),
            "diafaan" => new DiafaanSmsProvider(),
            "etxt" => CreateETxtProvider(configuration),
            _ => throw new InvalidOperationException($"Unsupported SMS provider: {smsProvider}")
        };

        Logger.LogInfo(
            provider: "Startup",
            eventType: "Configuration",
            messageID: "",
            details: $"Initialized SMS provider: {smsProvider}"
        );

        return provider;
    });

    // Register services
    builder.Services.AddSingleton<SmsQueueService>();

    var app = builder.Build();

    // API Key validation middleware
    var smsGatewayApi = app.MapGroup("/smsgateway")
        .AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;

            if (httpContext.Request.Host.Host.ToLower() is "localhost" or "127.0.0.1")
            {
                // Allow localhost access without API key validation
                return await next(context);
            }

            //// Check if the API key header exists
            //if (!httpContext.Request.Headers.TryGetValue("X-API-Key", out var requestApiKey))
            //{
            //    Logger.LogWarning(
            //        provider: "Security",
            //        eventType: "UnauthorizedAccess",
            //        messageID: "",
            //        details: $"Unauthorized access attempt from {httpContext.Connection.RemoteIpAddress}: Missing API Key"
            //    );
            //    return Results.Unauthorized();
            //}

            //// Validate the API key
            //if (requestApiKey != apiKey)
            //{
            //    Logger.LogWarning(
            //        provider: "Security",
            //        eventType: "UnauthorizedAccess",
            //        messageID: "",
            //        details: $"Unauthorized access attempt from {httpContext.Connection.RemoteIpAddress}: Invalid API Key"
            //    );
            //    return Results.Unauthorized();
            //}

            // Proceed with the request
            return await next(context);
        });

    // Send SMS using the configured provider
    smsGatewayApi.MapPost("/send-sms", async (SendSmsRequest request, SmsQueueService smsQueueService) =>
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
                            provider: "SmsGateway",
                            eventType: "DebugRedirect",
                            messageID: "",
                            details: $"Redirecting SMS from {destinationNumber} to {testNumber}"
                        );
                        destinationNumber = testNumber;
                    }
                }
            }

            var modifiedRequest = request with { PhoneNumber = destinationNumber };

            var messageID = smsQueueService.QueueSms(modifiedRequest);
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

    // Note externalID is the SMSBridgeID - it's the ID we use when talking to external systems (OD mostly)
    // and internalID is the ID used internally by providers (e.g. eTXT, Diafaan, etc.)
    smsGatewayApi.MapGet("/sms-status/{messageId}", async (string messageId, ISmsProvider smsProvider, SmsQueueService smsQueueService) =>
    {
        if (!Guid.TryParse(messageId, out var externalId))
        {
            return Results.BadRequest("Invalid message ID format");
        }

        if (!smsQueueService.TryGetInternalId(externalId, out var internalId))
        {
            return Results.NotFound("Unknown message ID");
        }

        var status = await smsProvider.GetMessageStatus(internalId);
        return Results.Ok(new MessageStatusResponse
        (
            MessageID: messageId,
            Status: status
        ));
    });

    smsGatewayApi.MapGet("/received-sms", async (ISmsProvider smsProvider) =>
    {
        try
        {
            if (smsProvider is not JustRemotePhoneSmsProvider provider)
            {
                return Results.BadRequest("Unsupported SMS provider");
            }

            var messages = await provider.GetReceivedMessages();
            var json = JsonSerializer.Serialize(messages, AppJsonSerializerContext.Default.IEnumerableReceiveSmsRequest);

            return Results.Ok(messages);
        }
        catch (Exception ex)
        {
            Logger.LogError(
                provider: "API",
                eventType: "GetReceivedMessagesEndpoint",
                messageID: "",
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
        try
        {
            if (smsProvider is not JustRemotePhoneSmsProvider provider)
            {
                return Results.BadRequest("Unsupported SMS provider");
            }

            var statuses = await provider.GetRecentMessageStatuses();
            var json = JsonSerializer.Serialize(statuses, AppJsonSerializerContext.Default.IEnumerableMessageStatusRecord);

            return Results.Ok(statuses);
        }
        catch (Exception ex)
        {
            Logger.LogError(
                provider: "API",
                eventType: "GetReceivedMessagesEndpoint",
                messageID: "",
                details: $"Error in received-sms endpoint: {ex.Message}"
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
            provider: "Startup",
            eventType: "Configuration",
            messageID: "",
            details: "Debug mode enabled - test endpoints registered"
        );
    }

    Logger.LogInfo(
        provider: "Startup",
        eventType: "Configuration",
        messageID: "",
        details: $"SMS Gateway initialized in {(isDebugMode ? "debug" : "production")} mode"
    );

    app.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"Application failed to start. About to log: {ex}");

    Logger.LogCritical(
        provider: "Startup",
        eventType: "StartupFailure",
        messageID: "",
        details: $"Application failed to start: {ex.Message}"
    );
    throw;
}

static ISmsProvider CreateETxtProvider(IConfiguration configuration)
{
    var httpClient = new HttpClient();
    var apiKey = configuration["ETxt:ApiKey"]!;
    var apiSecret = configuration["ETxt:ApiSecret"]!;
    var callbackBaseUrl = Environment.UserInteractive
        ? "https://sms-bridge.au.ngrok.io"
        : "https://office.massey-smiles.co.nz:5170";

    RegisterETxtWebhook(httpClient, apiKey, apiSecret, callbackBaseUrl).GetAwaiter().GetResult();
    return new ETxtSmsProvider(httpClient, apiKey, apiSecret);
}

static async Task RegisterETxtWebhook(HttpClient httpClient, string apiKey, string apiSecret, string callbackBaseUrl)
{
    var body = new
    {
        url = $"{callbackBaseUrl}/smsgateway/receive-reply",
        method = "POST",
        encoding = "JSON",
        events = new[] { "RECEIVED_SMS" },
        template = "{\"replyId\":\"$moId\",\"messageId\":\"$mtId\",\"replyContent\":\"$moContent\",\"sourceAddress\":\"$sourceAddress\",\"destinationAddress\":\"$destinationAddress\",\"timestamp\":\"$receivedTimestamp\"}",
        read_timeout = 5000,
        retries = 3,
        retry_delay = 30
    };

    var request = new HttpRequestMessage(HttpMethod.Post, "https://api.etxtservice.co.nz/v1/webhooks/messages")
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    request.Headers.Authorization = new AuthenticationHeaderValue(
        "Basic",
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{apiKey}:{apiSecret}"))
    );

    var resp = await httpClient.SendAsync(request);
    var msg = await resp.Content.ReadAsStringAsync();

    if (resp.IsSuccessStatusCode)
    {
        Logger.LogInfo("ETxt", "WebhookRegister", "", "Webhook registration succeeded.");
    }
    else
    {
        Logger.LogWarning("ETxt", "WebhookRegister", "", $"Webhook registration failed: {resp.StatusCode} - {msg}");
    }
}