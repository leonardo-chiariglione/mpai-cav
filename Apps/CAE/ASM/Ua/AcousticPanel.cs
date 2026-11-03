using System;
using System.Globalization;
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
            Text = "Say, for example: set the absorption of the walls to 0.4 - the diffusion to 0.6 - the reverberation time to 1.2 seconds - the reflection times to 0.05 and 0.3.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(0, 12, 0, 0), Foreground = Brushes.DimGray, FontSize = 12
        };
        Content = new StackPanel { Margin = new Thickness(16), Children = { _rows, hint } };
        Closing += (_, e) => { e.Cancel = true; Hide(); };           // closed, it is hidden: it is shown again, in its place, with the next listing
        LocationChanged += (_, _) => { if (IsVisible) _where = new Point(Left, Top); };
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
        Row("Absorption", Number(profile?["Absorption"]), "");
        Row("Diffusion", Number(profile?["Diffusion"]), "");
        Row("Reverberation time (RT60)", Number(reverberation?["RT60"]?["Plot"]?["2D"]?[0]?[1]), " s");
        Row("Early reflection time", Number(reflectivity?["EarlyReflectionTime"]), " s");
        Row("Late reflection time", Number(reflectivity?["LateReflectionTime"]), " s");
        if (profile is null)
            _rows.Children.Add(new TextBlock { Text = "The room has no acoustic profile yet: it is heard dry.", Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
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
