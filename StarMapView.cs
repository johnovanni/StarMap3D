using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace StarMap3D;

public enum LabelMode { Off, PlanetHosts, AllNamed }

/// <summary>
/// Custom-drawn 3D star map. All projection maths is done here so that thousands of dots and
/// rings can be drawn quickly and in exactly the style wanted (dots, one ring per planet).
///
/// Controls: left-drag = rotate, right/middle-drag (or Shift+left-drag) = pan,
/// Ctrl+left-drag = roll, wheel = zoom, click = select, double-click = centre on star.
/// </summary>
public sealed class StarMapView : FrameworkElement
{
    // ---- camera -----------------------------------------------------------------
    const double CamDist = 320.0;     // perspective camera distance (ly) from the view target
    public const double DefaultYaw = 30, DefaultPitch = 28;
    public const double MinZoom = 0.15, MaxZoom = 60;

    double _yaw = DefaultYaw, _pitch = DefaultPitch, _roll, _zoom = 1;
    double _tx, _ty, _tz;                       // view target (world, ly)
    double _ca = 1, _sa, _cb = 1, _sb, _cc = 1, _sc;
    double _cx, _cy, _scale;

    /// <summary>How many light years fit inside the window at zoom = 1. Kept in step with the
    /// currently selected maximum distance so "zoom 1" always frames the whole map.</summary>
    double _fitRadius = 118.0;
    public double MaxDisplayLy
    {
        get => _maxDisplayLy;
        set
        {
            _maxDisplayLy = Math.Max(1, value);
            _fitRadius = _maxDisplayLy * 1.18;
            InvalidateVisual();
        }
    }
    double _maxDisplayLy = 100.0;

    /// <summary>"Nice" ring radii for the current max distance: 1/4, 1/2, 3/4 and the full radius.</summary>
    double[] GridRings() => new[] { _maxDisplayLy * 0.25, _maxDisplayLy * 0.5, _maxDisplayLy * 0.75, _maxDisplayLy };

    public double Yaw { get => _yaw; set { _yaw = Wrap(value); Changed(); } }
    public double Pitch { get => _pitch; set { _pitch = Math.Clamp(value, -90, 90); Changed(); } }
    public double Roll { get => _roll; set { _roll = Wrap(value); Changed(); } }
    public double Zoom { get => _zoom; set { _zoom = Math.Clamp(value, MinZoom, MaxZoom); Changed(); } }
    public (double x, double y, double z) Target => (_tx, _ty, _tz);

    static double Wrap(double d)
    {
        d %= 360;
        if (d > 180) d -= 360;
        if (d < -180) d += 360;
        return d;
    }

    // ---- options ----------------------------------------------------------------
    bool _showGrid = true, _showAxes = true, _dropLines, _hostsOnly;
    LabelMode _labels = LabelMode.PlanetHosts;
    public bool ShowGrid { get => _showGrid; set { _showGrid = value; InvalidateVisual(); } }
    public bool ShowAxes { get => _showAxes; set { _showAxes = value; InvalidateVisual(); } }
    public bool DropLines { get => _dropLines; set { _dropLines = value; InvalidateVisual(); } }
    public bool HostsOnly { get => _hostsOnly; set { _hostsOnly = value; InvalidateVisual(); } }
    public LabelMode Labels { get => _labels; set { _labels = value; InvalidateVisual(); } }

    // ---- data -------------------------------------------------------------------
    IReadOnlyList<Star> _stars = Array.Empty<Star>();
    float[] _px = Array.Empty<float>(), _py = Array.Empty<float>(), _pw = Array.Empty<float>();
    double[] _keys = Array.Empty<double>();
    int[] _order = Array.Empty<int>();
    int _drawn;

    public Star? Hovered { get; private set; }
    public Star? Selected { get; private set; }
    public int VisibleCount => _drawn;

    public event EventHandler? ViewChanged;
    public event EventHandler? HoverChanged;
    public event EventHandler? SelectionChanged;

