using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Aif.Api;

// THE CONTROLLER API, WITHOUT WAITING. The same calls as IControllerApi,
// awaited. A browser runs WebAssembly on one thread and never lets it block on the
// network, and the workflow interpreter, which serves the browser and the desktop
// alike, holds this type.
public interface IAsyncControllerApi
{
    Task<(AifError Error, int ModuleId)> StartFlowAsync(string moduleName);
    Task<ControllerApi.Result> AdvanceAsync(int moduleId, IEnumerable<ControllerApi.Datum> inputs);

    // THE SAME EXCHANGE, SAYING WHAT IS WANTED: the Output Ports the caller will
    // read. An implementation that must ask for each output separately - over
    // MPAI-MAS - asks for these only; one that gets them all at once ignores it.
    Task<ControllerApi.Result> AdvanceAsync(int moduleId, IEnumerable<ControllerApi.Datum> inputs,
                                            IReadOnlyCollection<(string DataType, int PortNumber)> wanted) =>
        AdvanceAsync(moduleId, inputs);

    Task                       StopFlowAsync(int moduleId);
    Task<AifError>             InputWriteAsync(int moduleId, string dataType, int portNumber, string json, int timeoutMs = -1);
    Task<ControllerApi.Read>   OutputReadAsync(int moduleId, string dataType, int portNumber, int timeoutMs = -1);
    Task<AifError>             PauseAsync(int moduleId);
    Task<AifError>             ResumeAsync(int moduleId);
    Task<ControllerApi.ModuleStatus> StatusAsync(int moduleId);
    Task<AifError>             StopAimAsync(int moduleId, string aimName);
    Task<AifError>             SharedStorageInitAsync(int moduleId, string location);

    // MPAI_AIFU_External_Join (Basic API 3.9): the Trust Authority the User decided to trust, by its anchor, and where it is reached. Not over MPAI-MAS.
    Task<AifError> ExternalJoinAsync(int moduleId, string authority, string rendezvous, string? role, string? controllerId) =>
        Task.FromResult(AifError.Failed);

    // MPAI_AIFU_External_Authority and _Leave (Basic API 3.9): the Controller made the Trust Authority for the External links of the Module, under the policy; the links closed. Not over MPAI-MAS.
    Task<AifError> ExternalAuthorityAsync(int moduleId, string policy) => Task.FromResult(AifError.Failed);
    Task<AifError> ExternalLeaveAsync(int moduleId) => Task.FromResult(AifError.Failed);

    // A payload placed for a boundary Input Port, its reference returned (M3215
    // 3.6). Not over MPAI-MAS (Phase 15): there a payload goes inline.
    Task<(AifError Error, string? Reference)> PayloadPutAsync(int moduleId, string dataType, int portNumber, ReadOnlyMemory<byte> data) =>
        Task.FromResult((AifError.Failed, (string?)null));
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

        public Task<(AifError Error, int ModuleId)> StartFlowAsync(string moduleName) =>
            Task.Run(() => (api.StartFlow(moduleName, out var id), id));

        public Task<(AifError Error, string? Reference)> PayloadPutAsync(int moduleId, string dataType, int portNumber, ReadOnlyMemory<byte> data) =>
            api is ControllerApi local
                ? Task.Run(() => local.PayloadPut(moduleId, dataType, portNumber, data))
                : Task.FromResult((AifError.Failed, (string?)null));

        public Task<ControllerApi.Result> AdvanceAsync(int moduleId, IEnumerable<ControllerApi.Datum> inputs) =>
            Task.Run(() => api.Advance(moduleId, inputs));

        public Task StopFlowAsync(int moduleId) =>
            Task.Run(() => api.StopFlow(moduleId));

        public Task<AifError> InputWriteAsync(int moduleId, string dataType, int portNumber, string json, int timeoutMs = -1) =>
            Task.Run(() => api.InputWrite(moduleId, dataType, portNumber, json, timeoutMs));

        public Task<ControllerApi.Read> OutputReadAsync(int moduleId, string dataType, int portNumber, int timeoutMs = -1) =>
            Task.Run(() => api.OutputRead(moduleId, dataType, portNumber, timeoutMs));

        public Task<AifError> PauseAsync(int moduleId) => Task.Run(() => api.Pause(moduleId));

        public Task<AifError> ResumeAsync(int moduleId) => Task.Run(() => api.Resume(moduleId));

        public Task<ControllerApi.ModuleStatus> StatusAsync(int moduleId) => Task.Run(() => api.Status(moduleId));

        public Task<AifError> StopAimAsync(int moduleId, string aimName) => Task.Run(() => api.StopAim(moduleId, aimName));

        public Task<AifError> SharedStorageInitAsync(int moduleId, string location) => Task.Run(() => api.SharedStorageInit(moduleId, location));

        public Task<AifError> ExternalJoinAsync(int moduleId, string authority, string rendezvous, string? role, string? controllerId) =>
            Task.Run(() => api.ExternalJoin(moduleId, authority, rendezvous, role, controllerId));

        public Task<AifError> ExternalAuthorityAsync(int moduleId, string policy) => Task.Run(() => api.ExternalAuthority(moduleId, policy));

        public Task<AifError> ExternalLeaveAsync(int moduleId) => Task.Run(() => api.ExternalLeave(moduleId));
    }
}
