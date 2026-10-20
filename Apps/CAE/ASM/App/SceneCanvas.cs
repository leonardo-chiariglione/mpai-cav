using System.Drawing.Drawing2D;

namespace Mpai.Cae.AsmApp;

// THE EDITING SPACE: a view of the scene from above, as large as the window. The user starts at the origin, looking up the page; x is to the right and
// y ahead, both in metres, numbered along the edges. A sound is a dot with its name; drag it to move it, drag the tip of its arrow to turn it. The user ("you")
// is moved and turned the same way, and the walls of the room are dragged to resize it (its label moves it). Drag the library's items onto the space to add them, drag the empty space to pan, turn the wheel to zoom.
public sealed class SceneCanvas : Control
{
    private readonly Studio studio;
    private double scale = 60;                       // pixels to the metre
    private PointF origin;                           // where (0, 0) is on the control
    private bool userMoved;
    private Member? hover, dragged;
    private (double X, double Y)? dragTo;
    private PointF dragFrom, panFrom; private PointF originAtPan;
    private bool panning;
    private bool movingListener, turningListener; private (double X, double Y)? listenerTo; private double? listenerYawTo;   // the user, being dragged or turned
    private Member? turning; private double? turnTo;
    [Flags] private enum Edge { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8, Label = 16 }
    private Edge roomEdge; private (double X0, double Y0, double X1, double Y1) roomFrom; private (double X0, double Y0, double X1, double Y1)? roomTo; private (double X, double Y) roomGrab;   // the room, being resized or moved                                                                    // a sound, being turned

    public Member? Selected { get; private set; }
    public event Action<Member, double, double>? Moved;
    public event Action<Member, double>? Turned;                 // a sound turned by its arrow: its new yaw
    public event Action<double, double>? ListenerMoved;          // the user moved by the mouse
    public event Action<double>? ListenerTurned;                 // the user turned by the mouse: the new yaw
    public event Action<Point>? ListenerContext;
    public event Action<double, double, double, double>? RoomChanged;   // the room resized or moved: its new box (x0, y0, x1, y1)
    public event Action<Point>? RoomContext;
    public event Action<Item, double, double>? Dropped;
    public event Action<Member, Point>? Context;
    public event Action<double, double>? CursorMoved;
    public event Action<Member?>? SelectionChanged;

    public SceneCanvas(Studio studio)
    {
        this.studio = studio;
        DoubleBuffered = true; AllowDrop = true; ResizeRedraw = true;
        BackColor = Color.FromArgb(250, 251, 253);
        studio.SceneChanged += () => { if (IsHandleCreated) BeginInvoke(() => { if (Selected is not null) Selected = studio.Members.FirstOrDefault(m => m.Name == Selected.Name); Invalidate(); }); };
    }

    public void Select(Member? m) { Selected = m; SelectionChanged?.Invoke(m); Invalidate(); }

    private PointF ToScreen(double x, double y) => new((float)(origin.X + x * scale), (float)(origin.Y - y * scale));
    private (double X, double Y) ToWorld(float sx, float sy) => ((sx - origin.X) / scale, (origin.Y - sy) / scale);

