using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mpai.Cae.AsmUa;

// THE ACOUSTIC PROFILE OF THE ROOM, in a window of its own: it stays above the main window and does not block it, the person drags it by its title bar to where it covers nothing they want to see,
// and it is where they left it the next time. It shows the properties of the Scene's Acoustic Profile, each with its value or "not set", and it shows them again when one is changed.
internal sealed class AcousticPanel : Window
{
    private static Point? _where;                                   // where the person left it
    private readonly StackPanel _rows = new();

    public AcousticPanel(Window owner)
    {
        Owner = owner;
        Title = "Acoustic Profile of the room";
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.ToolWindow;                       // a title bar to drag it by; no minimise or maximise
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 380;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var hint = new TextBlock
        {
            Text = "Say, for example: set the absorption of the walls to 0.4 - the diffusion to 0.6 - the reverberation time to 1.2 seconds - the reflection times to 0.05 and 0.3 - the left wall to carpet - cover the ceiling with foam.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(0, 12, 0, 0), Foreground = Brushes.DimGray, FontSize = 12
        };
        Content = new StackPanel { Margin = new Thickness(16), Children = { _rows, Known(), hint } };
        Closing += (_, e) => { e.Cancel = true; Hide(); };           // closed, it is hidden: it is shown again, in its place, with the next listing
        LocationChanged += (_, _) => { if (IsVisible) _where = new Point(Left, Top); };
    }

