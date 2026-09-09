using System.Collections.Concurrent;
using System.IO;
using System.Reflection.PortableExecutable;
using TerminalBloops.Core;

namespace TerminalBloops.App.Capture;

/// <summary>Classifies executable files without launching them or querying another shell.</summary>
public sealed class TerminalClassifier
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "conhost.exe", "openconsole.exe", "windowsterminal.exe", "wt.exe", "mintty.exe",
        "wezterm-gui.exe", "alacritty.exe", "conemu.exe", "conemu64.exe"
    };
    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "wsl.exe", "bash.exe", "sh.exe", "zsh.exe",
        "fish.exe", "nu.exe", "cscript.exe"
    };
    private readonly ConcurrentDictionary<string, PeClassification> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ProcessRecord Classify(ProcessRecord process)
    {
        // PID reuse is possible in replayed history, so compare the image as well.
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath) &&
            string.Equals(process.Image, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            return process with { IsTerminal = false, IsHost = false, ClassificationReason = "Recorder" };

        var name = process.Name;
        if (Hosts.Contains(name))
            return process with { IsTerminal = true, IsHost = true, ClassificationReason = "Terminal host" };
        if (Shells.Contains(name))
            return process with { IsTerminal = true, IsHost = false, ClassificationReason = "Command-line shell" };

        var consoleImage = IsConsoleImage(process.Image);
        return process with
        {
            IsTerminal = consoleImage,
            IsHost = false,
            ClassificationReason = consoleImage ? "Console executable (PE subsystem)" : ""
        };
    }

    private bool IsConsoleImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            if (_cache.TryGetValue(path, out var previous) && previous.Length == info.Length &&
                previous.LastWriteUtc == info.LastWriteTimeUtc) return previous.IsConsole;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var isConsole = reader.PEHeaders.PEHeader?.Subsystem == Subsystem.WindowsCui;
            // Keep the cache bounded even on machines that create many temporary executables.
            if (_cache.Count >= 8192) _cache.Clear();
            _cache[path] = new PeClassification(info.Length, info.LastWriteTimeUtc, isConsole);
            return isConsole;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException
                                       or ArgumentException or NotSupportedException)
        {
            // Deleted, protected, or malformed images remain unclassified; no visibility is inferred.
            return false;
        }
    }

    private sealed record PeClassification(long Length, DateTime LastWriteUtc, bool IsConsole);
}
