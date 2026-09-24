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
    AifError StartFlow(string moduleName);
    ControllerApi.Result Advance(string moduleName, IEnumerable<ControllerApi.Datum> inputs);
    void StopFlow(string moduleName);

    // The data path of M3203 3.3 (M3213 3.2): a write and a read per boundary
    // Port, with named outcomes and a timeout in milliseconds (0: do not wait;
    // negative: wait without limit).
    AifError InputWrite(string moduleName, string dataType, int portNumber, string json, int timeoutMs = -1);
    ControllerApi.Read OutputRead(string moduleName, string dataType, int portNumber, int timeoutMs = -1);
}