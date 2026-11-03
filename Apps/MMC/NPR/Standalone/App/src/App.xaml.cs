using System;
using System.IO;
using System.Windows;

namespace MmcNpr;

// What the AIMs say goes to the crash log, beside the executable: a fault that gives no sign is worse than one that does.
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Mpai.Core.AimLog.Sink = (aim, message) =>
        {
            try { File.AppendAllText(Program.CrashLog, $"{DateTime.Now:o}  [{aim}]  {message}{Environment.NewLine}"); }
            catch { }
        };
    }
}
