# End-to-End Test: Principle → SMS → Reply → Principle

## Prerequisites

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

Principle integration is enabled by the presence of `Principle.API_KEY`. No separate flag needed.

## 2. Start Ngrok

```bash
ngrok http --domain=<your-subdomain>.ngrok-free.app 5170
```

Verify the tunnel is live: `curl https://<your-subdomain>.ngrok-free.app/smsgateway/gateway-status`

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
