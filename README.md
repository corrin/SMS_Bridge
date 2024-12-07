# SMS_Bridge

A lightweight SMS bridge application that integrates with multiple SMS providers. Designed for simplicity, reliability, and fail-fast operation.

## Features
- Supports multiple SMS providers:
  - JustRemotePhone
  - eTXT (stub implementation)
  - Diafaan (stub implementation)
- Queue-based SMS sending with clear logging and error handling.
- Fail-fast configuration for reliable operation.
- Designed to run as a Windows service or a standalone console application.

## Installation

1. **Copy Files**
   ```bash
   xcopy /E /I * "C:\Program Files\SMS_Bridge"

# Install Insructions

xcopy /E /I * "C:\Program Files\SMS_Bridge"

# Update service path
sc.exe config SMS_Bridge_Service binpath= "C:\Program Files\SMS_Bridge\SMS_Bridge.exe"

# Set permissions
icacls "C:\Program Files\SMS_Bridge" /grant "NT AUTHORITY\NetworkService":(OI)(CI)RX
icacls "C:\Program Files\SMS_Bridge" /grant "BUILTIN\Administrators":(OI)(CI)F
icacls "C:\Program Files\SMS_Bridge" /grant "NT AUTHORITY\SYSTEM":(OI)(CI)F
