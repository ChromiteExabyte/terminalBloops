using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TerminalBloops.App.Themes;
using TerminalBloops.App.ViewModels;
using TerminalBloops.Core;

namespace TerminalBloops.App.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        ThemeManager.EnsureInitialized();
        InitializeComponent();
        ThemeManager.FollowTitleBar(this);
        DataContext = viewModel;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ShowSearch();
                e.Handled = true;
            }
        };
    }

    public void ShowSearch()
    {
        SearchToggle.IsChecked = true;
        FocusSearch();
    }

    public void ShowSettings() => SettingsToggle.IsChecked = true;

    private void SearchToggleChecked(object sender, RoutedEventArgs e)
    {
        SettingsToggle.IsChecked = false;
        FocusSearch();
    }

    private void SettingsToggleChecked(object sender, RoutedEventArgs e) => SearchToggle.IsChecked = false;

    private void FocusSearch() => Dispatcher.BeginInvoke(() =>
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }, DispatcherPriority.Input);

    private void RecorderClick(object sender, RoutedEventArgs e) => ShowSettings();

    private void SetupClick(object sender, RoutedEventArgs e) => ((MainViewModel)DataContext).RequestSetup();

    private void CopyCommandClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ProcessRecord record, Tag: TextBlock status }) return;
        if (string.IsNullOrWhiteSpace(record.CommandLine)) { status.Text = "No command was recorded."; return; }
        try { System.Windows.Clipboard.SetText(record.CommandLine); status.Text = "Copied to clipboard."; }
        catch (System.Runtime.InteropServices.ExternalException) { status.Text = "Clipboard is busy. Try again."; }
    }

    private void HistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not { } selected) return;
        HistoryList.ScrollIntoView(selected);
    }
}
