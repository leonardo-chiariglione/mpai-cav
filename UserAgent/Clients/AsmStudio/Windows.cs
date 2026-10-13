using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

using NAudio.Wave;

namespace Mpai.AsmStudio;

public static class Look
{
    public static readonly Color Pane = Color.FromArgb(238, 241, 247);          // the colour of the left pane, the same for the list, Thalia and the text
    public static readonly Color PaneEdge = Color.FromArgb(215, 220, 230);
}

// What is heard: one sound at a time, from a WAV.
public static class Player
{
    private static WaveOutEvent? output;
    private static readonly object Lock = new();

    public static Task PlayAsync(byte[] wav)
    {
        Stop();
        var done = new TaskCompletionSource();
        var reader = new WaveFileReader(new MemoryStream(wav));
        var o = new WaveOutEvent();
        lock (Lock) output = o;
        o.PlaybackStopped += (_, _) => { lock (Lock) { if (ReferenceEquals(output, o)) output = null; } o.Dispose(); reader.Dispose(); done.TrySetResult(); };
        o.Init(reader); o.Play();
        return done.Task;
    }

    public static void Stop() { lock (Lock) { try { output?.Stop(); } catch { } } }
    public static bool Playing { get { lock (Lock) return output is not null; } }
}

// THALIA, SPEAKING: her face in the left pane; her mouth follows the loudness of what she says, half as open as the loudness would make it.
public sealed class ThaliaPanel : UserControl
{
    private readonly WebView2 web = new() { Dock = DockStyle.Fill };
    private readonly Label note = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(70, 80, 100), BackColor = Look.Pane, Padding = new Padding(12) };
    private bool ready;

    public ThaliaPanel()
    {
        BackColor = Look.Pane; Height = 230;
        note.Text = "Thalia is waking up...";
        Controls.Add(web); Controls.Add(note);
    }

    public async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Paths.Workspace, "webview"));
            await web.EnsureCoreWebView2Async(env);
            web.DefaultBackgroundColor = Look.Pane;
            web.CoreWebView2.SetVirtualHostNameToFolderMapping("cavapp.local", Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Allow);
            web.CoreWebView2.NavigationCompleted += (_, a) => { if (a.IsSuccess) { ready = true; note.Visible = false; } else note.Text = "Her face could not be loaded."; };
            web.CoreWebView2.Navigate("https://cavapp.local/thalia.html");
        }
        catch (Exception e) { note.Text = "Her face needs the WebView2 runtime and an internet connection (it draws with three.js).\r\n\r\n" + e.Message; }
    }

    private static float[] Envelope(byte[] wav, double frame)
    {
        var (rate, bits, channels) = Studio.Format(wav);
        if (bits != 16 || rate <= 0) return [];
        var step = Math.Max(1, (int)(rate * frame)) * channels;
        var list = new List<float>();
        for (var at = 44; at + step * 2 <= wav.Length; at += step * 2)
        {
            double sum = 0;
            for (var i = 0; i < step; i++) { var s = BitConverter.ToInt16(wav, at + i * 2) / 32768.0; sum += s * s; }
            list.Add((float)Math.Min(1, Math.Sqrt(sum / step) * 5) * 0.5f);
        }
        return [.. list];
    }

    public async Task SpeakAsync(byte[] wav)
    {
        var envelope = Envelope(wav, 0.04);
        var playing = Player.PlayAsync(wav);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!playing.IsCompleted)
        {
            var i = (int)(clock.Elapsed.TotalSeconds / 0.04);
            if (ready && i < envelope.Length && IsHandleCreated)
                try { await web.ExecuteScriptAsync($"window.thaliaMouth && window.thaliaMouth({envelope[i]:0.00})"); } catch { }
            await Task.Delay(40);
        }
        if (ready) try { await web.ExecuteScriptAsync("window.thaliaMouth && window.thaliaMouth(0)"); } catch { }
    }
}

// THE SMALL TEXT WINDOW, under her: what the user types, and what comes back; and the button that listens - press it to begin, press it again to end.
public sealed class DialoguePanel : UserControl
{
    private readonly RichTextBox log = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Look.Pane, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 9.5f) };
    private readonly TextBox input = new() { Dock = DockStyle.Bottom, Font = new Font("Segoe UI", 10f) };
    public Button Talk { get; } = new() { Text = "Press to talk", Dock = DockStyle.Bottom, Height = 36, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), BackColor = Color.FromArgb(220, 230, 246), FlatStyle = FlatStyle.Flat };
    public event Action<string>? Submitted;

    public DialoguePanel()
    {
        BackColor = Look.Pane; Height = 240;
        Controls.Add(log); Controls.Add(Talk); Controls.Add(input);
        input.PlaceholderText = "type what you want, and press Enter";
        Talk.FlatAppearance.BorderColor = Look.PaneEdge;
        input.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || string.IsNullOrWhiteSpace(input.Text)) return;
            e.SuppressKeyPress = true;
            var text = input.Text.Trim(); input.Clear(); Submitted?.Invoke(text);
        };
    }

    public void Add(string who, string text, Color colour)
    {
        if (InvokeRequired) { BeginInvoke(() => Add(who, text, colour)); return; }
        log.SelectionStart = log.TextLength; log.SelectionColor = colour; log.SelectionFont = new Font(log.Font, FontStyle.Bold);
        log.AppendText(who.Length > 0 ? who + "  " : ""); log.SelectionFont = log.Font; log.SelectionColor = who.Length > 0 ? Color.Black : colour; log.AppendText(text + "\n");
        log.ScrollToCaret();
    }
}

