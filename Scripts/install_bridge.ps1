# Installs or upgrades the SMS bridge on the server, in an elevated PowerShell as MSAdministrator:
#   Invoke-WebRequest https://raw.githubusercontent.com/corrin/SMS_Bridge/master/Scripts/install_bridge.ps1 -OutFile $env:TEMP\install_bridge.ps1; Set-ExecutionPolicy Bypass -Scope Process -Force; & $env:TEMP\install_bridge.ps1
# Installs the latest GitHub release, or -Tag <release> (for example to roll back). Safe to re-run,
# including after a failure; it stops at the first error. Call Centre must be installed first.
param([string]$Tag)
$ErrorActionPreference = 'Stop'
$root = 'C:\Program Files\SMS_Bridge'
$data = 'C:\ProgramData\SMS_Bridge'
$settings = "$data\install-settings.json"
# The bridge drives Call Centre's desktop, so both run in this account's automatic sign-in.
$user = "$env:COMPUTERNAME\MSAdministrator"
$sdk = 'C:\Program Files (x86)\JustPhone\CallCenter\SDK\bin\RemotePhoneService.dll'
if (-not (Test-Path $sdk)) { throw "Install JustRemotePhone Call Centre first ($sdk is missing)." }

# Settings: written once, kept on every upgrade.
New-Item -ItemType Directory -Force $data | Out-Null
if (-not (Test-Path $settings)) {
    $apiKey = Read-Host 'Principle API key'
    $webhookSecret = Read-Host "Principle webhook signing secret"
    if (-not $apiKey -or -not $webhookSecret) { throw 'Both values are needed; nothing changed.' }
    @{
        BRIDGE_API_KEY = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
        Principle = @{ API_KEY = $apiKey; WEBHOOK_SECRET = $webhookSecret }
        Hosting = @{ ListenUrl = 'http://localhost:5170;http://192.168.192.30:5170' }
    } | ConvertTo-Json | Set-Content $settings -Encoding UTF8
}
# The task runs with a standard user token, which the Administrators group doesn't cover.
icacls $data /grant "$($user):(OI)(CI)M" | Out-Null
icacls $settings /inheritance:r /grant:r 'SYSTEM:F' 'Administrators:F' "$($user):R" | Out-Null
$key = (Get-Content $settings -Raw | ConvertFrom-Json).BRIDGE_API_KEY

# The release
$release = Invoke-RestMethod ('https://api.github.com/repos/corrin/SMS_Bridge/releases/' + $(if ($Tag) { "tags/$Tag" } else { 'latest' }))
Invoke-WebRequest $release.assets[0].browser_download_url -OutFile $env:TEMP\SMS_Bridge.zip
Stop-ScheduledTask -TaskName 'SMS bridge' -ErrorAction SilentlyContinue
Get-Process SMS_Bridge -ErrorAction SilentlyContinue | ForEach-Object { $_ | Stop-Process -Force; $_.WaitForExit() }
if (Test-Path $root) { Remove-Item -Recurse -Force $root }
Expand-Archive $env:TEMP\SMS_Bridge.zip $root
Remove-Item $env:TEMP\SMS_Bridge.zip

# Starts at each sign-in, comes back if it stops, and admits reception's SMS warning check only.
$action = New-ScheduledTaskAction -Execute "$root\SMS_Bridge.exe" -WorkingDirectory $root
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive
$options = New-ScheduledTaskSettingsSet -ExecutionTimeLimit 0 -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName 'SMS bridge' -Action $action -Trigger $trigger -Principal $principal -Settings $options -Force | Out-Null
if (-not (Get-NetFirewallRule -DisplayName 'SMS bridge from reception' -ErrorAction SilentlyContinue)) { New-NetFirewallRule -DisplayName 'SMS bridge from reception' -Direction Inbound -Protocol TCP -LocalPort 5170 -RemoteAddress 192.168.192.125 -Action Allow -Profile Any | Out-Null }

# An interactive task runs only while its user is signed in.
if (-not (Get-Process explorer -IncludeUserName | Where-Object UserName -eq $user)) {
    Write-Host "Installed $($release.tag_name); it starts when MSAdministrator signs in." -ForegroundColor Yellow
    return
}
Start-ScheduledTask -TaskName 'SMS bridge'
$status = $null; $deadline = (Get-Date).AddMinutes(1)
while (-not $status -and (Get-Date) -lt $deadline) { Start-Sleep 3; try { $status = Invoke-RestMethod http://localhost:5170/smsgateway/gateway-status -Headers @{ 'X-API-Key' = $key } } catch { } }
if (-not $status) { throw "The bridge didn't answer; see $data\od_logs" }
$phone = Invoke-RestMethod http://localhost:5170/smsgateway/phone-status -Headers @{ 'X-API-Key' = $key }
Write-Host "$status`nPhone: $($phone.detail)" -ForegroundColor $(if ($phone.up) { 'Green' } else { 'Yellow' })
Write-Host "Done: $($release.tag_name)." -ForegroundColor Green
