using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Mpai.Cae.AsmUa;

// THE AUDIO SCENE AS A PICTURE, FROM ABOVE, in metres: the listener at the middle looking ahead (up the picture), each sound where the scene puts it, with its name and its place.
// It draws what the scene descriptors say and decides nothing: x is to the right of the listener, y ahead.
public sealed class SceneView : FrameworkElement
{
    public sealed record Sound(string Name, double X, double Y, double Z, double Yaw);

    public sealed record Room(double CentreX, double CentreY, double Width, double Depth, double Height);

    private IReadOnlyList<Sound> _sounds = [];
    private double _scale = 1, _w, _h;
    private (string Name, double X, double Y)? _drag, _dropped;                // a sound being dragged, and one let go and waiting for the scene to say where it is

    // A sound dragged with the mouse and let go: its name and the place, in metres. The window sends it as the command it is; the scene then says where the sound is.
    public event Action<string, double, double, double>? Dropped;
    private Room? _room;
    private static readonly Brush[] Colours =
        [Brushes.SteelBlue, Brushes.IndianRed, Brushes.SeaGreen, Brushes.DarkOrange, Brushes.MediumPurple, Brushes.Teal, Brushes.Goldenrod, Brushes.SlateGray];

    private (double X, double Y, double Yaw) _listener = (0, 0, 0);          // where the listener is and where it looks: yaw in degrees, 0 ahead, counter-clockwise (90 is to its left)
    private (double X, double Y, double Yaw)? _listenerDrag, _listenerDropped;
    private bool _turning;                                                    // the arrow, not the listener, is held
    private const double ArrowLength = 46;

    // The listener dragged with the mouse and let go: where, in metres.
    public event Action<double, double, double>? ListenerDropped;

