namespace TerminalBloops.Core;

public static class NotificationPolicy
{
    public static bool ShouldNotify(ProcessRecord record, AppSettings settings, string currentUser, bool isReplay, bool newlyCompleted)
        => !isReplay && newlyCompleted && settings.NotificationsEnabled && record.IsBrief && record.IsTimelineEntry
            && string.Equals(record.User, currentUser, StringComparison.OrdinalIgnoreCase)
            && !settings.MutedLaunchers.Contains(LauncherKey(record), StringComparer.OrdinalIgnoreCase);

    public static string LauncherKey(ProcessRecord record) => record.ParentImage.Trim();

    public static AppSettings SetLauncherMuted(AppSettings settings, ProcessRecord record, bool muted)
    {
        var key = LauncherKey(record);
        if (string.IsNullOrEmpty(key)) return settings;
        var paths = new HashSet<string>(settings.MutedLaunchers, StringComparer.OrdinalIgnoreCase);
        if (muted) paths.Add(key); else paths.Remove(key);
        return settings with { MutedLaunchers = paths.Order(StringComparer.OrdinalIgnoreCase).ToArray() };
    }
}
