# End-to-End Test: Principle → SMS → Reply → Principle

## Prerequisites

This guide uses ngrok for development. Production uses Caddy and the office domain; follow [PRODUCTION.md](PRODUCTION.md) for installation and HTTPS setup. Use a controlled test patient/phone: Principle webhook messages bypass the general `/send-sms` endpoint's debug recipient redirection.

- **JustRemotePhone "Call Centre"** running on the same machine (the bridge uses its SDK to send/receive SMS)
- **Ngrok** installed and authenticated (`ngrok config add-authtoken <token>`)
- **Principle API** accessible at `https://api.principle.dental` with valid credentials
- **A test phone** that can receive SMS at a number you control
- **At least one PracticeId** of a practice you can access in Principle

## 1. Configuration

### `appsettings.json`

Add a real PracticeId:
```json
{
  "Principle": {
    "PracticeIds": ["<your-practice-id>"]
  }
}
```

### `install-settings.json`

Create at `C:\ProgramData\SMS_Bridge\install-settings.json`. Required — the app won't start without it. Minimum content for dev testing with Principle:

```json
{
  "BRIDGE_API_KEY": "<any-key-for-inbound-req-auth>",
  "Principle": {
    "API_KEY": "<principle-api-key>",
    "WEBHOOK_SECRET": "<shared-secret-for-webhook-signatures>"
  },
  "Hosting": {
    "AppBaseUrl": "https://<your-subdomain>.ngrok-free.app/"
  }
}
```

Set `Principle.Enabled` to `true` (the default in `appsettings.json`) and provide `Principle.API_KEY`. `Hosting.AppBaseUrl` is used by eTXT, not Principle; configure Principle's webhook URL in Principle itself.

## 2. Start Ngrok

```bash
ngrok http --domain=<your-subdomain>.ngrok-free.app 5170
```

After starting the bridge in step 4, verify the tunnel by requesting `https://<your-subdomain>.ngrok-free.app/smsgateway/gateway-status` with `X-API-Key` set to `BRIDGE_API_KEY`. Expect HTTP 200; without the header, expect 401.

## 3. Configure Principle

In Principle, set the webhook URL to:

```
https://<your-subdomain>.ngrok-free.app/smsgateway/webhooks/principle
```

Use the same webhook secret that's in `install-settings.json` under `Principle.WEBHOOK_SECRET`.

## 4. Start the Bridge

```bash
dotnet run
```

Check:
- `GET /smsgateway/gateway-status` → "Gateway is up and running"
- `GET /smsgateway/debug-status` → `isDebugMode: true` + your test phone number
- `GET /smsgateway/phone-status` → `up: true` once Call Centre is connected to the phone

## 5. Run the Test

1. **Trigger outbound SMS from Principle** — send an SMS to your test phone via the Principle UI
2. Watch bridge console: should log `PrincipleOutboundSmsQueued` with the SMSBridgeID
3. **SMS arrives on your test phone** — sent by the JustRemotePhone provider
4. **Reply to that SMS** from the test phone
5. Watch bridge console: should log `SMSReceived`, then `PrincipleInboundSmsCreated`
6. **Check Principle** — the inbound reply should appear as an SMS message on the patient's record

## 6. Troubleshooting

| Symptom | Check |
|---------|-------|
| Bridge fails to start | `install-settings.json` exists with `BRIDGE_API_KEY` set, and `appsettings.json` has `SmsSettings.Provider` configured |
| Webhook returns 401 | `Principle.WEBHOOK_SECRET` matches on both sides; headers `X-Principle-Signature` and `X-Principle-Timestamp` are present |
| Webhook returns "ignored" | Payload has `eventType: "message.created"`, `direction: "outbound"`, `status: "pending"` |
| Inbound SMS not matched to patient | `PracticeIds` is populated; patient's phone number exists in Principle; try calling `GET /v1/patients?practiceId=<id>&phoneNumber=<number>` directly |
| SMS never arrives at phone | "Call Centre" is running; JustRemotePhone SDK shows connected status; check `QueueSettings.ProcessInterval` isn't too long |
