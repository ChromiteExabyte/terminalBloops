using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using TerminalBloops.App.Capture;
using TerminalBloops.Core;

namespace TerminalBloops.App.Diagnostics;

/// <summary>
/// Explicit, opt-in live verification. Launches one visible, known command fixture and never
/// changes terminal settings, installs a recorder, or saves unrelated window titles/commands.
/// </summary>
public static class WindowSmokeTest
{
    public static async Task RunAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var marker = "TerminalBloops-WindowTest-" + Guid.NewGuid().ToString("N");
        var beganUtc = DateTimeOffset.UtcNow;
        var elapsed = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var observer = new WindowObserver();
        using var collector = new SysmonCollector();
        var gate = new object();
        var windows = new List<WindowObservation>();
        var hosts = new List<FixtureHost>();
        var noticeKinds = new HashSet<string>(StringComparer.Ordinal);
        ProcessRecord? capturedFixture = null;
        Process? fixture = null;
        int? fixturePid = null;
        DateTimeOffset? fixtureCreationUtc = null;
        var passed = false;
        Exception? failure = null;
        FixtureWindow[] matchedWindows = [];

        collector.EventReceived += envelope =>
        {
            if (envelope.Started is not { } process || process.StartUtc < beganUtc) return;
            lock (gate)
            {
                if (process.Name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) &&
                    process.CommandLine.Contains(marker, StringComparison.Ordinal))
                    capturedFixture = process;
                if (hosts.Count < 256 && process.Name.ToLowerInvariant() is "conhost.exe" or "openconsole.exe" or "windowsterminal.exe")
                    hosts.Add(new FixtureHost(process.ProcessId, process.ParentId, process.StartUtc));
            }
        };
        observer.ObservationReceived += observation =>
        {
            if (!observation.IsTerminalWindow || observation.TimestampUtc < beganUtc) return;
            lock (gate)
            {
                // Candidate metadata is transient only. The report below includes fixture matches
                // and deliberately has no raw title or command-line property.
                if (windows.Count < 2048) windows.Add(observation);
            }
        };
        void ObserveNotice(CaptureNotice notice)
        {
            lock (gate) noticeKinds.Add(notice.Kind);
        }
        collector.Notice += ObserveNotice;
        observer.Notice += ObserveNotice;

        try
        {
            observer.Start();
            await collector.StartAsync(null, deadline.Token).WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var commandPath = Path.Combine(systemDirectory, "cmd.exe");
            var pingPath = Path.Combine(systemDirectory, "ping.exe");

            // /d disables cmd AutoRun entries. /s /c strips this outer command-string pair of
            // quotes, preserving the quoted, absolute ping path inside. Only loopback is pinged.
            // Windows chooses its configured console/terminal host; no undocumented conhost CLI.
            fixture = Process.Start(new ProcessStartInfo
            {
                FileName = commandPath,
                Arguments = $"/d /s /c \"title {marker} & echo Terminal Bloops window verification & \"{pingPath}\" -n 3 -w 1000 127.0.0.1 >nul\"",
                WorkingDirectory = systemDirectory,
                UseShellExecute = false,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal
            }) ?? throw new InvalidOperationException("The controlled cmd.exe fixture did not start.");
            fixturePid = fixture.Id;
            fixtureCreationUtc = new DateTimeOffset(fixture.StartTime.ToUniversalTime());

            while (!deadline.IsCancellationRequested)
            {
                ProcessRecord? captured;
                lock (gate)
                {
                    captured = capturedFixture;
                    matchedWindows = MatchFixtureWindows(windows, hosts, marker, fixturePid.Value,
                        fixtureCreationUtc.Value, captured);
                }
                if (captured is not null && captured.ProcessId == fixturePid &&
                    SameMillisecond(captured.StartUtc, fixtureCreationUtc) &&
                    matchedWindows.Any(window => !window.IsClosed) && fixture.HasExited)
                {
                    passed = true;
                    break;
                }
                await Task.Delay(50, deadline.Token);
            }
            if (!passed)
                throw new TimeoutException("The controlled fixture did not produce both a matching Sysmon creation event and an identified terminal-window observation within 15 seconds.");
        }
        catch (OperationCanceledException)
        {
            failure = new TimeoutException("The controlled fixture did not produce both a matching Sysmon creation event and an identified terminal-window observation within 15 seconds.");
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            deadline.Cancel();
            // Handlers only append in-memory metadata; neither disposal waits for the UI thread.
            observer.Dispose();
            collector.Dispose();
            if (fixture is not null)
            {
                try
                {
                    if (!fixture.HasExited)
                    {
                        // Terminate only our retained cmd process handle, never a terminal host
                        // or an entire tree that might contain a shared terminal window.
                        fixture.Kill();
                        await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
                {
                    failure ??= new InvalidOperationException("The controlled fixture could not finish cleanup.", exception);
                    passed = false;
                }
                finally { fixture.Dispose(); }
            }

            ProcessRecord? captured;
            string[] healthKinds;
            lock (gate)
            {
                captured = capturedFixture;
                healthKinds = noticeKinds.Order(StringComparer.Ordinal).ToArray();
                if (fixturePid is { } pid && fixtureCreationUtc is { } created)
                    matchedWindows = MatchFixtureWindows(windows, hosts, marker, pid, created, captured);
                windows.Clear();
                hosts.Clear();
            }
            var report = new
            {
                Passed = passed,
                Failure = failure?.Message,
                BeganUtc = beganUtc,
                ElapsedMilliseconds = elapsed.ElapsedMilliseconds,
                ObservationDeadlineSeconds = 15,
                Fixture = new
                {
                    Marker = marker,
                    ProcessId = fixturePid,
                    ProcessCreationUtc = fixtureCreationUtc,
                    CapturedProcessId = captured?.ProcessId,
                    CapturedProcessGuid = captured?.Id,
                    CapturedStartUtc = captured?.StartUtc,
                    SysmonCreationSeen = captured is not null,
                    ExactClassicClientMappingSeen = matchedWindows.Any(window => window.Match == "exact-classic-client")
                },
                Observations = matchedWindows,
                HealthNoticeKinds = healthKinds,
                Limit = "Window observation does not prove unobscured pixels or identify a Windows Terminal pane. Raw window titles and command lines are omitted."
            };
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "window-capture.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static FixtureWindow[] MatchFixtureWindows(IEnumerable<WindowObservation> observations,
        IEnumerable<FixtureHost> hosts, string marker, int fixturePid, DateTimeOffset fixtureStart,
        ProcessRecord? capturedFixture)
    {
        var output = new List<FixtureWindow>();
        foreach (var observation in observations)
        {
            // A visible-window signal alone cannot establish which lifetime of a reused PID owns it.
            if (!observation.IsTerminalWindow || observation.ProcessStartUtc is null) continue;
            string? match = null;
            if (observation.WindowClass == "ConsoleWindowClass" && observation.ClientProcessId == fixturePid &&
                SameMillisecond(observation.ClientProcessStartUtc, fixtureStart))
                match = "exact-classic-client";
            else if (observation.ProcessId == fixturePid && SameMillisecond(observation.ProcessStartUtc, fixtureStart))
                match = "exact-window-owner";
            else if (capturedFixture is not null && hosts.Any(host => host.ProcessId == observation.ProcessId &&
                         host.ParentGuid == capturedFixture.Id && SameMillisecond(host.StartUtc, observation.ProcessStartUtc)))
                match = "fixture-child-host";
            else if (observation.Title.Contains(marker, StringComparison.Ordinal))
                match = "unique-fixture-title";
            if (match is null) continue;

            output.Add(new FixtureWindow(observation.ProcessId, observation.ProcessStartUtc.Value,
                observation.ClientProcessId == fixturePid ? observation.ClientProcessId : null,
                observation.ClientProcessId == fixturePid ? observation.ClientProcessStartUtc : null,
                observation.TimestampUtc, observation.IsClosed, observation.WindowClass, observation.Hwnd, match));
        }
        return output.ToArray();
    }

    private static bool SameMillisecond(DateTimeOffset? left, DateTimeOffset? right) =>
        left is { } first && right is { } second && first.ToUnixTimeMilliseconds() == second.ToUnixTimeMilliseconds();

    private sealed record FixtureHost(int ProcessId, string ParentGuid, DateTimeOffset StartUtc);
    private sealed record FixtureWindow(int ProcessId, DateTimeOffset ProcessStartUtc, int? ClientProcessId,
        DateTimeOffset? ClientProcessStartUtc, DateTimeOffset TimestampUtc, bool IsClosed, string WindowClass,
        long Hwnd, string Match);
}
