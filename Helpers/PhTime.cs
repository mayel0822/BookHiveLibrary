using System;
using System.Linq;

namespace BookHiveLibrary.Helpers
{
    /// <summary>
    /// Philippine-time helpers.
    ///
    /// Why this exists: DateTime.Now returns the SERVER's OS clock, not Philippine
    /// time. On Azure that clock is UTC; on a developer's Windows PC set to Manila
    /// time, it's already PH time. Code that used DateTime.Now for wall-clock rules
    /// (e.g. "reservations close at 3:00 PM") worked by accident during local
    /// testing and was silently wrong by 8 hours once deployed to Azure — a 3:00 PM
    /// cutoff was actually enforced at 3:00 PM UTC, i.e. 11:00 PM in the Philippines.
    ///
    /// The fix: store timestamps as DateTime.UtcNow (unambiguous everywhere), and
    /// convert to PH time with these helpers only at the two points where it
    /// actually matters — comparing against a wall-clock rule, and displaying a
    /// time to a person.
    /// </summary>
    public static class PhTime
    {
        // Falls back to "Singapore Standard Time" because Windows doesn't ship an
        // "Asia/Manila" zone under that name (only Linux/Azure do) — both are UTC+8
        // with no daylight saving, so the fallback is exact, not approximate.
        private static readonly TimeZoneInfo Zone =
            TimeZoneInfo.GetSystemTimeZones().FirstOrDefault(zone =>
                zone.Id == "Asia/Manila" || zone.Id == "Singapore Standard Time")
            ?? TimeZoneInfo.Utc;

        /// <summary>The current wall-clock time in the Philippines. Use for comparing
        /// against a business rule expressed as a time of day (e.g. a 3 PM cutoff).</summary>
        public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);

        /// <summary>Converts a UTC DateTime (as stored in the database) to Philippine
        /// time for display. Only call this on a value that is actually UTC.</summary>
        public static DateTime FromUtc(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

        /// <summary>Same as FromUtc, but passes through null.</summary>
        public static DateTime? FromUtc(DateTime? utc) => utc.HasValue ? FromUtc(utc.Value) : null;

        /// <summary>Converts a Philippine wall-clock DateTime (e.g. "today at midnight
        /// PH time", built from PhTime.Now) to UTC for a database query/comparison.</summary>
        public static DateTime ToUtc(DateTime phLocal) => TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(phLocal, DateTimeKind.Unspecified), Zone);
    }
}
