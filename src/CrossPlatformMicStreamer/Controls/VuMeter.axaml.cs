using Avalonia;
using Avalonia.Controls;

namespace CrossPlatformMicStreamer.Controls;

public partial class VuMeter : UserControl
{
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<VuMeter, double>(nameof(Level), coerce: CoerceLevel);

    private Border? _fillBar;

    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public VuMeter()
    {
        InitializeComponent();
        _fillBar = this.FindControl<Border>("FillBar");
        LevelProperty.Changed.AddClassHandler<VuMeter>((meter, _) => meter.UpdateFillWidth());
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateFillWidth();
    }

    private static double CoerceLevel(AvaloniaObject _, double value) =>
        Math.Clamp(value, 0d, 1d);

    private void UpdateFillWidth()
    {
        if (_fillBar == null)
        {
            return;
        }

        var width = Bounds.Width * Level;
        _fillBar.Width = double.IsFinite(width) ? Math.Max(0d, width) : 0d;
    }
}
