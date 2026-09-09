using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using TerminalBloops.App.Diagnostics;
using TerminalBloops.App.Services;
using TerminalBloops.App.ViewModels;
using TerminalBloops.App.Views;
using TerminalBloops.Core;
using Forms = System.Windows.Forms;

namespace TerminalBloops.App;

public partial class App : System.Windows.Application
{
    private Mutex? instance;
    private EventWaitHandle? openSignal;
    private RegisteredWaitHandle? openWait;
    private AppController? controller;
    private MainWindow? history;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private bool quitting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            System.Windows.MessageBox.Show("Terminal Bloops encountered an error. " + args.Exception.Message,
                "Terminal Bloops", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        if (e.Args.Length >= 2 && e.Args[0] == "--self-test")
        {
            try { await UiSmokeTest.RunAsync(e.Args[1]); Shutdown(0); }
            catch (Exception exception) { System.IO.Directory.CreateDirectory(e.Args[1]); System.IO.File.WriteAllText(System.IO.Path.Combine(e.Args[1], "failure.txt"), exception.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--capture-test")
        {
            try { await CaptureSmokeTest.RunAsync(e.Args[1]); Shutdown(0); }
            catch (Exception exception) { System.IO.Directory.CreateDirectory(e.Args[1]); System.IO.File.WriteAllText(System.IO.Path.Combine(e.Args[1], "capture-failure.txt"), exception.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--window-test")
        {
            try { await WindowSmokeTest.RunAsync(e.Args[1]); Shutdown(0); }
            catch (Exception exception) { System.IO.Directory.CreateDirectory(e.Args[1]); System.IO.File.WriteAllText(System.IO.Path.Combine(e.Args[1], "window-failure.txt"), exception.ToString()); Shutdown(1); }
            return;
        }

        var sid = WindowsIdentity.GetCurrent().User!.Value;
        instance = new Mutex(true, "Local\\TerminalBloops." + sid, out var firstInstance);
        openSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\TerminalBloops.Open." + sid);
        if (!firstInstance) { openSignal.Set(); Shutdown(); return; }

        var dataDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerminalBloops");
        var viewModel = new MainViewModel();
        controller = new AppController(viewModel, Dispatcher, dataDirectory, sid, OpenHistory);
        history = new MainWindow(viewModel);
        MainWindow = history;
        history.Closing += (_, args) => { if (!quitting) { args.Cancel = true; history.Hide(); } };
        openWait = ThreadPool.RegisterWaitForSingleObject(openSignal, (_, _) => Dispatcher.BeginInvoke(() => OpenHistory(null)), null, -1, false);

        trayIcon = CreateTrayIcon();
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "Terminal Bloops · What just ran?", Visible = true };
        tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) OpenHistory(null); };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("What just ran?", null, (_, _) => OpenHistory(null));
        var muteItem = new Forms.ToolStripMenuItem("Mute notifications") { CheckOnClick = true, Checked = !viewModel.NotificationsEnabled };
        muteItem.CheckedChanged += (_, _) => viewModel.NotificationsEnabled = !muteItem.Checked;
        viewModel.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(viewModel.NotificationsEnabled)) muteItem.Checked = !viewModel.NotificationsEnabled; };
        menu.Items.Add(muteItem);
        menu.Items.Add("Set up recorder…", null, async (_, _) => await controller.SetupRecorderAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Terminal Bloops", null, (_, _) => { quitting = true; Shutdown(); });
        tray.ContextMenuStrip = menu;

        if (!e.Args.Contains("--tray")) OpenHistory(null);
        await controller.StartAsync();
    }

    private void OpenHistory(string? id)
    {
        if (history is null) return;
        history.Show();
        if (history.WindowState == WindowState.Minimized) history.WindowState = WindowState.Normal;
        history.Activate();
        if (id is not null) controller?.SelectEvent(id);
    }

    private static System.Drawing.Icon CreateTrayIcon()
    {
        using var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/TerminalBloops.ico")).Stream;
        using var icon = new System.Drawing.Icon(stream, 32, 32);
        return (System.Drawing.Icon)icon.Clone();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        quitting = true;
        openWait?.Unregister(null);
        controller?.Dispose();
        tray?.Dispose(); trayIcon?.Dispose(); openSignal?.Dispose(); instance?.Dispose();
        base.OnExit(e);
    }
}
