// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
using System.Text;

namespace AIF.Controller;

// WHAT THE CONTROLLER GIVES UNDER A SETTING is a file of Access (AccessSources: Name, Uri, Sha256, Setting): the Controller has obtained it and checked its SHA-256, and gives the
// AIM the place where it is kept. An AIM that needs the CONTENT of a small datum (a polynomial, a table) takes it from here; it does not choose, open or write any other file, and it
// never acquires real-world data (AIF V3.0, AIM-5). A large model is not read here: it is handed to the library that runs it.
public static class SettingFile
{
    // The text of the file the Controller gave under the setting; null where the AIM Instance was given none.
    public static string? ReadText(IReadOnlyDictionary<string, string> settings, string setting) =>
        settings.TryGetValue(setting, out var path) && path.Length > 0 ? Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(path)) : null;

    // The text of a file whose place the Controller gave under a setting (an AIM that was handed the place, not the content).
    public static string ReadGiven(string path) => Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(path));
}
