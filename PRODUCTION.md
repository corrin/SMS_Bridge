# Production installation

```text
Principle -> HTTPS office.massey-smiles.co.nz:443 -> Caddy (on OPENDENTAL)
          -> HTTP 127.0.0.1:5170 -> SMS_Bridge -> JustRemotePhone Call Centre -> phone
```

The bridge runs on OPENDENTAL in MSAdministrator's session, beside Call Centre: the JustRemotePhone
SDK drives Call Centre's desktop, so both need a signed-in session. MSAdministrator signs in
automatically after a restart. Caddy belongs to the admin app
([dental_practice_admin](https://github.com/corrin/dental_practice_admin)), whose install script
sets it up and must run first; the bridge's install adds its own site to it,
`C:\ProgramData\Caddy\sites\sms-bridge.caddy`.

## Install and upgrade

In an elevated PowerShell on the server, as MSAdministrator:

```powershell
Invoke-WebRequest https://raw.githubusercontent.com/corrin/SMS_Bridge/master/Scripts/install_bridge.ps1 -OutFile $env:TEMP\install_bridge.ps1; Set-ExecutionPolicy Bypass -Scope Process -Force; & $env:TEMP\install_bridge.ps1
```

It installs the latest GitHub release into `C:\Program Files\SMS_Bridge`, serves `office.massey-smiles.co.nz` through Caddy, registers the "SMS
bridge" task that starts it at MSAdministrator's sign-in and restarts it if it stops, and opens
port 5170 to reception only. On the first install it asks for Principle's API key and webhook
signing secret, generates `BRIDGE_API_KEY`, and writes `C:\ProgramData\SMS_Bridge\install-settings.json`
readable only by administrators and MSAdministrator; upgrades keep that file. `-Tag <release>`
installs an earlier release, to roll back. When MSAdministrator is signed in, it starts the bridge
and prints the build and the phone's state.

Before the first install, once, signed in to the server as MSAdministrator by Remote Desktop:

1. Install JustRemotePhone Call Centre, set it to start at sign-in, and pair the practice phone. A
   phone pairs with one Call Centre, so unpair any other first.
2. Sign MSAdministrator in automatically with Sysinternals
   [Autologon](https://learn.microsoft.com/sysinternals/downloads/autologon), which stores the
   password encrypted. Lock the screen when done; leave Remote Desktop by closing the window,
   never by Sign out, which stops Call Centre and the bridge.

## Releasing

A release is built where Call Centre is installed: the bridge compiles against JustRemotePhone's
SDK (`RemotePhoneService.dll`), which releases leave out and the bridge loads from Call Centre's
install at run time. From this repository, after merging to `master`:

```powershell
powershell -ExecutionPolicy Bypass -File Scripts\publish_release.ps1
```

It builds a fresh clone of `master` self-contained for win-x64 and publishes it as a GitHub
release named after the commit. Then run the install command on the server.

## SMS warning on reception

Principle doesn't report texts that fail to go out, and the bridge can't report its own crash. So a scheduled task on reception, separate from the bridge, runs `check-sms.ps1` every 5 minutes against the bridge on the server. If `phone-status` answers anything but `"up": true`, it shows reception a warning box. That covers the phone being off or disconnected, Call Centre being closed, the bridge being stopped, crashed or hung, and the server being unreachable. The box asks staff to restart the phone, then ring Corrin.

The box appears on the first failed check. Once dismissed, it reappears at most once an hour while texts still aren't going out, counted from the dismissal. The time of the last dismissal is kept in `%LOCALAPPDATA%\SMS_Bridge\warned-at` on reception, and a good check deletes it, so the next outage warns at once. If that file can't be read or written, the box shows on every check. The task never opens a second box while one is still open.

`phone-status` returns `{"up": bool, "detail": "Call Centre: <state>, phone: <state>"}`, read live from the phone SDK on each request. `up` means Call Centre is connected and can see the phone.

The release includes the script as `Scripts\check-sms.ps1`. Copy it to reception as `C:\ProgramData\SMS_Bridge\check-sms.ps1`. Then, signed in on reception as the user who will see the box, run this in PowerShell (no elevation needed), with `BRIDGE_API_KEY` from the server's `install-settings.json` and Corrin's number in place of the `<...>` values:

```powershell
$action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument '--headless powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "C:\ProgramData\SMS_Bridge\check-sms.ps1" -Url "http://192.168.192.30:5170/smsgateway/phone-status" -ApiKey "<BRIDGE_API_KEY>" -CorrinPhone "<number>"'
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 5)
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit 0
Register-ScheduledTask -TaskName 'SMS warning' -Action $action -Trigger $trigger -Principal $principal -Settings $settings
```

`conhost.exe --headless` (Windows 11) runs the check with no console window. Launching `powershell.exe -WindowStyle Hidden` directly instead opens a terminal that takes focus for a moment every 5 minutes, which can swallow what reception is typing. Keep the quotes around all three values: a value with spaces otherwise splits into several arguments. With a missing or split value, `-NonInteractive` makes the script fail without showing a box, so acceptance check 5 must see the box appear.

## Principle setup and acceptance checks

Set Principle's webhook URL to `https://office.massey-smiles.co.nz/smsgateway/webhooks/principle`, enable `message.created` events, and use the same signing secret as `Principle.WEBHOOK_SECRET`. The webhook validates `X-Principle-Signature` and `X-Principle-Timestamp`; it does not require the general gateway's `X-API-Key` header.

1. The install prints `gateway-status` and the phone's state; after pairing, `http://localhost:5170/smsgateway/phone-status` with the `X-API-Key` header returns `"up": true`, and so does `http://192.168.192.30:5170/smsgateway/phone-status` from reception. From any other LAN machine, port 5170 on the server should refuse or time out.
2. From outside the office, request `https://office.massey-smiles.co.nz/smsgateway/gateway-status` with `X-API-Key` set to `BRIDGE_API_KEY`. Expect HTTP 200 and the build identity, with a trusted certificate. Without that header, expect 401.
3. Send an SMS from Principle to a controlled test patient/phone, then reply. Verify both directions and the `PrincipleOutboundSmsQueued` and `PrincipleInboundSmsCreated` log events.
4. Restart the server without signing in. Confirm MSAdministrator signs in automatically, Call Centre reconnects to the phone, and the bridge answers.
5. SMS warning, watched from reception: with everything working, no box appears within 5 minutes. Stop the bridge on the server: the box appears within 5 minutes. Dismiss it: no box reappears for an hour. Start the bridge again, wait for one good check, then turn the phone's Wi-Fi off: the box appears within 5 minutes, without waiting out the hour. Turn Wi-Fi back on and check `phone-status` shows `"up": true` again.

Opening the webhook in a browser sends GET; the endpoint requires POST. Test it with a signed Principle event. Debug mode redirects recipients in the general `/send-sms` endpoint, but Principle webhooks queue messages directly: do not rely on debug mode to redirect Principle test messages.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| HTTPS fails or certificate is untrusted | DNS, the router's port forwards, `C:\ProgramData\Caddy\sites\sms-bridge.caddy`, and Caddy on the server (the admin app's install). |
| Caddy returns 502 | The bridge: is MSAdministrator signed in, is the "SMS bridge" task running, and `C:\ProgramData\SMS_Bridge\od_logs`. |
| The bridge won't start | `od_logs`; "Install JustRemotePhone Call Centre" means its SDK is missing from `C:\Program Files (x86)\JustPhone\CallCenter\SDK\bin`. |
| Webhook returns 401 or invalid-signature 400 | Matching signing secret and Principle's signature/timestamp headers. |
| Gateway works but SMS fails | Call Centre, phone pairing, and the phone's connection (`phone-status`). |

[Scripts/collect-evidence.ps1](Scripts/collect-evidence.ps1) includes Caddy process/configuration discovery and bridge diagnostics. Adjust its historical log-date filters for a new incident. Its output can include patient information.
