using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using Microsoft.Win32;
using TerminalBloops.App.Capture;
using TerminalBloops.App.ViewModels;
using TerminalBloops.Core;

namespace TerminalBloops.App.Services;

public sealed class AppController : IDisposable
{
    private readonly MainViewModel vm;
    private readonly Dispatcher dispatcher;
    private readonly HistoryStore store;
    private readonly string dataDirectory;
    private readonly string userSid;
    private readonly string userName = WindowsIdentity.GetCurrent().Name;
    private readonly TerminalClassifier classifier = new();
    private readonly WindowObserver observer = new();
    private readonly NotificationManager notifications;
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<object> inbox = Channel.CreateBounded<object>(new BoundedChannelOptions(8192) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly List<WindowObservation> pendingObservations = [];
    private readonly Dictionary<string, DateTimeOffset> pendingNotifications = [];
    private readonly DispatcherTimer refreshTimer;
    private readonly SemaphoreSlim setupLock = new(1,1);
    private SysmonCollector? collector;
    private Task? worker;
    private AppSettings settings;
    private bool dirty = true;
    private bool refreshing;
    private bool disposed;
    private DateTimeOffset lastPrune = DateTimeOffset.MinValue;

    public AppController(MainViewModel vm, Dispatcher dispatcher, string dataDirectory, string userSid, Action<string?> openHistory)
    {
        this.vm = vm; this.dispatcher = dispatcher; this.dataDirectory = dataDirectory; this.userSid = userSid;
        store = new HistoryStore(Path.Combine(dataDirectory, "history.db"));
        settings = store.LoadSettings();
        vm.SetSettings(settings);
        notifications = new NotificationManager(openHistory);
        vm.FilterChanged += () => dirty = true;
        vm.SelectionChanged += UpdateSelection;
        vm.NotificationsChanged += value => ChangeSettings(settings with { NotificationsEnabled = value });
        vm.StartAtLoginChanged += value => { ChangeSettings(settings with { StartAtLogin = value }); SetStartAtLogin(value); };
        vm.LauncherMuteChanged += (record, muted) => ChangeSettings(NotificationPolicy.SetLauncherMuted(settings, record, muted));
        vm.SetupRequested += async () => await SetupRecorderAsync();
        refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(450), DispatcherPriority.Background, async (_, _) => await RefreshAsync(), dispatcher);
    }

