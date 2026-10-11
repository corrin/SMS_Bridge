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

## Production installation

Follow [PRODUCTION.md](PRODUCTION.md) for installing, upgrading, releasing, and verification.

Production requires **SMS_Bridge, JustRemotePhone Call Centre with a connected phone, and Caddy**. Caddy serves `https://office.massey-smiles.co.nz` and forwards requests to the bridge's HTTP listener on port 5170. Caddy handles certificates outside the .NET application.

## Development

See [TESTING.md](TESTING.md) for development using ngrok and an end-to-end Principle/SMS test. The application does not switch public domains based on its environment.

## Configuration

[appsettings.json](appsettings.json) declares the configuration keys and defaults. Copy [install-settings.example.json](install-settings.example.json) to `C:\ProgramData\SMS_Bridge\install-settings.json` for installation overrides and secrets. Keep real credentials out of the repository.
