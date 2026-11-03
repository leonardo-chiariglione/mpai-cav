using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace Mpai.Mmm.ScreenHelper;

// THE SCREEN HELPER OF THE MMM CONFERENCE. The browser shows a bar of its own ("Stop sharing") whenever a page captures a screen; this program shares without it. It runs in the tray of the
// machine of the sharer. The page of the meeting talks to it on http://127.0.0.1:7391:
//   GET  /status                 -> {helper, sharing, title}
//   POST /pick   {server, edit}  -> the sharer chooses, in a window of this program, the window or the screen to share; {ok, kind, title}. With edit, the window chosen is brought to the front.
//   POST /share  {server, token, id} -> the picture goes to the M-Instance, as JPEG frames of the Visual Object `id` (WebSocket /ws/v2.2/screen/{id}), only when it changes
//   POST /stop
// Only the page that asked for the choice (its Origin is the server given) can start the sharing, and what is shared is what the sharer chose.
// --auto screen | --auto "part of a window title": chooses without asking (for trying it).
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string? auto = null;
        for (var i = 0; i < args.Length - 1; i++) if (args[i] == "--auto") auto = args[i + 1];
        using var mutex = new Mutex(true, "Mpai.Mmm.ScreenHelper", out var first);
        if (!first) return;                                                              // already running
        Application.Run(new Helper(auto));
    }
}

internal sealed record Target(IntPtr Hwnd, string Title, bool Screen);

internal sealed class Helper : ApplicationContext
{
    public const int Port = 7391;
    private readonly NotifyIcon tray = new() { Icon = SystemIcons.Application, Visible = true, Text = "MMM screen helper" };
    private readonly Control ui = new();
    private readonly HttpListener http = new();
    private readonly string? auto;
    private Target? picked; private string? pickedFor; private bool pickedEdit;
    private CancellationTokenSource? sharing; private string title = ""; private Target? shared;

    public Helper(string? auto)
    {
        this.auto = auto;
        ui.CreateControl(); _ = ui.Handle;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Stop sharing", null, (_, _) => Stop());
        menu.Items.Add("Quit", null, (_, _) => { Stop(); tray.Visible = false; ExitThread(); });
        tray.ContextMenuStrip = menu;
        http.Prefixes.Add($"http://127.0.0.1:{Port}/");
        http.Start();
        _ = Task.Run(Serve);
    }

    private async Task Serve()
    {
        while (http.IsListening)
        {
            HttpListenerContext c;
            try { c = await http.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => Handle(c));
        }
    }

