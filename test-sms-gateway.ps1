<#
.SYNOPSIS
    Tests connectivity and functionality of the SMS Gateway.

.DESCRIPTION
    This script performs a series of tests on the SMS Gateway to diagnose connectivity issues
    and verify functionality. It tests TCP connectivity, API authentication, and core API functions
    including sending SMS messages and checking their status.

.NOTES
    Author: Roo
    Date: May 25, 2025
#>

# Configuration
$gatewayHost = "192.168.192.125"
$gatewayPort = 5170
$apiKey = "jMcExWyi6oCj9Ebo"
$testPhoneNumber = "+6421467784"
$baseUrl = "http://$gatewayHost`:$gatewayPort/smsgateway"
$headers = @{
    "X-API-Key" = $apiKey
    "Content-Type" = "application/json"
}

# Helper function for logging
function Write-Log {
    param (
        [Parameter(Mandatory=$true)]
        [string]$Message,
        
        [Parameter(Mandatory=$false)]
        [ValidateSet("INFO", "SUCCESS", "WARNING", "ERROR")]
        [string]$Level = "INFO"
    )
    
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $color = switch ($Level) {
        "INFO" { "White" }
        "SUCCESS" { "Green" }
        "WARNING" { "Yellow" }
        "ERROR" { "Red" }
    }
    
    Write-Host "[$timestamp] [$Level] $Message" -ForegroundColor $color
}

# Start testing
Write-Log "Starting SMS Gateway connectivity tests" "INFO"
Write-Log "Target: $baseUrl" "INFO"
Write-Log "Testing TCP connectivity first to verify network access..." "INFO"

# Test 1: TCP Connectivity
try {
    Write-Log "Testing TCP connectivity to $gatewayHost on port $gatewayPort..." "INFO"
    $tcpTest = Test-NetConnection -ComputerName $gatewayHost -Port $gatewayPort
    
    if ($tcpTest.TcpTestSucceeded) {
        Write-Log "TCP connectivity test succeeded! The server is reachable." "SUCCESS"
    } else {
        Write-Log "TCP connectivity test failed. This indicates a network or firewall issue." "ERROR"
        Write-Log "Check if the server is running and if there are any firewall rules blocking access." "INFO"
        exit 1
    }
} catch {
    Write-Log "Error testing TCP connectivity: $_" "ERROR"
    exit 1
}

# Test 2: Gateway Status without API Key
Write-Log "Testing gateway-status endpoint WITHOUT API key (expecting 401 Unauthorized)..." "INFO"
try {
    $response = Invoke-WebRequest -Uri "$baseUrl/gateway-status" -Method GET -UseBasicParsing -ErrorAction SilentlyContinue
    Write-Log "Unexpected success! Got status code $($response.StatusCode) without API key." "WARNING"
} catch {
    if ($_.Exception.Response.StatusCode.value__ -eq 401) {
        Write-Log "Got expected 401 Unauthorized response when not providing API key." "SUCCESS"
        Write-Log "This confirms the API key security is working correctly." "INFO"
    } else {
        Write-Log "Unexpected error: $_" "ERROR"
    }
}

# Test 3: Gateway Status with API Key
Write-Log "Testing gateway-status endpoint WITH API key..." "INFO"
try {
    $response = Invoke-WebRequest -Uri "$baseUrl/gateway-status" -Method GET -Headers $headers -UseBasicParsing
    
    if ($response.StatusCode -eq 200) {
        Write-Log "Gateway status check succeeded! Response: $($response.Content)" "SUCCESS"
        Write-Log "This confirms the API key is valid and the gateway is running." "INFO"
    } else {
        Write-Log "Unexpected status code: $($response.StatusCode)" "WARNING"
    }
} catch {
    Write-Log "Error checking gateway status: $_" "ERROR"
    exit 1
}

