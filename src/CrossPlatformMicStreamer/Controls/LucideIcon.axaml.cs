using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace CrossPlatformMicStreamer.Controls;

public partial class LucideIcon : UserControl
{
    public static readonly StyledProperty<LucideKind> KindProperty =
        AvaloniaProperty.Register<LucideIcon, LucideKind>(nameof(Kind), LucideKind.Circle);

    private Canvas? _iconCanvas;

    public LucideKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    static LucideIcon()
    {
        KindProperty.Changed.AddClassHandler<LucideIcon>((icon, _) => icon.UpdateIcon());
        ForegroundProperty.Changed.AddClassHandler<LucideIcon>((icon, _) => icon.UpdateIcon());
    }

    public LucideIcon()
    {
        InitializeComponent();
        _iconCanvas = this.FindControl<Canvas>("IconCanvas");
        UpdateIcon();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateIcon();
    }

    private void UpdateIcon()
    {
        if (_iconCanvas == null)
        {
            return;
        }

        _iconCanvas.Children.Clear();

        var brush = Foreground ?? Brushes.White;
        foreach (var pathData in LucideGlyphs.GetPaths(Kind))
        {
            _iconCanvas.Children.Add(new Path
            {
                Data = StreamGeometry.Parse(pathData),
                Stroke = brush,
                StrokeThickness = 2,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Fill = null,
            });
        }
    }
}
