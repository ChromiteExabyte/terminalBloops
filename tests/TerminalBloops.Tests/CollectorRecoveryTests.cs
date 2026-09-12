using System.ComponentModel;
using TerminalBloops.App.Capture;
using TerminalBloops.Core;
using Xunit;

namespace TerminalBloops.Tests;

public sealed class CollectorRecoveryTests
{
    [Fact]
    public async Task InitialPermissionFailureReconnectsWhenAccessIsRestored()
    {
        var attempts = new List<string?>();
        var allowed = false;
        using var collector = new SysmonCollector(bookmark =>
        {
            attempts.Add(bookmark);
            if (!allowed) throw new Win32Exception(5);
        }, () => { }, () => (true, true));
        var notices = new List<CaptureNotice>();
        collector.Notice += notices.Add;

        await Assert.ThrowsAsync<Win32Exception>(() => collector.StartAsync("saved-position", CancellationToken.None));
        allowed = true;
        await collector.CheckProducerHealthAsync();
        await collector.CheckProducerHealthAsync();

        Assert.Equal(new string?[] { "saved-position", null }, attempts);
        Assert.Contains(notices, x => x.Kind == "producer-ready");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(null, true)]
    public async Task UnavailableProducerDoesNotTriggerRepeatedConnectionAttempts(bool? service, bool? channel)
    {
        var attempts = 0;
        using var collector = new SysmonCollector(_ =>
        {
            attempts++;
            throw new Win32Exception(15007);
        }, () => { }, () => (service, channel));
        await Assert.ThrowsAsync<Win32Exception>(() => collector.StartAsync(null, CancellationToken.None));

        await collector.CheckProducerHealthAsync();
        await collector.CheckProducerHealthAsync();
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task FailedReconnectionIsRetriedWithoutRepeatingIdenticalErrors()
    {
        var attempts = 0;
        var allowed = true;
        using var collector = new SysmonCollector(_ =>
        {
            attempts++;
            if (!allowed) throw new Win32Exception(5);
        }, () => { }, () => (true, true));
        var notices = new List<CaptureNotice>();
        collector.Notice += notices.Add;
        await collector.StartAsync(null, CancellationToken.None);

        allowed = false;
        await collector.ReconnectAsync(); // Simulate a lost native event subscription.
        await collector.CheckProducerHealthAsync();
        Assert.Single(notices, x => x.Message.StartsWith("Recorder reconnection failed:"));

        allowed = true;
        await collector.CheckProducerHealthAsync();
        await collector.CheckProducerHealthAsync();
        Assert.Equal(4, attempts);
        Assert.Equal("producer-ready", notices.Last().Kind);
    }

    [Fact]
    public async Task ConcurrentRecoveryRequestsOpenOnlyOneConnection()
    {
        var attempts = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var collector = new SysmonCollector(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 2)
            {
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            }
        }, () => { }, () => (true, true));
        await collector.StartAsync(null, CancellationToken.None);
        var first = collector.ReconnectAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await collector.ReconnectAsync();
            Assert.Equal(2, attempts);
        }
        finally { release.Set(); }
        await first;
    }

    [Fact]
    public async Task CancellationAfterFailedStartupClosesAndPreventsRecovery()
    {
        var attempts = 0;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var collector = new SysmonCollector(_ =>
        {
            attempts++;
            throw new Win32Exception(5);
        }, () => closed.TrySetResult(), () => (true, true));
        await Assert.ThrowsAsync<Win32Exception>(() => collector.StartAsync(null, cancellation.Token));

        cancellation.Cancel();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await collector.CheckProducerHealthAsync();
        await collector.ReconnectAsync();
        Assert.Equal(1, attempts);
    }
}
