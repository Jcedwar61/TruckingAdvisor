using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace TruckingAdvisor.UI.Controls;

/// <summary>
/// Instrument-style dial: a 270° arc with tick marks, a value arc, an optional marker (e.g. the
/// speed limit) and an optional red zone (e.g. engine redline). Drawn natively; scales with its size.
/// </summary>
public sealed class RadialGauge : FrameworkElement
{
    private static DependencyProperty Reg<T>(string name, T def) => DependencyProperty.Register(
        name, typeof(T), typeof(RadialGauge), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueProperty = Reg(nameof(Value), 0.0);
    public static readonly DependencyProperty MaximumProperty = Reg(nameof(Maximum), 100.0);
    public static readonly DependencyProperty MarkerProperty = Reg(nameof(Marker), 0.0);
    public static readonly DependencyProperty RedFromProperty = Reg(nameof(RedFrom), 0.0);
    public static readonly DependencyProperty MajorStepProperty = Reg(nameof(MajorStep), 20.0);
    public static readonly DependencyProperty LabelDivisorProperty = Reg(nameof(LabelDivisor), 1.0);
    public static readonly DependencyProperty IsAlertProperty = Reg(nameof(IsAlert), false);
    public static readonly DependencyProperty TitleProperty = Reg(nameof(Title), "");
    public static readonly DependencyProperty UnitProperty = Reg(nameof(Unit), "");
    public static readonly DependencyProperty ValueTextProperty = Reg(nameof(ValueText), "");
    public static readonly DependencyProperty ValueBrushProperty = Reg<Brush>(nameof(ValueBrush), Brushes.Orange);
    public static readonly DependencyProperty AlertBrushProperty = Reg<Brush>(nameof(AlertBrush), Brushes.Red);
    public static readonly DependencyProperty TrackBrushProperty = Reg<Brush>(nameof(TrackBrush), Brushes.DimGray);
    public static readonly DependencyProperty TextBrushProperty = Reg<Brush>(nameof(TextBrush), Brushes.White);
    public static readonly DependencyProperty MutedBrushProperty = Reg<Brush>(nameof(MutedBrush), Brushes.Gray);

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    /// <summary>Draws a marker at this value (0 = none).</summary>
    public double Marker { get => (double)GetValue(MarkerProperty); set => SetValue(MarkerProperty, value); }
    /// <summary>Red zone from this value to the maximum (0 = none).</summary>
    public double RedFrom { get => (double)GetValue(RedFromProperty); set => SetValue(RedFromProperty, value); }
    public double MajorStep { get => (double)GetValue(MajorStepProperty); set => SetValue(MajorStepProperty, value); }
    /// <summary>Tick labels show value / divisor (e.g. 100 for RPM x100).</summary>
    public double LabelDivisor { get => (double)GetValue(LabelDivisorProperty); set => SetValue(LabelDivisorProperty, value); }
    public bool IsAlert { get => (bool)GetValue(IsAlertProperty); set => SetValue(IsAlertProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    /// <summary>Big center text (e.g. "65" or "--").</summary>
    public string ValueText { get => (string)GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }
    public Brush ValueBrush { get => (Brush)GetValue(ValueBrushProperty); set => SetValue(ValueBrushProperty, value); }
    public Brush AlertBrush { get => (Brush)GetValue(AlertBrushProperty); set => SetValue(AlertBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush MutedBrush { get => (Brush)GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }

    private const double StartAngle = 135, Sweep = 270; // degrees, clockwise from +X (screen coordinates)

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 200 : available.Width;
        double h = double.IsInfinity(available.Height) ? w * 0.9 : available.Height;
        double side = Math.Clamp(Math.Min(w, h / 0.9), 110, 260);
        return new Size(side, side * 0.9);
    }

    private static Point On(Point c, double r, double deg) =>
        new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));

    private double AngleFor(double v) => StartAngle + Sweep * Math.Clamp(v / Math.Max(1e-9, Maximum), 0, 1);

    private static Geometry Arc(Point c, double r, double from, double to)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(On(c, r, from), false, false);
            ctx.ArcTo(On(c, r, to), new Size(r, r), 0, to - from > 180, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        return g;
    }

    private FormattedText Text(string s, double size, Brush brush, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40) return;
        double r = Math.Min(w / 2, h / 1.8) - 8;
        var c = new Point(w / 2, r + 8);
        double thick = Math.Max(6, r * 0.11);

        Pen Round(Brush b, double t) => new(b, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        // Track, red zone, value.
        dc.DrawGeometry(null, Round(TrackBrush, thick), Arc(c, r, StartAngle, StartAngle + Sweep));
        if (RedFrom > 0 && RedFrom < Maximum)
            dc.DrawGeometry(null, new Pen(AlertBrush, thick) { DashCap = PenLineCap.Flat }, Arc(c, r, AngleFor(RedFrom), StartAngle + Sweep));
        if (Value > 0)
            dc.DrawGeometry(null, Round(IsAlert ? AlertBrush : ValueBrush, thick), Arc(c, r, StartAngle, Math.Max(StartAngle + 0.5, AngleFor(Value))));

        // Ticks and labels.
        if (MajorStep > 0 && Maximum / MajorStep <= 20)
        {
            var tick = new Pen(MutedBrush, 1.5);
            for (double v = 0; v <= Maximum + 1e-6; v += MajorStep)
            {
                double a = AngleFor(v);
                dc.DrawLine(tick, On(c, r - thick * 0.9, a), On(c, r - thick * 1.6, a));
                var label = Text((v / LabelDivisor).ToString("0", CultureInfo.CurrentCulture), Math.Max(8, r * 0.105), MutedBrush);
                var p = On(c, r - thick * 2.35, a);
                dc.DrawText(label, new Point(p.X - label.Width / 2, p.Y - label.Height / 2));
            }
        }

        // Marker (e.g. speed limit): a notch outside the arc.
        if (Marker > 0 && Marker <= Maximum)
        {
            double a = AngleFor(Marker);
            dc.DrawLine(new Pen(AlertBrush, 3), On(c, r - thick * 0.7, a), On(c, r + thick * 0.9, a));
        }

        // Needle dot at the value.
        dc.DrawEllipse(TextBrush, null, On(c, r, AngleFor(Value)), thick * 0.42, thick * 0.42);

        // Center readout.
        var value = Text(string.IsNullOrEmpty(ValueText) ? Value.ToString("0", CultureInfo.CurrentCulture) : ValueText,
            r * 0.40, IsAlert ? AlertBrush : TextBrush, bold: true);
        dc.DrawText(value, new Point(c.X - value.Width / 2, c.Y - value.Height * 0.62));
        var unit = Text(Unit, Math.Max(9, r * 0.14), MutedBrush);
        dc.DrawText(unit, new Point(c.X - unit.Width / 2, c.Y + value.Height * 0.30));
        var title = Text(Title, Math.Max(9, r * 0.13), MutedBrush, bold: true);
        dc.DrawText(title, new Point(c.X - title.Width / 2, c.Y + r * 0.72));
    }
}