    public void Show(IReadOnlyList<Sound> sounds, Room? room = null, (double X, double Y, double Yaw)? listener = null)
    {
        _sounds = sounds; _room = room; _dropped = null; _listener = listener ?? (0, 0, 0); _listenerDropped = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(250, 250, 247)), null, new Rect(0, 0, w, h));
        if (w < 40 || h < 40) return;

        // The picture holds at least 4 m round the listener, more if a sound is farther.
        var roomReach = _room is { } rm ? Math.Max(Math.Max(Math.Abs(rm.CentreX) + rm.Width / 2, Math.Abs(rm.CentreY) + rm.Depth / 2), 0) + 1 : 0;
        var reach = Math.Max(Math.Max(4.0, roomReach), _sounds.Select(s => Math.Max(Math.Abs(s.X), Math.Abs(s.Y))).DefaultIfEmpty(0).Max() + 1);
        reach = Math.Max(reach, Math.Max(Math.Abs(_listener.X), Math.Abs(_listener.Y)) + 1);
        var scale = Math.Min(w, h) / 2 / reach * 0.92;
        _scale = scale; _w = w; _h = h;
        Point At(double x, double y) => new(w / 2 + x * scale, h / 2 - y * scale);

        var grid = new Pen(new SolidColorBrush(Color.FromRgb(228, 228, 222)), 1);
        var axis = new Pen(new SolidColorBrush(Color.FromRgb(170, 170, 160)), 1.5);
        for (var m = -(int)reach; m <= (int)reach; m++)
        {
            dc.DrawLine(m == 0 ? axis : grid, At(m, -reach), At(m, reach));
            dc.DrawLine(m == 0 ? axis : grid, At(-reach, m), At(reach, m));
            if (m != 0 && m % 2 == 0)
            {
                Text(dc, m.ToString(CultureInfo.InvariantCulture), At(m, 0) + new Vector(-4, 3), 10, Brushes.Gray);
                Text(dc, m.ToString(CultureInfo.InvariantCulture), At(0, m) + new Vector(4, -7), 10, Brushes.Gray);
            }
        }
        if (_room is { } room)                                                     // the room: a rectangle of its width and depth, with what it measures written on it
        {
            var a = At(room.CentreX - room.Width / 2, room.CentreY + room.Depth / 2); var b = At(room.CentreX + room.Width / 2, room.CentreY - room.Depth / 2);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 70, 130, 180)), new Pen(Brushes.SteelBlue, 3), new Rect(a, b));
            Text(dc, string.Create(CultureInfo.InvariantCulture, $"{room.Width:0.##} x {room.Depth:0.##} m, ceiling {room.Height:0.##} m"), new Point(a.X + 8, a.Y + 6), 14, Brushes.SteelBlue);
        }
        Text(dc, "ahead", At(0, reach) + new Vector(6, 2), 12, Brushes.Gray);
        Text(dc, "right", At(reach, 0) + new Vector(-36, 4), 12, Brushes.Gray);
        Text(dc, "metres", new Point(8, h - 20), 11, Brushes.Gray);

        // The listener: a triangle, and an arrow from it to where it looks - the arrow is held and turned with the mouse.
        var lp = _listenerDrag ?? _listenerDropped ?? _listener;
        var me = At(lp.X, lp.Y);
        var look = lp.Yaw * Math.PI / 180;
        var arrowTip = new Point(me.X - Math.Sin(look) * ArrowLength, me.Y - Math.Cos(look) * ArrowLength);
        dc.PushTransform(new RotateTransform(-lp.Yaw, me.X, me.Y));
        var triangle = new StreamGeometry();
        using (var g = triangle.Open())
        {
            g.BeginFigure(new Point(me.X, me.Y - 14), true, true);
            g.LineTo(new Point(me.X - 9, me.Y + 9), true, false);
            g.LineTo(new Point(me.X + 9, me.Y + 9), true, false);
        }
        dc.DrawGeometry(Brushes.DimGray, null, triangle);
        dc.Pop();
        var arrowPen = new Pen(Brushes.DimGray, 2.5);
        dc.DrawLine(arrowPen, new Point(me.X - Math.Sin(look) * 14, me.Y - Math.Cos(look) * 14), arrowTip);
        var head = new StreamGeometry();
        using (var g2 = head.Open())
        {
            g2.BeginFigure(new Point(arrowTip.X - Math.Sin(look) * 9, arrowTip.Y - Math.Cos(look) * 9), true, true);
            g2.LineTo(new Point(arrowTip.X + Math.Cos(look) * 6, arrowTip.Y - Math.Sin(look) * 6), true, false);
            g2.LineTo(new Point(arrowTip.X - Math.Cos(look) * 6, arrowTip.Y + Math.Sin(look) * 6), true, false);
        }
        dc.DrawGeometry(Brushes.DimGray, null, head);
        Text(dc, "you", me + new Vector(12, 6), 12, Brushes.DimGray);

        var i = 0;
        foreach (var shown in _sounds)
        {
            var s = _drag is { } d1 && d1.Name == shown.Name ? shown with { X = d1.X, Y = d1.Y } : _dropped is { } d2 && d2.Name == shown.Name ? shown with { X = d2.X, Y = d2.Y } : shown;
            var p = At(s.X, s.Y);
            var colour = Colours[i++ % Colours.Length];
            dc.DrawEllipse(colour, new Pen(Brushes.White, 2), p, 13, 13);
            // where it faces: 180 faces the listener, 0 faces away, 90 the listener's left
            var yaw = (s.Yaw - 180) * Math.PI / 180;
            var tip = new Point(p.X + Math.Sin(yaw) * 22 * -1, p.Y + Math.Cos(yaw) * 22);
            dc.DrawLine(new Pen(colour, 2), p, tip);
            Text(dc, s.Name, p + new Vector(-s.Name.Length * 3.6, 15), 13, Brushes.Black);
            Text(dc, $"{s.X:0.#}, {s.Y:0.#}, {s.Z:0.#}", p + new Vector(-22, 30), 10, Brushes.Gray);
        }
        if (_sounds.Count == 0) Text(dc, _room is null ? "Nothing in the scene yet." : "No sounds yet", new Point(w / 2 - 60, h / 2 + 60), 14, Brushes.Gray);
    }

    private Point ArrowTip() { var me = Where(_listener.X, _listener.Y); var a = _listener.Yaw * Math.PI / 180; return new Point(me.X - Math.Sin(a) * ArrowLength, me.Y - Math.Cos(a) * ArrowLength); }
    private Point Where(double x, double y) => new(_w / 2 + x * _scale, _h / 2 - y * _scale);
    private (double X, double Y) Metres(Point p) => (Math.Round((p.X - _w / 2) / _scale, 1), Math.Round((_h / 2 - p.Y) / _scale, 1));
    private Sound? SoundAt(Point p) => _sounds.Reverse().FirstOrDefault(s => (Where(s.X, s.Y) - p).Length <= 18);

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        if (SoundAt(p) is { } s) { _drag = (s.Name, s.X, s.Y); CaptureMouse(); InvalidateVisual(); }
        else if ((ArrowTip() - p).Length <= 14) { _listenerDrag = _listener; _turning = true; CaptureMouse(); InvalidateVisual(); }
        else if ((Where(_listener.X, _listener.Y) - p).Length <= 18) { _listenerDrag = _listener; _turning = false; CaptureMouse(); InvalidateVisual(); }
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drag is { } d) { var m = Metres(p); _drag = (d.Name, m.X, m.Y); InvalidateVisual(); }
        else if (_listenerDrag is { } ld)
        {
            if (_turning) { var me = Where(ld.X, ld.Y); _listenerDrag = (ld.X, ld.Y, Math.Round((Math.Atan2(-(p.X - me.X), me.Y - p.Y) * 180 / Math.PI + 360) % 360)); }
            else { var m = Metres(p); _listenerDrag = (m.X, m.Y, ld.Yaw); }
            InvalidateVisual();
        }
        else Cursor = SoundAt(p) is null && (Where(_listener.X, _listener.Y) - p).Length > 18 && (ArrowTip() - p).Length > 14 ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Hand;
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_listenerDrag is { } l)
        {
            _listenerDrag = null; ReleaseMouseCapture();
            if (Math.Abs(l.X - _listener.X) > 0.04 || Math.Abs(l.Y - _listener.Y) > 0.04 || Math.Abs(l.Yaw - _listener.Yaw) >= 1) { _listenerDropped = l; ListenerDropped?.Invoke(l.X, l.Y, l.Yaw); }
            InvalidateVisual();
            return;
        }
        if (_drag is not { } d) return;
        _drag = null; ReleaseMouseCapture();
        var was = _sounds.FirstOrDefault(s => s.Name == d.Name);
        if (was is not null && (Math.Abs(was.X - d.X) > 0.04 || Math.Abs(was.Y - d.Y) > 0.04)) { _dropped = d; Dropped?.Invoke(d.Name, d.X, d.Y, was.Z); }
        InvalidateVisual();
    }

    private static void Text(DrawingContext dc, string text, Point at, double size, Brush brush) =>
        dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, 1.0), at);
}
