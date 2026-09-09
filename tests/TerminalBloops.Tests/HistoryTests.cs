using TerminalBloops.Core;
using Xunit;

namespace TerminalBloops.Tests;

public sealed class HistoryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "TerminalBloops.Tests", Guid.NewGuid().ToString("N"));
    private readonly HistoryStore store;
    private static readonly DateTimeOffset Epoch = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
    public HistoryTests() => store = new HistoryStore(Path.Combine(directory, "history.db"));
    private static ProcessRecord Process(string id = "one", int pid = 42, int offset = 0) => new()
    {
        Id = id, ProcessId = pid, StartUtc = Epoch.AddMilliseconds(offset), User = "PC\\Carter",
        Image = "C:\\Windows\\System32\\cmd.exe", CommandLine = "cmd /c echo hello", IsTerminal = true,
        ParentImage = "C:\\apps\\launcher.exe"
    };
    private ProcessChange Start(ProcessRecord record, bool replay = false, long sequence = 1) => store.Apply(new(record, null, $"bookmark-{sequence}", sequence, replay));
    private ProcessChange End(string id, DateTimeOffset end, bool replay = false, long sequence = 2) => store.Apply(new(null, new(id, 42, end), $"bookmark-{sequence}", sequence, replay));

    [Theory]
    [InlineData(0, true)] [InlineData(17, true)] [InlineData(4999, true)] [InlineData(5000, true)] [InlineData(5001, false)] [InlineData(-1, false)]
    public void BriefBoundaryAndQueriesAgree(int milliseconds, bool expected)
    {
        Start(Process());
        var change = End("one", Epoch.AddMilliseconds(milliseconds));
        Assert.Equal(expected, change.Record!.IsBrief);
        Assert.Equal(expected ? 1 : 0, store.Query(new()).Count);
    }

    [Fact]
    public void StartAndBookmarkCommitTogetherAndDuplicateEndDoesNotNotify()
    {
        Start(Process(), sequence: 19);
        Assert.Equal("bookmark-19", store.GetCheckpoint().BookmarkXml);
        Assert.Equal(19, store.GetCheckpoint().RecordId);
        Assert.Equal($"1:one:{Epoch.UtcTicks}",store.GetCheckpoint().EventFingerprint);
        Assert.True(End("one", Epoch.AddMilliseconds(20)).NewlyCompleted);
        Assert.False(End("one", Epoch.AddMilliseconds(30)).NewlyCompleted);
        Assert.Equal(20, store.Get("one")!.DurationMs);
    }

    [Fact]
    public void SubmillisecondBoundaryDoesNotLeakIntoBriefQuery()
    {
        Start(Process());
        Assert.False(End("one", Epoch.AddSeconds(5).AddTicks(1)).Record!.IsBrief);
        Assert.Empty(store.Query(new()));
    }

    [Fact]
    public void RestartRetainsBookmarkAndHistory()
    {
        Start(Process()); End("one", Epoch.AddMilliseconds(50), sequence: 80);
        using var reopened = new HistoryStore(Path.Combine(directory, "history.db"));
        Assert.Equal(80, reopened.GetCheckpoint().RecordId);
        Assert.Single(reopened.Query(new()));
        Assert.False(reopened.Apply(new(null, new("one",42,Epoch.AddMilliseconds(50)),"repeat",81,false)).NewlyCompleted);
    }

    [Fact]
    public void ExitBeforeStartIsReconciledWithoutInventingDuration()
    {
        End("one", Epoch.AddMilliseconds(80));
        Assert.Empty(store.Query(new()));
        Assert.Equal(80, Start(Process()).Record!.DurationMs);
        Start(Process("missing-end", 43));
        Assert.Null(store.Get("missing-end")!.DurationMs);
    }

    [Fact]
    public void PidReuseJoinsByLifetimeAndDoesNotRewriteOldProcess()
    {
        Start(Process()); End("one", Epoch.AddMilliseconds(40));
        Start(Process("two", 42, 100));
        Assert.Equal("one", store.FindAt(42, Epoch.AddMilliseconds(30))!.Id);
        Assert.Null(store.FindAt(42, Epoch.AddMilliseconds(60)));
        Assert.Equal("two", store.FindAt(42, Epoch.AddMilliseconds(110))!.Id);
    }

    [Fact]
    public void ExactClassicClientMappingDeduplicatesHostAndRetainsEvidenceOnReplay()
    {
        Start(Process("host", 90) with { Image="C:\\Windows\\conhost.exe", IsHost=true });
        Start(Process());
        var show = new WindowObservation(90,42,Epoch.AddMilliseconds(10),false,"test","ConsoleWindowClass",200,true,Epoch,Epoch);
        Assert.True(store.ApplyObservation(show));
        Assert.Equal(WindowEvidence.Observed, store.Get("one")!.Evidence);
        Assert.Equal("one",store.Get("host")!.CorrelatedClientId);
        Assert.Single(store.Query(new(BriefOnly:false)));
        Start(Process(), replay:true);
        Assert.Equal(WindowEvidence.Observed,store.Get("one")!.Evidence);
    }

    [Fact]
    public void TerminalWindowWithoutClientIsExplicitlyUnresolved()
    {
        Start(Process("host",90) with { IsHost=true });
        Assert.True(store.ApplyObservation(new(90,null,Epoch.AddMilliseconds(10),false,"tab","CASCADIA_HOSTING_WINDOW_CLASS",200,true,Epoch)));
        Assert.Equal(WindowEvidence.CommandUnresolved,store.Get("host")!.Evidence);
        Assert.Null(store.Get("host")!.CorrelatedClientId);
    }

    [Fact]
    public void CloseWithoutShowDoesNotClaimVisibilityAndHiddenLaunchStaysUnconfirmed()
    {
        Start(Process());
        store.ApplyObservation(new(42,null,Epoch.AddMilliseconds(10),true,"","ConsoleWindowClass",200,true,Epoch));
        Assert.Equal(WindowEvidence.Unconfirmed,store.Get("one")!.Evidence);
    }

    [Fact]
    public void ParentChainSurvivesParentExitAndCyclesAreBounded()
    {
        Start(Process("parent",1) with { ParentId="one" }); End("parent",Epoch.AddMilliseconds(20));
        Start(Process() with { ParentId="parent" });
        Assert.Single(store.GetAncestors("one"));
        Assert.Equal("parent",store.GetAncestors("one")[0].Id);
    }

    [Fact]
    public void MissingExitAndDelayedReusedPidCannotAttachNewWindowToOldProcess()
    {
        Start(Process());
        var observation=new WindowObservation(42,null,Epoch.AddSeconds(2),false,"new window","ConsoleWindowClass",200,true,Epoch.AddSeconds(1));
        Assert.False(store.ApplyObservation(observation));
        Assert.Equal(WindowEvidence.Unconfirmed,store.Get("one")!.Evidence);
        Start(Process("two",42,1000));
        Assert.True(store.ApplyObservation(observation));
        Assert.Equal(WindowEvidence.Observed,store.Get("two")!.Evidence);
        Assert.Equal(WindowEvidence.Unconfirmed,store.Get("one")!.Evidence);
    }

    [Fact]
    public void DelayedShowPreservesEarlierCloseEvidence()
    {
        Start(Process());
        store.ApplyObservation(new(42,null,Epoch.AddMilliseconds(30),true,"","ConsoleWindowClass",200,true,Epoch));
        store.ApplyObservation(new(42,null,Epoch.AddMilliseconds(10),false,"test","ConsoleWindowClass",200,true,Epoch));
        Assert.Equal(Epoch.AddMilliseconds(10),store.Get("one")!.WindowFirstSeenUtc);
        Assert.Equal(Epoch.AddMilliseconds(30),store.Get("one")!.WindowLastSeenUtc);
    }

    [Fact]
    public void LiteralSearchAndUserFilteringAvoidSqlWildcardsAndInjection()
    {
        Start(Process() with { CommandLine="cmd /c echo %_special" });
        End("one",Epoch.AddMilliseconds(25));
        Assert.Single(store.Query(new(User:"pc\\carter",Search:"%_")));
        Assert.Empty(store.Query(new(User:"someone else")));
        Assert.Empty(store.Query(new(Search:"' OR 1=1 --")));
    }

    [Fact]
    public void RetentionPrunesOldestAndPreservesCheckpoint()
    {
        Start(Process("old") with { StartUtc=Epoch.AddDays(-8) });
        Start(Process("new"), sequence:4);
        store.Prune(Epoch);
        Assert.Null(store.Get("old")); Assert.NotNull(store.Get("new"));
        Assert.Equal(4,store.GetCheckpoint().RecordId);
        Assert.True(store.Prune(Epoch,maxPayloadBytes:0)>0);
        Assert.Null(store.Get("new"));
    }

    [Fact]
    public void NotificationsExcludeBackfillMutesOtherUsersAndUnknownDurations()
    {
        var process = Process() with { EndUtc=Epoch.AddMilliseconds(25) };
        var settings = new AppSettings();
        Assert.True(NotificationPolicy.ShouldNotify(process,settings,"PC\\Carter",false,true));
        Assert.False(NotificationPolicy.ShouldNotify(process,settings,"PC\\Carter",true,true));
        Assert.False(NotificationPolicy.ShouldNotify(process,settings,"PC\\Carter",false,false));
        Assert.False(NotificationPolicy.ShouldNotify(process,settings,"PC\\Other",false,true));
        Assert.False(NotificationPolicy.ShouldNotify(process with { EndUtc=null },settings,"PC\\Carter",false,true));
        settings=NotificationPolicy.SetLauncherMuted(settings,process,true);
        store.SaveSettings(settings);
        Assert.False(NotificationPolicy.ShouldNotify(process,store.LoadSettings(),"PC\\Carter",false,true));
        Assert.True(NotificationPolicy.ShouldNotify(process,NotificationPolicy.SetLauncherMuted(settings,process,false),"PC\\Carter",false,true));
    }

    public void Dispose()
    {
        store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }
}
