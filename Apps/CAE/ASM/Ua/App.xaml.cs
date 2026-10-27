using System;
using System.IO;
using System.Windows;

namespace Mpai.Cae.AsmUa;

// AN AIM IS SILENT UNTIL A HOST ASKS TO HEAR IT: what the AIMs say goes to the crash log beside the program, so that "it did not work and said nothing" becomes "here is what it said".
public partial class App : Application
{
    public static readonly string CrashLog = Path.Combine(AppContext.BaseDirectory, "asm-ua-crash.log");

    public static void Record(string context, Exception error)
    {
        try { File.AppendAllText(CrashLog, $"{DateTime.Now:o}  [{context}]  {error}{Environment.NewLine}"); }
        catch { }
    }

    public static void Log(string line)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "asm-ua.log"), $"{DateTime.Now:HH:mm:ss.fff}  {line}{Environment.NewLine}"); }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // A picture of the room, to a file, with nothing else started (no avatar, no sound): `AsmUa.exe --scene-png <file>`.
        var at = Array.IndexOf(e.Args, "--scene-png");
        if (at >= 0 && at + 1 < e.Args.Length)
        {
            var room = new SceneView { Width = 700, Height = 600 };
            room.Show([new SceneView.Sound("violin", 2, 1, 0, 180), new SceneView.Sound("drums", -1, 3, 0, 180), new SceneView.Sound("voice", 3.5, -2, 0, 90)]);
            room.Measure(new Size(700, 600)); room.Arrange(new Rect(0, 0, 700, 600)); room.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(700, 600, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(room);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var file = File.Create(e.Args[at + 1])) encoder.Save(file);
            Shutdown(0);
            return;
        }
        new MainWindow().Show();
        Mpai.Core.AimLog.Sink = (aim, message) =>
        {
            try { File.AppendAllText(CrashLog, $"{DateTime.Now:o}  [{aim}]  {message}{Environment.NewLine}"); }
            catch { }
        };
    }
}
