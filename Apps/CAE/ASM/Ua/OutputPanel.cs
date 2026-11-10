using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using Mpai.SpatialAudio;

namespace Mpai.Cae.AsmUa;

// HOW THE SCENE IS HEARD: on what - headphones, loudspeakers - and through which sound output of the machine. In a window of its own, as the acoustic panel is: it stays above the main window, does not
// block it, and what is chosen is used the next time the scene is heard. The first is the Module's: it is the Rendering Mode of the Acoustic Profile of the scene, so a choice is given to the Module as a command.
// The second is the User Agent's (the Loudspeaker Unit, the AOD): it is kept here and the Module is not told.
internal sealed class OutputPanel : Window
{
    // What the scene can be rendered for, with the words a person uses for it.
    public static readonly (SpatialRenderer.Layout Layout, string Label, string[] Words)[] Layouts =
    [
        (SpatialRenderer.Layout.Binaural,   "Headphones or headset (binaural)", ["headphones", "headphone", "headset", "earphones", "earbuds", "binaural"]),
        (SpatialRenderer.Layout.Stereo,     "Stereo loudspeakers",              ["stereo speakers", "stereo", "speakers", "loudspeakers", "speaker"]),
        (SpatialRenderer.Layout.Mono,       "Mono",                             ["mono"]),
        (SpatialRenderer.Layout.Quad,       "Quad loudspeakers",                ["quad", "quadraphonic"]),
        (SpatialRenderer.Layout.Surround51, "5.1 surround loudspeakers",        ["5.1", "surround"]),
        (SpatialRenderer.Layout.Surround71, "7.1 surround loudspeakers",        ["7.1"])
    ];

    public const string DefaultDevice = "Windows default";

    // The sound outputs of the machine, by name: the Loudspeaker Unit's (the AOD of the User Agent).
    public static List<string> Devices() => Mpai.PhysicalLayer.LoudspeakerUnit.OutputDevices();

    private readonly ComboBox _layout = new() { MinWidth = 300, Margin = new Thickness(0, 4, 0, 12) };
    private readonly ComboBox _device = new() { MinWidth = 300, Margin = new Thickness(0, 4, 0, 12) };
    private bool _filling;

    public OutputPanel(Window owner, SpatialRenderer.Layout layout, string? device, Action<SpatialRenderer.Layout> layoutChosen, Action<string?> deviceChosen)
    {
        Owner = owner;
        Title = "Sound output";
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Fill(layout, device);
        _layout.SelectionChanged += (_, _) => { if (!_filling && _layout.SelectedIndex >= 0) layoutChosen(Layouts[_layout.SelectedIndex].Layout); };
        _device.SelectionChanged += (_, _) => { if (!_filling && _device.SelectedIndex >= 0) deviceChosen(_device.SelectedIndex == 0 ? null : (string)_device.SelectedItem); };
        var hint = new TextBlock
        {
            Text = "Or type: play on headphones - play on stereo speakers - play on <the name of an output> - which sound outputs are there.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 340, Foreground = Brushes.DimGray, FontSize = 12
        };
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = "The scene is heard on", FontWeight = FontWeights.SemiBold }, _layout,
                new TextBlock { Text = "through the sound output", FontWeight = FontWeights.SemiBold }, _device,
                hint
            }
        };
        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }

    // The choices, with the ones in use selected; the outputs are read again each time, for a headset plugged in since.
    public void Fill(SpatialRenderer.Layout layout, string? device)
    {
        _filling = true;
        _layout.ItemsSource = Layouts.Select(l => l.Label).ToList();
        _layout.SelectedIndex = Array.FindIndex(Layouts, l => l.Layout == layout);
        var devices = new List<string> { DefaultDevice }; devices.AddRange(Devices());
        _device.ItemsSource = devices;
        _device.SelectedIndex = device is not null && devices.IndexOf(device) is var at and > 0 ? at : 0;
        _filling = false;
    }
}
