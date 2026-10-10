# Warns reception when patient texts can't go out. Task Scheduler runs it every 15 minutes in the
# signed-in user's session, separate from the bridge, so a crashed or hung bridge still raises the
# box: anything but "up": true from phone-status (down, an HTTP error, a timeout, a refused
# connection, a missing key) shows it. Setup is in PRODUCTION.md, "SMS warning on reception".
# Exit code: 0 texts can go out, 1 the warning was raised.
param(
    [Parameter(Mandatory = $true)] [string] $CorrinPhone,
    [string] $Url = "http://localhost:5170/smsgateway/phone-status",
    [string] $ApiKey = "",
    [int] $TimeoutSec = 10,
    [switch] $NoBox  # for tests: decide and exit without showing the box
)

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

if ($up) { exit 0 }

if (-not $NoBox) {
    Add-Type -AssemblyName System.Windows.Forms
    # A hidden topmost owner keeps the box above whatever reception has open.
    $owner = New-Object System.Windows.Forms.Form -Property @{ TopMost = $true }
    [System.Windows.Forms.MessageBox]::Show($owner,
        "Patient texts are NOT being sent. Try restarting the phone. If that doesn't fix it within 15 minutes, ring Corrin on $CorrinPhone. Until it's fixed, confirm tomorrow's appointments by phone.",
        "SMS warning", "OK", "Warning") | Out-Null
    $owner.Dispose()
}
exit 1
