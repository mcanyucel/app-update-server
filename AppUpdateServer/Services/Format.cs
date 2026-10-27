namespace AppUpdateServer.Services;

/// <summary>Display helpers shared by the pages.</summary>
public static class Format
{
    public static string Size(long bytes) =>
        bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
            _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB",
        };

    /// <summary>Timestamps are stored as UTC; say so rather than implying local time.</summary>
    public static string Utc(DateTime uploadedAt) =>
        $"{uploadedAt:yyyy-MM-dd HH:mm} UTC";

    public static string Relative(DateTime uploadedAt)
    {
        var elapsed = DateTime.UtcNow - uploadedAt;

        return elapsed switch
        {
            { TotalSeconds: < 60 } => "just now",
            { TotalMinutes: < 60 } => Plural((int)elapsed.TotalMinutes, "minute"),
            { TotalHours: < 24 } => Plural((int)elapsed.TotalHours, "hour"),
            { TotalDays: < 31 } => Plural((int)elapsed.TotalDays, "day"),
            { TotalDays: < 365 } => Plural((int)(elapsed.TotalDays / 30), "month"),
            _ => Plural((int)(elapsed.TotalDays / 365), "year"),
        };

        static string Plural(int count, string unit) =>
            $"{count} {unit}{(count == 1 ? "" : "s")} ago";
    }
}
