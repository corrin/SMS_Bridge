# SMS Bridge Future Enhancements

## Critical for Production
1. **Windows Service Setup**
   - Create installer/setup for running as Windows service
   - Configure auto-start on system boot
   - Implement proper shutdown handling
   - Document installation steps for reception machines
   - Create dev environment setup guide

2. **Monitoring & Reliability**
   - Add email notifications for persistent failures
   - Implement basic health monitoring
   - Add logging to file system
   - Consider adding reconnection logic for JustRemotePhone

## Future Improvements
1. **API Enhancements**
   - Add batch delete for received messages
   - Consider adding message history/archival
   - Add basic metrics endpoint

2. **Performance & Maintenance**
   - Implement the `CleanupOldEntries()` method on a timer
   - Add queue size limits
   - Add basic rate limiting

3. **Validation**
   - Add phone number format validation
   - Add message length validation