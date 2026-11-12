using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ScanStudio;

public enum CropMode { None, Single, Multi }

/// <summary>Rectangle: axis-aligned boxes. FourCorners: each corner moves freely; the area is straightened on apply.</summary>
public enum CropShape { Rectangle, FourCorners }

/// <summary>
/// Scrollable page preview with zoom (fit / steps / Ctrl+wheel), drag-to-pan, and crop areas
/// that can be drawn, moved and reshaped. Each area is 4 corners in image pixels, ordered
/// top-left, top-right, bottom-right, bottom-left. The viewer does not own <see cref="Image"/>.
/// </summary>
public sealed class PageViewer : ScrollableControl
{
    private const float MinZoom = 0.02f, MaxZoom = 8f;
    private const int FitPadding = 12;
    private static readonly float[] ZoomSteps =
        { 0.05f, 0.1f, 0.15f, 0.2f, 0.25f, 0.33f, 0.5f, 0.67f, 0.75f, 1f, 1.25f, 1.5f, 2f, 3f, 4f, 6f, 8f };

    [Flags] private enum Edge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }
    private enum DragKind { None, Pan, RectResize, RectMove, Points }
    private const int AllPoints = 0b1111;

    private static readonly Color Accent = Color.FromArgb(0, 120, 215);

    private Image _image;
    private float _zoom = 1f;
    private bool _fit = true;
    private CropMode _cropMode;
    private CropShape _shape;
    private readonly List<PointF[]> _regions = new();
    private int _active = -1;
    private Bitmap _cache;

    private DragKind _drag;
    private Edge _dragEdges;
    private int _dragMask;                 // Points drag: which corners move (bit k = corner k)
    private Point _dragStartMouse, _dragStartScroll;
    private PointF _dragStartImage;
    private RectangleF _dragStartRect;
    private PointF[] _dragStartPoints;
    private bool _dragCreated;
    private PointF[] _replacedRegion;      // single mode: the area a new drag replaced, restored if the drag is discarded

    public event EventHandler ZoomChanged;
    public event EventHandler RegionsChanged;

    public PageViewer()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        AutoScroll = true;
        BackColor = Color.FromArgb(64, 64, 64);
    }

    // ---------------------------------------------------------------- public API

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Image Image
    {
        get => _image;
        set
        {
            _image = value;
            InvalidateCache();
            _regions.Clear();
            _active = -1;
            if (_fit) ApplyFit(); else UpdateScrollSize();
            Invalidate();
            RegionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public float Zoom => _zoom;
    public bool FitToWindow => _fit;

    /// <summary>The crop areas, each as 4 corners (TL, TR, BR, BL) in image pixels. Copies; editing them has no effect.</summary>
    public IReadOnlyList<PointF[]> Regions => _regions.Select(r => (PointF[])r.Clone()).ToList();
    public int RegionCount => _regions.Count;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public CropMode CropMode
    {
        get => _cropMode;
        set
        {
            if (_cropMode == value) return;
            _cropMode = value;
            _regions.Clear();
            _active = -1;
            Cursor = value == CropMode.None ? Cursors.Default : Cursors.Cross;
            Invalidate();
            RegionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Switching to Rectangle turns any 4-corner areas into their bounding boxes.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public CropShape Shape
    {
        get => _shape;
        set
        {
            if (_shape == value) return;
            _shape = value;
            if (value == CropShape.Rectangle)
                for (int i = 0; i < _regions.Count; i++) _regions[i] = RectCorners(BoundsOf(_regions[i]));
            Invalidate();
            RegionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ZoomIn() => StepZoom(+1, ClientCenter);
    public void ZoomOut() => StepZoom(-1, ClientCenter);

    public void ZoomToFit()
    {
        _fit = true;
        ApplyFit();
        Invalidate();
    }

    public void ZoomActualSize()
    {
        _fit = false;
        SetZoom(1f, ClientCenter);
    }

    public void RemoveActiveRegion()
    {
        if (_active < 0) return;
        _regions.RemoveAt(_active);
        _active = _regions.Count - 1;
        Invalidate();
        RegionsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- geometry helpers

    public static PointF[] RectCorners(RectangleF r) =>
        new[] { new PointF(r.Left, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, r.Bottom), new PointF(r.Left, r.Bottom) };

    public static RectangleF BoundsOf(PointF[] q)
    {
        float l = q.Min(p => p.X), t = q.Min(p => p.Y), r = q.Max(p => p.X), b = q.Max(p => p.Y);
        return RectangleF.FromLTRB(l, t, r, b);
    }

    private static float Cross(PointF o, PointF a, PointF b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    /// <summary>True when the 4 corners make a proper (convex, non-crossed) shape that can be straightened.</summary>
    public static bool IsConvex(PointF[] q, float minArea = 4f)
    {
        int sign = 0;
        for (int k = 0; k < 4; k++)
        {
            float c = Cross(q[k], q[(k + 1) % 4], q[(k + 2) % 4]);
            if (Math.Abs(c) < 1e-3f) return false;
            int s = Math.Sign(c);
            if (sign == 0) sign = s; else if (s != sign) return false;
        }
        // Shoelace area: reject collapsed shapes.
        float area = 0;
        for (int k = 0; k < 4; k++) area += q[k].X * q[(k + 1) % 4].Y - q[(k + 1) % 4].X * q[k].Y;
        return Math.Abs(area) / 2 >= minArea;
    }

    private static bool ContainsPoint(PointF[] poly, PointF p)
    {
        int sign = 0;
        for (int k = 0; k < poly.Length; k++)
        {
            int s = Math.Sign(Cross(poly[k], poly[(k + 1) % poly.Length], p));
            if (s == 0) continue;
            if (sign == 0) sign = s; else if (s != sign) return false;
        }
        return true;
    }

    private static PointF Mid(PointF a, PointF b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
    private static float Dist(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ---------------------------------------------------------------- zoom & screen mapping

    private Point ClientCenter => new(ClientSize.Width / 2, ClientSize.Height / 2);

    /// <summary>Where the image is drawn on screen (centered when smaller than the view).</summary>
    private Rectangle ImageScreenRect()
    {
        int w = (int)Math.Round(_image.Width * _zoom), h = (int)Math.Round(_image.Height * _zoom);
        int x = w < ClientSize.Width ? (ClientSize.Width - w) / 2 : AutoScrollPosition.X;
        int y = h < ClientSize.Height ? (ClientSize.Height - h) / 2 : AutoScrollPosition.Y;
        return new Rectangle(x, y, w, h);
    }

    private PointF ScreenToImage(Point p)
    {
        var r = ImageScreenRect();
        return new PointF((p.X - r.X) / _zoom, (p.Y - r.Y) / _zoom);
    }

    private PointF[] ToScreen(PointF[] q)
    {
        var r = ImageScreenRect();
        return q.Select(p => new PointF(r.X + p.X * _zoom, r.Y + p.Y * _zoom)).ToArray();
    }

    private void UpdateScrollSize()
    {
        AutoScrollMinSize = _image == null
            ? Size.Empty
            : new Size((int)Math.Round(_image.Width * _zoom), (int)Math.Round(_image.Height * _zoom));
    }

    private void ApplyFit()
    {
        if (_image == null) return;
        AutoScrollMinSize = Size.Empty; // drop scrollbars so the full client area is measured
        float pad = LogicalToDeviceUnits(FitPadding);
        float z = Math.Min((ClientSize.Width - 2 * pad) / _image.Width, (ClientSize.Height - 2 * pad) / _image.Height);
        _zoom = Math.Clamp(Math.Min(z, 1f), MinZoom, MaxZoom); // never enlarge small images past 100%
        InvalidateCache();
        UpdateScrollSize();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StepZoom(int direction, Point anchor)
    {
        float target = direction > 0
            ? ZoomSteps.FirstOrDefault(s => s > _zoom * 1.01f, MaxZoom)
            : ZoomSteps.LastOrDefault(s => s < _zoom * 0.99f, MinZoom);
        _fit = false;
        SetZoom(target, anchor);
    }

    /// <summary>Changes zoom keeping the image point under <paramref name="anchor"/> in place.</summary>
    private void SetZoom(float zoom, Point anchor)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (_image == null) { _zoom = zoom; return; }
        PointF ip = ScreenToImage(anchor);
        _zoom = zoom;
        InvalidateCache();
        UpdateScrollSize();
        AutoScrollPosition = new Point(Math.Max(0, (int)(ip.X * _zoom - anchor.X)), Math.Max(0, (int)(ip.Y * _zoom - anchor.Y)));
        Invalidate();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    private void InvalidateCache()
    {
        _cache?.Dispose();
        _cache = null;
    }

    /// <summary>When zoomed out, a pre-scaled copy keeps repaints (e.g. while dragging a crop corner) fast and smooth.</summary>
    private Bitmap GetCache()
    {
        if (_image == null || _zoom >= 1f) return null;
        int w = (int)Math.Round(_image.Width * _zoom), h = (int)Math.Round(_image.Height * _zoom);
        if (w < 1 || h < 1 || (long)w * h > 30_000_000) return null;
        if (_cache != null) return _cache;
        try
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(_image, new Rectangle(0, 0, w, h));
            return _cache = bmp;
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- painting

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_image == null) return;

        var dest = ImageScreenRect();
        using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            g.FillRectangle(shadow, dest.X + 3, dest.Y + 3, dest.Width, dest.Height);

        var cache = GetCache();
        if (cache != null)
        {
            g.DrawImageUnscaled(cache, dest.X, dest.Y);
        }
        else
        {
            // Draw only the visible part: full-resolution scans are large.
            var visible = Rectangle.Intersect(dest, e.ClipRectangle);
            if (!visible.IsEmpty)
            {
                var src = new RectangleF((visible.X - dest.X) / _zoom, (visible.Y - dest.Y) / _zoom, visible.Width / _zoom, visible.Height / _zoom);
                g.InterpolationMode = _zoom >= 2f ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(_image, visible, src, GraphicsUnit.Pixel);
                g.PixelOffsetMode = PixelOffsetMode.Default;
            }
        }

        if (_cropMode != CropMode.None) PaintCropOverlay(g, dest);
    }

    private void PaintCropOverlay(Graphics g, Rectangle dest)
    {
        var screens = _regions.Select(ToScreen).ToList();
        using (var dim = new Region(dest))
        {
            foreach (var s in screens)
            {
                using var path = new GraphicsPath();
                path.AddPolygon(s);
                dim.Exclude(path);
            }
            using var b = new SolidBrush(Color.FromArgb(_regions.Count == 0 ? 60 : 130, 0, 0, 0));
            g.FillRegion(b, dim);
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var border = new Pen(Accent, LogicalToDeviceUnits(2));
        using var handleFill = new SolidBrush(Color.White);
        using var handlePen = new Pen(Accent, LogicalToDeviceUnits(2));
        using var badgeFill = new SolidBrush(Accent);
        using var badgeFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        float hs = LogicalToDeviceUnits(8);
        float corner = LogicalToDeviceUnits(16), sideDot = LogicalToDeviceUnits(10);
        float badge = LogicalToDeviceUnits(22);

        for (int i = 0; i < screens.Count; i++)
        {
            var s = screens[i];
            g.DrawPolygon(border, s);
            bool showHandles = i == _active || _cropMode == CropMode.Single;

            if (showHandles && _shape == CropShape.Rectangle)
            {
                foreach (var p in RectHandlePoints(BoundsOf(s)))
                {
                    var hr = new RectangleF(p.X - hs / 2, p.Y - hs / 2, hs, hs);
                    g.FillRectangle(handleFill, hr);
                    g.DrawRectangle(Pens.Black, hr.X, hr.Y, hr.Width, hr.Height);
                }
            }
            else if (showHandles)
            {
                // Big round corner handles (like phone scanner apps) and small side handles.
                for (int k = 0; k < 4; k++)
                {
                    var m = Mid(s[k], s[(k + 1) % 4]);
                    g.FillEllipse(handleFill, m.X - sideDot / 2, m.Y - sideDot / 2, sideDot, sideDot);
                    g.DrawEllipse(handlePen, m.X - sideDot / 2, m.Y - sideDot / 2, sideDot, sideDot);
                }
                foreach (var p in s)
                {
                    g.FillEllipse(handleFill, p.X - corner / 2, p.Y - corner / 2, corner, corner);
                    g.DrawEllipse(handlePen, p.X - corner / 2, p.Y - corner / 2, corner, corner);
                }
            }

            if (_cropMode == CropMode.Multi)
            {
                // Number badge a little inside the first (top-left) corner, toward the area's center.
                var center = new PointF(s.Average(p => p.X), s.Average(p => p.Y));
                float len = Dist(s[0], center), inset = badge * 1.4f;
                var c = len > inset
                    ? new PointF(s[0].X + (center.X - s[0].X) / len * inset, s[0].Y + (center.Y - s[0].Y) / len * inset)
                    : center;
                var br = new RectangleF(c.X - badge / 2, c.Y - badge / 2, badge, badge);
                g.FillEllipse(badgeFill, br);
                TextRenderer.DrawText(g, (i + 1).ToString(), badgeFont, Rectangle.Round(br), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
        g.SmoothingMode = SmoothingMode.Default;
    }

    private static IEnumerable<PointF> RectHandlePoints(RectangleF s)
    {
        float cx = s.X + s.Width / 2, cy = s.Y + s.Height / 2;
        yield return new(s.Left, s.Top); yield return new(cx, s.Top); yield return new(s.Right, s.Top);
        yield return new(s.Left, cy); yield return new(s.Right, cy);
        yield return new(s.Left, s.Bottom); yield return new(cx, s.Bottom); yield return new(s.Right, s.Bottom);
    }

    // ---------------------------------------------------------------- hit testing

    /// <summary>Rectangle shape: which region and which edges are under the mouse (no edges = inside).</summary>
    private (int Index, Edge Edges) HitTestRect(Point p)
    {
        float tol = LogicalToDeviceUnits(6);
        for (int i = _regions.Count - 1; i >= 0; i--)
        {
            var s = BoundsOf(ToScreen(_regions[i]));
            if (!RectangleF.Inflate(s, tol, tol).Contains(p)) continue;
            Edge e = Edge.None;
            if (Math.Abs(p.X - s.Left) <= tol) e |= Edge.Left;
            else if (Math.Abs(p.X - s.Right) <= tol) e |= Edge.Right;
            if (Math.Abs(p.Y - s.Top) <= tol) e |= Edge.Top;
            else if (Math.Abs(p.Y - s.Bottom) <= tol) e |= Edge.Bottom;
            return (i, e);
        }
        return (-1, Edge.None);
    }

    /// <summary>4-corner shape: which region and which corners a drag would move (1 corner, 2 for a side, all 4 inside).</summary>
    private (int Index, int Mask) HitTestQuad(Point p)
    {
        float tol = LogicalToDeviceUnits(12);
        // Handles of the active region win, so a corner can be grabbed even where regions overlap.
        var order = Enumerable.Range(0, _regions.Count).Reverse().OrderByDescending(i => i == _active).ToList();
        foreach (int i in order)
        {
            var s = ToScreen(_regions[i]);
            for (int k = 0; k < 4; k++)
                if (Dist(s[k], p) <= tol) return (i, 1 << k);
            for (int k = 0; k < 4; k++)
                if (Dist(Mid(s[k], s[(k + 1) % 4]), p) <= tol * 0.8f) return (i, (1 << k) | (1 << ((k + 1) % 4)));
        }
        foreach (int i in order)
            if (ContainsPoint(ToScreen(_regions[i]), p)) return (i, AllPoints);
        return (-1, 0);
    }

    private static Cursor CursorFor(Edge e) => e switch
    {
        Edge.Left | Edge.Top or Edge.Right | Edge.Bottom => Cursors.SizeNWSE,
        Edge.Right | Edge.Top or Edge.Left | Edge.Bottom => Cursors.SizeNESW,
        Edge.Left or Edge.Right => Cursors.SizeWE,
        Edge.Top or Edge.Bottom => Cursors.SizeNS,
        _ => Cursors.SizeAll,
    };

    // ---------------------------------------------------------------- mouse

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_image == null) return;

        _dragStartMouse = e.Location;
        _dragStartScroll = new Point(-AutoScrollPosition.X, -AutoScrollPosition.Y);
        _dragStartImage = ScreenToImage(e.Location);

        if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && _cropMode == CropMode.None))
        {
            _drag = DragKind.Pan;
            Cursor = Cursors.Hand;
            return;
        }
        if (_cropMode == CropMode.None) return;

        int index;
        Edge edges = Edge.None;
        int mask = 0;
        if (_shape == CropShape.Rectangle) (index, edges) = HitTestRect(e.Location);
        else (index, mask) = HitTestQuad(e.Location);

        if (e.Button == MouseButtons.Right)
        {
            if (index >= 0) { _active = index; RemoveActiveRegion(); }
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        _dragCreated = false;
        if (index >= 0)
        {
            _active = index;
            _dragStartPoints = (PointF[])_regions[index].Clone();
            _dragStartRect = BoundsOf(_dragStartPoints);
            if (_shape == CropShape.Rectangle)
            {
                _drag = edges == Edge.None ? DragKind.RectMove : DragKind.RectResize;
                _dragEdges = edges;
            }
            else
            {
                _drag = DragKind.Points;
                _dragMask = mask;
            }
        }
        else
        {
            // Start a new box at the click; growing its bottom-right corner handles any drag direction.
            // In 4-corner mode the corners are then dragged into place one by one.
            _replacedRegion = null;
            if (_cropMode == CropMode.Single && _regions.Count > 0)
            {
                _replacedRegion = _regions[0];
                _regions.Clear();
            }
            var start = ClampToImage(_dragStartImage);
            _dragStartImage = start;
            _dragStartRect = new RectangleF(start, SizeF.Empty);
            _regions.Add(RectCorners(_dragStartRect));
            _active = _regions.Count - 1;
            _drag = DragKind.RectResize;
            _dragEdges = Edge.Right | Edge.Bottom;
            _dragCreated = true;
        }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_image == null) return;

        var cur = ScreenToImage(e.Location);
        float dx = cur.X - _dragStartImage.X, dy = cur.Y - _dragStartImage.Y;

        switch (_drag)
        {
            case DragKind.Pan:
                AutoScrollPosition = new Point(_dragStartScroll.X - (e.X - _dragStartMouse.X), _dragStartScroll.Y - (e.Y - _dragStartMouse.Y));
                Invalidate();
                return;

            case DragKind.RectResize:
            {
                var r = _dragStartRect;
                float l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
                if (_dragEdges.HasFlag(Edge.Left)) l += dx;
                if (_dragEdges.HasFlag(Edge.Right)) rt += dx;
                if (_dragEdges.HasFlag(Edge.Top)) t += dy;
                if (_dragEdges.HasFlag(Edge.Bottom)) b += dy;
                _regions[_active] = RectCorners(MakeRect(l, t, rt, b));
                Invalidate();
                return;
            }

            case DragKind.RectMove:
            case DragKind.Points when _dragMask == AllPoints:
            {
                // Move the whole area, keeping it inside the image.
                var bb = _dragStartRect;
                float mx = Math.Clamp(dx, -bb.Left, _image.Width - bb.Right);
                float my = Math.Clamp(dy, -bb.Top, _image.Height - bb.Bottom);
                _regions[_active] = _dragStartPoints.Select(p => new PointF(MathF.Round(p.X + mx), MathF.Round(p.Y + my))).ToArray();
                Invalidate();
                return;
            }

            case DragKind.Points:
            {
                var q = (PointF[])_dragStartPoints.Clone();
                for (int k = 0; k < 4; k++)
                    if ((_dragMask & (1 << k)) != 0)
                        q[k] = ClampToImage(new PointF(MathF.Round(q[k].X + dx), MathF.Round(q[k].Y + dy)));
                // Refuse crossed or bent-in shapes: they can't be straightened. The corner just stops.
                if (IsConvex(q)) { _regions[_active] = q; Invalidate(); }
                return;
            }
        }

        if (_cropMode != CropMode.None)
        {
            if (_shape == CropShape.Rectangle)
            {
                var (index, edges) = HitTestRect(e.Location);
                Cursor = index >= 0 ? CursorFor(edges) : Cursors.Cross;
            }
            else
            {
                var (index, mask) = HitTestQuad(e.Location);
                Cursor = index < 0 ? Cursors.Cross : mask == AllPoints ? Cursors.SizeAll : Cursors.Hand;
            }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var kind = _drag;
        _drag = DragKind.None;
        if (kind == DragKind.None) return;
        if (kind == DragKind.Pan)
        {
            Cursor = _cropMode == CropMode.None ? Cursors.Default : Cursors.Cross;
            return;
        }

        // A click (or a tiny drag) doesn't make a box.
        if (_dragCreated && _active >= 0)
        {
            var s = BoundsOf(ToScreen(_regions[_active]));
            if (s.Width < LogicalToDeviceUnits(5) || s.Height < LogicalToDeviceUnits(5))
            {
                _regions.RemoveAt(_active);
                if (_replacedRegion != null) _regions.Add(_replacedRegion);
                _active = _regions.Count - 1;
            }
        }
        _dragCreated = false;
        _replacedRegion = null;
        Invalidate();
        RegionsChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (_image == null || _cropMode != CropMode.None || e.Button != MouseButtons.Left) return;
        // Toggle between fit and 100% at the clicked point.
        if (_fit || Math.Abs(_zoom - 1f) > 0.001f) { _fit = false; SetZoom(1f, e.Location); }
        else ZoomToFit();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) != 0 && _image != null)
        {
            StepZoom(Math.Sign(e.Delta), e.Location);
            if (e is HandledMouseEventArgs h) h.Handled = true;
            return;
        }
        base.OnMouseWheel(e);
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Delete && _cropMode != CropMode.None && _active >= 0)
        {
            RemoveActiveRegion();
            e.Handled = true;
        }
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_fit) ApplyFit();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) InvalidateCache();
        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- helpers

    private PointF ClampToImage(PointF p) => new(Math.Clamp(p.X, 0, _image.Width), Math.Clamp(p.Y, 0, _image.Height));

    private RectangleF MakeRect(float l, float t, float r, float b)
    {
        if (l > r) (l, r) = (r, l);
        if (t > b) (t, b) = (b, t);
        float L = MathF.Round(Math.Clamp(l, 0, _image.Width)), R = MathF.Round(Math.Clamp(r, 0, _image.Width));
        float T = MathF.Round(Math.Clamp(t, 0, _image.Height)), B = MathF.Round(Math.Clamp(b, 0, _image.Height));
        return RectangleF.FromLTRB(L, T, R, B);
    }
}