    private async Task Handle(HttpListenerContext c)
    {
        var rs = c.Response; var origin = c.Request.Headers["Origin"] ?? "";
        try
        {
            rs.AddHeader("Access-Control-Allow-Origin", origin.Length > 0 ? origin : "*");
            rs.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            rs.AddHeader("Access-Control-Allow-Headers", "Content-Type");
            rs.AddHeader("Access-Control-Allow-Private-Network", "true");                  // a page of another address, through Tailscale, asks the browser's permission and this
            rs.AddHeader("Vary", "Origin");
            if (c.Request.HttpMethod == "OPTIONS") { rs.StatusCode = 204; return; }
            JsonElement body = default;
            if (c.Request.HasEntityBody) { using var r = new StreamReader(c.Request.InputStream, Encoding.UTF8); var text = await r.ReadToEndAsync(); if (text.Length > 0) body = JsonDocument.Parse(text).RootElement.Clone(); }
            string Get(string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            object result = c.Request.Url!.AbsolutePath switch
            {
                "/status" => new { helper = true, sharing = sharing is not null, title },
                "/pick" => await Pick(origin, Get("server"), body.ValueKind == JsonValueKind.Object && body.TryGetProperty("edit", out var e) && e.ValueKind == JsonValueKind.True),
                "/share" => await Share(origin, Get("server"), Get("token"), Get("id")),
                "/stop" => Stopped(),
                "/front" => Front(),
                _ => new { ok = false, error = "no such call" }
            };
            var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result));
            rs.ContentType = "application/json"; rs.OutputStream.Write(json, 0, json.Length);
        }
        catch (Exception e) { try { rs.StatusCode = 500; var m = Encoding.UTF8.GetBytes(e.Message); rs.OutputStream.Write(m, 0, m.Length); } catch { } }
        finally { try { rs.Close(); } catch { } }
    }

    private object Stopped() { Stop(); return new { ok = true }; }

    // The window that is shared is brought to the front: the sharer goes to work on it (Edit slides while sharing).
    private object Front() { if (shared is { Screen: false } t && sharing is not null) { Windows.ToFront(t.Hwnd); return new { ok = true }; } return new { ok = false, error = "no window is shared" }; }

    // THE CHOICE: the sharer is shown the windows and the screens, and the address of the meeting that asks; nothing is shared before it says which.
    private async Task<object> Pick(string origin, string server, bool edit)
    {
        if (server.Length == 0 || !string.Equals(origin, server, StringComparison.OrdinalIgnoreCase)) return new { ok = false, error = "the page is not the meeting it names" };
        Target? t = null;
        if (auto is not null) t = Windows.Find(auto);
        else
        {
            var tcs = new TaskCompletionSource<Target?>();
            ui.BeginInvoke(() => { using var form = new PickerForm(server, Windows.List()); tcs.SetResult(form.ShowDialog() == DialogResult.OK ? form.Chosen : null); });
            t = await tcs.Task;
        }
        if (t is null) return new { ok = false, error = "nothing was chosen" };
        picked = t; pickedFor = server; pickedEdit = edit;
        if (edit && !t.Screen) Windows.ToFront(t.Hwnd);
        return new { ok = true, kind = t.Screen ? "screen" : "window", title = t.Title };
    }

    private async Task<object> Share(string origin, string server, string token, string id)
    {
        if (picked is null || !string.Equals(origin, pickedFor, StringComparison.OrdinalIgnoreCase) || !string.Equals(server, pickedFor, StringComparison.OrdinalIgnoreCase)) return new { ok = false, error = "nothing was chosen for this meeting" };
        Stop();
        var target = picked; picked = null;
        var cts = new CancellationTokenSource(); sharing = cts; title = target.Title; shared = target;
        tray.Text = ("Sharing: " + target.Title).Length > 60 ? ("Sharing: " + target.Title)[..60] : "Sharing: " + target.Title;
        var uri = new Uri(server.Replace("https://", "wss://").Replace("http://", "ws://") + $"/ws/v2.2/screen/{id}?token={Uri.EscapeDataString(token)}");
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(uri, cts.Token);
        _ = Task.Run(async () =>
        {
            try { await Capture.Stream(target, ws, cts.Token); }
            catch (Exception) { }
            finally { try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "over", CancellationToken.None); } catch { } ws.Dispose(); if (sharing == cts) { sharing = null; title = ""; tray.Text = "MMM screen helper"; } }
        });
        return new { ok = true };
    }

    private void Stop() { var s = sharing; if (s is not null) { sharing = null; s.Cancel(); } title = ""; tray.Text = "MMM screen helper"; }

    protected override void Dispose(bool disposing) { if (disposing) { try { http.Stop(); } catch { } tray.Dispose(); ui.Dispose(); } base.Dispose(disposing); }
}

// What the sharer can choose from: the windows and the screens.
internal static class Windows
{
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);

    public static List<Target> List()
    {
        var list = new List<Target>();
        var me = Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            if ((GetWindowLong(h, -20) & 0x80) != 0) return true;                                   // a tool window
            DwmGetWindowAttribute(h, 14, out var cloaked, 4); if (cloaked != 0) return true;         // hidden by the system (apps that are not on the desktop)
            var n = GetWindowTextLength(h); if (n == 0) return true;
            var sb = new StringBuilder(n + 1); GetWindowText(h, sb, sb.Capacity);
            var title = sb.ToString(); if (title == "Program Manager") return true;
            GetWindowThreadProcessId(h, out var pid); if (pid == me) return true;
            list.Add(new Target(h, title, false));
            return true;
        }, IntPtr.Zero);
        var screens = Screen.AllScreens.Select((s, i) => new Target(IntPtr.Zero - (i + 1), $"Entire screen {i + 1}" + (s.Primary ? " (main)" : ""), true));
        return screens.Concat(list.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)).ToList();
    }

    public static Target? Find(string what) => what.Equals("screen", StringComparison.OrdinalIgnoreCase) ? List().First(t => t.Screen) : List().FirstOrDefault(t => !t.Screen && t.Title.Contains(what, StringComparison.CurrentCultureIgnoreCase));

    public static void ToFront(IntPtr h) { if (IsIconic(h)) ShowWindow(h, 9); SetForegroundWindow(h); }
}

internal sealed class PickerForm : Form
{
    private readonly ListBox list = new() { Dock = DockStyle.Fill, IntegralHeight = false, Font = new Font("Segoe UI", 11) };
    private readonly List<Target> targets;
    public Target? Chosen { get; private set; }

