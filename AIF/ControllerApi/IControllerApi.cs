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
}