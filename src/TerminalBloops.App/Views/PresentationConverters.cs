using System.Globalization;
using System.Windows;
using System.Windows.Data;
using TerminalBloops.Core;

namespace TerminalBloops.App.Views;

public sealed class DurationConverter : IValueConverter
{
    public static string Format(double? milliseconds) => milliseconds switch
    {
        null => "Unknown",
        < 1 => "< 1 ms",
        < 1000 => $"{milliseconds:0} ms",
        < 60000 => $"{milliseconds / 1000:0.00} s",
        _ => $"{milliseconds / 60000:0.0} min"
    };
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Format(value as double?);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class LocalTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is DateTimeOffset timestamp
        ? timestamp.ToLocalTime().ToString(parameter as string ?? "HH:mm:ss", culture) : "Unknown";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class KnownTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string text && !string.IsNullOrWhiteSpace(text) ? text : parameter as string ?? "Unknown — not recorded";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class EvidenceDescriptionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        WindowEvidence.Observed => "A visible window was observed and linked to this process. This does not reveal what appeared inside it.",
        WindowEvidence.CommandUnresolved => "A terminal host was recorded, but its command could not be reliably linked. An existing window or pane can make attribution ambiguous.",
        _ => "No visible window was confirmed for this process. It may have run in the background, or the window observer may have missed it."
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class EmptyVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var empty = value is null || value is false || value is int count && count == 0;
        if (parameter as string == "Invert") empty = !empty;
        return empty ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class RailOffsetConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => new Thickness(value is int index ? (index % 3) switch { 1 => 5, 2 => -3, _ => 0 } : 0, 0, 0, 0);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
