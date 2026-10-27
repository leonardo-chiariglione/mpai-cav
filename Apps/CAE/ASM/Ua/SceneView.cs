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
    private Room? _room;
    private static readonly Brush[] Colours =
        [Brushes.SteelBlue, Brushes.IndianRed, Brushes.SeaGreen, Brushes.DarkOrange, Brushes.MediumPurple, Brushes.Teal, Brushes.Goldenrod, Brushes.SlateGray];

    public void Show(IReadOnlyList<Sound> sounds, Room? room = null)
    {
        _sounds = sounds; _room = room;
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
        var scale = Math.Min(w, h) / 2 / reach * 0.92;
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

        // The listener: a triangle looking ahead.
        var me = At(0, 0);
        var triangle = new StreamGeometry();
        using (var g = triangle.Open())
        {
            g.BeginFigure(new Point(me.X, me.Y - 14), true, true);
            g.LineTo(new Point(me.X - 9, me.Y + 9), true, false);
            g.LineTo(new Point(me.X + 9, me.Y + 9), true, false);
        }
        dc.DrawGeometry(Brushes.DimGray, null, triangle);
        Text(dc, "you", me + new Vector(12, 6), 12, Brushes.DimGray);

        var i = 0;
        foreach (var s in _sounds)
        {
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

    private static void Text(DrawingContext dc, string text, Point at, double size, Brush brush) =>
        dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, 1.0), at);
}
