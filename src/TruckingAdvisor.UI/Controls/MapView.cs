using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TruckingAdvisor.Presentation;
using TruckingAdvisor.Shared;

namespace TruckingAdvisor.UI.Controls;

/// <summary>
/// Draws the self-building map: roads you've driven, cities learned from your deliveries, your
/// destination (if you've been there before) and the truck. North is up (the game's -Z).
/// Mouse wheel zooms, drag pans, double-click goes back to following the truck.
/// </summary>
public sealed class MapView : FrameworkElement
{
    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(nameof(Snapshot), typeof(MapSnapshot), typeof(MapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((MapView)d).OnSnapshot()));
    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(MapView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RoadBrushProperty = DependencyProperty.Register(nameof(RoadBrush), typeof(Brush), typeof(MapView),
        new FrameworkPropertyMetadata(Brushes.SlateGray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(MapView),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(MapView),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MutedBrushProperty = DependencyProperty.Register(nameof(MutedBrush), typeof(Brush), typeof(MapView),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GoodBrushProperty = DependencyProperty.Register(nameof(GoodBrush), typeof(Brush), typeof(MapView),
        new FrameworkPropertyMetadata(Brushes.MediumSeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public MapSnapshot? Snapshot { get => (MapSnapshot?)GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }
    /// <summary>HUD mini-map: no labels or controls, always follows the truck.</summary>
    public bool Compact { get => (bool)GetValue(CompactProperty); set => SetValue(CompactProperty, value); }
    public Brush RoadBrush { get => (Brush)GetValue(RoadBrushProperty); set => SetValue(RoadBrushProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush MutedBrush { get => (Brush)GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }
    public Brush GoodBrush { get => (Brush)GetValue(GoodBrushProperty); set => SetValue(GoodBrushProperty, value); }

    /// <summary>Following the truck (true) or free panning (false). Double-click to follow again.</summary>
    public bool Follow { get; set; } = true;

    private StreamGeometry? _roads;
    private int _roadVersion = -1;
    private string _game = "";
    private Rect _roadBounds = Rect.Empty;
    private double _scale = 0.05;          // screen pixels per world unit
    private Point _center;                 // world point at the middle of the view
    private bool _hasView;
    private Point? _dragFrom;

    public MapView()
    {
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    private void OnSnapshot()
    {
        var s = Snapshot;
        if (s is null) return;
        if (s.RoadVersion != _roadVersion || s.Game != _game)
        {
            if (s.Game != _game) _hasView = false;    // another game: reframe
            _game = s.Game;
            _roadVersion = s.RoadVersion;
            BuildRoads(s.Roads);
        }
        if (!_hasView) Fit();
        if ((Follow || Compact) && s.HasTruck) _center = new Point(s.TruckX, s.TruckZ);
    }

    private void BuildRoads(IReadOnlyList<float[]> segments)
    {
        var g = new StreamGeometry();
        var bounds = Rect.Empty;
        using (var ctx = g.Open())
        {
            foreach (var seg in segments)
            {
                if (seg.Length < 4) continue;
                ctx.BeginFigure(new Point(seg[0], seg[1]), false, false);
                var pts = new List<Point>(seg.Length / 2);
                for (int i = 2; i < seg.Length; i += 2) pts.Add(new Point(seg[i], seg[i + 1]));
                ctx.PolyLineTo(pts, true, true);
                for (int i = 0; i < seg.Length; i += 2) bounds.Union(new Point(seg[i], seg[i + 1]));
            }
        }
        g.Freeze();
        _roads = g;
        _roadBounds = bounds;
    }

    /// <summary>Frames everything known: roads, cities and the truck.</summary>
    public void Fit()
    {
        var s = Snapshot;
        if (s is null || ActualWidth < 10 || ActualHeight < 10) return;
        var b = _roadBounds;
        foreach (var c in s.Cities) b.Union(new Point(c.X, c.Z));
        if (s.HasTruck) b.Union(new Point(s.TruckX, s.TruckZ));
        if (b.IsEmpty) { _scale = 0.05; _center = new Point(); _hasView = true; return; }
        double w = Math.Max(b.Width, 2000), h = Math.Max(b.Height, 2000);
        _scale = Math.Min(ActualWidth / w, ActualHeight / h) * 0.85;
        _center = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
        if (Compact) _scale = Math.Max(_scale, 0.08);
        _hasView = true;
    }

    private Point ToScreen(double x, double z) =>
        new((x - _center.X) * _scale + ActualWidth / 2, (z - _center.Y) * _scale + ActualHeight / 2);

    private Point ToWorld(Point screen) =>
        new((screen.X - ActualWidth / 2) / _scale + _center.X, (screen.Y - ActualHeight / 2) / _scale + _center.Y);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!_hasView) Fit();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit-testable for panning
        var s = Snapshot;
        if (s is null) return;
        if (!_hasView) Fit();
        if (Compact && s.HasTruck) _center = new Point(s.TruckX, s.TruckZ);

        // Roads, drawn in world coordinates with a pen that stays the same width on screen.
        if (_roads is not null && !_roads.IsEmpty())
        {
            dc.PushTransform(new MatrixTransform(_scale, 0, 0, _scale, w / 2 - _center.X * _scale, h / 2 - _center.Y * _scale));
            var pen = new Pen(RoadBrush, (Compact ? 3.0 : 2.4) / _scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, pen, _roads);
            dc.Pop();
        }

        // Cities.
        double dot = Compact ? 3 : 4;
        foreach (var c in s.Cities)
        {
            var p = ToScreen(c.X, c.Z);
            if (p.X < -50 || p.Y < -20 || p.X > w + 50 || p.Y > h + 20) continue;
            dc.DrawEllipse(TextBrush, null, p, dot, dot);
            if (!Compact) DrawLabel(dc, c.Name, p, MutedBrush, 11);
        }

        // Destination (only known if you've delivered there before).
        if (s.Destination is { } dest)
        {
            var p = ToScreen(dest.X, dest.Z);
            dc.DrawEllipse(null, new Pen(GoodBrush, 2.5), p, 10, 10);
            dc.DrawEllipse(GoodBrush, null, p, 4, 4);
            if (!Compact) DrawLabel(dc, $"Destination: {dest.Name}", p, GoodBrush, 12, bold: true);
            if (s.HasTruck)
                dc.DrawLine(new Pen(GoodBrush, 1.5) { DashStyle = DashStyles.Dash }, ToScreen(s.TruckX, s.TruckZ), p);
        }

        // Truck: arrow pointing along the compass heading (0 = north = up).
        if (s.HasTruck)
        {
            var p = ToScreen(s.TruckX, s.TruckZ);
            double size = Compact ? 9 : 11;
            var arrow = new StreamGeometry();
            using (var ctx = arrow.Open())
            {
                ctx.BeginFigure(new Point(0, -size), true, true);
                ctx.LineTo(new Point(size * 0.7, size * 0.8), true, true);
                ctx.LineTo(new Point(0, size * 0.35), true, true);
                ctx.LineTo(new Point(-size * 0.7, size * 0.8), true, true);
            }
            dc.PushTransform(new TranslateTransform(p.X, p.Y));
            dc.PushTransform(new RotateTransform(s.Heading));
            dc.DrawGeometry(AccentBrush, new Pen(Brushes.Black, 1.2), arrow);
            dc.Pop();
            dc.Pop();
        }

        if (Compact) return;

        // Scale bar (bottom left) and hints.
        DrawScaleBar(dc, h);
        if (s.Roads.Count == 0)
        {
            var t = Text("No roads recorded yet for this game.\nDrive with the telemetry plugin running and the map draws itself.", MutedBrush, 13);
            t.TextAlignment = TextAlignment.Center;
            dc.DrawText(t, new Point(w / 2, h / 2 - t.Height / 2 - 30));
        }
        var hint = Text(Follow ? "Following the truck · drag to look around · wheel to zoom" : "Double-click to follow the truck again", MutedBrush, 11);
        dc.DrawText(hint, new Point(w - hint.Width - 10, h - hint.Height - 8));
    }

    private void DrawScaleBar(DrawingContext dc, double h)
    {
        // World units are metres of driving. Pick a round length about 120 px wide.
        double worldPer120 = 120 / _scale;
        double display = Units.Metric ? worldPer120 / 1000 : worldPer120 / 1609.344;
        double p10 = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(display, 1e-6))));
        double best = p10;
        foreach (var m in new[] { 2, 5, 10 }) if (p10 * m <= display) best = p10 * m;
        display = best;
        double px = display * (Units.Metric ? 1000 : 1609.344) * _scale;
        var pen = new Pen(MutedBrush, 2);
        var y = h - 14;
        dc.DrawLine(pen, new Point(12, y), new Point(12 + px, y));
        dc.DrawLine(pen, new Point(12, y - 5), new Point(12, y + 1));
        dc.DrawLine(pen, new Point(12 + px, y - 5), new Point(12 + px, y + 1));
        var label = Text($"{display.ToString(display < 1 ? "0.#" : "N0", CultureInfo.CurrentCulture)} {Units.DistUnit}", MutedBrush, 11);
        dc.DrawText(label, new Point(12, y - 6 - label.Height));
    }

    private void DrawLabel(DrawingContext dc, string text, Point at, Brush brush, double size, bool bold = false)
    {
        var t = Text(text, brush, size, bold);
        dc.DrawText(t, new Point(at.X + 7, at.Y - t.Height / 2));
    }

    private FormattedText Text(string s, Brush brush, double size, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // ---- Mouse: wheel zoom, drag pan, double-click follow ----

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Compact) return;
        var mouse = e.GetPosition(this);
        var before = ToWorld(mouse);
        _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.25 : 0.8), 0.0005, 5);
        if (!Follow)
        {
            var after = ToWorld(mouse);
            _center = new Point(_center.X + before.X - after.X, _center.Y + before.Y - after.Y);
        }
        _hasView = true;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Compact) return;
        if (e.ClickCount == 2)
        {
            Follow = true;
            if (Snapshot is { HasTruck: true } s) _center = new Point(s.TruckX, s.TruckZ); else Fit();
            InvalidateVisual();
            return;
        }
        _dragFrom = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragFrom is not { } from || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(this);
        if (Follow && (now - from).Length < 4) return;
        Follow = false;
        _center = new Point(_center.X - (now.X - from.X) / _scale, _center.Y - (now.Y - from.Y) / _scale);
        _dragFrom = now;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragFrom = null;
        ReleaseMouseCapture();
    }

    /// <summary>Zoom buttons and "show everything" for the Map tab.</summary>
    public void ZoomBy(double factor) { _scale = Math.Clamp(_scale * factor, 0.0005, 5); _hasView = true; InvalidateVisual(); }
    public void ShowAll() { Follow = false; Fit(); InvalidateVisual(); }
    public void FollowTruck() { Follow = true; OnSnapshot(); InvalidateVisual(); }
}
