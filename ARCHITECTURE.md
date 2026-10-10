# SMS_Bridge Architecture Overview

## Core Components

### 1. Entry Point (Program.cs)
- Main application setup and configuration
- Web API endpoints for SMS operations
- Middleware for API key validation
- Debug/Production mode handling
- Running as either Windows Service or Console application
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
- Manages the SMS sending queue
- Handles message processing and retries
- Ensures reliable message delivery

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

- Can run as Windows Service or Console application
- Startup is configured separately through Windows services or Task Scheduler; see the production guide for working-directory and phone-session requirements.
- The application listens on `Hosting.ListenUrl`, by default `http://localhost:5170` (loopback only), so only a proxy on the same machine can reach it.
- Installation and data-directory permissions must allow access by the runtime account.
