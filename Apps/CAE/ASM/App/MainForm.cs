using System.Text.RegularExpressions;

using NAudio.Wave;

using Mpai.Core;

namespace Mpai.Cae.AsmApp;

// AUDIO SCENE MANAGEMENT, the App. The editing space fills the window; on the left, in one colour, the audio objects (named as in a directory), and at the bottom of that pane the
// small text window. Everything the user does to the scene is a User Command; Undo revokes the last. The App answers briefly: "Roger." where it is done.
public sealed class MainForm : Form
{
    private readonly Studio studio = new();
    private readonly SceneCanvas canvas;
    private readonly CommandsPanel commandsPanel = new();
    private readonly ListView library = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, LabelEdit = true, HideSelection = false, MultiSelect = false, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10f), BackColor = Look.Pane };
    private readonly Panel pane = new() { Dock = DockStyle.Left, Width = 310, BackColor = Look.Pane };
    private readonly Splitter splitter = new() { Dock = DockStyle.Left, Width = 5, BackColor = Look.PaneEdge };
    private readonly ToolStrip tools = new() { GripStyle = ToolStripGripStyle.Hidden, Font = new Font("Segoe UI", 10f), Padding = new Padding(6, 4, 6, 4) };
    private readonly ToolStripStatusLabel cursorLabel = new() { AutoSize = false, Width = 230, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel selectionLabel = new() { AutoSize = false, Width = 380, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel messageLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripButton undoButton = new("Undo") { ToolTipText = "Revoke the last command (Ctrl+Z)" };
    private readonly ToolStripButton dialogueToggle = new("Text") { CheckOnClick = true, Checked = true, ToolTipText = "The small text window" };
    private readonly ToolStripButton libraryToggle = new("Audio objects") { CheckOnClick = true, Checked = true };
    private readonly DialoguePanel dialogue = new() { Dock = DockStyle.Fill };
    private int acknowledged;

    public MainForm()
    {
        Text = "ASM Studio - Audio Scene Management"; WindowState = FormWindowState.Maximized; KeyPreview = true; Font = new Font("Segoe UI", 9.5f);
        canvas = new SceneCanvas(studio) { Dock = DockStyle.Fill };
        canvas.Controls.Add(commandsPanel); canvas.Resize += (_, _) => PlaceCommands(); PlaceCommands();

        // the audio objects, named as in a directory
        library.Columns.Add("Name", 150); library.Columns.Add("Type", 90); library.Columns.Add("s", 45, HorizontalAlignment.Right);
        library.Groups.Add(new ListViewGroup("auo", "Audio objects  (.auo)")); library.Groups.Add(new ListViewGroup("bso", "Speech  (.bso)")); library.Groups.Add(new ListViewGroup("asd", "Scenes  (.asd)"));
        var title = new Label { Text = "Audio objects", Dock = DockStyle.Top, Height = 28, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Padding = new Padding(8, 5, 0, 0), BackColor = Look.Pane };

        // at the bottom of the same pane, in the same colour: the text
        var lower = new Panel { Dock = DockStyle.Bottom, Height = 480, BackColor = Look.Pane };
        lower.Controls.Add(dialogue);
        pane.Controls.Add(library); pane.Controls.Add(lower); pane.Controls.Add(title);

        tools.Items.AddRange([
            new ToolStripButton("Import sound...", null, (_, _) => ImportSound()), new ToolStripSeparator(),
            undoButton, new ToolStripSeparator(),
            new ToolStripButton("Play scene", null, async (_, _) => await PlaySceneAsync()), new ToolStripButton("Stop", null, (_, _) => Player.Stop()), new ToolStripSeparator(),
            new ToolStripButton("Save scene...", null, (_, _) => SaveScene()), new ToolStripButton("Commands", null, (_, _) => ShowCommands()) { ToolTipText = "What can be said about the audio profile" }, new ToolStripButton("Reset view", null, (_, _) => canvas.ResetView()), new ToolStripSeparator(),
            libraryToggle, dialogueToggle]);
        undoButton.Click += async (_, _) => await UndoAsync();
        libraryToggle.CheckedChanged += (_, _) => library.Visible = libraryToggle.Checked;
        dialogueToggle.CheckedChanged += (_, _) => { dialogue.Visible = dialogueToggle.Checked; ArrangePane(); };

        var status = new StatusStrip { SizingGrip = false, Dock = DockStyle.Bottom };
        status.Items.AddRange([cursorLabel, selectionLabel, messageLabel]);

        Controls.Add(canvas); Controls.Add(splitter); Controls.Add(pane); Controls.Add(tools); Controls.Add(status);
        tools.Dock = DockStyle.Top;

        // wiring
        studio.CatalogChanged += () => BeginInvoke(FillLibrary);
        studio.SceneChanged += () => BeginInvoke(() => undoButton.Enabled = studio.CanUndo);
        canvas.CursorMoved += (x, y) => cursorLabel.Text = $"x = {x:0.00} m    y = {y:0.00} m";
        canvas.SelectionChanged += m => selectionLabel.Text = m is null ? "" : $"{m.Name}   at {m.X:0.00}, {m.Y:0.00} m   yaw {m.Yaw:0}   {(m.End is { } end ? $"sounds {m.Start:0.##} to {end:0.##} s" : $"starts {m.Start:0.##} s")}";
        canvas.Moved += async (m, x, y) => Report(await studio.MoveAsync(m, x, y));
        canvas.Turned += async (m, yaw) => Report(await studio.MoveAsync(m, m.X, m.Y, null, yaw, null, $"{m.Name} faces {yaw:0}\u00B0."));
        canvas.ListenerMoved += async (x, y) => Report(await studio.ListenerAsync(x, y, studio.Listener.Z, studio.Listener.Yaw));
        canvas.ListenerTurned += async yaw => Report(await studio.ListenerAsync(studio.Listener.X, studio.Listener.Y, studio.Listener.Z, yaw));
        canvas.ListenerContext += at => ListenerMenu(at);
        canvas.RoomChanged += async (x0, y0, x1, y1) => Report(await studio.ResizeRoomAsync(x0, y0, x1, y1));
        canvas.RoomContext += at => RoomMenu(at);
        canvas.Dropped += async (item, x, y) => await AddToSceneAsync(item, x, y);
        canvas.Context += (m, at) => SceneMenu(m, at);
        library.ItemDrag += (_, e) => { if ((e.Item as ListViewItem)?.Tag is Item item && item.Kind != "asd") library.DoDragDrop(item, DragDropEffects.Copy); };
        library.DoubleClick += async (_, _) => { if (library.SelectedItems.Count > 0 && library.SelectedItems[0].Tag is Item item) await OpenOrAddAsync(item); };
        library.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right && library.GetItemAt(e.X, e.Y)?.Tag is Item item) LibraryMenu(item, e.Location); };
        library.AfterLabelEdit += (_, e) =>
        {
            e.CancelEdit = true;
            if (e.Label is null || library.Items[e.Item].Tag is not Item item) return;
            var name = e.Label.Trim(); var ext = "." + item.Kind;
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name = name[..^ext.Length];
            if (name.Length > 0) studio.Rename(item, name);
        };
        library.KeyDown += (_, e) => { if (e.KeyCode == Keys.F2 && library.SelectedItems.Count > 0) library.SelectedItems[0].BeginEdit(); };
        dialogue.Submitted += async text => await WordsAsync(text);
        KeyDown += async (_, e) => { if (e.Control && e.KeyCode == Keys.Z) { e.SuppressKeyPress = true; await UndoAsync(); } };
        Shown += async (_, _) => await StartAsync();
        FormClosed += (_, _) => { Player.Stop(); studio.Dispose(); };
    }

    // The text window is shown or hidden with its pane.
    private void ArrangePane()
    {
        var lower = dialogue.Parent!;
        lower.Visible = dialogue.Visible;
        lower.Height = 250;
    }

    private async Task StartAsync()
    {
        undoButton.Enabled = false;
        if (studio.Catalog.Items.Count == 0) { messageLabel.Text = "Making a few sounds to begin with..."; await Task.Run(studio.SeedAsync); messageLabel.Text = ""; }
        FillLibrary();
        await studio.RestoreAsync();
        canvas.Invalidate();
        Say("Hello. Type what you want, for example: place the bell at 2 m x, 5 m y. I understand: place, move, remove, play, stop, undo, save.");
    }

    private void FillLibrary()
    {
        library.BeginUpdate(); library.Items.Clear();
        foreach (var item in studio.Catalog.Items.OrderBy(i => i.Kind).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            var li = new ListViewItem(item.File) { Tag = item, Group = library.Groups[item.Kind], ForeColor = item.Kind == "bso" ? Color.FromArgb(120, 60, 160) : item.Kind == "asd" ? Color.FromArgb(30, 100, 60) : Color.FromArgb(170, 90, 10) };
            li.SubItems.Add(item.Kind == "asd" ? "scene" : item.Kind == "bso" ? "speech" + (item.Language is { Length: > 0 } l ? " (" + l.Split('-', '_')[0] + ")" : "") : item.Audio ? "audio object" : "basic");
            li.SubItems.Add(item.Kind == "asd" ? "" : item.Seconds.ToString("0.0"));
            library.Items.Add(li);
        }
        library.EndUpdate();
    }

    // ---- what is said ------------------------------------------------------------------------------

    // It is written. (The vocal interface is not part of the Module now: nothing is spoken.)
    private void Say(string text, string? spoken = null)
    {
        dialogue.Add("ASM", text, Color.FromArgb(40, 90, 160));
        messageLabel.Text = text;
    }

    // Done: "Roger." (or "Understood."), the details in writing. Not done: why, in a few words.
    private void Report(Outcome outcome)
    {
        if (outcome.Ok) Say(outcome.Text, new[] { "Roger.", "Understood." }[acknowledged++ % 2]);
        else Say(outcome.Text, outcome.Text.Length <= 45 ? outcome.Text : "I cannot do that.");
    }

    // ---- the audio objects ---------------------------------------------------------------------------

    private void ImportSound()
    {
        using var dialog = new OpenFileDialog { Title = "Import a sound or an audio object", Filter = "Sounds and audio objects (*.wav;*.auo;*.json)|*.wav;*.auo;*.json|WAV sounds (*.wav)|*.wav|Audio objects (*.auo;*.json)|*.auo;*.json", Multiselect = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        foreach (var path in dialog.FileNames)
            _ = ImportAsync(path);
    }

    private async Task ImportAsync(string path)
    {
        try { var item = await studio.ImportFileAsync(path); messageLabel.Text = $"{item.File} is among the audio objects ({(item.Audio ? "audio object" : "basic")}, {item.Seconds:0.0} s)."; }
        catch (Exception e) { MessageBox.Show(this, e.Message, "That sound could not be imported", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private async Task OpenOrAddAsync(Item item)
    {
        if (item.Kind == "asd") { Report(await studio.OpenSceneAsync(item)); return; }
        await AddToSceneAsync(item, 0, 2);
    }

    private async Task AddToSceneAsync(Item item, double x, double y) => Report(await studio.AddAsync(item, x, y));

    private void LibraryMenu(Item item, Point at)
    {
        var menu = new ContextMenuStrip();
        if (item.Kind == "asd") menu.Items.Add("Open", null, async (_, _) => Report(await studio.OpenSceneAsync(item)));
        else { menu.Items.Add("Add to the scene", null, async (_, _) => await AddToSceneAsync(item, 0, 2)); menu.Items.Add("Hear it", null, async (_, _) => await Hear(item)); }
        menu.Items.Add("Rename (F2)", null, (_, _) => library.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.BeginEdit());
        menu.Items.Add("Take out of the list", null, (_, _) => studio.Forget(item));
        menu.Show(library, at);
    }

    private async Task Hear(Item item)
    {
        if (studio.SoundOf(item) is { Length: > 44 } wav) await Player.PlayAsync(wav);
    }

    // The room: drag a wall or a corner to resize it, drag its label to move it; the right button on a wall gives its height and a fit round the objects.
    private void RoomMenu(Point at)
    {
        if (studio.Room is not { } room) return;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Height...", null, async (_, _) =>
        {
            using var form = new Form { Text = "Height of the room", ClientSize = new Size(300, 90), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false };
            var box = new NumericUpDown { Left = 170, Top = 14, Width = 110, Minimum = 0.5m, Maximum = 100, DecimalPlaces = 1, Increment = 0.5m, Value = (decimal)Math.Clamp(room.Height, 0.5, 100) };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 130, Top = 52, Width = 75 }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 215, Top = 52, Width = 75 };
            form.Controls.AddRange([new Label { Text = "floor to ceiling (m)", Left = 14, Top = 17, Width = 150 }, box, ok, cancel]); form.AcceptButton = ok; form.CancelButton = cancel;
            if (form.ShowDialog(this) == DialogResult.OK) Report(await studio.ResizeRoomAsync(room.X0, room.Y0, room.X1, room.Y1, (double)box.Value));
        });
        menu.Items.Add("Fit round the objects", null, async (_, _) => Report(await studio.BuildRoomAsync(studio.Members.ToList(), 1, room.Height)));
        menu.Show(canvas, at);
    }

    // The user ("you"), moved with the mouse like a sound: drag the body to move, drag the nose to turn; the right button gives a few places.
    private void ListenerMenu(Point at)
    {
        var l = studio.Listener; var menu = new ContextMenuStrip();
        menu.Items.Add("Face ahead", null, async (_, _) => Report(await studio.ListenerAsync(l.X, l.Y, l.Z, 0)));
        menu.Items.Add("Go to the origin", null, async (_, _) => Report(await studio.ListenerAsync(0, 0, l.Z, l.Yaw)));
        if (studio.Room is { } room) menu.Items.Add("Go to the centre of the room", null, async (_, _) => Report(await studio.ListenerAsync((room.X0 + room.X1) / 2, (room.Y0 + room.Y1) / 2, l.Z, l.Yaw)));
        menu.Show(canvas, at);
    }

    private void SceneMenu(Member m, Point at)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Properties...", null, async (_, _) =>
        {
            using var form = new PropertiesForm(m);
            if (form.ShowDialog(this) == DialogResult.OK) { var v = form.Values; Report(await studio.MoveAsync(m, v.X, v.Y, v.Z, v.Yaw, v.Start, null, v.End, v.End is null)); }
        });
        menu.Items.Add("Save as an audio object...", null, async (_, _) => await SaveObjectAsync(m, null));
        menu.Items.Add("Face me", null, async (_, _) => Report(await studio.MoveAsync(m, m.X, m.Y, m.Z, 180)));
        menu.Items.Add("Rename...", null, (_, _) =>
        {
            if (studio.Catalog.ByFile(m.Name) is { } item) { libraryToggle.Checked = true; var li = library.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag == item); li?.BeginEdit(); }
        });
        menu.Items.Add("Take out of the scene", null, async (_, _) => Report(await studio.RemoveAsync(m)));
        menu.Show(canvas, at);
    }

    // ---- the scene ---------------------------------------------------------------------------------

    private async Task UndoAsync() => Report(await studio.UndoAsync());

    private async Task PlaySceneAsync()
    {
        if (studio.Members.Count == 0) { Report(new Outcome(false, "The scene is empty.")); return; }
        try
        {
            messageLabel.Text = "Rendering..."; var wav = await Task.Run(studio.Render);
            var heard = studio.Room is { } room ? $"Playing the scene in the room (tail {room.Rt60:0.0} s)." : "Playing the scene.";
            messageLabel.Text = heard; dialogue.Add("", heard, Color.FromArgb(120, 120, 120));
            await Player.PlayAsync(wav); messageLabel.Text = "Done playing.";
        }
        catch (Exception e) { messageLabel.Text = e.Message; }
    }

    private void SaveScene()
    {
        if (studio.Members.Count == 0) { Report(new Outcome(false, "There is no scene to save.")); return; }
        var current = studio.CurrentScene;
        using var form = new Form { Text = "Save scene", ClientSize = new Size(360, 90), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false };
        var name = new TextBox { Left = 70, Top = 14, Width = 270, Text = current?.Name ?? "scene" };
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Left = 180, Top = 50, Width = 75 }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 265, Top = 50, Width = 75 };
        form.Controls.AddRange([new Label { Text = "Name", Left = 14, Top = 17, Width = 50 }, name, ok, cancel]); form.AcceptButton = ok; form.CancelButton = cancel;
        if (form.ShowDialog(this) != DialogResult.OK || name.Text.Trim().Length == 0) return;
        var n = name.Text.Trim(); if (n.EndsWith(".asd", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        var item = studio.SaveScene(n);
        Report(new Outcome(item is not null, item is not null ? $"The scene is saved as {item.File}." : "There is no scene to save."));
    }

    // ---- saving: what is on stage, as the commands left it, under a name that keeps its extension ---------------------

    private (bool Scene, Member? Member)? pendingSave;

    // "save", "save the scene", "save the bell", "save it as concert": without a name she asks for one.
    private async Task SaveWordsAsync(string t, string said)
    {
        var rest = Regex.Replace(t, @"^save\b\s*", "");
        var named = Regex.Match(rest, @"^(.*?)\s*\b(?:as|called|named)\s+(.+)$");
        var what = named.Success ? named.Groups[1].Value : rest;
        var name = named.Success ? Regex.Match(said, @"\b(?:as|called|named)\s+(.+)$", RegexOptions.IgnoreCase).Groups[1].Value : "";
        var scene = Regex.IsMatch(what, @"\bscene\b");
        var member = scene ? null : MemberFor(what);
        if (!scene && member is null)
        {
            if (Regex.IsMatch(what, @"\b(object|sound)\b")) { Say("Which object? Say its name.", "Which object?"); return; }
            scene = true;                                       // "save" alone is the scene
        }
        if (scene && studio.Members.Count == 0) { Report(new Outcome(false, "There is no scene to save.")); return; }
        if (string.IsNullOrWhiteSpace(name)) { pendingSave = (scene, member); Say($"What name? It will be {(scene ? ".asd" : ".auo")}.", "What name?"); return; }
        await SaveNamedAsync(scene, member, name);
    }

    private async Task SaveNamedAsync(bool scene, Member? member, string name)
    {
        name = Regex.Replace(name.Trim(), @"\.(asd|auo)$", "", RegexOptions.IgnoreCase);
        if (name.Length == 0) { pendingSave = (scene, member); Say("What name?", "What name?"); return; }
        if (scene) { var item = studio.SaveScene(name); Report(new Outcome(item is not null, item is not null ? $"The scene is saved as {item.File}." : "There is no scene to save.")); }
        else await SaveObjectAsync(member!, name);
    }

    private async Task SaveObjectAsync(Member m, string? name)
    {
        if (name is null)
        {
            using var form = new Form { Text = "Save as an audio object", ClientSize = new Size(360, 90), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false };
            var box = new TextBox { Left = 70, Top = 14, Width = 270, Text = Path.GetFileNameWithoutExtension(m.Name) };
            var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Left = 180, Top = 50, Width = 75 }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 265, Top = 50, Width = 75 };
            form.Controls.AddRange([new Label { Text = "Name", Left = 14, Top = 17, Width = 50 }, box, ok, cancel]); form.AcceptButton = ok; form.CancelButton = cancel;
            if (form.ShowDialog(this) != DialogResult.OK || box.Text.Trim().Length == 0) return;
            name = Regex.Replace(box.Text.Trim(), @"\.auo$", "", RegexOptions.IgnoreCase);
        }
        try { var item = await Task.Run(() => studio.SaveObject(m, name)); Report(new Outcome(item is not null, item is not null ? $"The object is saved as {item.File}." : "That could not be saved.")); }
        catch (Exception e) { Report(new Outcome(false, "That could not be saved: " + e.Message)); }
    }

    // ---- words: typed, or spoken ------------------------------------------------------------------

    private async Task WordsAsync(string text)
    {
        dialogue.Add("You", text, Color.FromArgb(60, 60, 60));
        text = SpokenNames(text);
        var t = text.Trim().TrimEnd('.', '!', '?').ToLowerInvariant();
        try
        {
            if (pendingIntent is { } waiting)   // she asked which object (or where to): what is said now may be its name (or the place), and the command goes on
            {
                pendingIntent = null;
                if (waiting.Verb is "move" or "listener" && Intent.Parse(waiting.Words + " " + text) is { HasPlace: true } placed && await DoAsync(placed with { Words = waiting.Words + " " + text })) return;
                if (MemberFor(text) is not null || ItemFor(text) is not null)
                {
                    var carried = Intent.Parse(waiting.Words + " " + text);
                    if (carried is not null && await DoAsync(carried with { Words = waiting.Words + " " + text })) return;
                }
            }
            if (pendingSave is { } pending)   // she asked for a name: what is said now is the name
            {
                pendingSave = null;
                if (Regex.IsMatch(t, @"^(cancel|never ?mind|forget it|no)$")) { Say("Roger.", "Roger."); return; }
                await SaveNamedAsync(pending.Scene, pending.Member, text.Trim().TrimEnd('.', '!', '?'));
                return;
            }
            if (Intent.Parse(text) is { Verb: "start" or "end" or "time" or "profilehelp" or "profile" or "sceneprofile" } early && await DoAsync(early)) return;   // "stop the drum at 4 seconds" is not "stop"; "play the audio profile commands" is not "play"
            if (Regex.IsMatch(t, @"^(undo|take that back|revoke)")) { await UndoAsync(); return; }
            if (Regex.IsMatch(t, @"^(play|hear|listen)")) { if (ItemFor(text) is { Kind: not "asd" } heardItem) await Hear(heardItem); else await PlaySceneAsync(); return; }
            if (Regex.IsMatch(t, @"^(stop|quiet|silence)")) { Player.Stop(); return; }
            if (Regex.IsMatch(t, @"^save\b")) { await SaveWordsAsync(t, text.Trim().TrimEnd('.', '!', '?')); return; }

            if (Intent.Parse(text) is { } intent && await DoAsync(intent)) return;

            Say("I did not understand that. I understand: place, move, remove, play, stop, undo, save.");
        }
        catch (Exception e) { Say("Something went wrong: " + e.Message, "That failed."); }
    }

    // ---- the list of commands: an insert in the editing space, scrolled ------------------------------------------------

    private void PlaceCommands()
    {
        commandsPanel.Height = Math.Max(200, Math.Min(640, canvas.Height - 24));
        commandsPanel.Left = Math.Max(8, canvas.Width - commandsPanel.Width - 14); commandsPanel.Top = 12;
    }

    private void ShowCommands()
    {
        PlaceCommands();
        commandsPanel.Display("Audio profile commands", CommandsPanel.Profile);
    }

    // ---- doing what is asked, when it is plain ------------------------------------------------------

    private const string NameIt = "Please name the object.";

    // The audio object the words name - and only that: "it", "this", "that" mean the one chosen in the list.
    private Item? ItemFor(string words)
    {
        var w = words.ToLowerInvariant();
        var named = studio.Catalog.Items.Where(i => i.Kind != "asd" && Regex.IsMatch(w, @"(?<![\w])" + Regex.Escape(i.Name.ToLowerInvariant()) + @"(?![\w])")).OrderByDescending(i => i.Name.Length).FirstOrDefault();
        named ??= studio.Catalog.Items.Where(i => i.Kind != "asd" && i.Name.Length >= 3 && w.Contains(i.Name.ToLowerInvariant())).OrderByDescending(i => i.Name.Length).FirstOrDefault();
        if (named is not null) return named;
        if (Regex.IsMatch(w, @"\b(it|this|that|selected)\b") && library.SelectedItems.Count > 0 && library.SelectedItems[0].Tag is Item picked && picked.Kind != "asd") return picked;
        return null;
    }

    private Member? MemberFor(string words, bool allowLast = false)
    {
        var w = words.ToLowerInvariant();
        var named = studio.Members.Where(m => Regex.IsMatch(w, @"(?<![\w])" + Regex.Escape(Path.GetFileNameWithoutExtension(m.Name).ToLowerInvariant()) + @"(?![\w])")).OrderByDescending(m => m.Name.Length).FirstOrDefault();
        // the recogniser runs words together ("makedrum.au"): the name inside a word still counts, when it is not a short one
        named ??= studio.Members.Where(m => Path.GetFileNameWithoutExtension(m.Name).Length >= 3 && w.Contains(Path.GetFileNameWithoutExtension(m.Name).ToLowerInvariant())).OrderByDescending(m => m.Name.Length).FirstOrDefault();
        if (named is not null) { lastMember = named.Name; return named; }
        var chosen = Regex.IsMatch(w, @"\b(it|this|that|selected)\b") ? canvas.Selected : null;
        // after "display the audio profile commands", a short command goes to the object last named (or chosen)
        if (chosen is null && profileMode && allowLast) chosen = canvas.Selected ?? studio.Members.FirstOrDefault(m => m.Name == lastMember);
        if (chosen is not null) lastMember = chosen.Name;
        return chosen;
    }

    private bool profileMode;                                    // the profile commands have been displayed: short forms work on the object last named
    private string? lastMember;

    // What the recogniser writes for a file name: "drum dot a u o", "drum.au" - and for what is said, "drum" and "drum.auo" alike.
    private static string SpokenNames(string text)
    {
        text = Regex.Replace(text, @"\s+dot\s+a\s*u\s*o\b", ".auo", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s+dot\s+b\s*s\s*o\b", ".bso", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s+dot\s+a\s*s\s*d\b", ".asd", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\.au\b(?!o)", ".auo", RegexOptions.IgnoreCase);
        return text;
    }

    private Intent? pendingIntent;                               // what she could not do for want of a name, to go on when it is said

    // A command about the object the words name (or, after the profile commands were displayed, the one named last); she asks which when there is none.
    private async Task<bool> OnMemberAsync(Intent i, Func<Member, Task<Outcome>> act, bool quiet = false)
    {
        var m = MemberFor(i.Words, allowLast: true);
        if (m is null) { pendingIntent = i; Say(NameIt, NameIt); return true; }
        var outcome = await act(m);
        if (!quiet) Report(outcome);
        return true;
    }

    // True where it was done (or could not be, and said why); false where the words are for the dialogue AIM after all.
    private async Task<bool> DoAsync(Intent i)
    {
        switch (i.Verb)
        {
            case "add":
            {
                var item = ItemFor(i.Words);
                if (item is null) { pendingIntent = i; Say(NameIt + " For example: place the bell at 2 m x, 5 m y.", NameIt); return true; }
                Report(await studio.AddAsync(item, i.HasPlace ? i.X ?? 0 : null, i.HasPlace ? i.Y ?? 0 : null, i.Z ?? 0));
                return true;
            }
            case "speech":
                Say("Making a speech from text needs the vocal interface, which is not part of Audio Scene Management now.");
                return true;
            case "profilehelp":
            {
                profileMode = true;
                ShowCommands();
                Say("The audio profile commands are in the editing space: scroll to find what you need. Then a short form works on the object you named last.", "The audio profile commands are on the screen. Scroll to find what you need.");
                return true;
            }
            case "profile": return await OnMemberAsync(i, m => { Say(studio.ProfileText(m), "Here is its profile."); return Task.FromResult(new Outcome(true, "")); }, quiet: true);
            case "sceneprofile": Say(studio.SceneProfileText(), "Here is the profile of the scene."); return true;
            case "direction": return await OnMemberAsync(i, m => studio.DirectionAsync(m, i.Pattern!));
            case "loudness": return await OnMemberAsync(i, m => studio.LoudnessAsync(m, i.Db, i.Lufs));
            case "frequency": return await OnMemberAsync(i, m => studio.FrequencyRangeAsync(m, i.A, i.B));
            case "spectrogram": return await OnMemberAsync(i, m => studio.SpectrogramAsync(m));
            case "reflectivity": Report(await studio.ReflectivityAsync(i.A, i.B)); return true;
            case "reverb": Report(await studio.ReverbAsync(i.Field ?? "RT60", i.Tail, i.Factor)); return true;
            case "diffusion": Report(await studio.DiffusionAsync(i.A ?? 0)); return true;
            case "absorption": Report(await studio.AbsorptionAsync(i.A ?? 0)); return true;
            case "doppler": Report(await studio.DopplerAsync(i.A, i.B)); return true;
            case "room":
            {
                // the objects the words name, else all that are in the scene
                var named = studio.Members.Where(m => Regex.IsMatch(i.Words.ToLowerInvariant(), @"(?<![\w])" + Regex.Escape(Path.GetFileNameWithoutExtension(m.Name).ToLowerInvariant()) + @"(?![\w])")).ToList();
                Report(await studio.BuildRoomAsync(named.Count > 0 ? named : studio.Members.ToList(), i.Margin ?? 1, i.Height ?? 3));
                return true;
            }
            case "start" or "end" or "time":
            {
                var m = MemberFor(i.Words);
                if (m is null) { pendingIntent = i; Say(NameIt, NameIt); return true; }
                Report(await studio.TimeAsync(m, i.Start, i.StartRelative, i.End));
                return true;
            }
            case "move":
            {
                var m = MemberFor(i.Words);
                if (m is null) { pendingIntent = i; Say(NameIt, NameIt); return true; }
                if (!i.HasPlace) { pendingIntent = i; const string Where = "Move it where? For example: move bell.auo 3 m to the right."; Say(Where, "Move it where?"); return true; }
                if (i.Field == "center")
                {
                    if (studio.Room is not { } room) { Say("There is no room yet. Say: build a room round the objects.", "There is no room yet."); return true; }
                    Report(await studio.MoveAsync(m, (room.X0 + room.X1) / 2, (room.Y0 + room.Y1) / 2, m.Z)); return true;
                }
                var (x, y, z) = i.Relative ? (m.X + (i.X ?? 0), m.Y + (i.Y ?? 0), m.Z + (i.Z ?? 0)) : (i.X ?? m.X, i.Y ?? m.Y, i.Z ?? m.Z);
                Report(await studio.MoveAsync(m, x, y, z));
                return true;
            }
            case "listener":     // "move me to the centre of the room", "move the listener 3 metres up", "turn me 90 degrees"
            {
                var l = studio.Listener; double x = l.X, y = l.Y, z = l.Z, yaw = l.Yaw;
                if (i.Field == "center")
                {
                    if (studio.Room is not { } room) { Say("There is no room yet. Say: build a room round the objects.", "There is no room yet."); return true; }
                    (x, y) = ((room.X0 + room.X1) / 2, (room.Y0 + room.Y1) / 2);
                }
                else if (i.HasPlace) (x, y, z) = i.Relative ? (l.X + (i.X ?? 0), l.Y + (i.Y ?? 0), l.Z + (i.Z ?? 0)) : (i.X ?? l.X, i.Y ?? l.Y, i.Z ?? l.Z);
                else if (i.Yaw is null) { pendingIntent = i; const string Where = "Move you where? For example: move me 3 m to the right, or to the centre of the room."; Say(Where, "Move you where?"); return true; }
                if (i.Yaw is { } turn) yaw = Regex.IsMatch(i.Words, @"\b(by|more)\b", RegexOptions.IgnoreCase) ? l.Yaw + turn : turn;
                Report(await studio.ListenerAsync(x, y, z, yaw));
                return true;
            }
            case "remove":
            {
                var m = MemberFor(i.Words);
                if (m is null) { pendingIntent = i; Say(NameIt, NameIt); return true; }
                Report(await studio.RemoveAsync(m));
                return true;
            }
            case "turn":
            {
                var m = MemberFor(i.Words);
                if (m is null) { pendingIntent = i; Say(NameIt, NameIt); return true; }
                if (i.Yaw is null) return false;
                var relative = Regex.IsMatch(i.Words, @"\b(by|more)\b", RegexOptions.IgnoreCase);
                Report(await studio.MoveAsync(m, m.X, m.Y, m.Z, relative ? m.Yaw + i.Yaw.Value : i.Yaw.Value));
                return true;
            }
        }
        return false;
    }
}