    // THE MATERIALS AND THE SURFACES THAT ASM UNDERSTANDS: the ones its grammar is built from (Apps/CAE/ASM/Grammar/asm-grammar.json: Materials, with the absorption at each octave band 125 Hz to 4 kHz, and Faces).
    // A material not in the list is not understood by voice. Shown with the absorption at 1 kHz.
    private static UIElement Known()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        string materials = "", surfaces = "";
        try
        {
            var grammar = JsonNode.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(Mpai.Core.MpaiPaths.Root, "Apps", "CAE", "ASM", "Grammar", "asm-grammar.json"))) as JsonObject;
            var bands = (grammar?["Materials"]?["Bands"] as JsonArray)?.Select(b => Number(b)).ToList();
            var at1k = bands is null ? -1 : bands.FindIndex(b => b == 1000);
            materials = string.Join(", ", (grammar?["Materials"] as JsonObject ?? new JsonObject()).Where(m => m.Key is not ("_" or "Bands") && m.Value is JsonArray)
                .Select(m => at1k >= 0 && Number(((JsonArray)m.Value!)[at1k]) is { } a ? $"{m.Key} ({a.ToString("0.##", CultureInfo.InvariantCulture)})" : m.Key));
            surfaces = string.Join(", ", (grammar?["Faces"] as JsonObject ?? new JsonObject()).Select(f => f.Key).Append("walls (the four sides)"));
        }
        catch (Exception e) when (e is System.IO.IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        void Line(string title, string text) { panel.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) }); panel.Children.Add(new TextBlock { Text = text.Length == 0 ? "not available" : text, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 }); }
        Line("Materials ASM understands (absorption at 1 kHz)", materials);
        Line("Surfaces it can set", surfaces);
        return panel;
    }

    public void Show(JsonObject? profile)
    {
        Fill(profile);
        if (IsVisible) return;
        var at = _where ?? new Point(Owner.Left + Math.Max(0, Owner.ActualWidth - 420), Owner.Top + 90);
        Left = at.X; Top = at.Y;
        Show();
    }

    private static double? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private void Fill(JsonObject? profile)
    {
        _rows.Children.Clear();
        var reflectivity = profile?["Reflectivity"] is JsonArray { Count: > 0 } rf ? rf[0] : null;
        var reverberation = profile?["Reverberation"] is JsonArray { Count: > 0 } rv ? rv[0] : null;
        var doppler = profile?["Doppler"] is JsonArray { Count: > 0 } dp ? dp[0] : null;
        // EVERY PROPERTY OF THE ACOUSTIC PROFILE OF A SCENE (CAE-ACP-V1.0), each with its value or "not set".
        TextRow("Profile", (string?)profile?["AcousticProfileID"]);
        Row("Absorption", Number(profile?["Absorption"]), "");
        Row("Diffusion", Number(profile?["Diffusion"]), "");
        TextRow("Reverberation time (RT60)", Pairs(reverberation?["RT60"]));
        TextRow("Reverberation time (RT30)", Pairs(reverberation?["RT30"]));
        TextRow("Reverberation time (RT20)", Pairs(reverberation?["RT20"]));
        Row("Early decay time (EDT)", Number(reverberation?["EDT"]), " s");
        Row("Early reflection time", Number(reflectivity?["EarlyReflectionTime"]), " s");
        Row("Late reflection time", Number(reflectivity?["LateReflectionTime"]), " s");
        Row("Doppler, direct sound", Number(doppler?["DirectSoundFactor"]), "");
        Row("Doppler, reflected sound", Number(doppler?["IndirectSound"]), "");
        TextRow("Default material", MaterialName((profile?["DefaultMaterial"] as JsonArray)?.FirstOrDefault()));
        if (profile?["Walls"] is JsonArray { Count: > 0 } walls)
            foreach (var wall in walls.OfType<JsonObject>())
                TextRow("Wall: " + WallName(wall), MaterialName((wall["MaterialIDOrMaterial"] as JsonArray)?.FirstOrDefault()));
        else TextRow("Walls", null);
        if (profile is null)
            _rows.Children.Add(new TextBlock { Text = "The room has no acoustic profile yet: it is heard dry.", Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
    }

    // A Plot of pairs (frequency in Hz, value): "1.2 s" for one pair, which applies at all frequencies, else "1.2 s at 125 Hz; 0.9 s at 1000 Hz".
    private static string? Pairs(JsonNode? reverberationTime)
    {
        if (reverberationTime?["Plot"]?["2D"] is not JsonArray pairs) return null;
        var shown = pairs.OfType<JsonArray>().Where(q => q.Count >= 2 && Number(q[0]) is not null && Number(q[1]) is not null)
                         .Select(q => (Hz: Number(q[0])!.Value, Seconds: Number(q[1])!.Value)).ToList();
        if (shown.Count == 0) return null;
        return shown.Count == 1 ? shown[0].Seconds.ToString("0.###", CultureInfo.InvariantCulture) + " s"
             : string.Join("; ", shown.Select(x => x.Seconds.ToString("0.###", CultureInfo.InvariantCulture) + " s at " + x.Hz.ToString("0.###", CultureInfo.InvariantCulture) + " Hz"));
    }

    // A wall by the name it was given: a face (of a Right Parallelepiped, or a Region of it), a Polygon, or a surface.
    private static string WallName(JsonObject wall) =>
        (wall["Face"] is { } face ? (string?)face : null) is { } f ? f + f switch { "Left" => " (west)", "Right" => " (east)", "Front" => " (north)", "Back" => " (south)", _ => "" } + (wall["Region"] is not null ? " (part)" : "")
        : wall["Polygon"] is not null ? "polygon" : (string?)wall["SurfaceID"] ?? "wall";

    // A Material by its identifier (a string, or a Material that has one).
    private static string? MaterialName(JsonNode? material) =>
        material is JsonValue v && v.TryGetValue<string>(out var id) ? id : (string?)material?["MaterialID"];

    private void TextRow(string label, string? value)
    {
        var line = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(new TextBlock { Text = label, FontSize = 14 });
        var shown = new TextBlock { Text = string.IsNullOrEmpty(value) ? "not set" : value, FontSize = 14, FontWeight = string.IsNullOrEmpty(value) ? FontWeights.Normal : FontWeights.SemiBold, Foreground = string.IsNullOrEmpty(value) ? Brushes.Gray : Brushes.Black, Margin = new Thickness(16, 0, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 220 };
        Grid.SetColumn(shown, 1);
        line.Children.Add(shown);
        _rows.Children.Add(line);
    }

    private void Row(string label, double? value, string unit)
    {
        var line = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(new TextBlock { Text = label, FontSize = 14 });
        var shown = new TextBlock { Text = value is { } v ? v.ToString("0.###", CultureInfo.InvariantCulture) + unit : "not set", FontSize = 14, FontWeight = value is null ? FontWeights.Normal : FontWeights.SemiBold, Foreground = value is null ? Brushes.Gray : Brushes.Black, Margin = new Thickness(24, 0, 0, 0) };
        Grid.SetColumn(shown, 1);
        line.Children.Add(shown);
        _rows.Children.Add(line);
    }
}
