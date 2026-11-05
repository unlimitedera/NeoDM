using System.Globalization;
using NDM.App.Localization;

namespace NDM.App;

/// <summary>The result of checking what the user typed or picked for a schedule.</summary>
public sealed record ScheduleCheck(DateTime? Local, DateTimeOffset? At, string? Error)
{
    public bool IsValid => At is not null;
}

/// <summary>1.11.0: parsing, formatting, checking and the plain-words line of the schedule control. Pure functions of text, culture, zone and clock, so they are tested without a window.</summary>
public static class ScheduleText
{
    /// <summary>The culture with Gregorian dates (the calendar control is Gregorian), so text and picker always agree even for cultures whose default calendar is another one.</summary>
    public static CultureInfo Gregorian(CultureInfo culture)
    {
        var copy = (CultureInfo)culture.Clone();
        if (copy.DateTimeFormat.Calendar is not GregorianCalendar)
        {
            var greg = copy.OptionalCalendars.OfType<GregorianCalendar>().FirstOrDefault() ?? new GregorianCalendar();
            try { copy.DateTimeFormat.Calendar = greg; } catch (ArgumentOutOfRangeException) { copy = CultureInfo.InvariantCulture; }
        }
        return copy;
    }

    public static string Pattern(CultureInfo culture) => culture.DateTimeFormat.ShortDatePattern + " " + culture.DateTimeFormat.ShortTimePattern;
    public static bool Uses12Hour(CultureInfo culture) => culture.DateTimeFormat.ShortTimePattern.Contains("tt", StringComparison.Ordinal);
    public static string Format(DateTime value, CultureInfo culture) => value.ToString(Pattern(culture), culture);

    /// <summary>Reads a date and time in the culture's own short format (or the older yyyy-MM-dd HH:mm). A date without a time, a time without a date and text that is neither are refused. Seconds are dropped.</summary>
    public static bool TryParse(string? text, CultureInfo culture, out DateTime value)
    {
        value = default;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 64) return false;
        DateTime parsed;
        // A date on its own (in the short or long date format) is refused whatever era or spacing the culture adds to it.
        if (DateTime.TryParseExact(text, new[] { culture.DateTimeFormat.ShortDatePattern, culture.DateTimeFormat.LongDatePattern }, culture, DateTimeStyles.AllowWhiteSpaces, out _)) return false;
        if (!DateTime.TryParseExact(text, new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-ddTHH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
            && !DateTime.TryParseExact(text, new[] { Pattern(culture), culture.DateTimeFormat.ShortDatePattern + " " + culture.DateTimeFormat.LongTimePattern }, culture, DateTimeStyles.AllowWhiteSpaces, out parsed)
            && !DateTime.TryParse(text, culture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault, out parsed))
            return false;
        if (parsed.Year < 2000 || parsed.Year > 2200) return false;
        // The control asks for both parts: a date alone ("05.11.2026") has nothing after the date, so it is refused (a time alone has no valid year and fails above).
        if (!text.Any(char.IsWhiteSpace) && !text.Contains('T')) return false;
        value = new DateTime(parsed.Year, parsed.Month, parsed.Day, parsed.Hour, parsed.Minute, 0, DateTimeKind.Unspecified);
        return true;
    }

    /// <summary>Checks text against the clock: empty, unreadable, a time that does not exist or repeats (clock change) and the past are refused with a localized message; otherwise the instant is returned.</summary>
    public static ScheduleCheck Check(string? text, CultureInfo culture, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(null, null, Loc.T("schedule.error.empty"));
        if (!TryParse(text, culture, out var local)) return new(null, null, Loc.T("schedule.error.format", Format(new DateTime(now.Year, now.Month, now.Day, 23, 0, 0), culture)));
        return Check(local, zone, now);
    }

    public static ScheduleCheck Check(DateTime local, TimeZoneInfo zone, DateTimeOffset now)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local)) return new(local, null, Loc.T("validation.datetime_ambiguous"));
        var at = new DateTimeOffset(local, zone.GetUtcOffset(local));
        if (at <= now) return new(local, null, Loc.T("validation.datetime_future"));
        return new(local, at, null);
    }

    /// <summary>"Starts Tuesday November 5 at 11:00 PM": the weekday and month names come from the culture; the year only appears when it is not this year.</summary>
    public static string Words(DateTime value, CultureInfo culture, DateTime today)
    {
        var f = culture.DateTimeFormat;
        string day = f.GetDayName(value.DayOfWeek) + " " + value.ToString(f.MonthDayPattern, culture);
        if (value.Year != today.Year) day += " " + value.Year.ToString(culture);
        return Loc.T("schedule.starts", day, value.ToString(f.ShortTimePattern, culture));
    }

    public static DateTime InOneHour(DateTime now) => Truncate(now.AddHours(1));
    /// <summary>Tonight at 23:00; after 23:00 it is tomorrow at 23:00 (never the past).</summary>
    public static DateTime Tonight(DateTime now) { var t = now.Date.AddHours(23); return t <= now ? t.AddDays(1) : t; }
    public static DateTime TomorrowMorning(DateTime now) => now.Date.AddDays(1).AddHours(8);
    private static DateTime Truncate(DateTime v) => new(v.Year, v.Month, v.Day, v.Hour, v.Minute, 0, DateTimeKind.Unspecified);
}
