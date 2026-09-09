using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using TerminalBloops.Core;

namespace TerminalBloops.App.Capture;

/// <summary>Reads the persistent Sysmon stream. It never installs or reconfigures Sysmon.</summary>
public sealed class SysmonCollector : IDisposable
{
    public const string Channel = "Microsoft-Windows-Sysmon/Operational";
    private const int ErrorInsufficientBuffer = 122;
    private const uint SubscribeAfterBookmark = 3;
    private const uint SubscribeOldest = 2;
    private const uint SubscribeStrict = 0x10000;
    private readonly object _lifecycle = new();
    private readonly object _callbackGate = new();
    private readonly SubscribeCallback _callback;
    private nint _subscription;
    private nint _bookmark;
    private DateTimeOffset _startedUtc;
    private CancellationTokenRegistration _cancellationRegistration;
    private bool _started;
    private volatile bool _disposed;
    private int _recovering;
    private System.Threading.Timer? _healthTimer;
    private int _checkingHealth;
    private string? _lastHealthMessage;

    public event Action<CaptureEnvelope>? EventReceived;
    public event Action<CaptureNotice>? Notice;
    public string? ExpectedCheckpointFingerprint { get; set; }

    public SysmonCollector() => _callback = OnNativeEvent;

    public Task StartAsync(string? bookmarkXml, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("The recorder has already been started.");
            _started = true;
            _startedUtc = DateTimeOffset.UtcNow;
        }
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                lock (_lifecycle)
                {
                    if (_disposed) return;
                    Subscribe(bookmarkXml);
                }
            }
            finally
            {
                lock (_lifecycle)
                {
                    if (!_disposed)
                        _healthTimer = new System.Threading.Timer(_ => CheckProducerHealth(), null,
                            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
                }
                CheckProducerHealth();
            }
            // EvtClose waits for active native callbacks. Do not run it inside cancellation's
            // synchronous callback dispatch: other token callbacks may release a blocked consumer.
            _cancellationRegistration = cancellationToken.Register(() => { _ = Task.Run(Dispose); });
        }, cancellationToken);
    }

    private void Subscribe(string? bookmarkXml)
    {
        var hasBookmark = !string.IsNullOrWhiteSpace(bookmarkXml);
        if (hasBookmark && !string.IsNullOrWhiteSpace(ExpectedCheckpointFingerprint) &&
            !CheckpointMatches(bookmarkXml!, ExpectedCheckpointFingerprint))
        {
            Report("recording-gap", "The saved event no longer matches this log. The log may have been cleared or overwritten. Recovering up to seven days of available history.");
            hasBookmark = false;
        }
        _bookmark = EvtCreateBookmark(hasBookmark ? bookmarkXml : null);
        if (_bookmark == 0 && hasBookmark)
        {
            Report("recording-gap", "The saved recording position is invalid. Recovering up to seven days of available history.");
            hasBookmark = false;
            _bookmark = EvtCreateBookmark(null);
        }
        if (_bookmark == 0) throw NativeFailure("Create the event-log bookmark");

        // A fixed UTC cutoff, rather than a moving timediff predicate, keeps this subscription valid indefinitely.
        var cutoff = _startedUtc.AddDays(-7).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
        var query = hasBookmark
            ? "*[System[(EventID=1 or EventID=5)]]"
            : $"*[System[(EventID=1 or EventID=5) and TimeCreated[@SystemTime >= '{cutoff}']]]";
        _subscription = EvtSubscribe(0, 0, Channel, query, hasBookmark ? _bookmark : 0, 0,
            _callback, (hasBookmark ? SubscribeAfterBookmark : SubscribeOldest) | SubscribeStrict);

        if (_subscription == 0)
        {
            var error = Marshal.GetLastWin32Error();
            EvtClose(_bookmark);
            _bookmark = 0;
            if (hasBookmark && error is 1168 or 15011 or 15012)
            {
                Report("recording-gap", "The saved event-log position has expired or the log was cleared. Recovering up to seven days of available history.");
                Subscribe(null);
                return;
            }
            var message = error switch
            {
                5 => "Sysmon history is present but this account cannot read it. Recorder setup must grant access to this channel.",
                2 or 15007 => "Sysmon is not available. Complete recorder setup to capture launch commands and process history.",
                _ => $"The Sysmon recorder could not start: {new Win32Exception(error).Message} (Windows error {error})."
            };
            Report("recorder-unavailable", message);
            throw new Win32Exception(error, message);
        }
        Report("recorder-connected", "The Sysmon history subscription is connected. Existing history is being restored quietly.");
    }

    private void CheckProducerHealth()
    {
        if (_disposed || Interlocked.CompareExchange(ref _checkingHealth, 1, 0) != 0) return;
        try
        {
            var (kind, message) = EvaluateProducerHealth(ReadServiceRunning(), ReadChannelEnabled(),
                Volatile.Read(ref _subscription) != 0);
            var key = kind + ":" + message;
            if (!_disposed && !string.Equals(_lastHealthMessage, key, StringComparison.Ordinal))
            {
                _lastHealthMessage = key;
                Report(kind, message);
            }
        }
        catch (Exception ex)
        {
            var message = "The recorder's running state could not be checked: " + ex.Message;
            if (!_disposed && _lastHealthMessage != message)
            {
                _lastHealthMessage = message;
                Report("producer-status-unknown", message);
            }
        }
        finally { Interlocked.Exchange(ref _checkingHealth, 0); }
    }

    internal static (string Kind, string Message) EvaluateProducerHealth(bool? serviceRunning, bool? channelEnabled,
        bool readerConnected)
    {
        if (serviceRunning == false)
            return ("producer-stopped", "The Sysmon service is stopped or absent. Saved history remains available, but new process activity is not being recorded.");
        if (channelEnabled == false)
            return ("producer-stopped", "The Sysmon event channel is disabled or absent. New process history is not being recorded to this channel.");
        if (serviceRunning is null || channelEnabled is null)
            return ("producer-status-unknown", "The recorder's service or channel state could not be verified with this account's read permissions.");
        if (!readerConnected)
            return ("recorder-unavailable", "The Sysmon service is running and its channel is enabled, but this app is not connected to the process history.");
        return ("producer-ready", "The Sysmon service is running, its event channel is enabled, and process history is connected.");
    }

    private static bool? ReadServiceRunning()
    {
        var manager = OpenSCManager(null, null, 1); // SC_MANAGER_CONNECT only
        if (manager == 0) return null;
        try
        {
            bool? foundState = false;
            foreach (var name in new[] { "Sysmon64", "Sysmon" })
            {
                var service = OpenService(manager, name, 4); // SERVICE_QUERY_STATUS only
                if (service == 0)
                {
                    if (Marshal.GetLastWin32Error() != 1060) foundState = null;
                    continue;
                }
                try
                {
                    if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _))
                    {
                        foundState = null;
                        continue;
                    }
                    if (status.CurrentState == 4) return true; // SERVICE_RUNNING
                }
                finally { CloseServiceHandle(service); }
            }
            return foundState;
        }
        finally { CloseServiceHandle(manager); }
    }

    private static bool? ReadChannelEnabled()
    {
        var config = EvtOpenChannelConfig(0, Channel, 0);
        if (config == 0) return Marshal.GetLastWin32Error() is 2 or 15007 ? false : null;
        var buffer = Marshal.AllocHGlobal(16); // EVT_VARIANT's union + count + type
        try
        {
            if (!EvtGetChannelConfigProperty(config, 0, 0, 16, buffer, out _)) return null;
            if (Marshal.ReadInt32(buffer, 12) != 13) return null; // EvtVarTypeBoolean
            return Marshal.ReadInt32(buffer) != 0;
        }
        finally { Marshal.FreeHGlobal(buffer); EvtClose(config); }
    }

    private static bool CheckpointMatches(string bookmarkXml, string expectedFingerprint)
    {
        if (!SysmonCheckpointValidator.TryGetRecordId(bookmarkXml, out var recordId)) return false;
        var query = EvtQuery(0, Channel,
            $"*[System[EventRecordID={recordId.ToString(CultureInfo.InvariantCulture)}]]", 0x101);
        if (query == 0) throw NativeFailure("Read the saved event-log position");
        var events = new nint[1];
        try
        {
            if (!EvtNext(query, 1, events, 5000, 0, out var count))
            {
                var error = Marshal.GetLastWin32Error();
                if (error is 259 or 15011 or 15012) return false;
                throw new Win32Exception(error, "The saved event-log position could not be validated.");
            }
            return count == 1 && SysmonCheckpointValidator.Matches(expectedFingerprint, Render(events[0], 1));
        }
        finally
        {
            foreach (var handle in events) if (handle != 0) EvtClose(handle);
            EvtClose(query);
        }
    }

    private uint OnNativeEvent(uint action, nint context, nint nativeEvent)
    {
        if (_disposed) return 0;
        try
        {
            if (action == 0)
            {
                var error = unchecked((int)nativeEvent);
                Report("recording-gap", $"Windows reported missing or unavailable event-log records (error {error}). Available history will be reconnected.");
                ScheduleRecovery();
                return 0;
            }

            lock (_callbackGate)
            {
                if (_disposed) return 0;
                var xml = Render(nativeEvent, 1); // EvtRenderEventXml
                if (!EvtUpdateBookmark(_bookmark, nativeEvent)) throw NativeFailure("Update the recording position");
                var checkpoint = Render(_bookmark, 2); // EvtRenderBookmark
                var envelope = SysmonEventParser.Parse(xml, checkpoint, _startedUtc);
                if (envelope is not null) EventReceived?.Invoke(envelope);
            }
        }
        catch (Exception ex)
        {
            // Never let managed exceptions cross the Windows callback boundary.
            Report("capture-error", $"A process event could not be recorded: {ex.Message}");
        }
        return 0;
    }

    private void ScheduleRecovery()
    {
        if (Interlocked.CompareExchange(ref _recovering, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                lock (_lifecycle)
                {
                    if (_disposed) return;
                    // Closing off the callback thread avoids waiting for the callback itself.
                    CloseHandles();
                    _startedUtc = DateTimeOffset.UtcNow;
                    Subscribe(null);
                }
                CheckProducerHealth();
            }
            catch (Exception ex) { Report("recorder-unavailable", $"Recorder reconnection failed: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _recovering, 0); }
        });
    }

    private void Report(string kind, string message)
    {
        try { Notice?.Invoke(new CaptureNotice(DateTimeOffset.UtcNow, kind, message)); }
        catch { /* A UI notification handler must not unwind through native code. */ }
    }

    private static string Render(nint handle, uint flags)
    {
        EvtRender(0, handle, flags, 0, 0, out var used, out _);
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorInsufficientBuffer) throw new Win32Exception(error);
        var buffer = Marshal.AllocHGlobal(used);
        try
        {
            if (!EvtRender(0, handle, flags, used, buffer, out _, out _)) throw NativeFailure("Render the event");
            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static Win32Exception NativeFailure(string operation) => new(Marshal.GetLastWin32Error(), operation);

    private void CloseHandles()
    {
        if (_subscription != 0) { EvtClose(_subscription); _subscription = 0; }
        if (_bookmark != 0) { EvtClose(_bookmark); _bookmark = 0; }
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            _healthTimer?.Dispose();
            CloseHandles();
        }
        _cancellationRegistration.Unregister();
        GC.KeepAlive(_callback);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint SubscribeCallback(uint action, nint context, nint eventHandle);
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode;
        public uint CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenService(nint manager, string serviceName, uint desiredAccess);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(nint service, uint infoLevel, out ServiceStatusProcess status,
        int bufferSize, out int bytesNeeded);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);
    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint EvtOpenChannelConfig(nint session, string channelPath, uint flags);
    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtGetChannelConfigProperty(nint channelConfig, uint propertyId, uint flags,
        int bufferSize, nint propertyValue, out int propertyValueBufferUsed);
    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint EvtSubscribe(nint session, nint signalEvent, string channelPath, string query,
        nint bookmark, nint context, SubscribeCallback callback, uint flags);
    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint EvtCreateBookmark(string? bookmarkXml);
    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint EvtQuery(nint session, string path, string query, uint flags);
    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtNext(nint resultSet, int capacity, [Out] nint[] events, uint timeout,
        uint flags, out int returned);
    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtUpdateBookmark(nint bookmark, nint eventHandle);
    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtRender(nint context, nint fragment, uint flags, int bufferSize,
        nint buffer, out int bufferUsed, out int propertyCount);
    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtClose(nint handle);
}

