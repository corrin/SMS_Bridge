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
// -NoBox stops it before the message box; exit code 1 is the path that shows it, 2 is a reminder
// held back because the box was dismissed under an hour ago.
public class CheckSmsScriptTests : IAsyncLifetime
{
    private const string ApiKey = "test-key";
    private WebApplication _bridge = null!;
    private string _baseUrl = null!;
    private readonly string _tempDir = Directory.CreateTempSubdirectory("check-sms-").FullName;
    private string Marker => Path.Combine(_tempDir, "SMS_Bridge", "warned-at");

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

    public async Task DisposeAsync()
    {
        await _bridge.DisposeAsync();
        Directory.Delete(_tempDir, recursive: true);
    }

    private static string ScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "Scripts", "check-sms.ps1")))
        {
            dir = dir.Parent ?? throw new FileNotFoundException("Scripts/check-sms.ps1 not found above the test output");
        }
        return Path.Combine(dir.FullName, "Scripts", "check-sms.ps1");
    }

    private async Task<int> RunScript(string url, string key = ApiKey, string? marker = null)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptPath(),
                     "-CorrinPhone", "000", "-Url", url, "-ApiKey", key, "-TimeoutSec", "2", "-MarkerPath", marker ?? Marker, "-NoBox" })
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
    public async Task First_down_writes_the_marker_after_deciding_to_warn()
    {
        Assert.False(File.Exists(Marker));
        Assert.Equal(1, await RunScript($"{_baseUrl}/down"));
        Assert.True(File.Exists(Marker));
    }

    [Fact]
    public async Task Marker_left_from_a_dismissal_over_an_hour_ago_still_warns()
    {
        // A box killed by sign-out or reboot writes nothing, so the newest marker is the last real dismissal.
        Directory.CreateDirectory(Path.GetDirectoryName(Marker)!);
        File.WriteAllText(Marker, "");
        File.SetLastWriteTime(Marker, DateTime.Now.AddMinutes(-90));
        Assert.Equal(1, await RunScript($"{_baseUrl}/down"));
    }

    [Fact]
    public async Task Down_again_under_an_hour_after_dismissal_gives_no_box()
    {
        Assert.Equal(1, await RunScript($"{_baseUrl}/down"));
        File.SetLastWriteTime(Marker, DateTime.Now.AddMinutes(-59));
        Assert.Equal(2, await RunScript($"{_baseUrl}/down"));
    }

    [Fact]
    public async Task Down_an_hour_after_dismissal_gives_the_box_and_restarts_the_hour()
    {
        Assert.Equal(1, await RunScript($"{_baseUrl}/down"));
        File.SetLastWriteTime(Marker, DateTime.Now.AddMinutes(-61));
        Assert.Equal(1, await RunScript($"{_baseUrl}/down"));
        Assert.Equal(2, await RunScript($"{_baseUrl}/down"));
    }

    [Fact]
    public async Task Up_clears_the_marker_so_the_next_down_warns_at_once()
    {
        Assert.Equal(1, await RunScript($"{_baseUrl}/down"));
        Assert.Equal(0, await RunScript($"{_baseUrl}/up"));
        Assert.False(File.Exists(Marker));
        Assert.Equal(1, await RunScript($"{_baseUrl}/down"));
    }

    [Fact]
    public async Task Unreachable_bridge_is_reminded_like_any_other_down()
    {
        Assert.Equal(1, await RunScript($"{_baseUrl}/error"));
        Assert.Equal(2, await RunScript($"{_baseUrl}/hangs"));
    }

    [Fact]
    public async Task Unwritable_marker_still_gives_the_box_every_time()
    {
        // A file where the marker's folder should be makes every write fail.
        var blocker = Path.Combine(_tempDir, "not-a-folder");
        File.WriteAllText(blocker, "");
        var marker = Path.Combine(blocker, "warned-at");

        Assert.Equal(1, await RunScript($"{_baseUrl}/down", marker: marker));
        Assert.Equal(1, await RunScript($"{_baseUrl}/down", marker: marker));
    }

    [Fact]
    public async Task Marker_dated_in_the_future_does_not_hold_the_box_back()
    {
        Assert.Equal(1, await RunScript($"{_baseUrl}/down"));
        File.SetLastWriteTime(Marker, DateTime.Now.AddHours(5));
        Assert.Equal(1, await RunScript($"{_baseUrl}/down"));
    }

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
