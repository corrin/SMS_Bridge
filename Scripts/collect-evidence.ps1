# Read-only evidence collection for the Aug 25 Principle webhook failures.
# Run on RECEPTION-AIO in an elevated PowerShell:
#   powershell -ExecutionPolicy Bypass -File collect-evidence.ps1
# Output goes to evidence-<date>.txt next to this script. Makes no changes.

$out = Join-Path $PSScriptRoot ("evidence-{0:yyyyMMdd-HHmm}.txt" -f (Get-Date))
Start-Transcript -Path $out

$logDir = "C:\ProgramData\SMS_Bridge\od_logs"
$windowFiles = 22..26 | ForEach-Object { Join-Path $logDir ("SMS_Log_202608{0:d2}.log" -f $_) } |
    Where-Object { Test-Path $_ }

"===== 1. App log files in the failure window (Aug 22-26) ====="
if ($windowFiles.Count -eq 0) {
    "NO LOG FILES FOUND for Aug 22-26 in $logDir - app was likely not running at all."
    Get-ChildItem $logDir | Sort-Object Name | Select-Object -Last 10 Name, Length, LastWriteTime
}
else {
    foreach ($f in $windowFiles) {
        ""
        "--- $f ---"
        $first = Get-Content $f -TotalCount 1
        $last = Get-Content $f -Tail 1
        "FIRST: $first"
        "LAST:  $last"
        # Hourly heartbeats: ~24/day each expected while the app runs
        $heartbeats = (Select-String -Path $f -Pattern 'NoMessages|ConnectionHealthCheck').Count
        "HEARTBEAT LINES (expect ~24-48/day while up): $heartbeats"
    }
    ""
    "--- Webhook / startup / auth events in the window ---"
    Select-String -Path $windowFiles -Pattern 'PrincipleWebhook|StartupFailure|UnauthorizedAccess|ConnectionAttempt' |
        ForEach-Object { "$($_.Filename): $($_.Line)" }
}

""
"===== 1a. App uptime census: heartbeats per day, ALL history ====="
"Hourly heartbeats (NoMessages/ConnectionHealthCheck) per file; ~24-48 = up all day, low/0 = down"
Get-ChildItem $logDir -Filter "SMS_Log_*.log" -ErrorAction SilentlyContinue | Sort-Object Name |
    ForEach-Object {
        $hb = (Select-String -Path $_.FullName -Pattern 'NoMessages|ConnectionHealthCheck').Count
        $starts = (Select-String -Path $_.FullName -Pattern 'ConnectionAttempt|Initialized SMS provider').Count
        "{0}  heartbeats={1,3}  startups={2}" -f $_.Name, $hb, $starts
    }
"NOTE: a DATE WITH NO FILE AT ALL means the app wrote nothing that day - it was down all day."

""
"===== 1b. Has a Principle webhook EVER reached this box? (all history) ====="
$allLogs = Get-ChildItem $logDir -Filter "SMS_Log_*.log" -ErrorAction SilentlyContinue
"Log files present: $($allLogs.Count) (from $($allLogs | Sort Name | Select -First 1 -Expand Name) to $($allLogs | Sort Name | Select -Last 1 -Expand Name))"
$everWebhook = Select-String -Path ($allLogs.FullName) -Pattern 'PrincipleWebhook' -ErrorAction SilentlyContinue
"Total PrincipleWebhook lines EVER: $($everWebhook.Count)"
$everWebhook | Select-Object -First 5 | ForEach-Object { "EARLIEST: $($_.Filename): $($_.Line)" }
$everWebhook | Select-Object -Last 5 | ForEach-Object { "LATEST:   $($_.Filename): $($_.Line)" }
"--- Prod outbound store file (written only by successful webhook processing) ---"
$mapFile = "C:\ProgramData\SMS_Bridge\msg_guids\$env:COMPUTERNAME`_principle_sms_map.json"
if (Test-Path $mapFile) {
    Get-Item $mapFile | Select-Object FullName, Length, LastWriteTime, CreationTime
    Get-Content $mapFile
}
else {
    "DOES NOT EXIST: $mapFile - no Principle webhook has ever been successfully processed on this machine."
}

""
"===== 2. Caddy: how it runs, config, logs ====="
Get-Service | Where-Object { $_.Name -match 'caddy' } | Format-List Name, Status, StartType, BinaryPathName
Get-Process caddy -ErrorAction SilentlyContinue | Select-Object Path, StartTime | Format-List
Get-CimInstance Win32_Process -Filter "Name='caddy.exe'" -ErrorAction SilentlyContinue |
    Select-Object CommandLine | Format-List
"--- Searching common locations for Caddyfile / caddy logs ---"
foreach ($dir in @("C:\caddy", "C:\ProgramData\caddy", "$env:APPDATA\Caddy", "C:\Program Files\caddy")) {
    if (Test-Path $dir) {
        Get-ChildItem -Recurse $dir -ErrorAction SilentlyContinue |
            Select-Object FullName, Length, LastWriteTime
    }
}
"NOTE: if a Caddy access log exists, copy the Aug 24-25 slice alongside this output."

""
"===== 3. Reboots / shutdowns / crashes Aug 22-26 ====="
Get-WinEvent -FilterHashtable @{
    LogName = 'System'; Id = 6005, 6006, 6008, 1074, 41
    StartTime = [datetime]'2026-08-10'; EndTime = [datetime]'2026-08-26'
} -ErrorAction SilentlyContinue |
    Select-Object TimeCreated, Id, Message | Format-Table -Wrap
"--- Recent Windows updates ---"
Get-HotFix | Sort-Object InstalledOn -Descending | Select-Object -First 5 HotFixID, InstalledOn

""
"===== 4. Current state ====="
"Last boot: $((Get-CimInstance Win32_OperatingSystem).LastBootUpTime)"
"--- Scheduled tasks mentioning sms/caddy/bridge ---"
Get-ScheduledTask -ErrorAction SilentlyContinue |
    Where-Object { $_.TaskName -match 'sms|caddy|bridge' } |
    Format-List TaskName, State
"--- Startup folder / Run keys ---"
Get-ChildItem "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp",
    "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup" -ErrorAction SilentlyContinue |
    Select-Object FullName
Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
    "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" -ErrorAction SilentlyContinue |
    Format-List
"--- Listeners on 5170/443 ---"
Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
    Where-Object { $_.LocalPort -in 5170, 443 } |
    ForEach-Object {
        $p = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue
        "{0}:{1} -> {2}" -f $_.LocalAddress, $_.LocalPort, $p.ProcessName
    }

Stop-Transcript
Write-Host ""
Write-Host "Done. Send back: $out"
