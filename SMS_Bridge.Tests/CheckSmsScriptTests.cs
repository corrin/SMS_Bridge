using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using JustRemotePhone.RemotePhoneService;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SMS_Bridge.SmsProviders;
using Xunit;

namespace SMS_Bridge.Tests;

// Runs Scripts/check-sms.ps1 under Windows PowerShell against a fake bridge on a real port.
// -NoBox stops it before the message box; exit code 1 is the path that shows it.
public class CheckSmsScriptTests : IAsyncLifetime
{
    private const string ApiKey = "test-key";
    private WebApplication _bridge = null!;
    private string _baseUrl = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _bridge = builder.Build();

        _bridge.MapGet("/up", (HttpRequest r) => r.Headers["X-API-Key"] == ApiKey
            ? Results.Text("{\"up\":true,\"detail\":\"Call Centre: Connected, phone: Idle\"}", "application/json")
            : Results.Unauthorized());
        _bridge.MapGet("/down", () => Results.Text("{\"up\":false,\"detail\":\"Call Centre: Connected, phone: Unknown\"}", "application/json"));
        _bridge.MapGet("/up-as-text", () => Results.Text("{\"up\":\"true\"}", "application/json"));
        _bridge.MapGet("/error", () => Results.StatusCode(500));
        _bridge.MapGet("/hangs", async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            return Results.Text("{\"up\":true}", "application/json");
        });

        await _bridge.StartAsync();
        _baseUrl = _bridge.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    }

    public async Task DisposeAsync() => await _bridge.DisposeAsync();

    private static string ScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "Scripts", "check-sms.ps1")))
        {
            dir = dir.Parent ?? throw new FileNotFoundException("Scripts/check-sms.ps1 not found above the test output");
        }
        return Path.Combine(dir.FullName, "Scripts", "check-sms.ps1");
    }

    private static async Task<int> RunScript(string url, string key = ApiKey)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptPath(),
                     "-CorrinPhone", "000", "-Url", url, "-ApiKey", key, "-TimeoutSec", "2", "-NoBox" })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(await errors == "", await errors);
        await output;
        return process.ExitCode;
    }

    [Fact]
    public async Task Up_gives_no_box() => Assert.Equal(0, await RunScript($"{_baseUrl}/up"));

    [Fact]
    public async Task Down_gives_the_box() => Assert.Equal(1, await RunScript($"{_baseUrl}/down"));

    [Fact]
    public async Task Http_error_gives_the_box() => Assert.Equal(1, await RunScript($"{_baseUrl}/error"));

    [Fact]
    public async Task Wrong_key_gives_the_box() => Assert.Equal(1, await RunScript($"{_baseUrl}/up", key: "wrong"));

    [Fact]
    public async Task Timeout_gives_the_box() => Assert.Equal(1, await RunScript($"{_baseUrl}/hangs"));

    [Fact]
    public async Task Up_that_is_not_a_boolean_gives_the_box() => Assert.Equal(1, await RunScript($"{_baseUrl}/up-as-text"));

    [Fact]
    public async Task Refused_connection_gives_the_box()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.Equal(1, await RunScript($"http://127.0.0.1:{closedPort}/smsgateway/phone-status"));
    }
}

public class PhoneLinkRuleTests
{
    [Theory]
    [InlineData(ApplicationState.Connected, PhoneState.Idle, true)]
    [InlineData(ApplicationState.Connected, PhoneState.Ringing, true)]
    [InlineData(ApplicationState.Connected, PhoneState.OffHook, true)]
    [InlineData(ApplicationState.Connected, PhoneState.Unknown, false)]
    [InlineData(ApplicationState.Disconnected, PhoneState.Idle, false)]
    [InlineData(ApplicationState.Connecting, PhoneState.Unknown, false)]
    public void Up_only_when_connected_and_the_phone_is_known(ApplicationState application, PhoneState phone, bool up)
    {
        Assert.Equal(up, PhoneLink.IsUp(application, phone));
    }
}
