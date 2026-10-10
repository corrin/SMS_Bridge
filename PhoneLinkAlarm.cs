using System.Runtime.InteropServices;
using SMS_Bridge.Services;
using SMS_Bridge.SmsProviders;

namespace SMS_Bridge
{
    /// <summary>
    /// The bridge's half of the SMS link alarm: the phone-status endpoint dental_practice_admin
    /// polls, and one message box on reception's screen per outage.
    /// </summary>
    public static class PhoneLinkAlarm
    {
        public const string PopupCaption = "SMS Bridge";
        public const string PopupText = "Patient texts are not being sent. The phone is not connected to Call Centre. "
            + "Check the phone is on and connected, then open Call Centre.";

        private const uint MB_ICONWARNING = 0x00000030;
        private const uint MB_SYSTEMMODAL = 0x00001000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        public static RouteHandlerBuilder MapPhoneStatus(this RouteGroupBuilder group)
        {
            return group.MapGet("/phone-status", (PhoneLinkMonitor monitor) => Results.Ok(monitor.GetStatus()));
        }

        public static PhoneLinkMonitor CreateMonitor()
        {
            // A Windows service has no desktop to show a box on; phone-status reports that as popup: false.
            var monitor = new PhoneLinkMonitor(TimeProvider.System, Environment.UserInteractive);

            monitor.OutageStarted += (outage, detail) =>
            {
                Logger.LogError(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "PhoneLinkOutageStarted",
                    details: $"Outage {outage.Id}, link down since {outage.Started:O}: {detail}"
                );
                if (Environment.UserInteractive)
                {
                    ShowPopup();
                }
                else
                {
                    // No desktop; the staff banner says reception can't show warnings
                }
            };
            monitor.OutageEnded += outage =>
            {
                Logger.LogInfo(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "PhoneLinkOutageEnded",
                    details: $"Outage {outage.Id} ended after {PhoneLinkMonitor.OutageEndAfter.TotalMinutes} minutes up"
                );
            };

            return monitor;
        }

        // MessageBoxW blocks until dismissed, so it gets its own thread; a background thread
        // doesn't hold the process open at shutdown.
        private static void ShowPopup()
        {
            new Thread(() => MessageBoxW(IntPtr.Zero, PopupText, PopupCaption, MB_SYSTEMMODAL | MB_ICONWARNING))
            {
                IsBackground = true,
                Name = "PhoneLinkPopup"
            }.Start();
        }
    }
}
