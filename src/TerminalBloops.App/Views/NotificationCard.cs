using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TerminalBloops.App.Themes;
using TerminalBloops.Core;

namespace TerminalBloops.App.Views;

/// <summary>A mouse-only glance surface. The same events remain keyboard-accessible in history.</summary>
public class PassiveCard : Window
{
    protected PassiveCard()
    {
        ThemeManager.EnsureInitialized();
        SetResourceReference(StyleProperty, typeof(Window));
        Width = 368;
        Height = 150;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        UseLayoutRounding = true;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | 0x08000000L | 0x00000080L); // NOACTIVATE | TOOLWINDOW
            HwndSource.FromHwnd(handle)?.AddHook(WindowProcedure);
        };
    }

    protected Border MakeSurface(Action activate)
    {
        var surface = new Border
        {
            CornerRadius = new CornerRadius(20),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            Margin = new Thickness(2),
            SnapsToDevicePixels = true
        };
        surface.SetResourceReference(Border.BackgroundProperty, "GlassBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "GlassBorderBrush");
        surface.MouseLeftButtonUp += (_, e) => { e.Handled = true; activate(); };
        Content = surface;
        return surface;
    }
    protected static Border MakeBevel(double radius = 19)
    {
        var bevel = new Border
        {
            CornerRadius = new CornerRadius(radius),
            BorderThickness = new Thickness(1, 1, 0, 0),
            SnapsToDevicePixels = true
        };
        bevel.SetResourceReference(Border.BorderBrushProperty, "OrbHighlightBrush");
        return bevel;
    }
    protected static FrameworkElement MakeTerminalOrb(double size)
    {
        var orb = new Grid
        {
            Width = size,
            Height = size,
            IsHitTestVisible = false,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        var sphere = new System.Windows.Shapes.Ellipse { StrokeThickness = 1 };
        sphere.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "OrbBrush");
        sphere.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "GlassBorderBrush");
        orb.Children.Add(sphere);

        // The small highlight and terminal prompt are native shapes/text. The theme removes
        // this gloss in high contrast and supplies the system highlight text color for the glyph.
        var highlight = new System.Windows.Shapes.Ellipse
        {
            Width = size * .68,
            Height = size * .32,
            Margin = new Thickness(0, size * .09, 0, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        highlight.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "OrbHighlightBrush");
        orb.Children.Add(highlight);
        var prompt = Label(">_", size * .39, "OrbTextBrush");
        prompt.FontFamily = new FontFamily("Consolas");
        prompt.FontWeight = FontWeights.SemiBold;
        prompt.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        prompt.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        prompt.Margin = new Thickness(0, 0, 0, 1);
        orb.Children.Add(prompt);
        return orb;
    }
    protected static TextBlock Label(string text, double size, string brushKey = "TextBrush")
    {
        var label = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return label;
    }
    private static nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return 3; } // WM_MOUSEACTIVATE -> MA_NOACTIVATE, preserve click.
        return 0;
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern long GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern long SetWindowLongPtr(nint handle, int index, long value);
}

public sealed class NotificationCard : PassiveCard
{
    public NotificationCard(ProcessRecord record, Action<string> openEvent)
    {
        Title = $"Terminal Bloops · {record.Name}";
        AutomationProperties.SetName(this, $"Brief activity: {record.Name}, via {record.LauncherName}, {record.EvidenceLabel}");
        var surface = MakeSurface(() => openEvent(record.Id));
        var bevel = MakeBevel();
        surface.Child = bevel;
        var body = new Grid
        {
            Margin = new Thickness(20, 22, 20, 22),
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bevel.Child = body;
        var orb = MakeTerminalOrb(36);
        orb.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        orb.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        orb.Margin = new Thickness(0, 4, 0, 0);
        body.Children.Add(orb);
        var details = new StackPanel();
        Grid.SetColumn(details, 1);
        body.Children.Add(details);
        var headline = new DockPanel { LastChildFill = true };
        var duration = Label(DurationConverter.Format(record.DurationMs), 11, "AccentTextBrush");
        duration.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        duration.Margin = new Thickness(12, 2, 0, 0);
        DockPanel.SetDock(duration, Dock.Right);
        headline.Children.Add(duration);
        var name = Label(string.IsNullOrWhiteSpace(record.Name) ? "Unknown executable" : record.Name, 19);
        name.FontWeight = FontWeights.SemiBold;
        headline.Children.Add(name);
        details.Children.Add(headline);
        var launcher = Label($"via {record.LauncherName}", 12, "MutedBrush");
        launcher.Margin = new Thickness(0, 4, 0, 0);
        details.Children.Add(launcher);
        var evidence = Label(record.EvidenceLabel, 11, "MutedBrush");
        evidence.Margin = new Thickness(0, 18, 0, 0);
        details.Children.Add(evidence);
    }
}

public sealed class OverflowCard : PassiveCard
{
    private readonly TextBlock _count;
    public OverflowCard(int count, Action openHistory)
    {
        Height = 58;
        Title = "Terminal Bloops · more activity";
        var surface = MakeSurface(openHistory);
        surface.CornerRadius = new CornerRadius(27);
        var bevel = MakeBevel(26);
        surface.Child = bevel;
        var grid = new Grid { Margin = new Thickness(16, 8, 18, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(41) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
        bevel.Child = grid;
        var orb = MakeTerminalOrb(28);
        orb.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        grid.Children.Add(orb);
        _count = Label("", 12);
        _count.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        Grid.SetColumn(_count, 1);
        grid.Children.Add(_count);
        var chevron = Label("›", 20, "AccentTextBrush");
        chevron.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        chevron.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);
        UpdateCount(count);
    }
    public void UpdateCount(int count)
    {
        _count.Text = $"+ {count:N0} more {(count == 1 ? "event" : "events")}";
        AutomationProperties.SetName(this, $"{count:N0} more brief terminal events. Open history.");
    }
}
