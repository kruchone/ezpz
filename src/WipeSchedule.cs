using System;
using System.Globalization;

namespace Ezpz
{
    // When the next scheduled wipe is due, and what it deletes.
    public static class WipeSchedule
    {
        public static readonly string[] Presets = { "New world", "Fresh map, keep characters", "New world + wipe accounts" };

        public static TimeSpan TimeOfDay(AppSettings s)
        {
            TimeSpan t;
            return TimeSpan.TryParse(s.WipeTime, CultureInfo.InvariantCulture, out t) && t.TotalHours < 24 ? t : new TimeSpan(18, 0, 0);
        }

        public static DateTime Last(AppSettings s)
        {
            DateTime d;
            return DateTime.TryParse(s.WipeLast, null, DateTimeStyles.RoundtripKind, out d) ? d : DateTime.MinValue;
        }

        // The wipe ezpz is waiting for (MinValue if none is recorded yet). See MainForm.Wipe.
        public static DateTime Due(AppSettings s)
        {
            DateTime d;
            return DateTime.TryParse(s.WipeDue, null, DateTimeStyles.RoundtripKind, out d) ? d : DateTime.MinValue;
        }

        // The next wipe after "now" (and after the last one that ran). null when scheduled wipes are off.
        public static DateTime? Next(AppSettings s, DateTime now)
        {
            if (!s.WipeEnabled) return null;
            var at = TimeOfDay(s);
            var last = Last(s);
            var after = last > now ? last : now;
            if (s.WipeRepeat == 0)
            {
                int day = Math.Max(1, Math.Min(28, s.WipeDay));
                var c = new DateTime(after.Year, after.Month, day) + at;
                if (c <= after) c = new DateTime(after.Year, after.Month, 1).AddMonths(1).AddDays(day - 1) + at;
                return c;
            }
            int weeks = Math.Max(1, Math.Min(12, s.WipeWeeks));
            var wd = (DayOfWeek)Math.Max(0, Math.Min(6, s.WipeWeekday));
            if (last != DateTime.MinValue)
            {
                // every N weeks counted from the last wipe, on the chosen weekday
                var c = last.Date.AddDays(7 * weeks);
                while (c.DayOfWeek != wd) c = c.AddDays(1);
                c += at;
                while (c <= now) c = c.AddDays(7 * weeks);
                return c;
            }
            // first one: the next chosen weekday
            var d = now.Date;
            while (d.DayOfWeek != wd || d + at <= now) d = d.AddDays(1);
            return d + at;
        }

        public static ResetPlan Plan(AppSettings s)
        {
            return new ResetPlan { World = true, KeepCharacters = s.WipePreset == 1, Accounts = s.WipePreset == 2, Config = false, Logs = false };
        }

        // "in 3 days", "in 5 hours", "in 12 minutes"
        public static string Until(DateTime when, DateTime now)
        {
            var d = when - now;
            if (d.TotalDays >= 2) return "in " + (int)Math.Round(d.TotalDays) + " days";
            if (d.TotalHours >= 2) return "in " + (int)Math.Round(d.TotalHours) + " hours";
            if (d.TotalMinutes >= 2) return "in " + (int)Math.Round(d.TotalMinutes) + " minutes";
            return "in a minute";
        }
    }
}