# Test 4: Send SMS
Write-Log "Testing send-sms endpoint (sending a test message)..." "INFO"
$messageText = "Please reply 'test' without the quotes- Test from PowerShell script at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$body = @{
    PhoneNumber = $testPhoneNumber
    Message = $messageText
    SenderId = "TESTSCRIPT"
} | ConvertTo-Json

try {
    $response = Invoke-WebRequest -Uri "$baseUrl/send-sms" -Method POST -Headers $headers -Body $body -UseBasicParsing
    
    if ($response.StatusCode -eq 200) {
        $result = $response.Content | ConvertFrom-Json
        $smsBridgeId = $result.smsBridgeID -replace "SmsBridgeId \{ Value = ([^}]+) \}", '$1'
        Write-Log "SMS sent successfully! Message ID: $smsBridgeId" "SUCCESS"
        Write-Log "Response: $($response.Content)" "INFO"
    } else {
        Write-Log "Unexpected status code when sending SMS: $($response.StatusCode)" "WARNING"
    }
} catch {
    Write-Log "Error sending SMS: $_" "ERROR"
    exit 1
}

# Test 5: Check SMS Status
if ($smsBridgeId) {
    Write-Log "Waiting 5 seconds before checking message status..." "INFO"
    Start-Sleep -Seconds 5
    
    Write-Log "Checking status of sent message (ID: $smsBridgeId)..." "INFO"
    $statusSuccess = $false
    try {
        $response = Invoke-WebRequest -Uri "$baseUrl/sms-status/$smsBridgeId" -Method GET -Headers $headers -UseBasicParsing
        
        if ($response.StatusCode -eq 200) {
            $statusResult = $response.Content | ConvertFrom-Json
            Write-Log "Message status check succeeded! Status: $($statusResult.statusDisplay)" "SUCCESS"
            Write-Log "Response: $($response.Content)" "INFO"
            $statusSuccess = $true
        } else {
            Write-Log "Unexpected status code when checking message status: $($response.StatusCode)" "WARNING"
        }
    } catch {
        Write-Log "Error checking message status: $_" "ERROR"
    }
    
    # If status check was successful, wait for reply
    if ($statusSuccess) {
        Write-Log "Waiting 30 seconds for user to reply with 'test'..." "INFO"
        Write-Log "Please reply to the SMS with 'test' now" "INFO"
        Start-Sleep -Seconds 30
    }
}

# Test 6: Check Received Messages
Write-Log "Checking for received SMS messages..." "INFO"
try {
    $response = Invoke-WebRequest -Uri "$baseUrl/received-sms" -Method GET -Headers $headers -UseBasicParsing
    
    if ($response.StatusCode -eq 200) {
        $messages = $response.Content | ConvertFrom-Json
        $messageCount = ($messages | Measure-Object).Count
        
        if ($messageCount -gt 0) {
            Write-Log "Found $messageCount received message(s)!" "SUCCESS"
            $foundYesReply = $false
            
            foreach ($msg in $messages) {
                Write-Log "Message from: $($msg.fromNumber), Text: $($msg.messageText), Received at: $($msg.receivedAt)" "INFO"
                
                # Check if the reply contains "test"
                if ($msg.messageText -match "test") {
                    Write-Log "Reply contains 'test' as expected!" "SUCCESS"
                    $foundYesReply = $true
                }
            }
            
            if (-not $foundYesReply) {
                Write-Log "No reply containing 'test' was found. Please check if you replied to the SMS." "WARNING"
            }
        } else {
            Write-Log "No received messages found. Expected a reply with 'test'." "WARNING"
        }
    } else {
        Write-Log "Unexpected status code when checking received messages: $($response.StatusCode)" "WARNING"
    }
} catch {
    Write-Log "Error checking received messages: $_" "ERROR"
}

# Summary
Write-Log "SMS Gateway testing completed!" "INFO"
Write-Log "If all tests passed, the SMS Gateway is functioning correctly." "INFO"
Write-Log "If any tests failed, check the error messages for troubleshooting." "INFO"