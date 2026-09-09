using System.Windows;
using System.Windows.Threading;
using TerminalBloops.App.Views;
using TerminalBloops.Core;

namespace TerminalBloops.App.Services;

public sealed class NotificationManager(Action<string?> openHistory) : IDisposable
{
    private readonly List<NotificationCard> cards = [];
    private OverflowCard? overflow;
    private int overflowCount;
    private readonly List<DispatcherTimer> timers = [];
    public void Show(ProcessRecord record)
    {
        if (cards.Count >= 3)
        {
            overflowCount++;
            if (overflow is null)
            {
                overflow = new OverflowCard(overflowCount, () => { ClearOverflow(); openHistory(null); });
                overflow.Closed += (_, _) => { overflow = null; overflowCount = 0; };
                overflow.Show();
            }
            else overflow.UpdateCount(overflowCount);
            PositionCards();
            return;
        }
        var card = new NotificationCard(record, id => openHistory(id));
        cards.Add(card);
        card.Closed += (_, _) => { cards.Remove(card); PositionCards(); };
        card.Show(); PositionCards();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) => { timer.Stop(); timers.Remove(timer); card.Close(); };
        timers.Add(timer); timer.Start();
    }

    private void PositionCards()
    {
        var area = SystemParameters.WorkArea;
        var bottom = area.Bottom - 16;
        if (overflow is not null)
        {
            overflow.Left = area.Right-overflow.Width-16;
            overflow.Top = bottom-overflow.ActualHeight;
            bottom=overflow.Top-10;
        }
        foreach (var card in cards.AsEnumerable().Reverse())
        {
            card.Left=area.Right-card.Width-16;
            card.Top=bottom-card.ActualHeight;
            bottom=card.Top-10;
        }
    }
    private void ClearOverflow() { overflow?.Close(); overflow=null; overflowCount=0; }
    public void Clear()
    {
        foreach(var timer in timers) timer.Stop(); timers.Clear();
        foreach(var card in cards.ToArray()) card.Close();
        ClearOverflow();
    }
    public void Dispose() => Clear();
}
