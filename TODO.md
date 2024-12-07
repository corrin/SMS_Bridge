# SMS_Bridge - Future Enhancements

## Critical for Production
1. **Service Installation**
   - Automate service setup with a simple installer script.
   - Include pre-checks for configuration file and directory permissions.

2. **Monitoring and Alerting**
   - Add email notifications for persistent SMS failures.
   - Implement health-check endpoints for monitoring the system status.

3. **Configuration Validation**
   - Validate all required keys during startup and log missing optional keys for visibility.

4. **Provider Implementations**
   - Complete eTXT and Diafaan provider implementations.

## Medium Priority
1. **Queue Enhancements**
   - Add optional rate-limiting for SMS sending to avoid throttling issues.
   - Add a feature to limit queue size and reject new messages if the queue is full.

2. **Received Message Management**
   - Implement batch deletion of received messages.
   - Add a user-friendly interface to view and manage received messages.

3. **Improved Logging**
   - Implement log rotation or archival to prevent excessive log sizes.

4. **Testing Improvements**
   - Extend unit tests for critical components like `SmsQueueService`.
   - Add integration tests for provider-specific implementations.

## Low Priority
1. **Performance Optimizations**
   - Optimize disk I/O during message save/load.
   - Profile and improve queue processing under high load.
2. **API Extensions**
   - Add support for message history and archival.

## Completed Tasks
- Resolved potential issues with `ReceivedAt` timestamps during message load.
- Improved logging for API key validation in `Program.cs`.
