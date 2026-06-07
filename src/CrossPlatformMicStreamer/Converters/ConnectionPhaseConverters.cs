using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using CrossPlatformMicStreamer.Audio;
using CrossPlatformMicStreamer.Controls;
using CrossPlatformMicStreamer.ViewModels;

namespace CrossPlatformMicStreamer.Converters;

public sealed class ConnectionPhaseToIconConverter : IValueConverter
{
    public static ConnectionPhaseToIconConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ConnectionPhase phase)
        {
            return LucideKind.Circle;
        }

        return phase switch
        {
            ConnectionPhase.Connected => LucideKind.CircleDot,
            ConnectionPhase.Connecting or ConnectionPhase.Reconnecting => LucideKind.LoaderCircle,
            ConnectionPhase.Error => LucideKind.CircleAlert,
            _ => LucideKind.Circle,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ConnectionPhaseToBrushConverter : IValueConverter
{
    public static ConnectionPhaseToBrushConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ConnectionPhase phase)
        {
            return Brushes.Gray;
        }

        return phase switch
        {
            ConnectionPhase.Connected => Brush.Parse("#22C55E"),
            ConnectionPhase.Connecting or ConnectionPhase.Reconnecting => Brush.Parse("#EAB308"),
            ConnectionPhase.Error => Brush.Parse("#EF4444"),
            _ => Brush.Parse("#94A3B8"),
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class AudioBitDepthLabelConverter : IValueConverter
{
    public static AudioBitDepthLabelConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AudioBitDepth bitDepth ? bitDepth.GetLabel() : value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToLucideKindConverter : IValueConverter
{
    public static BoolToLucideKindConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var enabled = value is true;
        var icon = parameter as string;

        return icon switch
        {
            "Mic" => enabled ? LucideKind.Mic : LucideKind.MicOff,
            "Volume" => enabled ? LucideKind.Volume2 : LucideKind.VolumeX,
            "Lock" => enabled ? LucideKind.Lock : LucideKind.LockOpen,
            _ => LucideKind.Circle,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
