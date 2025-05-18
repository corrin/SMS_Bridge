@echo off
REM Test without API key (expect 401)
curl -v -L -X POST ^
  -H "Content-Type: application/json" ^
  -d "{\"PhoneNumber\":\"+6421467784\",\"Message\":\"Debug test without key\",\"SenderId\":\"MYSENDER\"}" ^
  https://sms-bridge.au.ngrok.io/smsgateway/send-sms

echo.
REM Test with API key (expect 200)
curl -v -L -X POST ^
  -H "Content-Type: application/json" ^
  -H "X-API-Key: jMcExWyi6oCj9Ebo" ^
  -d "{\"PhoneNumber\":\"+6421467784\",\"Message\":\"Debug test with key\",\"SenderId\":\"MYSENDER\"}" ^
  https://sms-bridge.au.ngrok.io/smsgateway/send-sms