    public async Task StartAsync()
    {
        worker = Task.Run(ProcessInboxAsync);
        var setupResultPath = Path.Combine(dataDirectory,"setup-result.json");
        if (File.Exists(setupResultPath))
        {
            try
            {
                using var result = JsonDocument.Parse(await File.ReadAllTextAsync(setupResultPath));
                if(result.RootElement.TryGetProperty("message",out var message) && message.GetString() is { Length: > 0 } text)
                    AddNotice(new(DateTimeOffset.UtcNow,"Last recorder setup",text.Replace("\0","")));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            { AddNotice(new(DateTimeOffset.UtcNow,"Setup status","The previous setup result could not be read. Recorder health is checked independently.")); }
        }
        AddNotice(new(DateTimeOffset.UtcNow, "Observer", "Window observation started. Windows shown while Terminal Bloops was closed cannot be reconstructed."));
        observer.ObservationReceived += Enqueue;
        observer.Notice += Enqueue;
        observer.Start();
        SetStartAtLogin(settings.StartAtLogin);
        await StartCollectorAsync();
    }

    private async Task StartCollectorAsync()
    {
        var previousCollector = collector;
        if (previousCollector is not null) await Task.Run(previousCollector.Dispose);
        var checkpoint = store.GetCheckpoint();
        collector = new SysmonCollector { ExpectedCheckpointFingerprint = checkpoint.EventFingerprint };
        collector.EventReceived += Enqueue;
        collector.Notice += Enqueue;
        try { await collector.StartAsync(checkpoint.BookmarkXml, stop.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AddNotice(new(DateTimeOffset.UtcNow, "Recorder unavailable", ex.Message)); }
    }

    private void Enqueue(object item)
    {
        try { inbox.Writer.WriteAsync(item, stop.Token).AsTask().GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
    }

    private async Task ProcessInboxAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                while (inbox.Reader.TryRead(out var item))
                {
                    switch (item)
                    {
                        case CaptureEnvelope envelope:
                            if (envelope.Started is { } record) envelope = envelope with { Started = classifier.Classify(record) };
                            var change = store.Apply(envelope);
                            if (change.Record is { } completed && !envelope.IsReplay && change.NewlyCompleted)
                                pendingNotifications[completed.Id] = DateTimeOffset.UtcNow.AddMilliseconds(750);
                            dirty = true;
                            break;
                        case WindowObservation observation:
                            if (!store.ApplyObservation(observation)) pendingObservations.Add(observation);
                            dirty = true;
                            break;
                        case CaptureNotice notice:
                            AddNotice(notice);
                            break;
                    }
                }
                pendingObservations.Sort((a,b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
                for (var i = 0; i < pendingObservations.Count;)
                {
                    var observation = pendingObservations[i];
                    if (store.ApplyObservation(observation)) { pendingObservations.RemoveAt(i); dirty = true; }
                    else if (DateTimeOffset.UtcNow - observation.TimestampUtc > TimeSpan.FromSeconds(30))
                    {
                        pendingObservations.RemoveAt(i);
                        if (!observation.IsClosed)
                            AddNotice(new(observation.TimestampUtc, "Command unresolved", $"Window observed at {observation.TimestampUtc.ToLocalTime():HH:mm:ss.fff}; owner PID {observation.ProcessId}, class {observation.WindowClass}, title '{observation.Title}'. Its command could not be matched to a process identity in recorder history."));
                    }
                    else i++;
                }
                foreach (var (id, due) in pendingNotifications.ToArray())
                {
                    if (due > DateTimeOffset.UtcNow) continue;
                    pendingNotifications.Remove(id);
                    if (store.Get(id) is { } record)
                        await dispatcher.InvokeAsync(() =>
                        {
                            if (NotificationPolicy.ShouldNotify(record, settings, userName, false, true)) notifications.Show(record);
                        });
                }
                if (DateTimeOffset.UtcNow - lastPrune > TimeSpan.FromMinutes(10))
                {
                    lastPrune = DateTimeOffset.UtcNow;
                    var pruned = store.Prune(lastPrune);
                    if (pruned > 0) AddNotice(new(lastPrune, "Retention", "Older history was pruned to keep local storage bounded."));
                }
                await Task.Delay(100, stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _ = dispatcher.BeginInvoke(() => vm.SetStatus("Recording paused: " + ex.Message));
            try { store.AddNotice(new(DateTimeOffset.UtcNow, "Recording gap", "Ingestion stopped after a local storage error. Restart after correcting the problem; retained Sysmon events will be replayed.")); } catch { }
        }
    }

    private void AddNotice(CaptureNotice notice)
    {
        store.AddNotice(notice);
        dirty = true;
        if (notice.Kind is "recorder-ready" or "recorder-unavailable" or "producer-stopped" or "producer-ready" or "producer-unavailable" or "producer-status-unknown" or "capture-error" or "recording-gap" or "Recording gap" or "Recorder unavailable" or "Setup failed" or "Setup cancelled")
            dispatcher.BeginInvoke(() => vm.SetStatus(notice.Message));
    }

    private async Task RefreshAsync()
    {
        if (!dirty || refreshing || disposed) return;
        refreshing = true; dirty = false;
        try
        {
            var evidence = vm.EvidenceFilter switch { "Observed" => WindowEvidence.Observed, "Unconfirmed" => WindowEvidence.Unconfirmed, "Unresolved" => WindowEvidence.CommandUnresolved, _ => (WindowEvidence?)null };
            var filter = new HistoryFilter(userName, vm.Search, vm.BriefOnly, evidence);
            var result = await Task.Run(() => (Records: store.Query(filter), Notices: store.GetNotices()));
            if (disposed) return;
            vm.ReplaceItems(result.Records);
            vm.Notices.Clear(); foreach (var notice in result.Notices) vm.Notices.Add(notice);
            UpdateSelection(vm.Selected);
        }
        catch (ObjectDisposedException) when (disposed) { }
        catch (Microsoft.Data.Sqlite.SqliteException) when (disposed) { }
        catch (Exception ex) { vm.SetStatus("History could not be read: " + ex.Message); }
        finally { refreshing = false; }
    }

    private void UpdateSelection(ProcessRecord? record)
    {
        vm.ReplaceAncestors(record is null ? [] : store.GetAncestors(record.Id));
        vm.SelectedLauncherMuted = record is not null && settings.MutedLaunchers.Contains(NotificationPolicy.LauncherKey(record), StringComparer.OrdinalIgnoreCase);
    }

    public void SelectEvent(string id)
    {
        var record = store.Get(id);
        if (record is null) return;
        vm.Search = ""; vm.BriefOnly = false; vm.EvidenceFilter = "All";
        if (!vm.Items.Any(item => item.Id == id)) vm.Items.Insert(0,record);
        vm.Selected = record;
    }

    private void ChangeSettings(AppSettings next)
    {
        settings = next; store.SaveSettings(settings); vm.SetSettings(settings);
        if (!settings.NotificationsEnabled) notifications.Clear();
        UpdateSelection(vm.Selected);
    }

    private void SetStartAtLogin(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
            if (enabled)
            {
                var executable = Environment.ProcessPath!;
                var command = $"\"{executable}\" --tray";
                if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase))
                    command = $"\"{executable}\" \"{typeof(App).Assembly.Location}\" --tray";
                key.SetValue("TerminalBloops",command);
            }
            else key.DeleteValue("TerminalBloops",false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { AddNotice(new(DateTimeOffset.UtcNow,"Startup unavailable", "Could not update start-at-login: " + ex.Message)); }
    }

    public async Task SetupRecorderAsync()
    {
        if (!await setupLock.WaitAsync(0)) return;
        try
        {
            var script = Path.Combine(AppContext.BaseDirectory, "scripts", "Setup-Recorder.ps1");
            if (!File.Exists(script))
            {
                var cursor = new DirectoryInfo(AppContext.BaseDirectory);
                while (cursor is not null && !File.Exists(Path.Combine(cursor.FullName,"scripts","Setup-Recorder.ps1"))) cursor = cursor.Parent;
                if (cursor is not null) script = Path.Combine(cursor.FullName,"scripts","Setup-Recorder.ps1");
            }
            if (!File.Exists(script)) { AddNotice(new(DateTimeOffset.UtcNow,"Setup unavailable","Setup-Recorder.ps1 is missing. Keep the scripts folder beside TerminalBloops.exe.")); return; }
            vm.SetStatus("Setting up the recorder. Windows may request administrator permission.");
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -UserSid \"{userSid}\" -DataDirectory \"{dataDirectory}\""
            };
            using var process = Process.Start(start);
            if (process is null) throw new InvalidOperationException("Windows did not launch recorder setup.");
            await process.WaitForExitAsync();
            var resultPath = Path.Combine(dataDirectory,"setup-result.json");
            var message = process.ExitCode == 0 ? "Recorder setup completed." : $"Recorder setup failed (exit {process.ExitCode}).";
            if (File.Exists(resultPath))
            {
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
                if (json.RootElement.TryGetProperty("message",out var detail)) message=detail.GetString() ?? message;
            }
            AddNotice(new(DateTimeOffset.UtcNow,process.ExitCode == 0 ? "Setup" : "Setup failed",message));
            if (process.ExitCode == 0) await StartCollectorAsync();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { AddNotice(new(DateTimeOffset.UtcNow,"Setup cancelled","Administrator setup was cancelled. Persistent recording is not enabled by this action.")); }
        catch (Exception ex) { AddNotice(new(DateTimeOffset.UtcNow,"Setup failed",ex.Message)); }
        finally { setupLock.Release(); }
    }

    public void Dispose()
    {
        disposed = true; refreshTimer.Stop(); stop.Cancel(); inbox.Writer.TryComplete();
        collector?.Dispose(); observer.Dispose(); notifications.Dispose();
        // Avoid waiting on a worker that may be dispatching onto this UI thread.
        _ = Task.Run(async () => { if(worker is not null) await worker; store.Dispose(); stop.Dispose(); });
    }
}
