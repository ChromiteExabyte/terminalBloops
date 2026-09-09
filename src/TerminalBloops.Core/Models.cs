namespace TerminalBloops.Core;

public enum WindowEvidence { Unconfirmed, Observed, CommandUnresolved }

public sealed record ProcessRecord
{
    public required string Id { get; init; }
    public int ProcessId { get; init; }
    public int SessionId { get; init; }
    public DateTimeOffset StartUtc { get; init; }
    public DateTimeOffset? EndUtc { get; init; }
    public string Image { get; init; } = "";
    public string CommandLine { get; init; } = "";
    public string User { get; init; } = "";
    public string ParentId { get; init; } = "";
    public string ParentImage { get; init; } = "";
    public string ParentCommandLine { get; init; } = "";
    public bool IsTerminal { get; init; }
    public bool IsHost { get; init; }
    public string ClassificationReason { get; init; } = "";
    public WindowEvidence Evidence { get; init; }
    public string WindowTitle { get; init; } = "";
    public DateTimeOffset? WindowFirstSeenUtc { get; init; }
    public DateTimeOffset? WindowLastSeenUtc { get; init; }
    public string? CorrelatedClientId { get; init; }
    public string Name => Path.GetFileName(Image.Replace('\\', '/'));
    public string LauncherName => string.IsNullOrWhiteSpace(ParentImage) ? "Unknown launcher" : Path.GetFileName(ParentImage.Replace('\\', '/'));
    public double? DurationMs => EndUtc is { } end && end >= StartUtc ? (end - StartUtc).TotalMilliseconds : null;
    public bool IsBrief => DurationMs is >= 0 and <= 5000;
    public bool IsTimelineEntry => IsTerminal && (!IsHost || CorrelatedClientId is null);
    public string EvidenceLabel => Evidence switch
    {
        WindowEvidence.Observed => "Window observed",
        WindowEvidence.CommandUnresolved => "Command unresolved",
        _ => "Visibility unconfirmed"
    };
}

public sealed record ProcessExit(string Id, int ProcessId, DateTimeOffset EndUtc);
public sealed record CaptureEnvelope(ProcessRecord? Started, ProcessExit? Exited, string BookmarkXml, long RecordId, bool IsReplay)
{
    public string Fingerprint => Started is { } start ? $"1:{start.Id}:{start.StartUtc.UtcTicks}"
        : Exited is { } end ? $"5:{end.Id}:{end.EndUtc.UtcTicks}" : "";
}
public sealed record CaptureNotice(DateTimeOffset AtUtc, string Kind, string Message);
public sealed record WindowObservation(int ProcessId, int? ClientProcessId, DateTimeOffset TimestampUtc,
    bool IsClosed, string Title, string WindowClass, long Hwnd, bool IsTerminalWindow,
    DateTimeOffset? ProcessStartUtc = null, DateTimeOffset? ClientProcessStartUtc = null);

public sealed record AppSettings
{
    public bool NotificationsEnabled { get; init; } = true;
    public bool StartAtLogin { get; init; } = true;
    public string[] MutedLaunchers { get; init; } = [];
}

public sealed record HistoryFilter(string User = "", string Search = "", bool BriefOnly = true,
    WindowEvidence? Evidence = null, int Limit = 500);

public sealed record RecorderCheckpoint(string? BookmarkXml, long RecordId, string? EventFingerprint = null);
