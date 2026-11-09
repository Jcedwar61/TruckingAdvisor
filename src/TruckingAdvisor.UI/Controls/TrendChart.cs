using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace TruckingAdvisor.UI.Controls;

/// <summary>
/// Small native line chart (no chart library): one series, min/max/latest labels, resizes with its container.
/// </summary>
public sealed class TrendChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(TrendChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(TrendChart), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
        nameof(Format), typeof(string), typeof(TrendChart), new FrameworkPropertyMetadata("F0", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(TrendChart), new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(TrendChart), new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 160 : Math.Min(availableSize.Height, 220));

    private FormattedText Text(string s, double size, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        dc.DrawText(Text(Title, 12, bold: true), new Point(0, 0));

        var v = Values;
        if (v is null || v.Count < 2)
        {
            dc.DrawText(Text("Not enough recorded trips yet", 11), new Point(0, 24));
            return;
        }

        double left = 44, top = 24, right = w - 8, bottom = h - 18;
        if (right - left < 20 || bottom - top < 20) return;
        double min = v.Min(), max = v.Max();
        if (Math.Abs(max - min) < 1e-9) { min -= 1; max += 1; }
        double pad = (max - min) * 0.1; min -= pad; max += pad;

        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x94, 0xA3, 0xB8)), 1);
        foreach (double frac in new[] { 0.0, 0.5, 1.0 })
        {
            double y = bottom - frac * (bottom - top);
            dc.DrawLine(grid, new Point(left, y), new Point(right, y));
            dc.DrawText(Text((min + frac * (max - min)).ToString(Format, CultureInfo.CurrentCulture), 10), new Point(0, y - 7));
        }

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (int i = 0; i < v.Count; i++)
            {
                var p = new Point(left + i * (right - left) / (v.Count - 1), bottom - (v[i] - min) / (max - min) * (bottom - top));
                if (i == 0) ctx.BeginFigure(p, false, false); else ctx.LineTo(p, true, true);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(LineBrush, 2), geo);

        var last = new Point(right, bottom - (v[^1] - min) / (max - min) * (bottom - top));
        dc.DrawEllipse(LineBrush, null, last, 3.5, 3.5);
        dc.DrawText(Text($"latest {v[^1].ToString(Format, CultureInfo.CurrentCulture)} • {v.Count} trips", 10), new Point(left, bottom + 2));
    }
}
