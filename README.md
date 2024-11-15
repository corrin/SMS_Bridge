# Install Insructions

xcopy /E /I * "C:\Program Files\SMS_Bridge"

# Update service path
sc.exe config SMS_Bridge_Service binpath= "C:\Program Files\SMS_Bridge\SMS_Bridge.exe"

# Set permissions
icacls "C:\Program Files\SMS_Bridge" /grant "NT AUTHORITY\NetworkService":(OI)(CI)RX
icacls "C:\Program Files\SMS_Bridge" /grant "BUILTIN\Administrators":(OI)(CI)F
icacls "C:\Program Files\SMS_Bridge" /grant "NT AUTHORITY\SYSTEM":(OI)(CI)F
