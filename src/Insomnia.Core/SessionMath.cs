using System.Globalization;

namespace Insomnia.Core;

public static class SessionMath
{
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(Config.MaxDurationMinutes);

    /// <summary>A fresh session, clamped to the 30-day ceiling. Null for a non-positive duration.</summary>
    public static Session? Start(DateTimeOffset now, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return null;
        if (duration > MaxDuration) duration = MaxDuration;
        var start = Truncate(now);
        return new Session { StartedAt = start, EndsAt = start + duration };
    }

    /// <summary>
    /// Adds time to a running session. The total from the start, extensions
    /// included, never passes 30 days. Returns the minutes actually added
    /// (0 when the ceiling is already reached).
    /// </summary>
    public static int Extend(Session s, DateTimeOffset now, TimeSpan add)
    {
        if (add <= TimeSpan.Zero) return 0;
        var baseline = s.EndsAt > now ? s.EndsAt : Truncate(now);
        var ceiling = s.StartedAt + MaxDuration;
        var newEnd = baseline + add;
        if (newEnd > ceiling) newEnd = ceiling;
        if (newEnd <= s.EndsAt) return 0;
        var added = (int)Math.Round((newEnd - s.EndsAt).TotalMinutes);
        s.EndsAt = newEnd;
        s.ExtensionsMinutes.Add(added);
        return added;
    }

    static DateTimeOffset Truncate(DateTimeOffset t) =>
        new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, t.Offset);

    /// <summary>Countdown text: "2d 03:04:05", "03:04:05", "04:05".</summary>
    public static string Countdown(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var total = (long)Math.Ceiling(t.TotalSeconds);
        long d = total / 86400, h = total / 3600 % 24, m = total / 60 % 60, s = total % 60;
        if (d > 0) return $"{d}d {h:00}:{m:00}:{s:00}";
        if (h > 0) return $"{h}:{m:00}:{s:00}";
        return $"{m}:{s:00}";
    }

    /// <summary>Short human text: "2d 3h", "4h 5m", "12m", "45s".</summary>
    public static string Short(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalDays >= 1) return t.Hours > 0 ? $"{(int)t.TotalDays}d {t.Hours}h" : $"{(int)t.TotalDays}d";
        if (t.TotalHours >= 1) return t.Minutes > 0 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{(int)t.TotalHours}h";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m";
        return $"{(int)t.TotalSeconds}s";
    }

    public static string Gap(TimeSpan t)
    {
        var s = (int)Math.Round(t.TotalSeconds);
        return s >= 60 ? $"{s / 60}m {s % 60}s" : $"{s}s";
    }

    /// <summary>
    /// Parses a CLI duration such as "90", "90m", "4h", "1d2h30m". A bare
    /// number is minutes.
    /// </summary>
    public static TimeSpan? ParseDuration(string text)
    {
        text = text.Trim().ToLowerInvariant();
        if (text.Length == 0) return null;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var bare))
            return bare > 0 ? TimeSpan.FromMinutes(bare) : null;
        var total = TimeSpan.Zero;
        var num = "";
        foreach (var c in text)
        {
            if (char.IsDigit(c)) { num += c; continue; }
            if (num.Length == 0 || !long.TryParse(num, out var n)) return null;
            total += c switch
            {
                'd' => TimeSpan.FromDays(n),
                'h' => TimeSpan.FromHours(n),
                'm' => TimeSpan.FromMinutes(n),
                's' => TimeSpan.FromSeconds(n),
                _ => TimeSpan.MinValue,
            };
            if (total < TimeSpan.Zero) return null;
            num = "";
        }
        if (num.Length > 0) return null;
        return total > TimeSpan.Zero ? total : null;
    }
}

/// <summary>The Days / Hours / Minutes pills.</summary>
public static class DurationInput
{
    public enum Field { Days, Hours, Minutes }

    public static int Max(Field f) => f switch { Field.Days => 30, Field.Hours => 23 * 31, _ => 59 * 31 * 24 };

    public static string Help(Field f) => f switch
    {
        Field.Days => "Up to 30 days",
        Field.Hours => "Hours",
        _ => "Minutes",
    };

    /// <summary>Keeps digits only, at most four.</summary>
    public static string Sanitize(string raw) => new string(raw.Where(char.IsAsciiDigit).Take(4).ToArray());

    public enum Outcome { Empty, Ok, TooLong, Zero }

    /// <summary>
    /// Reads the three pills. Every pill empty means "use the default preset".
    /// Over 30 days total is refused rather than silently clamped, so the user
    /// sees why.
    /// </summary>
    public static (Outcome outcome, TimeSpan duration) Read(string days, string hours, string minutes)
    {
        days = Sanitize(days); hours = Sanitize(hours); minutes = Sanitize(minutes);
        if (days.Length == 0 && hours.Length == 0 && minutes.Length == 0) return (Outcome.Empty, TimeSpan.Zero);
        long d = days.Length > 0 ? long.Parse(days) : 0;
        long h = hours.Length > 0 ? long.Parse(hours) : 0;
        long m = minutes.Length > 0 ? long.Parse(minutes) : 0;
        var total = TimeSpan.FromDays(d) + TimeSpan.FromHours(h) + TimeSpan.FromMinutes(m);
        if (total <= TimeSpan.Zero) return (Outcome.Zero, TimeSpan.Zero);
        if (total > SessionMath.MaxDuration) return (Outcome.TooLong, total);
        return (Outcome.Ok, total);
    }
}
