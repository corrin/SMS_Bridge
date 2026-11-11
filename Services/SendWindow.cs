namespace SMS_Bridge.Services
{
    // Held texts go out between 08:00 and 20:00 at the practice, in New Zealand time whatever the
    // server's clock zone. Neither bound falls in a daylight-saving jump (02:00–03:00), so each
    // converts to exactly one instant.
    public static class SendWindow
    {
        private static readonly TimeZoneInfo Practice = TimeZoneInfo.FindSystemTimeZoneById("New Zealand Standard Time");
        private static readonly TimeSpan Opens = TimeSpan.FromHours(8);
        private static readonly TimeSpan Closes = TimeSpan.FromHours(20);

        public static DateTimeOffset PracticeTime(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Practice);

        public static bool IsOpen(DateTimeOffset instant)
        {
            var local = TimeZoneInfo.ConvertTime(instant, Practice).TimeOfDay;
            return local >= Opens && local < Closes;
        }

        // How much of [from, to] falls inside sending hours.
        public static TimeSpan SendingTimeBetween(DateTimeOffset from, DateTimeOffset to)
        {
            var total = TimeSpan.Zero;
            var day = TimeZoneInfo.ConvertTime(from, Practice).Date;
            var lastDay = TimeZoneInfo.ConvertTime(to, Practice).Date;
            for (; day <= lastDay; day = day.AddDays(1))
            {
                var opens = Instant(day + Opens);
                var closes = Instant(day + Closes);
                var start = from > opens ? from : opens;
                var end = to < closes ? to : closes;
                if (end > start) total += end - start;
            }
            return total;
        }

        private static DateTimeOffset Instant(DateTime practiceLocal) =>
            new(practiceLocal, Practice.GetUtcOffset(practiceLocal));
    }
}
