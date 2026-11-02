using System;
using System.IO;
namespace MmcAmc;
internal static class Program
{
    // THE CONTROLLER this User Agent talks to (MmcController, a program of its own, started by whoever launches the App): `--controller <url>`.
    public static readonly string ControllerUrl = ControllerArg();
    private static string ControllerArg() { var a = Environment.GetCommandLineArgs(); var i = Array.IndexOf(a, "--controller"); return i >= 0 && i + 1 < a.Length ? a[i + 1] : "http://127.0.0.1:5058"; }
    public static readonly string CrashLog = Path.Combine(AppContext.BaseDirectory, "amc-crash.log");
    public static void Record(string context, Exception error)
    {
        try { File.AppendAllText(CrashLog, $"{DateTime.Now:o}  [{context}]  {error}{Environment.NewLine}"); }
        catch { }
    }
}