/// <summary>Checks event identity as well as record number, which can be reused after a cleared log.</summary>
public static class SysmonCheckpointValidator
{
    public static bool TryGetRecordId(string bookmarkXml, out long recordId)
    {
        recordId = 0;
        try
        {
            var root = XElement.Parse(bookmarkXml);
            var entries = root.DescendantsAndSelf().Where(x => x.Name.LocalName == "Bookmark" &&
                string.Equals(x.Attribute("Channel")?.Value, SysmonCollector.Channel, StringComparison.OrdinalIgnoreCase)).ToArray();
            return entries.Length == 1 && long.TryParse(entries[0].Attribute("RecordId")?.Value,
                NumberStyles.None, CultureInfo.InvariantCulture, out recordId) && recordId > 0;
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException) { return false; }
    }

    public static bool Matches(string expectedFingerprint, string eventXml)
    {
        if (string.IsNullOrWhiteSpace(expectedFingerprint)) return false;
        try
        {
            var actual = SysmonEventParser.Parse(eventXml, "", DateTimeOffset.MaxValue);
            return actual is not null && string.Equals(expectedFingerprint, actual.Fingerprint, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException or FormatException or OverflowException)
        {
            return false;
        }
    }
}

/// <summary>Pure XML parsing, usable by tests without an installed recorder.</summary>
public static class SysmonEventParser
{
    public static CaptureEnvelope? Parse(string eventXml, string bookmarkXml, DateTimeOffset replayBoundaryUtc)
    {
        var root = XElement.Parse(eventXml);
        var ns = root.Name.Namespace;
        var system = root.Element(ns + "System") ?? throw new FormatException("The event has no System element.");
        var eventId = int.Parse(system.Element(ns + "EventID")?.Value ?? "0", CultureInfo.InvariantCulture);
        if (eventId is not (1 or 5)) return null;
        var fields = root.Element(ns + "EventData")?.Elements(ns + "Data")
            .Where(x => x.Attribute("Name") is not null)
            .ToDictionary(x => x.Attribute("Name")!.Value, x => x.Value, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Field(string name) => fields.GetValueOrDefault(name, "");
        int Number(string name) => int.TryParse(Field(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        if (!Guid.TryParse(Field("ProcessGuid"), out var guid)) throw new FormatException("The process event has no valid process GUID.");
        var timestampText = Field("UtcTime");
        if (string.IsNullOrWhiteSpace(timestampText)) timestampText = system.Element(ns + "TimeCreated")?.Attribute("SystemTime")?.Value ?? "";
        if (!DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
            throw new FormatException("The process event has no valid timestamp.");
        _ = long.TryParse(system.Element(ns + "EventRecordID")?.Value, CultureInfo.InvariantCulture, out var recordId);
        var id = guid.ToString("D");
        var isReplay = timestamp <= replayBoundaryUtc;
        if (eventId == 5) return new CaptureEnvelope(null, new ProcessExit(id, Number("ProcessId"), timestamp), bookmarkXml, recordId, isReplay);
        var parentId = Guid.TryParse(Field("ParentProcessGuid"), out var parentGuid) ? parentGuid.ToString("D") : "";
        return new CaptureEnvelope(new ProcessRecord
        {
            Id = id, ProcessId = Number("ProcessId"), SessionId = Number("TerminalSessionId"),
            StartUtc = timestamp, Image = Field("Image"), CommandLine = Field("CommandLine"),
            User = Field("User"), ParentId = parentId, ParentImage = Field("ParentImage"),
            ParentCommandLine = Field("ParentCommandLine")
        }, null, bookmarkXml, recordId, isReplay);
    }
}
