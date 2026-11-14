using System.Collections.Generic;

using AIF.Controller;

namespace Mpai.Aif.Api;

// The Controller API (formerly called the North API), as the User Agent depends on it.
//
// Two implementations: ControllerApi, in process, and RemoteControllerApi, across a
// network. The UA holds this type and does not know which it has - which is the
// whole point of a typed, stateless seam.
public interface IControllerApi
{
    AifError StartFlow(string moduleName, out int moduleId);
    ControllerApi.Result Advance(int moduleId, IEnumerable<ControllerApi.Datum> inputs);
    void StopFlow(int moduleId);

    // The data path of M3203 3.3 (M3213 3.2): a write and a read per boundary
    // Port, with named outcomes and a timeout in milliseconds (0: do not wait;
    // negative: wait without limit).
    AifError InputWrite(int moduleId, string dataType, int portNumber, string json, int timeoutMs = -1);
    ControllerApi.Read OutputRead(int moduleId, string dataType, int portNumber, int timeoutMs = -1);

    // MPAI_AIFU_MODULE_Pause and _Resume (M3213 3.4): the whole Module, at every
    // depth, until Resume.
    AifError Pause(int moduleId);
    AifError Resume(int moduleId);

    // The status of each AIM of the Module and the reports of its last run, and
    // MPAI_AIFU_AIM_Stop (M3213 3.5).
    ControllerApi.ModuleStatus Status(int moduleId);
    AifError StopAim(int moduleId, string aimName);

    // MPAI_AIFU_SharedStorage_Init (M3203 3.4.1, M3213 3.6): where the Module's
    // Shared Storage is held.
    AifError SharedStorageInit(int moduleId, string location);

    // THE USER AGENT'S OWN CALLS ON THE MODULE'S SHARED STORAGE (additive, 2026/10/27: store and delete are functions of any storage, for the User Agent as for the AIMs): Store, Read,
    // Delete and List, as the holder UserAgent, under the rules and the Grants of the storage - a call the rules do not allow is NOT_AUTHORISED. A Controller that does not offer them
    // (over MPAI-MAS) answers FAILED.
    AifError SharedStorageStore(int moduleId, string key, byte[] data, string category = "Data") => AifError.Failed;
    AifError SharedStorageRead(int moduleId, string key, out byte[] data) { data = []; return AifError.Failed; }
    AifError SharedStorageDelete(int moduleId, string key) => AifError.Failed;
    IReadOnlyList<string> SharedStorageList(int moduleId, string prefix = "") => [];

    // BETWEEN CONTROLLERS (Basic API 3.9; Communication 9): MPAI_AIFU_External_Join, _Leave and
    // _Authority. The User Agent passes its User's decision to trust a Trust Authority to the
    // Controller, or makes the Controller the Authority of a service; the Controller acts on no
    // other occasion. A Controller that does not offer them (over MPAI-MAS) answers FAILED.
    AifError ExternalAuthority(int moduleId, string policy, string host = "127.0.0.1", int port = 0) => AifError.Failed;
    AifError ExternalJoin(int moduleId, string authority, string? rendezvous, string? role = null, string? controllerId = null) => AifError.Failed;
    AifError ExternalLeave(int moduleId) => AifError.Failed;
    string? ExternalAnchor(int moduleId) => null;
    string? ExternalRendezvous(int moduleId) => null;
}