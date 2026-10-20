using System;
using System.IO;
using System.Threading;

using Mpai.Aif.Api;
using Mpai.Wdl;

namespace Mpai.Ua;

// A WORKFLOW RUN TO ITS END, for a Module that executes by exchange: the User Agent acquires what the workflow names from the sources the App registered (by Data Type, or by the label the
// workflow gives the datum), offers it to the Module, asks for what it wants back, and presents it to the presenters the App registered; then the workflow ends. It uses only the
// Controller API.
public sealed class WorkflowRun(IControllerApi api, string workflowFile, DeviceRegistry devices, Action<string>? say = null)
{
    public void Run(CancellationToken cancel = default)
    {
        var workflow = new WorkflowReader().Read(File.ReadAllText(workflowFile));
        var interpreter = new WorkflowInterpreter(api.Async(), devices, line => say?.Invoke(line));
        interpreter.RunAsync(workflow, cancel).GetAwaiter().GetResult();
    }
}