    public void SetStars(IReadOnlyList<Star> stars)
    {
        _stars = stars;
        Hovered = Selected = null;
        _labelCache.Clear();
        int n = stars.Count;
        _px = new float[n]; _py = new float[n]; _pw = new float[n];
        _keys = new double[n]; _order = new int[n];
        Array.Fill(_px, float.NaN);   // nothing is hit-testable until the first render
        HoverChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    void Changed()
    {
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- brushes / pens ---------------------------------------------------------
    const int Levels = 5;
    static readonly byte[] LevelAlpha = { 105, 150, 190, 226, 255 };
    static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromRgb(4, 6, 12)));
    static readonly Brush[][] StarBrushes = MakeStarBrushes();
    static readonly Pen[] RingPens = MakeRingPens();
    static readonly Pen GridPen = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(70, 90, 140, 200)), 1));
    static readonly Pen SpokePen = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(38, 90, 140, 200)), 1));
    static readonly Pen DropPen = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(60, 120, 160, 210)), 1));
    static readonly Pen HoverPen = FreezePen(new Pen(Brushes.White, 1.5));
    static readonly Pen SelectPen = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(255, 214, 90)), 1.5) { DashStyle = DashStyles.Dash });
    static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromArgb(215, 205, 220, 240)));
    static readonly Brush HostLabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(140, 235, 255)));
    static readonly Brush GridLabelBrush = Freeze(new SolidColorBrush(Color.FromArgb(150, 130, 170, 220)));
    static readonly Typeface Font = new("Segoe UI");

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
    static Pen FreezePen(Pen p) { p.Brush.Freeze(); p.Freeze(); return p; }

    static Brush[][] MakeStarBrushes()
    {
        var res = new Brush[Star.ClassColors.Length][];
        for (int c = 0; c < res.Length; c++)
        {
            res[c] = new Brush[Levels];
            for (int l = 0; l < Levels; l++)
            {
                var col = Star.ClassColors[c];
                res[c][l] = Freeze(new SolidColorBrush(Color.FromArgb(LevelAlpha[l], col.R, col.G, col.B)));
            }
        }
        return res;
    }

    static Pen[] MakeRingPens()
    {
        var res = new Pen[Levels];
        for (int l = 0; l < Levels; l++)
            res[l] = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(LevelAlpha[l], 90, 225, 255)), 1.1));
        return res;
    }

    // ---- construction -----------------------------------------------------------
    public StarMapView()
    {
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = false;
    }

    // ---- maths ------------------------------------------------------------------
    void UpdateTrig()
    {
        double d = Math.PI / 180.0;
        _ca = Math.Cos(_yaw * d); _sa = Math.Sin(_yaw * d);
        _cb = Math.Cos(_pitch * d); _sb = Math.Sin(_pitch * d);
        _cc = Math.Cos(_roll * d); _sc = Math.Sin(_roll * d);
    }

    // world (ly) -> view space. View +X = screen right, +Y = screen up, +Z = towards the viewer.
    void ToView(double x, double y, double z, out double vx, out double vy, out double vz)
    {
        x -= _tx; y -= _ty; z -= _tz;
        double x1 = x * _ca - y * _sa;          // yaw about the celestial pole
        double y1 = x * _sa + y * _ca;
        double sx = x1, sy = z, sz = -y1;        // celestial pole becomes screen-up
        double ty = sy * _cb - sz * _sb;         // pitch about screen X
        double tz = sy * _sb + sz * _cb;
        vx = sx * _cc - ty * _sc;                // roll about the view axis
        vy = sx * _sc + ty * _cc;
        vz = tz;
    }

    // view-space direction -> world direction (inverse rotation only)
    void FromViewDir(double vx, double vy, double vz, out double x, out double y, out double z)
    {
        double sx = vx * _cc + vy * _sc;
        double ty = -vx * _sc + vy * _cc;
        double tz = vz;
        double sy = ty * _cb + tz * _sb;
        double sz = -ty * _sb + tz * _cb;
        double x1 = sx, z1 = sy, y1 = -sz;
        x = x1 * _ca + y1 * _sa;
        y = -x1 * _sa + y1 * _ca;
        z = z1;
    }

    bool Project(double x, double y, double z, out double px, out double py, out double w, out double depth)
    {
        ToView(x, y, z, out double vx, out double vy, out double vz);
        double den = CamDist - vz;
        if (den < 25) { px = py = w = depth = 0; return false; }
        w = CamDist / den;
        px = _cx + vx * _scale * w;
        py = _cy - vy * _scale * w;
        depth = vz;
        return true;
    }

    void Prepare()
    {
        double W = ActualWidth, H = ActualHeight;
        _cx = W / 2; _cy = H / 2;
        _scale = _zoom * Math.Min(W, H) / (2 * _fitRadius);
        UpdateTrig();
    }

    // ---- public view commands ---------------------------------------------------
    public void ResetView()
    {
        _yaw = DefaultYaw; _pitch = DefaultPitch; _roll = 0; _zoom = 1; _tx = _ty = _tz = 0;
        Changed();
    }

    public void SetOrientation(double yaw, double pitch, double roll)
    {
        _yaw = Wrap(yaw); _pitch = Math.Clamp(pitch, -90, 90); _roll = Wrap(roll);
        Changed();
    }

    public void CenterOnSun() { _tx = _ty = _tz = 0; Changed(); }

    public void CenterOn(Star s) { _tx = s.X; _ty = s.Y; _tz = s.Z; Changed(); }

    void PanView(double vx, double vy, double vz)
    {
        UpdateTrig();
        FromViewDir(vx, vy, vz, out double dx, out double dy, out double dz);
        _tx += dx; _ty += dy; _tz += dz;
        Changed();
    }

    public bool HandleKey(Key key)
    {
        double step = 8.0 / _zoom;
        switch (key)
        {
            case Key.Left: Yaw -= 5; break;
            case Key.Right: Yaw += 5; break;
            case Key.Up: Pitch += 5; break;
            case Key.Down: Pitch -= 5; break;
            case Key.Z: Roll -= 5; break;
            case Key.X: Roll += 5; break;
            case Key.A: PanView(-step, 0, 0); break;
            case Key.D: PanView(step, 0, 0); break;
            case Key.W: PanView(0, step, 0); break;
            case Key.S: PanView(0, -step, 0); break;
            case Key.Q: PanView(0, 0, step); break;    // towards the viewer
            case Key.E: PanView(0, 0, -step); break;   // away from the viewer
            case Key.Add:
            case Key.OemPlus: Zoom *= 1.15; break;
            case Key.Subtract:
            case Key.OemMinus: Zoom /= 1.15; break;
            case Key.R:
            case Key.Home: ResetView(); break;
            default: return false;
        }
        return true;
    }

    // ---- mouse ------------------------------------------------------------------
    Point _last, _down;
    bool _dragging;
    MouseButton _dragButton;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left)
        {
            var s = Hovered ?? HitTest(e.GetPosition(this), 12);
            if (s != null) CenterOn(s);
            return;
        }
        _dragging = true;
        _dragButton = e.ChangedButton;
        _last = _down = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_dragging && IsMouseCaptured)
        {
            double dx = p.X - _last.X, dy = p.Y - _last.Y;
            _last = p;
            bool pan = _dragButton == MouseButton.Right || _dragButton == MouseButton.Middle ||
                       (_dragButton == MouseButton.Left && (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            bool roll = _dragButton == MouseButton.Left && (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (pan)
            {
                // The scene follows the cursor: move the target the opposite way.
                PanView(-dx / _scale, dy / _scale, 0);
            }
            else if (roll)
            {
                _roll = Wrap(_roll + dx * 0.4);
                Changed();
            }
            else if (_dragButton == MouseButton.Left)
            {
                _yaw = Wrap(_yaw + dx * 0.4);
                _pitch = Math.Clamp(_pitch + dy * 0.4, -90, 90);
                Changed();
            }
        }
        else
        {
            var h = HitTest(p, 9);
            if (!ReferenceEquals(h, Hovered))
            {
                Hovered = h;
                Cursor = h != null ? Cursors.Hand : Cursors.Arrow;
                HoverChanged?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
            }
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging || e.ChangedButton != _dragButton) return;
        _dragging = false;
        ReleaseMouseCapture();
        var p = e.GetPosition(this);
        bool click = Math.Abs(p.X - _down.X) < 4 && Math.Abs(p.Y - _down.Y) < 4;
        if (click && e.ChangedButton == MouseButton.Left)
        {
            Selected = HitTest(p, 12);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (Hovered != null && !_dragging)
        {
            Hovered = null;
            HoverChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        Zoom *= Math.Pow(1.12, e.Delta / 120.0);
    }

    Star? HitTest(Point p, double maxDist)
    {
        Star? best = null;
        double bd = maxDist * maxDist;
        for (int i = 0; i < _stars.Count; i++)
        {
            float x = _px[i];
            if (float.IsNaN(x)) continue;
            double dx = x - p.X, dy = _py[i] - p.Y;
            double d = dx * dx + dy * dy;
            if (d <= bd) { bd = d; best = _stars[i]; }
        }
        return best;
    }

    // ---- rendering --------------------------------------------------------------
    readonly Dictionary<Star, FormattedText> _labelCache = new();

    FormattedText MakeText(string text, double size, Brush brush)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, size, brush, dpi);
    }

    FormattedText LabelFor(Star s)
    {
        if (!_labelCache.TryGetValue(s, out var ft))
        {
            ft = MakeText(s.Name, 11, s.Planets > 0 ? HostLabelBrush : LabelBrush);
            _labelCache[s] = ft;
        }
        return ft;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double W = ActualWidth, H = ActualHeight;
        dc.DrawRectangle(Background, null, new Rect(0, 0, W, H));
        if (W < 4 || H < 4) return;
        Prepare();

        if (_showGrid) DrawGrid(dc);
        if (_showAxes) DrawAxes(dc);

        // ---- project every star
        int n = _stars.Count, m = 0;
        for (int i = 0; i < n; i++)
        {
            var s = _stars[i];
            if (_hostsOnly && s.Planets == 0 && i != 0) { _px[i] = float.NaN; continue; }
            if (!Project(s.X, s.Y, s.Z, out double px, out double py, out double w, out double depth) ||
                px < -50 || py < -50 || px > W + 50 || py > H + 50)
            {
                _px[i] = float.NaN;
                continue;
            }
            _px[i] = (float)px; _py[i] = (float)py; _pw[i] = (float)w;
            _keys[m] = depth; _order[m] = i; m++;
        }
        _drawn = m;
        Array.Sort(_keys, _order, 0, m);   // far -> near (painter's algorithm)

        // ---- drop lines to the equatorial plane
        if (_dropLines)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                for (int k = 0; k < m; k++)
                {
                    var s = _stars[_order[k]];
                    if (Project(s.X, s.Y, 0, out double qx, out double qy, out _, out _))
                    {
                        ctx.BeginFigure(new Point(_px[_order[k]], _py[_order[k]]), false, false);
                        ctx.LineTo(new Point(qx, qy), true, false);
                    }
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, DropPen, g);
        }

        // ---- stars
        double zf = Math.Clamp(Math.Pow(_zoom, 0.35), 0.8, 2.6);
        for (int k = 0; k < m; k++)
        {
            int i = _order[k];
            var s = _stars[i];
            double w = _pw[i];
            Point c = new(_px[i], _py[i]);
            int lvl = Math.Clamp((int)((w - 0.75) / 0.7 * 4.99), 0, Levels - 1);

            double r = s.BaseRadius * w * zf;
            dc.DrawEllipse(StarBrushes[s.ColorClass][lvl], null, c, r, r);

            if (s.Planets > 0)
            {
                double gap = Math.Max(2.7, 3.1 * w * Math.Min(zf, 1.6));
                double rr = r + gap * 1.15;
                int rings = Math.Min(s.Planets, 16);
                for (int p = 0; p < rings; p++, rr += gap)
                    dc.DrawEllipse(null, RingPens[lvl], c, rr, rr);
            }
        }

        // ---- hover / selection highlight
        DrawHighlight(dc, Selected, SelectPen, zf);
        if (!ReferenceEquals(Hovered, Selected)) DrawHighlight(dc, Hovered, HoverPen, zf);

        // ---- labels (nearest first, skipping any that would overlap one already placed)
        if (_labels != LabelMode.Off)
        {
            var placed = new List<Rect>();
            for (int k = m - 1; k >= 0; k--)
            {
                int i = _order[k];
                var s = _stars[i];
                bool show = s.Name.Length > 0 &&
                            (_labels == LabelMode.AllNamed ? s.IsNamed : s.Planets > 0 || i == 0);
                if (!show && !ReferenceEquals(s, Hovered) && !ReferenceEquals(s, Selected)) continue;
                if (s.Name.Length == 0) continue;

                var ft = LabelFor(s);
                double w = _pw[i];
                double r = s.BaseRadius * w * zf + (s.Planets > 0 ? 3.1 * w * Math.Min(zf, 1.6) * (Math.Min(s.Planets, 16) + 0.15) : 0);
                var rect = new Rect(_px[i] + r + 3, _py[i] - ft.Height / 2, ft.Width, ft.Height);
                bool force = ReferenceEquals(s, Hovered) || ReferenceEquals(s, Selected);
                if (!force && placed.Exists(q => q.IntersectsWith(rect))) continue;
                placed.Add(rect);
                dc.DrawText(ft, rect.TopLeft);
            }
        }
    }

    void DrawHighlight(DrawingContext dc, Star? s, Pen pen, double zf)
    {
        if (s == null) return;
        int i = IndexOf(s);
        if (i < 0 || float.IsNaN(_px[i])) return;
        double w = _pw[i];
        double r = s.BaseRadius * w * zf;
        if (s.Planets > 0) r += 3.1 * w * Math.Min(zf, 1.6) * (Math.Min(s.Planets, 16) + 0.6);
        else r += 5;
        dc.DrawEllipse(null, pen, new Point(_px[i], _py[i]), r, r);
    }

    int IndexOf(Star s)
    {
        for (int i = 0; i < _stars.Count; i++) if (ReferenceEquals(_stars[i], s)) return i;
        return -1;
    }

    void DrawGrid(DrawingContext dc)
    {
        var ringRadii = GridRings();
        var rings = new StreamGeometry();
        var spokes = new StreamGeometry();
        using (var ctx = rings.Open())
        {
            foreach (double R in ringRadii)
            {
                bool pen = false;
                for (int i = 0; i <= 96; i++)
                {
                    double a = i * 2 * Math.PI / 96;
                    if (Project(R * Math.Cos(a), R * Math.Sin(a), 0, out double x, out double y, out _, out _))
                    {
                        if (!pen) { ctx.BeginFigure(new Point(x, y), false, false); pen = true; }
                        else ctx.LineTo(new Point(x, y), true, false);
                    }
                    else pen = false;
                }
            }
        }
        using (var ctx = spokes.Open())
        {
            for (int h = 0; h < 12; h++)
            {
                double a = h * Math.PI / 6;
                if (Project(0, 0, 0, out double x0, out double y0, out _, out _) &&
                    Project(_maxDisplayLy * Math.Cos(a), _maxDisplayLy * Math.Sin(a), 0, out double x1, out double y1, out _, out _))
                {
                    ctx.BeginFigure(new Point(x0, y0), false, false);
                    ctx.LineTo(new Point(x1, y1), true, false);
                }
            }
        }
        rings.Freeze(); spokes.Freeze();
        dc.DrawGeometry(null, SpokePen, spokes);
        dc.DrawGeometry(null, GridPen, rings);

        foreach (double R in ringRadii)
        {
            double a = Math.PI * 0.75;
            if (Project(R * Math.Cos(a), R * Math.Sin(a), 0, out double x, out double y, out _, out _))
                dc.DrawText(MakeText($"{R:0} ly", 10, GridLabelBrush), new Point(x + 3, y - 6));
        }
    }

    void DrawAxes(DrawingContext dc)
    {
        double a = _maxDisplayLy * 1.1;
        (double x, double y, double z, string t, Color c)[] axes =
        {
            (a, 0, 0, "RA 0h", Color.FromRgb(230, 90, 90)),
            (0, a, 0, "RA 6h", Color.FromRgb(110, 210, 110)),
            (0, 0, a, "North celestial pole", Color.FromRgb(110, 150, 255)),
        };
        if (!Project(0, 0, 0, out double ox, out double oy, out _, out _)) return;
        foreach (var (x, y, z, t, c) in axes)
        {
            if (!Project(x, y, z, out double ex, out double ey, out _, out _)) continue;
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(150, c.R, c.G, c.B)), 1.2);
            dc.DrawLine(pen, new Point(ox, oy), new Point(ex, ey));
            dc.DrawText(MakeText(t, 10, new SolidColorBrush(Color.FromArgb(200, c.R, c.G, c.B))), new Point(ex + 4, ey - 6));
        }
    }
}
