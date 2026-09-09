using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using TerminalBloops.App.Capture;
using TerminalBloops.Core;

namespace TerminalBloops.App.Diagnostics;

public static class CaptureSmokeTest
{
    public static async Task RunAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var captured = new ConcurrentQueue<CaptureEnvelope>();
        var notices = new ConcurrentQueue<CaptureNotice>();
        using var collector = new SysmonCollector();
        collector.EventReceived += captured.Enqueue;
        collector.Notice += notices.Enqueue;
        await collector.StartAsync(null,CancellationToken.None);
        // Record known, harmless commands. No console output or other processes' command lines are exported.
        var marker = "terminal-bloops-check-" + Guid.NewGuid().ToString("N");
        using var child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"cmd.exe"))
        {
            Arguments="/d /c echo " + marker, UseShellExecute=false, CreateNoWindow=true,
            RedirectStandardOutput=true, RedirectStandardError=true
        }) ?? throw new InvalidOperationException("Could not launch the controlled capture fixture.");
        await child.StandardOutput.ReadToEndAsync();
        await child.WaitForExitAsync();
        var deadline=DateTimeOffset.UtcNow.AddSeconds(20);
        CaptureEnvelope? start=null;
        CaptureEnvelope? end=null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            start=captured.FirstOrDefault(item => item.Started?.CommandLine.Contains(marker,StringComparison.Ordinal)==true);
            if (start?.Started is { } process) end=captured.FirstOrDefault(item=>item.Exited?.Id==process.Id);
            if (start is not null && end is not null) break;
            await Task.Delay(100);
        }
        if (start?.Started is not { } record || end?.Exited is not { } exited)
            throw new InvalidOperationException("The controlled command did not produce both readable process-start and process-exit events within 20 seconds. " + string.Join(" ",notices.Select(n=>n.Message)));

        var classification=new TerminalClassifier().Classify(record);
        using var testStore=new HistoryStore(Path.Combine(outputDirectory,"capture-test.db"));
        testStore.Apply(start with { Started=classification });
        testStore.Apply(end);
        var saved=testStore.Get(record.Id)!;
        using var replay=new SysmonCollector { ExpectedCheckpointFingerprint=testStore.GetCheckpoint().EventFingerprint };
        var replayNotices=new ConcurrentQueue<CaptureNotice>();
        replay.Notice+=replayNotices.Enqueue;
        await replay.StartAsync(testStore.GetCheckpoint().BookmarkXml,CancellationToken.None);
        var results=new
        {
            passed=classification.IsTerminal && saved.IsBrief && !start.IsReplay && !end.IsReplay && record.User.Equals(WindowsIdentity.GetCurrent().Name,StringComparison.OrdinalIgnoreCase),
            user=WindowsIdentity.GetCurrent().Name, startCaptured=true, exitCaptured=true, commandMatched=true,
            parentRecorded=!string.IsNullOrWhiteSpace(record.ParentImage), durationMs=saved.DurationMs,
            visibility=saved.EvidenceLabel, liveEvents=!start.IsReplay && !end.IsReplay,
            resumedFromBookmark=true, channelReadAsAdministrator=new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)
        };
        File.WriteAllText(Path.Combine(outputDirectory,"capture-checks.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions { WriteIndented=true }));
        if (!results.passed) throw new InvalidOperationException("A live capture check failed; see capture-checks.json.");
    }
}