// A place, as numbers: the properties of a sound in the scene.
public sealed class PropertiesForm : Form
{
    private readonly NumericUpDown x = Box(-500, 500), y = Box(-500, 500), z = Box(-50, 50), yaw = Box(-360, 360), start = Box(0, 3600), end = Box(0, 3600);

    private static NumericUpDown Box(double min, double max) => new() { Minimum = (decimal)min, Maximum = (decimal)max, DecimalPlaces = 2, Increment = 0.1m, Width = 110 };

    public PropertiesForm(Member m)
    {
        Text = m.Name; FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterParent; MaximizeBox = MinimizeBox = false; ClientSize = new Size(300, 250);
        var rows = new (string, NumericUpDown, double)[] { ("x, to the right (m)", x, m.X), ("y, ahead (m)", y, m.Y), ("z, up (m)", z, m.Z), ("yaw (degrees; 180 faces you)", yaw, m.Yaw), ("starts at (seconds)", start, m.Start), ("ends at (seconds; 0: its end)", end, m.End ?? 0) };
        var top = 14;
        foreach (var (text, box, value) in rows)
        {
            Controls.Add(new Label { Text = text, Left = 14, Top = top + 3, Width = 170 }); box.Left = 180; box.Top = top; box.Value = (decimal)value; Controls.Add(box); top += 30;
        }
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 130, Top = 210, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 215, Top = 210, Width = 75 };
        Controls.AddRange([ok, cancel]); AcceptButton = ok; CancelButton = cancel;
    }

    public (double X, double Y, double Z, double Yaw, double Start, double? End) Values => ((double)x.Value, (double)y.Value, (double)z.Value, (double)yaw.Value, (double)start.Value, end.Value > 0 ? (double)end.Value : null);
}

// WHAT CAN BE SAID, in the editing space: large enough to read, one command to a line, and scrolled to find what is needed. Its text is marked: "# " a group,
// "## " a field, "_" a note, anything else a command.
public sealed class CommandsPanel : Panel
{

    // What can be said about the audio profile: one group for the object's fields and one for the scene's, a field each.
    public const string Profile = """
            # Of an object
            _Name the object, or choose it first.
            ## FrequencyRange
            limit the frequency range of bell.auo from 200 Hz to 8 kHz
            limit bell.auo to above 300 Hz
            limit bell.auo to below 5 kHz
            ## Spectrogram
            measure the spectrogram of bell.auo
            ## Loudness
            set the loudness of bell.auo to -20 LUFS
            make bell.auo 6 dB louder
            make bell.auo 3 dB quieter
            ## DirectionalPatterns
            make bell.auo cardioid
            _mostly forward
            make bell.auo bidirectional
            _forward and back
            make bell.auo omnidirectional
            _every direction
            # Of the scene
            ## Reflectivity
            set the early reflection time to 20 ms and the late reflection time to 80 ms
            ## Reverberation
            set the reverberation time (RT60) to 1.5 seconds
            set the RT30 to 0.7 seconds
            set the RT20 to 0.5 seconds
            set the early decay time (EDT) to 0.8 seconds
            add reverberation
            less reverberation
            ## Diffusion
            set the diffusion to 0.7
            ## Absorption
            set the absorption to 0.3
            ## Doppler
            set the Doppler direct sound factor to 0.5 and the indirect sound to 1
            _kept in the profile; nothing in a scene moves, so nothing is heard of it
            # To read the profile
            show the profile of bell.auo
            show the scene profile
            """;

    private readonly RichTextBox text = new() { ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 11f), ScrollBars = RichTextBoxScrollBars.Vertical, DetectUrls = false, TabStop = false, Cursor = Cursors.Default };
    private readonly Label title = new() { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 12f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0) };

    public CommandsPanel()
    {
        Visible = false; BorderStyle = BorderStyle.FixedSingle; BackColor = Color.White; Width = 560;
        var bar = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Look.Pane };
        var close = new Button { Text = "\u00D7", Dock = DockStyle.Right, Width = 40, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 14f), Cursor = Cursors.Hand };
        close.FlatAppearance.BorderSize = 0; close.Click += (_, _) => Visible = false;
        bar.Controls.Add(title); bar.Controls.Add(close);
        text.Padding = new Padding(10);
        Controls.Add(text); Controls.Add(bar);
        text.MouseEnter += (_, _) => text.Focus();                   // the wheel scrolls it as soon as the pointer is over it
    }

    private void Add(string line, Font font, Color colour, int indent, int before = 0)
    {
        if (before > 0 && text.TextLength > 0) { text.SelectionStart = text.TextLength; text.SelectionLength = 0; text.SelectionIndent = 0; text.AppendText(new string('\n', before)); }
        text.SelectionStart = text.TextLength; text.SelectionLength = 0;
        text.SelectionFont = font; text.SelectionColor = colour; text.SelectionIndent = indent; text.SelectionHangingIndent = 0;
        text.SelectedText = line + "\n";
    }

    public void Display(string heading, string markup)
    {
        title.Text = heading; text.Clear();
        using var group = new Font("Segoe UI", 13f, FontStyle.Bold); using var field = new Font("Segoe UI", 11.5f, FontStyle.Bold);
        using var command = new Font("Segoe UI", 11f); using var note = new Font("Segoe UI", 10f, FontStyle.Italic);
        foreach (var raw in markup.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith("## ")) Add(line[3..], field, Color.FromArgb(25, 70, 130), 10, 1);
            else if (line.StartsWith("# ")) Add(line[2..].ToUpperInvariant(), group, Color.FromArgb(150, 80, 10), 0, 2);
            else if (line.StartsWith("_")) Add(line[1..], note, Color.FromArgb(100, 100, 100), 28);
            else Add(line, command, Color.Black, 28);
        }
        text.SelectionStart = 0; text.ScrollToCaret(); Visible = true; BringToFront();
    }
}