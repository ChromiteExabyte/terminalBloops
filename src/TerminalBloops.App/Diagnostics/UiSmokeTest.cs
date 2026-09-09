using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TerminalBloops.App.ViewModels;
using TerminalBloops.App.Views;
using TerminalBloops.App.Services;
using TerminalBloops.App.Themes;
using TerminalBloops.Core;

namespace TerminalBloops.App.Diagnostics;

/// <summary>Explicit developer-only UI verification; never writes demo records into real history.</summary>
public static class UiSmokeTest
{
    public static async Task RunAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var now = DateTimeOffset.UtcNow;
        var records = new[]
        {
            new ProcessRecord { Id="test-1",ProcessId=4220,Image=@"C:\Windows\System32\cmd.exe",CommandLine="cmd.exe /c echo recorder-check",User="TEST\\User",ParentImage=@"C:\Tools\Maintenance.exe",ParentId="test-parent",StartUtc=now.AddSeconds(-20),EndUtc=now.AddSeconds(-19.82),IsTerminal=true,Evidence=WindowEvidence.Observed },
            new ProcessRecord { Id="test-2",ProcessId=2210,Image=@"C:\Program Files\PowerShell\7\pwsh.exe",CommandLine="pwsh.exe -NoProfile -Command Write-Output done",User="TEST\\User",ParentImage=@"C:\Tools\BuildWorker.exe",StartUtc=now.AddMinutes(-1),EndUtc=now.AddMinutes(-1).AddMilliseconds(810),IsTerminal=true },
            new ProcessRecord { Id="test-3",ProcessId=6530,Image=@"C:\Program Files\WindowsApps\WindowsTerminal.exe",User="TEST\\User",ParentImage=@"C:\Windows\explorer.exe",StartUtc=now.AddMinutes(-2),EndUtc=now.AddMinutes(-2).AddSeconds(1.2),IsTerminal=true,IsHost=true,Evidence=WindowEvidence.CommandUnresolved },
            new ProcessRecord { Id="test-4",ProcessId=2318,Image=@"C:\Tools\helper-with-a-long-executable-name.exe",CommandLine="helper-with-a-long-executable-name.exe --check",User="TEST\\User",ParentImage=@"C:\Tools\TaskLauncher.exe",StartUtc=now.AddMinutes(-5),EndUtc=now.AddMinutes(-5).AddMilliseconds(34),IsTerminal=true }
        };
        var vm = new MainViewModel();
        vm.SetStatus("UI verification · sample events only · no recorder or startup settings changed");
        vm.ReplaceItems(records);
        vm.ReplaceAncestors([new ProcessRecord { Id="test-parent",Image=@"C:\Tools\Maintenance.exe",StartUtc=now.AddHours(-1),EndUtc=now.AddSeconds(-19),User="TEST\\User",CommandLine="Maintenance.exe --scheduled" }]);
        vm.Notices.Add(new(now,"test","Sample capture health entry. No system activity is represented by this preview."));
        var main = new MainWindow(vm);
        main.Show(); main.Activate();
        await main.Dispatcher.InvokeAsync(() => main.UpdateLayout(),DispatcherPriority.ApplicationIdle);
        await Task.Delay(250);
        var history=(ListBox)main.FindName("HistoryList");
        Expander EventAt(int index)
        {
            history.ScrollIntoView(records[index]); main.UpdateLayout();
            var container=(ListBoxItem)history.ItemContainerGenerator.ContainerFromItem(records[index]);
            return Descendants(container).OfType<Expander>().Single(item => item.Name=="EventExpander");
        }
        var searchToggle=(ToggleButton)main.FindName("SearchToggle");
        var settingsToggle=(ToggleButton)main.FindName("SettingsToggle");
        var searchDrawer=(FrameworkElement)main.FindName("SearchDrawer");
        var settingsDrawer=(FrameworkElement)main.FindName("SettingsDrawer");
        var initiallyMinimal=!searchDrawer.IsVisible && !settingsDrawer.IsVisible && !EventAt(0).IsExpanded;
        ThemeManager.ApplyPalette(true,false); main.UpdateLayout();
        Render(main,Path.Combine(outputDirectory,"timeline.png"));
        ThemeManager.ApplyPalette(false,false); main.UpdateLayout();
        Render(main,Path.Combine(outputDirectory,"timeline-dark.png"));
        ThemeManager.ApplyPalette(true,true); main.UpdateLayout();
        Render(main,Path.Combine(outputDirectory,"timeline-high-contrast.png"));
        ThemeManager.ApplyPalette(true,false); main.UpdateLayout();
        main.Width=main.MinWidth; main.Height=main.MinHeight; main.UpdateLayout();
        Render(main,Path.Combine(outputDirectory,"timeline-compact.png"));
        main.Width=860; main.Height=660; main.UpdateLayout();
        EventAt(0).SetCurrentValue(Expander.IsExpandedProperty,true); main.UpdateLayout();
        var rowOpens=vm.Selected?.Id==records[0].Id;
        Render(main,Path.Combine(outputDirectory,"timeline-expanded.png"));
        EventAt(1).SetCurrentValue(Expander.IsExpandedProperty,true); main.UpdateLayout();
        var oneExpanded=vm.Selected?.Id==records[1].Id && !EventAt(0).IsExpanded;
        EventAt(1).SetCurrentValue(Expander.IsExpandedProperty,false); main.UpdateLayout();
        var rowCloses=vm.Selected is null;
        vm.Selected=records[0]; main.UpdateLayout();
        var selectedEventOpens=EventAt(0).IsExpanded;
        ThemeManager.ApplyPalette(true,true); main.UpdateLayout();
        Render(main,Path.Combine(outputDirectory,"timeline-expanded-high-contrast.png"));
        ThemeManager.ApplyPalette(true,false);
        vm.Selected=null; main.UpdateLayout();
        searchToggle.IsChecked=true; main.UpdateLayout();
        var searchOpens=searchDrawer.IsVisible;
        var brief=(CheckBox)main.FindName("BriefOnlyCheckBox");
        var evidence=(ComboBox)main.FindName("EvidenceFilterBox");
        brief.SetCurrentValue(ToggleButton.IsCheckedProperty,false); evidence.SetCurrentValue(Selector.SelectedItemProperty,"Observed");
        var filtersWork=!vm.BriefOnly && vm.EvidenceFilter=="Observed";
        main.UpdateLayout();
        Render(main,Path.Combine(outputDirectory,"search.png"));
        brief.SetCurrentValue(ToggleButton.IsCheckedProperty,true); evidence.SetCurrentValue(Selector.SelectedItemProperty,"All");
        settingsToggle.IsChecked=true; main.UpdateLayout();
        var settingsOpen=settingsDrawer.IsVisible && !searchDrawer.IsVisible;
        Render(main,Path.Combine(outputDirectory,"settings.png"));
        searchToggle.IsChecked=true; main.UpdateLayout();
        var exclusiveDrawers=searchDrawer.IsVisible && !settingsDrawer.IsVisible;
        searchToggle.IsChecked=false; main.UpdateLayout();
        var progressiveDisclosure=initiallyMinimal && rowOpens && oneExpanded && rowCloses && selectedEventOpens && searchOpens && settingsOpen && filtersWork && exclusiveDrawers;
        var foregroundBefore=GetForegroundWindow();
        var card=new NotificationCard(records[0],_ => { });
        card.Show(); card.UpdateLayout();
        await Task.Delay(150);
        var foregroundAfter=GetForegroundWindow();
        Render(card,Path.Combine(outputDirectory,"notification.png"));
        ThemeManager.ApplyPalette(false,false); card.UpdateLayout();
        Render(card,Path.Combine(outputDirectory,"notification-dark.png"));
        ThemeManager.ApplyPalette(true,true); card.UpdateLayout();
        Render(card,Path.Combine(outputDirectory,"notification-high-contrast.png"));
        ThemeManager.ApplyPalette(true,false); card.UpdateLayout();
        var handle=new WindowInteropHelper(card).Handle;
        var passiveStyle=(GetWindowLongPtr(handle,-20).ToInt64() & 0x08000000)!=0;
        var cardDidNotStealFocus=foregroundBefore==foregroundAfter;
        var noConsole=GetConsoleWindow()==0;
        card.Close();
        using var burst = new NotificationManager(_ => { });
        for(var i=0;i<7;i++) burst.Show(records[i%records.Length]);
        var burstLimit=System.Windows.Application.Current.Windows.OfType<NotificationCard>().Count()==3
            && System.Windows.Application.Current.Windows.OfType<OverflowCard>().Count()==1;
        var overflow=System.Windows.Application.Current.Windows.OfType<OverflowCard>().Single();
        overflow.UpdateLayout(); Render(overflow,Path.Combine(outputDirectory,"notification-overflow.png"));
        await Task.Delay(6500);
        var cardsExpired=!System.Windows.Application.Current.Windows.OfType<NotificationCard>().Any();
        burst.Clear();
        vm.ReplaceItems([]); vm.Selected=null; main.UpdateLayout();
        Render(main,Path.Combine(outputDirectory,"empty.png"));
        main.Close();
        var results=new { passed=passiveStyle && cardDidNotStealFocus && noConsole && burstLimit && cardsExpired && progressiveDisclosure, noConsole, passiveStyle, cardDidNotStealFocus, burstLimit, cardsExpired, progressiveDisclosure, initiallyMinimal, rowOpens, oneExpanded, rowCloses, selectedEventOpens, searchOpens, settingsOpen, filtersWork, exclusiveDrawers, renderedTimeline=true, renderedNotification=true };
        File.WriteAllText(Path.Combine(outputDirectory,"ui-checks.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions { WriteIndented=true }));
        if (!results.passed) throw new InvalidOperationException("A UI smoke check failed; see ui-checks.json.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(var index=0;index<VisualTreeHelper.GetChildrenCount(root);index++)
        {
            var child=VisualTreeHelper.GetChild(root,index);
            yield return child;
            foreach(var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Render(Window window,string path)
    {
        // WPF renders the client area, not its native caption. Root-content margins must
        // remain in the preview, so use the actual HWND client size converted to WPF units.
        GetClientRect(new WindowInteropHelper(window).Handle,out var client);
        var scale=PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var size=scale.Transform(new Point(client.Right,client.Bottom));
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(size.X),(int)Math.Ceiling(size.Y),96,96,PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file=File.Create(path); encoder.Save(file);
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd,out NativeRect rect);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd,int index);
    [DllImport("kernel32.dll")] private static extern nint GetConsoleWindow();
}
