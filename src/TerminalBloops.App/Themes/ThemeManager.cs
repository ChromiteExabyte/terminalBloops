using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TerminalBloops.App.Themes;

public static class ThemeManager
{
    private static bool _initialized;
    private static bool _dark;
    private static bool _highContrast;
    private static event Action? Changed;
    public static void FollowTitleBar(Window window)
    {
        void RefreshTitle()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == 0) return;
            var dark = _dark && !SystemParameters.HighContrast ? 1 : 0;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            // Keep the real Windows caption, including Snap and system keyboard commands.
            var caption = _highContrast ? -1 : ColorRef(_dark ? "#17333F" : "#D9EFF7");
            var text = _highContrast ? -1 : ColorRef(_dark ? "#EAF9FC" : "#244B5B");
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        }
        window.SourceInitialized += (_, _) => RefreshTitle();
        Changed += RefreshTitle;
        window.Closed += (_, _) => Changed -= RefreshTitle;
    }
    public static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        Apply();
        SystemEvents.UserPreferenceChanged += (_, _) => Refresh();
        SystemParameters.StaticPropertyChanged += (_, e) => { if (e.PropertyName == nameof(SystemParameters.HighContrast)) Refresh(); };
    }
    private static void Refresh() => System.Windows.Application.Current?.Dispatcher.BeginInvoke(Apply);
    private static void Apply()
    {
        bool light;
        try { light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is not int preference || preference != 0; }
        catch { light = true; }
        ApplyPalette(light, SystemParameters.HighContrast);
    }
    internal static void ApplyPalette(bool light, bool highContrast)
    {
        var resources = System.Windows.Application.Current?.Resources;
        if (resources is null) return;
        _dark = !light;
        _highContrast = highContrast;
        if (highContrast)
        {
            Set("CanvasBrush", SystemColors.WindowColor);
            Set("SurfaceBrush", SystemColors.WindowColor);
            Set("RaisedBrush", SystemColors.WindowColor);
            Set("TextBrush", SystemColors.WindowTextColor);
            Set("MutedBrush", SystemColors.WindowTextColor);
            Set("FaintBrush", SystemColors.GrayTextColor);
            Set("BorderBrush", SystemColors.WindowTextColor);
            Set("ControlBorderBrush", SystemColors.WindowTextColor);
            Set("AccentBrush", SystemColors.HighlightColor);
            Set("AccentTextBrush", SystemColors.WindowTextColor);
            Set("AccentSurfaceBrush", SystemColors.WindowColor);
            Set("RustBrush", SystemColors.WindowTextColor);
            Set("SelectionBrush", SystemColors.HighlightColor);
            Set("SelectionTextBrush", SystemColors.HighlightTextColor);
            Set("NavigationBrush", SystemColors.WindowColor);
            Set("ToolbarBrush", SystemColors.WindowColor);
            Set("HighlightBrush", SystemColors.WindowColor);
            Set("GroupHeaderBrush", SystemColors.ControlColor);
            Set("GroupHeaderTextBrush", SystemColors.ControlTextColor);
            Set("CaptionBrush", SystemColors.HighlightColor);
            Set("CaptionTextBrush", SystemColors.HighlightTextColor);
            Set("AeroBackgroundBrush", SystemColors.WindowColor);
            Set("GlassBrush", SystemColors.WindowColor);
            Set("GlassBorderBrush", SystemColors.WindowTextColor);
            Set("OrbBrush", SystemColors.HighlightColor);
            Set("OrbHighlightBrush", Colors.Transparent);
            Set("OrbTextBrush", SystemColors.HighlightTextColor);
            Changed?.Invoke();
            return;
        }
        Set("CanvasBrush", light ? "#E6F3F6" : "#122D36");
        Set("SurfaceBrush", light ? "#F8FDFE" : "#1A3842");
        Set("RaisedBrush", light ? "#FFFFFF" : "#24434E");
        Set("TextBrush", light ? "#203F4C" : "#E8F5F8");
        Set("MutedBrush", light ? "#4B6974" : "#B1CDD5");
        Set("FaintBrush", light ? "#537582" : "#A2C4CE");
        Set("BorderBrush", light ? "#BBD8DF" : "#3E626C");
        Set("ControlBorderBrush", light ? "#6A8B97" : "#82A6B0");
        Set("AccentBrush", light ? "#107C91" : "#83D9E6");
        Set("AccentTextBrush", light ? "#18677B" : "#B4E9ED");
        Set("AccentSurfaceBrush", light ? "#DCF2F3" : "#28515B");
        Set("RustBrush", light ? "#965C0C" : "#EABF74");
        Set("SelectionBrush", light ? "#D3ECEF" : "#25515C");
        Set("SelectionTextBrush", light ? "#1D4857" : "#F0FBFD");
        Set("NavigationBrush", light ? "#DFEFF2" : "#193C46");
        Set("HighlightBrush", light ? "#FFFFFF" : "#52757E");
        Set("GroupHeaderTextBrush", light ? "#285667" : "#D6F0F5");
        Set("CaptionTextBrush", light ? "#244B5B" : "#EAF9FC");
        Set("GlassBorderBrush", light ? "#F4FFFFFF" : "#668FB0B8");
        Set("OrbTextBrush", "#FFFFFF");
        Gradient("ToolbarBrush", light ? "#FEFFFF" : "#365563", light ? "#E3F1F4" : "#24434D");
        Gradient("GroupHeaderBrush", light ? "#F6FDFF" : "#3B6370", light ? "#D3EAF0" : "#254651");
        Gradient("CaptionBrush", light ? "#D9EFF7" : "#17333F", light ? "#E6F5F7" : "#203F4B");
        Gradient("GlassBrush", light ? "#F7FFFFFF" : "#EF274A55", light ? "#DDF7FCFC" : "#EB1C3944");
        Gradient("OrbHighlightBrush", light ? "#CFFFFFFF" : "#99FFFFFF", "#00FFFFFF");
        Gradient("OrbBrush", "#66D4EF", "#008CAA", "#56C79F");
        Gradient("AeroBackgroundBrush", light ? "#D2ECF9" : "#142F3D", light ? "#EDF8F9" : "#183B46", light ? "#D4EFD9" : "#1D423D");
        Changed?.Invoke();

        void Set(string key, object color)
        {
            var brush = new SolidColorBrush(color is Color typed ? typed : (Color)ColorConverter.ConvertFromString((string)color));
            brush.Freeze();
            resources[key] = brush;
        }
        void Gradient(string key, string start, string end, string? bottom = null)
        {
            var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(start),
                (Color)ColorConverter.ConvertFromString(end), 90);
            if (bottom is not null)
            {
                brush.GradientStops[1].Offset = .52;
                brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(bottom), 1));
            }
            brush.Freeze(); resources[key] = brush;
        }
    }
    private static int ColorRef(string value)
    {
        var color = (Color)ColorConverter.ConvertFromString(value);
        return color.R | color.G << 8 | color.B << 16;
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint handle, int attribute, ref int value, int size);
}
