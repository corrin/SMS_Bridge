# Warns reception when patient texts can't go out. Task Scheduler runs it every 5 minutes in the
# signed-in user's session, separate from the bridge, so a crashed or hung bridge still raises the
# box: anything but "up": true from phone-status (down, an HTTP error, a timeout, a refused
# connection, a missing key) counts as down. Setup is in PRODUCTION.md, "SMS warning on reception".
#
# The box shows on the first down check. After it's dismissed, it shows again at most once an hour,
# timed from the dismissal by the marker file's LastWriteTime. An up check deletes the marker, so
# the next outage warns at once. A marker that can't be read or written never stops the box.
#
# Exit code: 0 texts can go out, 1 the box was raised, 2 down but dismissed under an hour ago.
param(
    [Parameter(Mandatory = $true)] [string] $CorrinPhone,
    [string] $Url = "http://localhost:5170/smsgateway/phone-status",
    [string] $ApiKey = "",
    [int] $TimeoutSec = 10,
    [string] $MarkerPath = (Join-Path $env:LOCALAPPDATA "SMS_Bridge\warned-at"),
    [switch] $NoBox  # for tests: decide and exit without showing the box
)

$remindEvery = [TimeSpan]::FromMinutes(60)

function Touch-Marker {
    try {
        New-Item -ItemType Directory -Force -Path (Split-Path $MarkerPath) | Out-Null
        [System.IO.File]::WriteAllText($MarkerPath, (Get-Date -Format o))
    }
    catch {
        # Without a marker the next check warns again, which is the safe direction
    }
}

$up = $false
try {
    if ($ApiKey -eq "") {
        $ApiKey = (Get-Content "C:\ProgramData\SMS_Bridge\install-settings.json" -Raw | ConvertFrom-Json).BRIDGE_API_KEY
    }
    $status = Invoke-RestMethod -Uri $Url -Headers @{ "X-API-Key" = $ApiKey } -TimeoutSec $TimeoutSec -UseBasicParsing
    $up = ($status.up -is [bool]) -and $status.up
}
catch {
    $up = $false
}

if ($up) {
    Remove-Item -LiteralPath $MarkerPath -Force -ErrorAction SilentlyContinue
    exit 0
}

try {
    if (Test-Path -LiteralPath $MarkerPath) {
        # A marker dated in the future (clock moved back) doesn't count as recent.
        $sinceDismissed = (Get-Date) - (Get-Item -LiteralPath $MarkerPath).LastWriteTime
        if ($sinceDismissed -ge [TimeSpan]::Zero -and $sinceDismissed -lt $remindEvery) { exit 2 }
    }
}
catch {
    # Unreadable marker: warn
}

Touch-Marker
if (-not $NoBox) {
    Add-Type -AssemblyName System.Windows.Forms
    # A hidden topmost owner keeps the box above whatever reception has open.
    $owner = New-Object System.Windows.Forms.Form -Property @{ TopMost = $true }
    [System.Windows.Forms.MessageBox]::Show($owner,
        "Patient texts are NOT being sent. Try restarting the phone. If that doesn't fix it within 15 minutes, ring Corrin on $CorrinPhone. Until it's fixed, confirm tomorrow's appointments by phone.",
        "SMS warning", "OK", "Warning") | Out-Null
    $owner.Dispose()
}
# The hour counts from the dismissal, not from when the box appeared.
Touch-Marker
exit 1
