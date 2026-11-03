using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace Mpai.Cae.AsmUa;

// AN AIM IS SILENT UNTIL A HOST ASKS TO HEAR IT: what the AIMs say goes to the crash log beside the program, so that "it did not work and said nothing" becomes "here is what it said".
public partial class App : Application
{
    public static readonly string CrashLog = Path.Combine(AppContext.BaseDirectory, "asm-ua-crash.log");

    // THE MODES. Demonstration (the default): typed words go straight to the Module as text, the microphone is off at the start, the grammar alone understands, nothing needs the language model.
    // --live: the whole chain as before (typed words are spoken and recognised, the language model answers what the grammar does not). --silent: nothing is played (a test).
    // --script <file>: the real window is driven by the lines of the file and what it shows is written to <file>.dump.txt.
    public static bool Live, Silent;
    public static string? Script, Storage;
    // THE CONTROLLER this User Agent talks to (a program of its own, started by whoever launches the App): `--controller <url>`.
    public static string ControllerUrl = "http://127.0.0.1:5057";

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
        // DIAGNOSTICS ARE ALWAYS ON in this window, before anything reads them: every capture leaves its levels (mic.log) and its audio (mic_*.wav) in <temporary folder>\mpai-diag.
        Environment.SetEnvironmentVariable("MPAI_DIAG", "1");
        base.OnStartup(e);
        if (Array.IndexOf(e.Args, "--drive") >= 0) { Shutdown(Task.Run(() => Driver.Run(e.Args)).GetAwaiter().GetResult()); return; }
        // The microphone button, on and off, to a file: `AsmUa.exe --mic-png <file>`.
        var mic = Array.IndexOf(e.Args, "--mic-png");
        if (mic >= 0 && mic + 1 < e.Args.Length)
        {
            var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Background = System.Windows.Media.Brushes.White };
            foreach (var on in new[] { true, false })
                row.Children.Add(new System.Windows.Controls.Primitives.ToggleButton { Style = (Style)FindResource("MicButton"), IsChecked = on, Margin = new Thickness(10), MinWidth = 250, Content = on ? "Microphone is on" : "Microphone is off - click to listen" });
            row.Measure(new Size(700, 80)); row.Arrange(new Rect(0, 0, 700, 80)); row.UpdateLayout();
            var shot = new System.Windows.Media.Imaging.RenderTargetBitmap(700, 80, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            shot.Render(row);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(shot));
            using (var file = File.Create(e.Args[mic + 1])) enc.Save(file);
            Shutdown(0); return;
        }
        // The whole window as it is laid out, to a file, nothing started (no avatar, no sound): `AsmUa.exe --window-png <file> [--mode voice|text]`.
        var win = Array.IndexOf(e.Args, "--window-png");
        if (win >= 0 && win + 1 < e.Args.Length)
        {
            var preview = new Mpai.Cae.AsmUa.MainWindow();
            var mode = Array.IndexOf(e.Args, "--mode"); if (mode >= 0 && mode + 1 < e.Args.Length) preview.ShowMode(e.Args[mode + 1]);
            var content = (FrameworkElement)preview.Content;
            content.Measure(new Size(1180, 740)); content.Arrange(new Rect(0, 0, 1180, 740)); content.UpdateLayout();
            var picture = new System.Windows.Media.Imaging.RenderTargetBitmap(1180, 740, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            picture.Render(content);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(picture));
            using (var file = File.Create(e.Args[win + 1])) png.Save(file);
            Shutdown(0); return;
        }
        // A picture of the room, to a file, with nothing else started (no avatar, no sound): `AsmUa.exe --scene-png <file>`.
        var at = Array.IndexOf(e.Args, "--scene-png");
        if (at >= 0 && at + 1 < e.Args.Length)
        {
            var room = new SceneView { Width = 700, Height = 600 };
            var asd = Array.IndexOf(e.Args, "--asd");                                 // `--asd <file>`: the Audio Scene Descriptors to draw, as the Scene device gets them
            if (asd >= 0 && asd + 1 < e.Args.Length) { var json = File.ReadAllText(e.Args[asd + 1]); room.Show(Mpai.Cae.AsmUa.MainWindow.SoundsOf(json), Mpai.Cae.AsmUa.MainWindow.RoomOf(json)); }
            else room.Show([new SceneView.Sound("violin", 2, 1, 0, 180), new SceneView.Sound("drums", -1, 3, 0, 180), new SceneView.Sound("voice", 3.5, -2, 0, 90)]);
            room.Measure(new Size(700, 600)); room.Arrange(new Rect(0, 0, 700, 600)); room.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(700, 600, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(room);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var file = File.Create(e.Args[at + 1])) encoder.Save(file);
            Shutdown(0);
            return;
        }
        Live = e.Args.Contains("--live"); Silent = e.Args.Contains("--silent");
        var script = Array.IndexOf(e.Args, "--script"); if (script >= 0 && script + 1 < e.Args.Length) Script = e.Args[script + 1];
        var controller = Array.IndexOf(e.Args, "--controller"); if (controller >= 0 && controller + 1 < e.Args.Length) ControllerUrl = e.Args[controller + 1];
        var store = Array.IndexOf(e.Args, "--storage"); if (store >= 0 && store + 1 < e.Args.Length) Storage = e.Args[store + 1];
        var window = new MainWindow();
        if (Script is not null) { window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -3000; window.Top = 0; window.ShowActivated = false; }
        window.Show();
        Mpai.Core.AimLog.Sink = (aim, message) =>
        {
            try { File.AppendAllText(CrashLog, $"{DateTime.Now:o}  [{aim}]  {message}{Environment.NewLine}"); if (message.StartsWith("[timing]")) Log($"[{aim}] {message}"); }
            catch { }
        };
    }
}