    private (double X, double Y) PlaceOf(Member m) => dragged is not null && m.Name == dragged.Name && dragTo is { } d ? d : (m.X, m.Y);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!userMoved && Width > 100) { origin = new PointF(Width / 2f, Height * 0.72f); Invalidate(); }
    }

    private static string Metres(double v) { var r = Math.Round(v, 2); var s = (r == 0 ? 0.0 : r).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture); return s is "-0" or "-0.0" ? "0" : s; }

    private static double NiceStep(double minMetres)
    {
        foreach (var s in new[] { 0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500 }) if (s >= minMetres) return s;
        return 1000;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(BackColor);
        using var small = new Font("Segoe UI", 8.5f); using var normal = new Font("Segoe UI", 9.5f); using var bold = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        var step = NiceStep(55 / scale);
        var (x0, y1) = ToWorld(0, 0); var (x1, y0) = ToWorld(Width, Height);

        using var grid = new Pen(Color.FromArgb(226, 230, 237)); using var major = new Pen(Color.FromArgb(200, 207, 218)); using var axis = new Pen(Color.FromArgb(120, 130, 150), 1.6f);
        using var label = new SolidBrush(Color.FromArgb(90, 100, 120)); using var halo = new SolidBrush(Color.FromArgb(235, 250, 251, 253));

        // The grid and the numbers (metres) along the bottom and the left edge.
        for (var gx = Math.Ceiling(x0 / step) * step; gx <= x1; gx += step)
        {
            var sx = ToScreen(gx, 0).X; var isMajor = Math.Abs(gx / (step * 5) - Math.Round(gx / (step * 5))) < 1e-6;
            g.DrawLine(isMajor ? major : grid, sx, 0, sx, Height);
        }
        for (var gy = Math.Ceiling(y0 / step) * step; gy <= y1; gy += step)
        {
            var sy = ToScreen(0, gy).Y; var isMajor = Math.Abs(gy / (step * 5) - Math.Round(gy / (step * 5))) < 1e-6;
            g.DrawLine(isMajor ? major : grid, 0, sy, Width, sy);
        }
        g.DrawLine(axis, 0, origin.Y, Width, origin.Y); g.DrawLine(axis, origin.X, 0, origin.X, Height);
        var decimals = step < 1 ? 1 : 0;
        for (var gx = Math.Ceiling(x0 / step) * step; gx <= x1; gx += step)
        {
            var sx = ToScreen(gx, 0).X; var text = Metres(gx);
            var size = g.MeasureString(text, small);
            g.FillRectangle(halo, sx - size.Width / 2, Height - 18, size.Width, 15);
            g.DrawString(text, small, label, sx - size.Width / 2, Height - 19);
            g.DrawLine(axis, sx, Height - 22, sx, Height - 28 + 6);
        }
        for (var gy = Math.Ceiling(y0 / step) * step; gy <= y1; gy += step)
        {
            var sy = ToScreen(0, gy).Y; var text = Metres(gy);
            var size = g.MeasureString(text, small);
            g.FillRectangle(halo, 3, sy - 7, size.Width, 14);
            g.DrawString(text, small, label, 3, sy - 8);
        }
        g.DrawString("x  (metres, to the right)", bold, label, Width - 190, Height - 40);
        g.DrawString("y  (metres, ahead)", bold, label, 30, 6);

        // The room, if the scene is in one: its walls, and what it measures.
        if (studio.Room is { } room)
        {
            var (rx0, ry0, rx1, ry1) = roomTo ?? (room.X0, room.Y0, room.X1, room.Y1);
            var a = ToScreen(rx0, ry1); var b = ToScreen(rx1, ry0);
            using var wall = new Pen(Color.FromArgb(70, 95, 140), 3f); using var floor = new SolidBrush(Color.FromArgb(24, 70, 95, 140));
            g.FillRectangle(floor, a.X, a.Y, b.X - a.X, b.Y - a.Y); g.DrawRectangle(wall, a.X, a.Y, b.X - a.X, b.Y - a.Y);
            var text = $"room  {rx1 - rx0:0.#} m by {ry1 - ry0:0.#} m, {room.Height:0.#} m high   (tail {room.Rt60:0.0} s)";
            var size = g.MeasureString(text, small);
            g.FillRectangle(halo, a.X + 4, a.Y + 3, size.Width, size.Height); g.DrawString(text, small, label, a.X + 4, a.Y + 3);
        }

        // The listener.
        var ear = studio.Listener; var earAt = listenerTo ?? (ear.X, ear.Y); var earYaw = listenerYawTo ?? ear.Yaw; var me = ToScreen(earAt.Item1, earAt.Item2);
        using (var head = new SolidBrush(Color.FromArgb(40, 70, 120)))
        {
            // the user: a head with a nose pointing the way the user faces (yaw 0 is ahead, up the screen)
            var rad = earYaw * Math.PI / 180; var fx = (float)(-Math.Sin(rad)); var fy = (float)(-Math.Cos(rad)); var sx = -fy; var sy = fx;
            PointF At(float forward, float side) => new(me.X + fx * forward + sx * side, me.Y + fy * forward + sy * side);
            g.FillEllipse(head, me.X - 13, me.Y - 13, 26, 26);
            g.FillPolygon(head, [At(22, 0), At(11, -7), At(11, 7)]);
            g.FillEllipse(Brushes.White, me.X - 4, me.Y - 4, 8, 8);
        }
        g.DrawString("you", bold, Brushes.DarkSlateBlue, me.X + 16, me.Y + 4);

        // The sounds.
        foreach (var m in studio.Members)
        {
            var (px, py) = PlaceOf(m); var p = ToScreen(px, py);
            var speech = m.Name.EndsWith(".bso", StringComparison.OrdinalIgnoreCase);
            var colour = speech ? Color.FromArgb(150, 90, 190) : Color.FromArgb(225, 130, 40);
            var isSelected = Selected is not null && Selected.Name == m.Name;
            var r = isSelected || (hover is not null && hover.Name == m.Name) ? 14 : 12;
            var rad = (turning is not null && turning.Name == m.Name && turnTo is { } turnedTo ? turnedTo : m.Yaw) * Math.PI / 180; var dx = (float)(-Math.Sin(rad)); var dy = (float)(-Math.Cos(rad));    // on the screen: up is ahead
            using (var arrow = new Pen(colour, 2.5f) { EndCap = LineCap.ArrowAnchor }) g.DrawLine(arrow, p.X, p.Y, p.X + dx * 30, p.Y + dy * 30);
            using (var fill = new SolidBrush(colour)) g.FillEllipse(fill, p.X - r, p.Y - r, 2 * r, 2 * r);
            using (var ring = new Pen(isSelected ? Color.FromArgb(30, 40, 60) : Color.White, isSelected ? 3 : 2)) g.DrawEllipse(ring, p.X - r, p.Y - r, 2 * r, 2 * r);
            var name = m.Name; var size = g.MeasureString(name, bold);
            g.FillRectangle(halo, p.X + r + 3, p.Y - size.Height / 2, size.Width, size.Height);
            g.DrawString(name, bold, Brushes.Black, p.X + r + 3, p.Y - size.Height / 2);
            var coords = $"{px:0.0}, {py:0.0} m";
            g.DrawString(coords, small, label, p.X + r + 3, p.Y + size.Height / 2 - 3);
        }
        if (studio.Members.Count == 0)
        {
            const string hint = "Drag a sound from the library into this space, or say or type what you want.";
            var size = g.MeasureString(hint, normal);
            g.DrawString(hint, normal, label, (Width - size.Width) / 2, Height * 0.30f);
        }
    }

    private Member? HitTest(Point at)
    {
        Member? best = null; var distance = 18.0;
        foreach (var m in studio.Members)
        {
            var (px, py) = PlaceOf(m); var p = ToScreen(px, py);
            var d = Math.Sqrt((p.X - at.X) * (p.X - at.X) + (p.Y - at.Y) * (p.Y - at.Y));
            if (d < distance) { distance = d; best = m; }
        }
        return best;
    }

    // The user: the body, and the nose (the tip of the arrow) that turns.
    private PointF EarScreen() { var ear = studio.Listener; return ToScreen(ear.X, ear.Y); }
    private bool HitListener(Point at) { var me = EarScreen(); return Math.Sqrt((at.X - me.X) * (at.X - me.X) + (at.Y - me.Y) * (at.Y - me.Y)) < 18; }
    private bool HitListenerNose(Point at)
    {
        var me = EarScreen(); var rad = studio.Listener.Yaw * Math.PI / 180; var tip = new PointF(me.X + (float)(-Math.Sin(rad)) * 24, me.Y + (float)(-Math.Cos(rad)) * 24);
        return Math.Sqrt((at.X - tip.X) * (at.X - tip.X) + (at.Y - tip.Y) * (at.Y - tip.Y)) < 10;
    }
    // A sound's arrow tip, 30 pixels from its dot in the way it faces.
    private Member? HitArrow(Point at)
    {
        foreach (var m in studio.Members)
        {
            var p = ToScreen(m.X, m.Y); var rad = m.Yaw * Math.PI / 180; var tip = new PointF(p.X + (float)(-Math.Sin(rad)) * 30, p.Y + (float)(-Math.Cos(rad)) * 30);
            if (Math.Sqrt((at.X - tip.X) * (at.X - tip.X) + (at.Y - tip.Y) * (at.Y - tip.Y)) < 9) return m;
        }
        return null;
    }
    // The walls of the room (7 pixels each side of a line), its corners, and its label inside the top-left corner, which moves it.
    private Edge HitRoom(Point at)
    {
        if (studio.Room is not { } r) return Edge.None;
        var a = ToScreen(r.X0, r.Y1); var b = ToScreen(r.X1, r.Y0); const int tol = 7;
        if (at.X < a.X - tol || at.X > b.X + tol || at.Y < a.Y - tol || at.Y > b.Y + tol) return Edge.None;
        var e = Edge.None;
        if (Math.Abs(at.X - a.X) <= tol) e |= Edge.Left; if (Math.Abs(at.X - b.X) <= tol) e |= Edge.Right;
        if (Math.Abs(at.Y - a.Y) <= tol) e |= Edge.Top; if (Math.Abs(at.Y - b.Y) <= tol) e |= Edge.Bottom;
        if (e != Edge.None) return e;
        return at.X >= a.X + 4 && at.X <= a.X + 340 && at.Y >= a.Y + 3 && at.Y <= a.Y + 20 ? Edge.Label : Edge.None;
    }
    private static Cursor CursorOf(Edge e) => e switch
    {
        Edge.Left or Edge.Right => Cursors.SizeWE, Edge.Top or Edge.Bottom => Cursors.SizeNS,
        Edge.Left | Edge.Top or Edge.Right | Edge.Bottom => Cursors.SizeNWSE, Edge.Right | Edge.Top or Edge.Left | Edge.Bottom => Cursors.SizeNESW,
        Edge.Label => Cursors.SizeAll, _ => Cursors.Default
    };

    private static double YawToward(double dx, double dy) => Math.Round(Math.Atan2(-dx, dy) * 180 / Math.PI, 0);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); Focus();
        if (e.Button == MouseButtons.Left)
        {
            if (HitListenerNose(e.Location)) { turningListener = true; listenerYawTo = studio.Listener.Yaw; return; }
            if (HitArrow(e.Location) is { } arrowed) { Select(arrowed); turning = arrowed; turnTo = arrowed.Yaw; return; }
        }
        var hit = HitTest(e.Location);
        if (hit is null && HitListener(e.Location))
        {
            if (e.Button == MouseButtons.Right) { Select(null); ListenerContext?.Invoke(e.Location); return; }
            if (e.Button == MouseButtons.Left) { Select(null); movingListener = true; listenerTo = (studio.Listener.X, studio.Listener.Y); return; }
        }
        if (e.Button == MouseButtons.Right) { Select(hit); if (hit is not null) Context?.Invoke(hit, e.Location); else if (HitRoom(e.Location) != Edge.None) RoomContext?.Invoke(e.Location); return; }
        if (e.Button != MouseButtons.Left) return;
        Select(hit);
        if (hit is not null) { dragged = hit; dragFrom = e.Location; dragTo = (hit.X, hit.Y); }
        else if (HitRoom(e.Location) is var wall && wall != Edge.None && studio.Room is { } room)
        {
            roomEdge = wall; roomFrom = (room.X0, room.Y0, room.X1, room.Y1); roomTo = roomFrom; roomGrab = ToWorld(e.X, e.Y);
        }
        else { panning = true; userMoved = true; panFrom = e.Location; originAtPan = origin; Cursor_ = Cursors.SizeAll; }
    }

    private Cursor Cursor_ { set => base.Cursor = value; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var (wx, wy) = ToWorld(e.X, e.Y); CursorMoved?.Invoke(wx, wy);
        if (roomEdge != Edge.None)
        {
            var o = roomFrom; double x0 = o.X0, y0 = o.Y0, x1 = o.X1, y1 = o.Y1;
            if (roomEdge == Edge.Label) { var mx = wx - roomGrab.X; var my = wy - roomGrab.Y; (x0, x1, y0, y1) = (o.X0 + mx, o.X1 + mx, o.Y0 + my, o.Y1 + my); }
            else
            {
                if (roomEdge.HasFlag(Edge.Left)) x0 = Math.Min(wx, o.X1 - 0.5);
                if (roomEdge.HasFlag(Edge.Right)) x1 = Math.Max(wx, o.X0 + 0.5);
                if (roomEdge.HasFlag(Edge.Top)) y1 = Math.Max(wy, o.Y0 + 0.5);
                if (roomEdge.HasFlag(Edge.Bottom)) y0 = Math.Min(wy, o.Y1 - 0.5);
            }
            roomTo = (Math.Round(x0, 2), Math.Round(y0, 2), Math.Round(x1, 2), Math.Round(y1, 2)); Invalidate(); return;
        }
        if (movingListener) { listenerTo = (Math.Round(wx, 2), Math.Round(wy, 2)); Invalidate(); return; }
        if (turningListener) { listenerYawTo = YawToward(wx - studio.Listener.X, wy - studio.Listener.Y); Invalidate(); return; }
        if (turning is not null) { turnTo = YawToward(wx - turning.X, wy - turning.Y); Invalidate(); return; }
        if (dragged is not null) { dragTo = (Math.Round(wx, 2), Math.Round(wy, 2)); Invalidate(); return; }
        if (panning) { origin = new PointF(originAtPan.X + e.X - panFrom.X, originAtPan.Y + e.Y - panFrom.Y); Invalidate(); return; }
        var h = HitTest(e.Location);
        if (h?.Name != hover?.Name) { hover = h; Invalidate(); }
        base.Cursor = HitListenerNose(e.Location) || HitArrow(e.Location) is not null ? Cursors.Cross : h is not null || HitListener(e.Location) ? Cursors.Hand : CursorOf(HitRoom(e.Location));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (roomEdge != Edge.None)
        {
            roomEdge = Edge.None; var boxTo = roomTo; roomTo = null; Invalidate();
            if (boxTo is { } n && (Math.Abs(n.X0 - roomFrom.X0) > 0.04 || Math.Abs(n.Y0 - roomFrom.Y0) > 0.04 || Math.Abs(n.X1 - roomFrom.X1) > 0.04 || Math.Abs(n.Y1 - roomFrom.Y1) > 0.04)) RoomChanged?.Invoke(n.X0, n.Y0, n.X1, n.Y1);
            base.Cursor = Cursors.Default; return;
        }
        if (movingListener)
        {
            movingListener = false; var dest = listenerTo; listenerTo = null; Invalidate();
            if (dest is { } at && (Math.Abs(at.X - studio.Listener.X) > 0.04 || Math.Abs(at.Y - studio.Listener.Y) > 0.04)) ListenerMoved?.Invoke(at.X, at.Y);
            return;
        }
        if (turningListener)
        {
            turningListener = false; var yaw = listenerYawTo; listenerYawTo = null; Invalidate();
            if (yaw is { } y && Math.Abs(y - studio.Listener.Yaw) >= 1) ListenerTurned?.Invoke(y);
            return;
        }
        if (turning is not null)
        {
            var m = turning; var yaw = turnTo; turning = null; turnTo = null; Invalidate();
            if (yaw is { } y && Math.Abs(y - m.Yaw) >= 1) Turned?.Invoke(m, y);
            return;
        }
        if (dragged is not null && dragTo is { } to)
        {
            var m = dragged; dragged = null;
            if (Math.Abs(to.X - m.X) > 0.04 || Math.Abs(to.Y - m.Y) > 0.04) Moved?.Invoke(m, to.X, to.Y);
            dragTo = null; Invalidate();
        }
        panning = false; base.Cursor = Cursors.Default;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var (wx, wy) = ToWorld(e.X, e.Y);
        userMoved = true; scale = Math.Clamp(scale * Math.Pow(1.0015, e.Delta), 8, 600);
        origin = new PointF((float)(e.X - wx * scale), (float)(e.Y + wy * scale));
        Invalidate();
    }

    protected override void OnDragOver(DragEventArgs e) => e.Effect = e.Data?.GetDataPresent(typeof(Item)) == true ? DragDropEffects.Copy : DragDropEffects.None;

    protected override void OnDragDrop(DragEventArgs e)
    {
        if (e.Data?.GetData(typeof(Item)) is not Item item) return;
        var p = PointToClient(new Point(e.X, e.Y)); var (wx, wy) = ToWorld(p.X, p.Y);
        Dropped?.Invoke(item, Math.Round(wx, 2), Math.Round(wy, 2));
    }

    public void ResetView() { userMoved = false; scale = 60; origin = new PointF(Width / 2f, Height * 0.72f); Invalidate(); }
}