    public PickerForm(string server, List<Target> targets)
    {
        this.targets = targets;
        Text = "MMM: what do you share?"; Width = 620; Height = 520; StartPosition = FormStartPosition.CenterScreen; TopMost = true; ShowInTaskbar = true;
        var head = new Label { Text = "The meeting at " + server + " asks to show a window or a screen of yours. Choose it:", Dock = DockStyle.Top, Height = 44, Padding = new Padding(8), Font = new Font("Segoe UI", 10) };
        var share = new Button { Text = "Share", Width = 110, Height = 36 }; var cancel = new Button { Text = "Cancel", Width = 110, Height = 36, DialogResult = DialogResult.Cancel };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        bottom.Controls.Add(cancel); bottom.Controls.Add(share);
        foreach (var t in targets) list.Items.Add(t.Title);
        list.SelectedIndex = targets.FindIndex(t => !t.Screen) is var i and >= 0 ? i : 0;
        void Take() { if (list.SelectedIndex >= 0) { Chosen = targets[list.SelectedIndex]; DialogResult = DialogResult.OK; } }
        share.Click += (_, _) => Take(); list.DoubleClick += (_, _) => Take();
        Controls.Add(list); Controls.Add(bottom); Controls.Add(head);
        AcceptButton = share; CancelButton = cancel;
    }
}

// THE PICTURE: a window is captured with PrintWindow (also when other windows are in front of it), a screen by copying it; scaled to 1280 pixels across at most, as JPEG, and sent only when it
// has changed (and now and then, for who comes in): the message is a byte 1 and the JPEG, as the page of the meeting sends it.
internal static class Capture
{
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    public static async Task Stream(Target t, ClientWebSocket ws, CancellationToken ct)
    {
        var jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg");
        var q = new EncoderParameters(1); q.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 72L);
        byte[]? previous = null; var lastSent = DateTime.MinValue;
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            await Task.Delay(300, ct);
            if (!t.Screen && !IsWindow(t.Hwnd)) return;                                                       // the window was closed: the sharing is over
            using var full = t.Screen ? Screenshot(-(int)(t.Hwnd.ToInt64()) - 1) : Window(t.Hwnd);
            if (full is null) continue;
            var w = Math.Min(1280, full.Width); var h = Math.Max(1, full.Height * w / full.Width);
            using var small = new Bitmap(48, 27); using (var g = Graphics.FromImage(small)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear; g.DrawImage(full, 0, 0, 48, 27); }
            var now = Pixels(small); long diff = 0;
            if (previous is not null) for (var i = 0; i < now.Length; i++) diff += Math.Abs(now[i] - previous[i]);
            previous = now;
            if (diff / (48.0 * 27) < 1.5 && DateTime.UtcNow - lastSent < TimeSpan.FromSeconds(2.5)) continue;       // nothing changed: nothing is sent
            using var scaled = new Bitmap(w, h); using (var g = Graphics.FromImage(scaled)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(full, 0, 0, w, h); }
            using var ms = new MemoryStream(); ms.WriteByte(1); scaled.Save(ms, jpeg, q);
            await ws.SendAsync(ms.ToArray(), WebSocketMessageType.Binary, true, ct);
            lastSent = DateTime.UtcNow;
        }
    }

    private static byte[] Pixels(Bitmap b)
    {
        var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[data.Stride * b.Height]; Marshal.Copy(data.Scan0, bytes, 0, bytes.Length); b.UnlockBits(data); return bytes;
    }

    private static Bitmap? Screenshot(int index)
    {
        var screens = Screen.AllScreens; if (index < 0 || index >= screens.Length) index = 0;
        var r = screens[index].Bounds; var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp); g.CopyFromScreen(r.Location, Point.Empty, r.Size); return bmp;
    }

    private static Bitmap? Window(IntPtr h)
    {
        if (IsIconic(h) || !GetWindowRect(h, out var win)) return null;                                       // minimised: nothing to see until it is back
        var ok = DwmGetWindowAttribute(h, 9, out var ext, Marshal.SizeOf<RECT>()) == 0;                          // the frame as it is seen, without the invisible borders
        if (!ok) ext = win;
        int W = win.Right - win.Left, H = win.Bottom - win.Top; if (W <= 0 || H <= 0) return null;
        using var whole = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(whole)) { var dc = g.GetHdc(); try { PrintWindow(h, dc, 2); } finally { g.ReleaseHdc(dc); } }       // 2: the whole content, also of the windows drawn by the system
        var crop = new Rectangle(ext.Left - win.Left, ext.Top - win.Top, ext.Right - ext.Left, ext.Bottom - ext.Top);
        crop.Intersect(new Rectangle(0, 0, W, H)); if (crop.Width <= 0 || crop.Height <= 0) return null;
        return whole.Clone(crop, PixelFormat.Format32bppArgb);
    }
}
