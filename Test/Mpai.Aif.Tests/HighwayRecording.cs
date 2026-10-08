using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace Mpai.Aif.Tests;

// THE RECORDING OF A HIGHWAY RUN (M3253), to be shown without the machine that made it: a folder with the page
// (index.html), the state of every step (data.js), and the pictures of the two cameras as JPEG files (f/ for the front
// camera, r/ for the window of the rear one). The page opened from the folder, by double-clicking it, plays it at the
// speed of real life, or as asked; nothing runs and no server is needed.
public sealed class HighwayRecording
{
    private readonly string folder;
    private readonly List<string> states = [];
    private readonly JpegEncoder jpeg = new() { Quality = 82 };

    public HighwayRecording(string folder)
    {
        this.folder = folder;
        Directory.CreateDirectory(Path.Combine(folder, "f"));
        Directory.CreateDirectory(Path.Combine(folder, "r"));
    }

    public void State(string json) => states.Add(json);

    // A picture given as PNG (the front camera's: it is what the ESS was given).
    public void Front(int step, byte[] png)
    {
        using var image = Image.Load<Rgb24>(png);
        image.SaveAsJpeg(Path.Combine(folder, "f", $"{step:D6}.jpg"), jpeg);
    }

    // A picture given as raw RGB (the rear camera's window, 640 x 360).
    public void Rear(int step, byte[] rgb)
    {
        using var image = Image.LoadPixelData<Rgb24>(rgb, 640, 360);
        image.SaveAsJpeg(Path.Combine(folder, "r", $"{step:D6}.jpg"), jpeg);
    }

    public void Finish(string label)
    {
        File.WriteAllText(Path.Combine(folder, "data.js"),
            "window.REPLAY = { label: " + System.Text.Json.JsonSerializer.Serialize(label) + ", states: [\n" + string.Join(",\n", states) + "\n] };\n");
        File.Copy(Path.Combine(Repository.Root, "CAV", "Viewer", "highway.html"), Path.Combine(folder, "index.html"), overwrite: true);
    }
}
