using AIF.Channels;

namespace AIF.Controller;

// AN AIM THAT RUNS FROM START TO STOP (M3205 5.2, M3215 3.3). The contract of an
// AIM written for the continuous executor, beside IAimProcessor: it is given its
// Ports and runs until stopped, reading its Input Ports when Messages are pending
// and writing its Output Ports when it has something to write. An AIM that is an
// IAimProcessor runs on the continuous executor too, through the adapter.
public interface IAimRunner
{
    string InstanceId { get; }

    // Returns at Stop. The context carries Pause, Stop, Report and StopAim, as
    // for an IAimProcessor.
    Task RunAsync(IAimPorts ports, AimContext context);
}

// An AIM that can say how far its inputs have been from those its model was trained on (MPAI_AIFM_Model_Drift): 0 as in training, 1 never like it.
public interface IModelDrift
{
    double? Drift { get; }
}

// An AIM's own Ports, addressed by Data Type and Port Number, the Port Number
// being 1 where the Metadata omits it; a Port name never reaches the code. The
// implementation's form of M3203 4.6, 4.12 and 4.13. Behind it are the Channel
// ends the Controller opened for the AIM, whatever their transport.
public interface IAimPorts
{
    // The oldest Message pending on an Input Port, within timeoutMs (0: do not
    // wait; negative: without limit); null when none came.
    ValueTask<PortMessage?> ReadAsync(string dataType, int portNumber = 1, int timeoutMs = -1);

    // Writes a datum on an Output Port; false when it could not be written within
    // timeoutMs. The Controller stamps it.
    ValueTask<bool> WriteAsync(string dataType, int portNumber, string json, int timeoutMs = -1);

    // The first of these Input Ports with a Message pending, within timeoutMs.
    ValueTask<(string DataType, int PortNumber)?> SelectAsync(int timeoutMs, params (string DataType, int PortNumber)[] inputs);

    int  Pending(string dataType, int portNumber = 1);
    long Dropped(string dataType, int portNumber = 1);

    // The Controller's time, on the time base of the clock plugged in (M3215 3.5).
    DateTimeOffset Now { get; }

    // Completes at the AIM's next Period; at once where it has none.
    Task NextPeriodAsync();

    // MACHINE LEARNING (Basic API 4.12): MPAI_AIFM_Model_Update - the URI names the updated model, one of the AccessSources of the AIM: the Controller obtains it, checked against the SHA-256 its Metadata
    // states, and the AIM reads it from Access (Access_Version, Access_Path); the model it was reading is not touched until then. MPAI_AIFM_Model_Drift - how far the inputs of an AIM of the Module
    // have been from those its model was trained on, 0 (as in training) to 1; null where the AIM keeps no such measure.
    ValueTask<AifError> ModelUpdateAsync(string modelName) => ValueTask.FromResult(AifError.NotFound);
    double? ModelDrift(string aimName) => null;

    // THE EXTERNAL PORTS OF THE AIM (Basic API 5): the Controllers with which a link is open and the Ports of their Modules, addressed by controllerID, AIM_name, Data Type and Port Number. An
    // AIM that declares no External Port reaches none. Where the Controller has no link, the answer is the one of an empty list and NOT_FOUND.
    (int NumInRange, IReadOnlyList<(int ControllerID, string Metadata)> Controllers) ExternalList() => (0, []);
    ValueTask<AifError> ExternalProbeAsync(int controllerId, string aimName, string dataType, int portNumber, int timeoutMs) => ValueTask.FromResult(AifError.NotFound);
    ValueTask<(AifError Error, PortMessage? Message)> ExternalOutputReadAsync(int controllerId, string aimName, string dataType, int portNumber, int timeoutMs) => ValueTask.FromResult<(AifError, PortMessage?)>((AifError.NotFound, null));
    ValueTask<AifError> ExternalInputWriteAsync(int controllerId, string aimName, string dataType, int portNumber, string json, int timeoutMs) => ValueTask.FromResult(AifError.NotFound);

    // PAYLOADS BY REFERENCE (M3215 3.6): placed for the Channel of an Output Port,
    // the reference returned going in the Object's DataURI; fetched and released
    // by a reader at the far end of that Channel.
    string PutPayload(string dataType, int portNumber, ReadOnlyMemory<byte> data);
    ReadOnlyMemory<byte> GetPayload(string reference);
    void ReleasePayload(string reference);
}
