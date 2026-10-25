using AIF.Controller;

namespace Mpai.Aif.Tests;

// THE MODELS OF THE TESTS: there is no Models folder. What an AIM reads is declared in the AccessSources of its L3, and a test that builds an
// Implementation itself does what the Controller does for that AIM: obtains what the L3 declares, through Access, as the AIM (ModelFiles),
// and gives the Implementation its settings. A test that needs a model is skipped when it cannot be had - offline, with nothing held.
internal static class TestModels
{
    // Declared by an L3 under that Name or that file name.
    public static bool Has(string name) => ModelFiles.Declares(Repository.Root, name);

    // The file of that model in Access, obtained as the AIM that declares it.
    public static string Of(string name, string? l3 = null) => ModelFiles.Of(Repository.Root, name, l3);

    // Whether the files can be had here and now: declared, and held or obtainable.
    public static bool Can(params string[] names) => names.All(n => ModelFiles.TryOf(Repository.Root, n, out _));

    // The settings of an AIM as its Controller gives them to its Implementation: aim-settings.json with the files of Access.
    public static Dictionary<string, string> Settings(string l3)
    {
        var all = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "AIMs", "aim-settings.json")))!;
        var plain = (all[l3] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject()).ToDictionary(p => p.Key, p => (string)p.Value!);
        return new Dictionary<string, string>(ModelFiles.SettingsOf(Repository.Root, l3, plain));
    }

    // The settings of the speech synthesis for one voice of a person - a Piper voice and its configuration, as the L3 of TTS declares them.
    public static Dictionary<string, string> Voice(string voice)
    {
        var settings = Settings("1MMC-TTS-V2.5-I01");
        var language = voice switch { "en_GB-alan-medium" => "en-GB", "en_US-lessiac-medium" => "en-US", "en_US-amy-medium" => "", _ => throw new ArgumentException($"The L3 of TTS declares no voice {voice}.") };
        if (language.Length > 0)
        {
            settings["VoiceModel"] = settings["Voice:" + language];
            settings["VoiceConfig"] = settings["VoiceConfig:" + language];
        }
        foreach (var k in settings.Keys.Where(k => k.StartsWith("Voice:") || k.StartsWith("VoiceConfig:")).ToList()) settings.Remove(k);
        return settings;
    }
}
