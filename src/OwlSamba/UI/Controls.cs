using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace OwlSamba.UI;

/// <summary>
/// Renders a Lucide icon (24×24 stroke icon) at any size. The stroke color follows the inherited
/// Foreground, so icons inside buttons automatically match the button text.
/// </summary>
public sealed class LucideIcon : FrameworkElement
{
    private static readonly Dictionary<string, Geometry> Cache = new();

    static LucideIcon()
    {
        // Icons sit centered in their slot unless told otherwise (avoids top-aligned icons in grids).
        VerticalAlignmentProperty.OverrideMetadata(typeof(LucideIcon), new FrameworkPropertyMetadata(VerticalAlignment.Center));
    }

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(LucideIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(LucideIcon),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(LucideIcon),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(LucideIcon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public string? Kind { get => (string?)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public static Geometry? GetGeometry(string? kind)
    {
        if (string.IsNullOrEmpty(kind)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(kind, out var g)) return g;
            if (!LucideData.Paths.TryGetValue(kind, out var data)) return null;
            g = Geometry.Parse(data);
            g.Freeze();
            Cache[kind] = g;
            return g;
        }
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        var geometry = GetGeometry(Kind);
        if (geometry == null) return;
        var scale = Size / 24.0;
        var pen = new Pen(Foreground, StrokeThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(null, pen, geometry);
        dc.Pop();
    }
}

/// <summary>Border with the Corsair-style 45° cut on the bottom-right corner.</summary>
public sealed class ChamferBorder : System.Windows.Controls.Decorator
{
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register(
        nameof(BorderBrush), typeof(Brush), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderThicknessProperty = DependencyProperty.Register(
        nameof(BorderThickness), typeof(double), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ChamferProperty = DependencyProperty.Register(
        nameof(Chamfer), typeof(double), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(new Thickness(0), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public Brush? Background { get => (Brush?)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Brush? BorderBrush { get => (Brush?)GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public double BorderThickness { get => (double)GetValue(BorderThicknessProperty); set => SetValue(BorderThicknessProperty, value); }
    public double Chamfer { get => (double)GetValue(ChamferProperty); set => SetValue(ChamferProperty, value); }
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size constraint)
    {
        var p = Padding;
        var extra = new System.Windows.Size(p.Left + p.Right, p.Top + p.Bottom);
        if (Child == null) return extra;
        Child.Measure(new System.Windows.Size(
            Math.Max(0, constraint.Width - extra.Width), Math.Max(0, constraint.Height - extra.Height)));
        return new System.Windows.Size(Child.DesiredSize.Width + extra.Width, Child.DesiredSize.Height + extra.Height);
    }

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size arrangeSize)
    {
        var p = Padding;
        Child?.Arrange(new Rect(p.Left, p.Top,
            Math.Max(0, arrangeSize.Width - p.Left - p.Right), Math.Max(0, arrangeSize.Height - p.Top - p.Bottom)));
        return arrangeSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, t = BorderThickness, half = t / 2;
        var c = Math.Min(Chamfer, Math.Min(w, h) / 2);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(half, half), true, true);
            ctx.LineTo(new Point(w - half, half), true, false);
            ctx.LineTo(new Point(w - half, h - c), true, false);
            ctx.LineTo(new Point(w - c, h - half), true, false);
            ctx.LineTo(new Point(half, h - half), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(Background, t > 0 && BorderBrush != null ? new Pen(BorderBrush, t) : null, g);
    }
}

/// <summary>Attached properties used by the theme templates.</summary>
public static class Ext
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(string), typeof(Ext), new FrameworkPropertyMetadata(null));
    public static string? GetIcon(DependencyObject d) => (string?)d.GetValue(IconProperty);
    public static void SetIcon(DependencyObject d, string? v) => d.SetValue(IconProperty, v);

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Ext), new FrameworkPropertyMetadata(null));
    public static string? GetPlaceholder(DependencyObject d) => (string?)d.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject d, string? v) => d.SetValue(PlaceholderProperty, v);
}
