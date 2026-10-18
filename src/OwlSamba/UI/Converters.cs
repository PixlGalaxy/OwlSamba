using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using OwlSamba.Core;

namespace OwlSamba.UI;

public static class Fmt
{
    public static DateTime Local(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

    public static string Time(long? ms, bool full = false)
    {
        if (ms is not { } v || v <= 0) return "—";
        var d = Local(v);
        if (full) return d.ToString("yyyy-MM-dd HH:mm:ss");
        return d.Date == DateTime.Today ? d.ToString("HH:mm:ss") : d.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
    }

    public static string Relative(long? ms)
    {
        if (ms is not { } v || v <= 0) return "—";
        var span = DateTime.Now - Local(v);
        if (span.TotalSeconds < 0) return "now";
        if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s ago";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
        return $"{(int)span.TotalDays} d ago";
    }

    public static string Remaining(long? untilMs)
    {
        if (untilMs is not { } u) return "PERMANENT";
        var span = Local(u) - DateTime.Now;
        if (span <= TimeSpan.Zero) return "expiring…";
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m" : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    public static string Source(string? s) => s switch
    {
        Sources.Security => "4625",
        Sources.SmbServer => "SMB",
        Sources.Web => "WEB",
        _ => s ?? "",
    };

    public static string BanKind(string? k, long? until) => k switch
    {
        null => "",
        BanKinds.Temporary => "TEMPORARY",
        BanKinds.Permanent => "PERMANENT",
        _ => until == null ? "MANUAL · PERM" : "MANUAL",
    };
}

public sealed class MsToTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Fmt.Time(value as long? ?? (value is long l ? l : null), parameter as string == "full");
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class MsToRelativeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Fmt.Relative(value as long?);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class SourceConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Fmt.Source(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>null / false / empty string / 0 → Collapsed. ConverterParameter="invert" flips it.</summary>
public sealed class VisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            long l => l != 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter as string == "invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Maps a status keyword to one of the theme brushes.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value?.ToString() ?? "") switch
        {
            BanKinds.Permanent or "perm" or "danger" or "error" or "False" => "DangerBrush",
            BanKinds.Temporary or "temp" or "warn" => "AccentBrush",
            BanKinds.Manual or "manual" or "alert" => "WarnBrush",
            "ok" or "True" or "unban" => "OkBrush",
            "info" or "web" => "InfoBrush",
            _ => "Text3Brush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Bar height for the 24h chart: value / max * available height.</summary>
public sealed class BarHeightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values[0] is not long v || values[1] is not long max || values[2] is not double h) return 0.0;
        if (max <= 0 || v <= 0) return 2.0;
        return Math.Max(3.0, v / (double)max * Math.Max(0, h - 4));
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}

/// <summary>Visible when value.ToString() equals the ConverterParameter (e.g. enum names).</summary>
public sealed class EqualsVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter as string, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
