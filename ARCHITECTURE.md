# SMS_Bridge Architecture Overview

## Core Components

### 1. Entry Point (Program.cs)
- Main application setup and configuration
- Web API endpoints for SMS operations
- Middleware for API key validation
- Debug/Production mode handling
- Port configuration (default: 5170)

### 2. Services

#### Logger (Services/Logger.cs)
- Centralized logging system
- Handles different log levels and event types
- Structured logging with provider, event type, message ID, and details

#### Configuration (Services/Configuration.cs)
- Manages application configuration
- Handles API keys and environment-specific settings
- Validates critical configuration parameters

#### SmsQueueProcessor (Services/SmsQueueProcessor.cs)
- Holds texts while the phone can't send (`PhoneState` Unknown) and sends them when it can, one every 5 seconds
- Holds texts while the phone isn't confirming sends (one unconfirmed after 30 seconds, or a timeout), sending one as a test every 15 minutes until one is confirmed; `phone-status` reports it

- Retries a failed send after 1, 5, then every 15 minutes; resends a text the phone never confirmed once only, since it may have gone out
- Sends held texts only 08:00–20:00 New Zealand time (`Services/SendWindow.cs`); a text that arrives outside those hours goes straight out
- Gives up after `QueueSettings:GiveUpAfterSendingHours` of sending hours
- Principle texts are kept in `PrincipleOutboundSmsStore`, so a restart resumes them; their outcome is set on the Principle message (`sent` or `failed`), and given-up ones are listed at `GET /smsgateway/failed-sms`

#### CallCentreWatchdog (Services/CallCentreWatchdog.cs)
- Restarts Call Centre when its texting connection to the phone (port 12321) is missing while the calls connection (12331) is up, or texts go unconfirmed
- At most once per 15 minutes, and twice until a restart is seen to work; reported in `phone-status`

### 3. SMS Providers

#### ISmsProvider Interface (SmsProviders/ISmsProvider.cs)
- Core interface for SMS provider implementations
- Defines standard methods for sending/receiving SMS
- Status checking capabilities

#### Provider Implementations
1. **JustRemotePhone** (SmsProviders/JustRemotePhoneSmsProvider.cs)
   - Primary/fully implemented provider
   - Handles SMS sending and receiving
   - Message status tracking
   - Real-time status updates

2. **ETxt** (SmsProviders/ETxtSmsProvider.cs)
   - Stub implementation
   - Placeholder for future integration

3. **Diafaan** (SmsProviders/DiafaanSmsProvider.cs)
   - Stub implementation
   - Placeholder for future integration

## API Endpoints

### SMS Gateway (/smsgateway)
1. **POST /send-sms**
   - Queues new SMS messages
   - Handles debug mode redirection
   - Returns message ID for tracking

2. **GET /sms-status/{messageId}**
   - Retrieves status of sent messages
   - Supports message tracking

3. **GET /received-sms**
   - Retrieves incoming messages
   - Currently supports JustRemotePhone provider only

4. **GET /recent-status-values**
   - Gets recent message status updates
   - Currently supports JustRemotePhone provider only

5. **GET /failed-sms**
   - Principle texts the bridge gave up on, until a later text to the same number goes out
   - `{"failed": [{principleMessageId, patientId, patientPhoneNumber, receivedAt, failedAt, attempts, reason}]}`, times in UTC
   - dental_practice_admin shows them to staff

## Security Features
- `X-API-Key` required on every route except provider webhooks, which use HMAC or a callback key
- Production machine validation
- Debug mode restrictions
- Test number redirection in debug mode

## Configuration
- Environment-specific settings (appsettings.json)
- Production machine list
- Test phone numbers for debug mode
- Provider-specific configurations

## Deployment

Production uses Caddy to terminate HTTPS for `office.massey-smiles.co.nz` and proxy requests to SMS_Bridge on HTTP port 5170. Caddy manages certificate issuance and renewal separately from the application. Development uses an ngrok HTTPS tunnel to the same application port. See [PRODUCTION.md](PRODUCTION.md) for prerequisites, configuration, startup constraints, and verification.

- Release builds are a windowless WinExe; Debug builds keep a console. It can't run as a Windows service, because the JustRemotePhone SDK needs the signed-in desktop session.
- Task Scheduler starts it at sign-in; see the production guide for working-directory and phone-session requirements.
- The application listens on `Hosting.ListenUrl`, by default `http://localhost:5170` (loopback only), so only a proxy on the same machine can reach it.
- Installation and data-directory permissions must allow access by the runtime account.
