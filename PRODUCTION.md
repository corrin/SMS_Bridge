# Production installation

## Layout and prerequisites

```text
Principle -> HTTPS office.massey-smiles.co.nz:443 -> Caddy
          -> HTTP 127.0.0.1:5170 -> SMS_Bridge -> JustRemotePhone -> phone
```

The public office endpoint identifies itself as Caddy. The repository's diagnostic script targets `RECEPTION-AIO`. The deployed Caddyfile, service account, certificate storage location, and router rules are not stored here. Inspect those on the production machine before changing an existing installation. The example below assumes Caddy and the bridge run on the same Windows host; it is not a copy of the deployed configuration.

Install or provide:

- Windows x64, with administrator access for services and firewall configuration.
- JustRemotePhone Call Centre, its SDK, and a paired phone capable of sending and receiving SMS. Verify connectivity under the account/session that will run the bridge.
- The complete published SMS_Bridge release. Framework-dependent builds require the .NET 8 ASP.NET Core Runtime for Windows x64; self-contained builds include the runtime.
- [Caddy for Windows amd64](https://caddyserver.com/download), using the standard distribution.
- Access to DNS, the office router/firewall, and Principle's webhook settings and API credentials.

Development uses ngrok instead of Caddy; see [TESTING.md](TESTING.md). Both environments use HTTP port 5170 inside the bridge. Neither `ASPNETCORE_ENVIRONMENT` nor `SmsSettings.EnableDebugMode` selects a public domain. Development and production on the same machine share `C:\ProgramData\SMS_Bridge\install-settings.json`.

## Install and configure SMS_Bridge

To build from source, install the .NET 8 SDK on the build machine. The project references `Newtonsoft.Json.dll` from `C:\Program Files (x86)\Open Dental` and `RemotePhoneService.dll` from `C:\Program Files (x86)\JustPhone\CallCenter\SDK\bin`; these dependencies must exist. From the repository directory:

```powershell
dotnet publish .\SMS_Bridge.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\production
```

Copy the complete publish output, including dependency DLLs and `appsettings.json`, to `C:\Program Files\SMS_Bridge`. Stop an existing bridge before replacing its binaries and retain the previous release for rollback.

Create `C:\ProgramData\SMS_Bridge` and copy [install-settings.example.json](install-settings.example.json) there as `install-settings.json`. For JustRemotePhone with Principle, use these installation overrides with real values:

```json
{
  "BRIDGE_API_KEY": "<unique-bridge-api-key>",
  "SmsSettings": {
    "Provider": "JustRemotePhone",
    "EnableDebugMode": false
  },
  "Principle": {
    "Enabled": true,
    "PracticeIds": ["<production-practice-id>"],
    "API_KEY": "<principle-api-key>",
    "WEBHOOK_SECRET": "<principle-webhook-signing-secret>"
  }
}
```

Every override key must also be declared in `appsettings.json`; startup validates this. Restrict secrets to administrators and the bridge's runtime account. That account needs read/execute access to the installation and modify access to `C:\ProgramData\SMS_Bridge` for logs, caches, and message records.

`Hosting.AppBaseUrl` is used by the eTXT integration. It does not configure DNS, Caddy, or Principle's webhook URL.

Start Call Centre, then start the bridge from its installation directory:

```powershell
Set-Location 'C:\Program Files\SMS_Bridge'
.\SMS_Bridge.exe
```

In another terminal, verify `curl.exe -H "X-API-Key: <BRIDGE_API_KEY>" http://localhost:5170/smsgateway/gateway-status` returns the build identity; without the header it returns 401. Check `C:\ProgramData\SMS_Bridge\od_logs` for startup failures and phone connection errors.

## DNS, networking, and HTTPS

1. Point the DNS A record for `office.massey-smiles.co.nz` at the office public IP. If an AAAA record exists, IPv6 must also reach Caddy.
2. Forward inbound TCP 80 and 443 on the router to the Caddy host and allow them through Windows Firewall. Keep these routes available for certificate validation and renewal. Caddy also needs outbound HTTPS and DNS resolution.
3. Keep port 5170 off the public Internet. The bridge listens on `http://localhost:5170` (`Hosting.ListenUrl`), so only processes on the same machine can reach it. Keep Caddy's administration port 2019 private.
4. Place `caddy.exe` in `C:\caddy`. For a new installation, save this example as `C:\caddy\Caddyfile`. For an existing site, merge the route into its existing site block, preserving other applications and settings.

```caddyfile
office.massey-smiles.co.nz {
    handle /smsgateway/* {
        reverse_proxy 127.0.0.1:5170
    }

    handle {
        respond "Not found" 404
    }
}
```

`handle` preserves the `/smsgateway` prefix required by the bridge; `handle_path` would strip it. If Caddy runs on another machine, use the bridge's internal address instead of loopback, set `Hosting.ListenUrl` in `install-settings.json` to that address (e.g. `http://192.168.192.125:5170`), and firewall port 5170 to the Caddy host only. See Caddy's [reverse proxy reference](https://caddyserver.com/docs/caddyfile/directives/reverse_proxy).

Caddy's [automatic HTTPS](https://caddyserver.com/docs/automatic-https) obtains and renews certificates for the public hostname and redirects HTTP to HTTPS. SMS_Bridge does not load a certificate. Caddy needs persistent, writable certificate storage under its runtime account. Inspect the active data directory on the host and preserve it during upgrades; a foreground run under your login can use different storage from the service.

## Automatic startup

Validate Caddy's configuration:

```powershell
C:\caddy\caddy.exe validate --config C:\caddy\Caddyfile --adapter caddyfile
```

For a new Caddy installation, run in elevated PowerShell, following Caddy's [Windows service instructions](https://caddyserver.com/docs/running#windows-service):

```powershell
sc.exe create caddy start= auto binPath= "C:\caddy\caddy.exe run --config C:\caddy\Caddyfile --adapter caddyfile"
sc.exe failure caddy reset= 86400 actions= restart/5000/restart/10000/restart/30000
sc.exe start caddy
```

Inspect an existing service with `sc.exe qc caddy` instead of creating another. Its account needs read/execute access to Caddy and its configuration, and write access to its data directory. After configuration edits, validate and apply them with `C:\caddy\caddy.exe reload --config C:\caddy\Caddyfile --adapter caddyfile`.

SMS_Bridge supports Windows service hosting, but its startup validation opens `appsettings.json` relative to the process working directory. A bare `sc.exe create` installation does not set that directory and must not be assumed to work. For a console installation using Call Centre's interactive session, configure Windows Task Scheduler:

- Account: the user running Call Centre; select **Run only when user is logged on**.
- Trigger: at that user's logon. Arrange for Call Centre to start in that session too.
- Program: `C:\Program Files\SMS_Bridge\SMS_Bridge.exe`.
- Start in: `C:\Program Files\SMS_Bridge`.
- Settings: restart on failure, disable the execution time limit, and prevent overlapping instances.

This arrangement requires user logon after reboot and an active logged-in session. Unattended service operation before logon must address the working directory and verify JustRemotePhone connectivity from the service account/session; the repository does not supply a complete installer for that arrangement. Inspect any existing `SMS_Bridge_Service` and scheduled tasks before adding startup entries. Run only one bridge instance on port 5170.

## SMS warning on reception

Principle doesn't report texts that fail to go out, and the bridge can't report its own crash. So a scheduled task on reception, separate from the bridge, runs `Scripts\check-sms.ps1` every 5 minutes. If `phone-status` answers anything but `"up": true`, the task shows reception a warning box. That covers the phone being off or disconnected, Call Centre being closed, and the bridge being stopped, crashed or hung. The box asks staff to restart the phone, then ring Corrin. It appears on the first failed check. Once dismissed, it reappears at most once an hour while texts still aren't going out, counted from the dismissal. The time of the last dismissal is kept in `%LOCALAPPDATA%\SMS_Bridge\warned-at`, and a good check deletes it, so the next outage warns at once. If that file can't be read or written, the box shows on every check. The task never opens a second box while one is still open.

The script ships in the release as `C:\Program Files\SMS_Bridge\Scripts\check-sms.ps1` and reads `BRIDGE_API_KEY` from `install-settings.json`. Signed in as the user who runs Call Centre, in PowerShell (no elevation needed), with Corrin's number in place of `<number>`:

```powershell
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "C:\Program Files\SMS_Bridge\Scripts\check-sms.ps1" -CorrinPhone "<number>"'
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 5)
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit 0
Register-ScheduledTask -TaskName 'SMS warning' -Action $action -Trigger $trigger -Principal $principal -Settings $settings
```

Keep the quotes around the number. A number with spaces splits into several arguments without them, and the task then fails silently.

## Principle setup and acceptance checks

Set Principle's webhook URL to `https://office.massey-smiles.co.nz/smsgateway/webhooks/principle`, enable `message.created` events, and use the same signing secret as `Principle.WEBHOOK_SECRET`. The webhook validates `X-Principle-Signature` and `X-Principle-Timestamp`; it does not require the general gateway's `X-API-Key` header.

1. Verify the local gateway-status endpoint as above. With the same header, `http://localhost:5170/smsgateway/phone-status` should return `"up": true`.
2. From outside the office, request `https://office.massey-smiles.co.nz/smsgateway/gateway-status` with `X-API-Key` set to `BRIDGE_API_KEY`. Expect HTTP 200 and the build identity, with a trusted certificate. Without that header, expect 401.
3. Send an SMS from Principle to a controlled test patient/phone, then reply. Verify both directions and the `PrincipleOutboundSmsQueued` and `PrincipleInboundSmsCreated` log events.
4. Reboot and verify startup, including user logon for the interactive task setup. Confirm the phone reconnects and both Caddy and the bridge recover.
5. SMS warning: with everything working, no box appears within 5 minutes. Stop the bridge: the box appears within 5 minutes. Dismiss it: no box reappears for an hour. Start the bridge again, wait for one good check, then turn the phone's Wi-Fi off: the box appears within 5 minutes, without waiting out the hour. Turn Wi-Fi back on and check `phone-status` shows `"up": true` again.

Opening the webhook in a browser sends GET; the endpoint requires POST. Test it with a signed Principle event. Debug mode redirects recipients in the general `/send-sms` endpoint, but Principle webhooks queue messages directly: do not rely on debug mode to redirect Principle test messages.

## Troubleshooting and upgrades

| Symptom | Check |
| --- | --- |
| HTTPS fails or certificate is untrusted | DNS, forwarding, firewall, Caddy status, certificate issuance errors, and data-directory permissions. |
| Caddy returns 502 | Bridge process, local port 5170, startup logs, and configured upstream address. A successful TLS handshake with 502 points to a proxy/upstream problem. |
| Local gateway check fails | Installation settings, working directory, DLLs, runtime, permissions, and port conflicts. |
| Webhook returns 401 or invalid-signature 400 | Matching signing secret and Principle's signature/timestamp headers. |
| Gateway works but SMS fails | Call Centre, phone pairing, connection state, and the bridge's account/session. |

Before upgrades, back up installation settings, application data, the active Caddyfile, and Caddy's certificate data. Stop the bridge, replace the published release, restart, and repeat acceptance checks. Preserve settings and message records. If the release fails, restore the previous published binaries while retaining data. Validate and reload Caddy if its configuration changes.

[Scripts/collect-evidence.ps1](Scripts/collect-evidence.ps1) includes Caddy process/configuration discovery and bridge diagnostics. Adjust its historical log-date filters for a new incident. Its output can include patient information.
