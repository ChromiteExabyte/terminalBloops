using System.Buffers.Binary;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using TerminalBloops.App.Capture;
using TerminalBloops.Core;
using Xunit;

namespace TerminalBloops.Tests;

public sealed class CaptureTests : IDisposable
{
    private static readonly XNamespace EventNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";
    private static readonly DateTimeOffset Epoch = new(2026, 9, 9, 12, 30, 0, TimeSpan.Zero);
    private const string ProcessGuid = "{A76D3CB2-ABCD-4823-AB11-0123456789AB}";
    private const string ParentGuid = "{B76D3CB2-ABCD-4823-AB11-0123456789AB}";
    private static readonly string FixtureRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "TerminalBloops.CaptureTests"));
    private readonly string directory = Path.Combine(FixtureRoot, Guid.NewGuid().ToString("N"));

    [Fact]
    public void ProcessCreationUsesNamedFieldsAndPreservesCommandText()
    {
        const string command = "\"C:\\工具\\worker.exe\" --message \"a & b < c\"\t--path C:\\Data";
        var fields = Fields();
        fields["CommandLine"] = command;
        var xml = EventXml(1, fields.Reverse(), recordId: 1234);
        var envelope = Assert.IsType<CaptureEnvelope>(SysmonEventParser.Parse(xml, "saved-bookmark", Epoch));
        var process = Assert.IsType<ProcessRecord>(envelope.Started);

        Assert.Equal(Guid.Parse(ProcessGuid).ToString("D"), process.Id);
        Assert.Equal(Guid.Parse(ParentGuid).ToString("D"), process.ParentId);
        Assert.Equal(4524, process.ProcessId);
        Assert.Equal(3, process.SessionId);
        Assert.Equal(Epoch, process.StartUtc);
        Assert.Equal("C:\\tools\\worker.exe", process.Image);
        Assert.Equal(command, process.CommandLine);
        Assert.Equal("DESKTOP\\Carter", process.User);
        Assert.Equal("C:\\Vendor\\updater.exe", process.ParentImage);
        Assert.Equal("updater.exe /daily", process.ParentCommandLine);
        Assert.Null(process.EndUtc);
        Assert.Equal(WindowEvidence.Unconfirmed, process.Evidence);
        Assert.False(process.IsTerminal);
        Assert.Null(envelope.Exited);
        Assert.Equal("saved-bookmark", envelope.BookmarkXml);
        Assert.Equal(1234, envelope.RecordId);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void ReplayUsesProcessTimestampAtTheStartupBoundary(int offsetMilliseconds, bool replay)
    {
        var fields = Fields();
        fields["UtcTime"] = FormatUtc(Epoch.AddMilliseconds(offsetMilliseconds));
        // The Windows event's write time may be later than the process event itself.
        var envelope = SysmonEventParser.Parse(EventXml(1, fields, systemTime: Epoch.AddSeconds(10)), "", Epoch)!;
        Assert.Equal(replay, envelope.IsReplay);
        Assert.Equal(Epoch.AddMilliseconds(offsetMilliseconds), envelope.Started!.StartUtc);
    }

    [Fact]
    public void TerminationNeedsNoCreationOnlyFields()
    {
        var fields = new Dictionary<string, string>
        {
            ["ProcessGuid"] = ProcessGuid,
            ["ProcessId"] = "4524",
            ["UtcTime"] = FormatUtc(Epoch.AddMilliseconds(45))
        };
        var envelope = SysmonEventParser.Parse(EventXml(5, fields), "end-bookmark", Epoch)!;
        Assert.Null(envelope.Started);
        Assert.Equal(new ProcessExit(Guid.Parse(ProcessGuid).ToString("D"), 4524, Epoch.AddMilliseconds(45)), envelope.Exited);
        Assert.False(envelope.IsReplay);
        Assert.Equal("end-bookmark", envelope.BookmarkXml);
    }

    [Fact]
    public void MissingOptionalParentFieldsStayUnknown()
    {
        var fields = Fields();
        fields.Remove("ParentProcessGuid");
        fields.Remove("ParentImage");
        fields.Remove("ParentCommandLine");
        fields.Remove("User");
        fields.Remove("TerminalSessionId");
        var process = SysmonEventParser.Parse(EventXml(1, fields), "", Epoch)!.Started!;
        Assert.Equal("", process.ParentId);
        Assert.Equal("", process.ParentImage);
        Assert.Equal("", process.ParentCommandLine);
        Assert.Equal("", process.User);
        Assert.Equal(0, process.SessionId);
    }

    [Fact]
    public void InvalidOptionalParentGuidDoesNotInventAParent()
    {
        var fields = Fields();
        fields["ParentProcessGuid"] = "unavailable";
        Assert.Equal("", SysmonEventParser.Parse(EventXml(1, fields), "", Epoch)!.Started!.ParentId);
    }

    [Theory]
    [InlineData("ProcessGuid", "not-a-guid")]
    [InlineData("ProcessGuid", "")]
    [InlineData("UtcTime", "yesterday afternoon")]
    public void InvalidRequiredIdentityOrTimestampIsRejected(string name, string value)
    {
        var fields = Fields();
        fields[name] = value;
        Assert.Throws<FormatException>(() => SysmonEventParser.Parse(EventXml(1, fields), "", Epoch));
    }

    [Fact]
    public void MissingProcessGuidIsRejectedRatherThanCorrelatedByPid()
    {
        var fields = Fields();
        fields.Remove("ProcessGuid");
        Assert.Throws<FormatException>(() => SysmonEventParser.Parse(EventXml(5, fields), "", Epoch));
    }

    [Fact]
    public void MissingEventTimestampUsesTheSystemTimestamp()
    {
        var fields = Fields();
        fields.Remove("UtcTime");
        var timestamp = Epoch.AddMilliseconds(731);
        var process = SysmonEventParser.Parse(EventXml(1, fields, systemTime: timestamp), "", Epoch)!.Started!;
        Assert.Equal(timestamp, process.StartUtc);
    }

    [Fact]
    public void MissingBothTimestampSourcesIsRejected()
    {
        var fields = Fields();
        fields.Remove("UtcTime");
        var xml = XElement.Parse(EventXml(1, fields));
        xml.Element(EventNamespace + "System")!.Element(EventNamespace + "TimeCreated")!.Remove();
        Assert.Throws<FormatException>(() => SysmonEventParser.Parse(xml.ToString(), "", Epoch));
    }

    [Fact]
    public void MalformedXmlAndMissingSystemAreRejected()
    {
        Assert.Throws<XmlException>(() => SysmonEventParser.Parse("<Event>", "", Epoch));
        Assert.Throws<FormatException>(() => SysmonEventParser.Parse("<Event/>", "", Epoch));
    }

    [Fact]
    public void NonProcessEventsAreIgnoredWithoutRequiringProcessFields()
    {
        Assert.Null(SysmonEventParser.Parse(EventXml(4, []), "", Epoch));
        Assert.Null(SysmonEventParser.Parse(EventXml(255, []), "", Epoch));
    }

    [Fact]
    public void ExportWithoutDefaultNamespaceIsAlsoParsed()
    {
        var document = XElement.Parse(EventXml(1, Fields()));
        foreach (var element in document.DescendantsAndSelf()) element.Name = element.Name.LocalName;
        foreach (var attribute in document.Attributes().Where(x => x.IsNamespaceDeclaration).ToArray()) attribute.Remove();
        Assert.Equal(4524, SysmonEventParser.Parse(document.ToString(), "", Epoch)!.Started!.ProcessId);
    }

    [Fact]
    public void SavedBookmarkRecordIdIsReadOnlyFromTheSysmonChannel()
    {
        var bookmark = $"<BookmarkList><Bookmark Channel=\"{SysmonCollector.Channel}\" RecordId=\"12345\" IsCurrent=\"true\" /></BookmarkList>";
        Assert.True(SysmonCheckpointValidator.TryGetRecordId(bookmark, out var id));
        Assert.Equal(12345, id);
        Assert.False(SysmonCheckpointValidator.TryGetRecordId(bookmark.Replace(SysmonCollector.Channel, "Security"), out _));
    }

    [Theory]
    [InlineData("<BookmarkList />")]
    [InlineData("<BookmarkList>")]
    [InlineData("<Bookmark Channel='Microsoft-Windows-Sysmon/Operational' RecordId='0' />")]
    [InlineData("<Bookmark Channel='Microsoft-Windows-Sysmon/Operational' RecordId='-1' />")]
    [InlineData("<Bookmark Channel='Microsoft-Windows-Sysmon/Operational' RecordId='abc' />")]
    [InlineData("<Bookmark Channel='Microsoft-Windows-Sysmon/Operational' />")]
    public void InvalidBookmarkCannotBeMistakenForAValidPosition(string bookmark)
    {
        Assert.False(SysmonCheckpointValidator.TryGetRecordId(bookmark, out _));
    }

    [Fact]
    public void FingerprintMatchesEventIdentityRegardlessOfRenderingOrder()
    {
        var fields = Fields();
        var fingerprint = SysmonEventParser.Parse(EventXml(1, fields), "", Epoch)!.Fingerprint;
        Assert.True(SysmonCheckpointValidator.Matches(fingerprint, EventXml(1, fields.Reverse())));
        Assert.Equal($"1:{Guid.Parse(ProcessGuid):D}:{Epoch.UtcTicks}", fingerprint);
    }

    [Theory]
    [InlineData("guid")]
    [InlineData("timestamp")]
    [InlineData("event-type")]
    public void ReusedRecordIdWithADifferentEventCausesCheckpointMismatch(string changedPart)
    {
        var original = Fields();
        var fingerprint = SysmonEventParser.Parse(EventXml(1, original, recordId: 42), "", Epoch)!.Fingerprint;
        var replacement = Fields();
        if (changedPart == "guid") replacement["ProcessGuid"] = ParentGuid;
        if (changedPart == "timestamp") replacement["UtcTime"] = FormatUtc(Epoch.AddMilliseconds(1));
        var id = changedPart == "event-type" ? 5 : 1;
        Assert.False(SysmonCheckpointValidator.Matches(fingerprint, EventXml(id, replacement, recordId: 42)));
    }

    [Fact]
    public void MissingOrMalformedSavedEventCannotValidateAResume()
    {
        var fingerprint = SysmonEventParser.Parse(EventXml(1, Fields()), "", Epoch)!.Fingerprint;
        Assert.False(SysmonCheckpointValidator.Matches(fingerprint, ""));
        Assert.False(SysmonCheckpointValidator.Matches(fingerprint, "<Event />"));
        Assert.False(SysmonCheckpointValidator.Matches(fingerprint, EventXml(4, [])));
        Assert.False(SysmonCheckpointValidator.Matches("", EventXml(1, Fields())));
    }

    [Theory]
    [InlineData(true, true, true, "producer-ready")]
    [InlineData(false, true, true, "producer-stopped")]
    [InlineData(true, false, true, "producer-stopped")]
    [InlineData(false, false, true, "producer-stopped")]
    [InlineData(null, true, true, "producer-status-unknown")]
    [InlineData(true, null, true, "producer-status-unknown")]
    [InlineData(null, null, true, "producer-status-unknown")]
    [InlineData(true, true, false, "recorder-unavailable")]
    public void ReadableHistoryIsNotConfusedWithARunningProducer(bool? serviceRunning, bool? channelEnabled,
        bool readerConnected, string expectedKind)
    {
        var status = SysmonCollector.EvaluateProducerHealth(serviceRunning, channelEnabled, readerConnected);
        Assert.Equal(expectedKind, status.Kind);
        Assert.NotEmpty(status.Message);
    }

    [Theory]
    [InlineData("CMD.EXE", false)]
    [InlineData("pwsh.exe", false)]
    [InlineData("powershell.exe", false)]
    [InlineData("wsl.exe", false)]
    [InlineData("cscript.exe", false)]
    [InlineData("conhost.exe", true)]
    [InlineData("OpenConsole.exe", true)]
    [InlineData("WindowsTerminal.exe", true)]
    [InlineData("wt.exe", true)]
    [InlineData("mintty.exe", true)]
    public void KnownShellsAndHostsAreClassifiedWithoutReadingAFile(string name, bool isHost)
    {
        var process = new TerminalClassifier().Classify(Record("C:\\nonexistent-fixture\\" + name));
        Assert.True(process.IsTerminal);
        Assert.Equal(isHost, process.IsHost);
        Assert.Equal(WindowEvidence.Unconfirmed, process.Evidence);
        Assert.NotEmpty(process.ClassificationReason);
    }

    [Theory]
    [InlineData(3, true)] // IMAGE_SUBSYSTEM_WINDOWS_CUI
    [InlineData(2, false)] // IMAGE_SUBSYSTEM_WINDOWS_GUI
    public void ArbitraryExecutableUsesPeSubsystemRatherThanItsName(ushort subsystem, bool expected)
    {
        var path = WritePeFixture("arbitrary-worker.exe", subsystem);
        var original = Record(path) with { CommandLine = "worker.exe --task maintenance", ParentId = "parent-id", User = "PC\\User" };
        var classified = new TerminalClassifier().Classify(original);
        Assert.Equal(expected, classified.IsTerminal);
        Assert.False(classified.IsHost);
        Assert.Equal(original.Id, classified.Id);
        Assert.Equal(original.StartUtc, classified.StartUtc);
        Assert.Equal(original.CommandLine, classified.CommandLine);
        Assert.Equal(original.ParentId, classified.ParentId);
        Assert.Equal(original.User, classified.User);
        Assert.Equal(WindowEvidence.Unconfirmed, classified.Evidence);
    }

    [Fact]
    public void ChangedExecutableInvalidatesThePeClassificationCache()
    {
        var path = WritePeFixture("replaced-worker.exe", 3);
        var classifier = new TerminalClassifier();
        Assert.True(classifier.Classify(Record(path)).IsTerminal);
        var previousWrite = File.GetLastWriteTimeUtc(path);
        WritePeFixture("replaced-worker.exe", 2);
        File.SetLastWriteTimeUtc(path, previousWrite.AddSeconds(2));
        Assert.False(classifier.Classify(Record(path)).IsTerminal);
    }

    [Fact]
    public void DeletedAndMalformedImagesRemainUnconfirmed()
    {
        var path = WritePeFixture("temporary-worker.exe", 3);
        var classifier = new TerminalClassifier();
        Assert.True(classifier.Classify(Record(path)).IsTerminal);
        File.Delete(path);
        Assert.False(classifier.Classify(Record(path)).IsTerminal);
        File.WriteAllText(path, "not an executable");
        Assert.False(classifier.Classify(Record(path)).IsTerminal);
        Assert.False(classifier.Classify(Record("")).IsTerminal);
    }

    private static Dictionary<string, string> Fields() => new()
    {
        ["UtcTime"] = FormatUtc(Epoch), ["ProcessGuid"] = ProcessGuid, ["ProcessId"] = "4524",
        ["TerminalSessionId"] = "3", ["Image"] = "C:\\tools\\worker.exe", ["CommandLine"] = "worker.exe --run",
        ["User"] = "DESKTOP\\Carter", ["ParentProcessGuid"] = ParentGuid,
        ["ParentImage"] = "C:\\Vendor\\updater.exe", ["ParentCommandLine"] = "updater.exe /daily"
    };

    private static string FormatUtc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static string EventXml(int id, IEnumerable<KeyValuePair<string, string>> fields, long recordId = 42,
        DateTimeOffset? systemTime = null) => new XElement(EventNamespace + "Event",
        new XElement(EventNamespace + "System",
            new XElement(EventNamespace + "Provider", new XAttribute("Name", "Microsoft-Windows-Sysmon")),
            new XElement(EventNamespace + "EventID", id),
            new XElement(EventNamespace + "Version", 5),
            new XElement(EventNamespace + "TimeCreated", new XAttribute("SystemTime", (systemTime ?? Epoch).ToString("O"))),
            new XElement(EventNamespace + "EventRecordID", recordId)),
        new XElement(EventNamespace + "EventData", fields.Select(pair =>
            new XElement(EventNamespace + "Data", new XAttribute("Name", pair.Key), pair.Value)))).ToString();

    private static ProcessRecord Record(string image) => new()
    {
        Id = "test-process", Image = image, StartUtc = Epoch, ProcessId = 12345
    };

    private string WritePeFixture(string name, ushort subsystem)
    {
        Directory.CreateDirectory(directory);
        // Header-only PE32+ fixture: sufficient for metadata inspection, with no executable code.
        var bytes = new byte[512];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0), 0x5A4D);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x80), 0x00004550);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x84), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x94), 0xF0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), 0x0022);
        const int optionalHeader = 0x98;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(optionalHeader), 0x20B);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(optionalHeader + 24), 0x140000000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(optionalHeader + 32), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(optionalHeader + 36), 0x200);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(optionalHeader + 40), 6);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(optionalHeader + 48), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(optionalHeader + 56), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(optionalHeader + 60), 0x200);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(optionalHeader + 68), subsystem);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(optionalHeader + 108), 16);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(resolved), FixtureRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The fixture cleanup path is outside the test directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
