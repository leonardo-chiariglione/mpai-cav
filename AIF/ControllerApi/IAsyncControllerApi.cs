using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Aif.Api;

// THE CONTROLLER API, WITHOUT WAITING. The same three calls as IControllerApi,
// awaited. A browser runs WebAssembly on one thread and never lets it block on the
// network, and the workflow interpreter, which serves the browser and the desktop
// alike, holds this type.
public interface IAsyncControllerApi
{
    Task<AifError>             StartFlowAsync(string moduleName);
    Task<ControllerApi.Result> AdvanceAsync(string moduleName, IEnumerable<ControllerApi.Datum> inputs);
    Task                       StopFlowAsync(string moduleName);
}

public static class ControllerApiAsync
{
    // A synchronous Controller API, awaited: each call is made on a thread of the
    // pool, so whoever awaits it - a window, typically - is never blocked.
    public static IAsyncControllerApi Async(this IControllerApi api) => new OnThePool(api);

    private sealed class OnThePool : IAsyncControllerApi
    {
        private readonly IControllerApi api;
        public OnThePool(IControllerApi api) => this.api = api;

        public Task<AifError> StartFlowAsync(string moduleName) =>
            Task.Run(() => api.StartFlow(moduleName));

        public Task<ControllerApi.Result> AdvanceAsync(string moduleName, IEnumerable<ControllerApi.Datum> inputs) =>
            Task.Run(() => api.Advance(moduleName, inputs));

        public Task StopFlowAsync(string moduleName) =>
            Task.Run(() => api.StopFlow(moduleName));
    }
}
