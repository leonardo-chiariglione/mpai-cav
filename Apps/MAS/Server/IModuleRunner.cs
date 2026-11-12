using System;
using System.Collections.Generic;

namespace Mpai.Mas.Server;

// A boundary Port of a Module, as the MAS server needs to see it.
//
// NO NAME. A Port is (DataType, PortNumber) and nothing else here - not even for
// error messages, because a name available is a name that eventually gets routed
// on. That is how the previous MAS server came to switch on "InputVisual" and
// carry cases for Modules it was not running.
public sealed class BoundaryPort
{
    public string Direction { get; init; } = string.Empty;   // "Input" | "Output"

    // The Data Type this Port is KEYED by - the first of its declared set. The
    // Controller builds its boundary key from this one, whatever the RCA sends.
    public string DataType { get; init; } = string.Empty;

    // Every Data Type this Port accepts. A Port taking either a Basic or a full
    // Text Object has two; most have one.
    public IReadOnlyList<string> DataTypes { get; init; } = Array.Empty<string>();

    public int PortNumber { get; init; } = 1;

    public bool IsOptional { get; init; }

    // Whether the Port may be given as a Bout (MPAI-MAS V1.1, AIM Description; MPAI-AIF, Communication 9.1).
    public bool Bout { get; init; }

    public bool Accepts(string dataType)
    {
        if (string.Equals(DataType, dataType, StringComparison.Ordinal)) return true;

        foreach (var candidate in DataTypes)
            if (string.Equals(candidate, dataType, StringComparison.Ordinal)) return true;

        return false;
    }

    // The boundary key the executor routes on.
    public string Key => DataType + "#" + PortNumber;

    // The two path segments for this Port, naming the Data Type actually being sent.
    public string SegmentFor(string dataType) => PortSegment.Path(dataType, PortNumber);
}

// What the MAS server needs from whatever runs Modules.
//
// DELIBERATELY NOT THE CONTROLLER API ITSELF. ControllerApi lives in Mpai.Aif.Api, and a
// server that referenced it would drag an application-flavoured assembly into
// the one layer that must know no application. The host adapts its ControllerApi to
// this interface in a handful of lines, and this library depends on Data Types
// and nothing else.
public interface IModuleRunner
{
    // The Module''s boundary Ports, from its AMD. The server needs these to turn
    // a URL segment into a boundary key: the RCA names the Data Type it is
    // sending, which may be any of the Port''s accepted types, while the
    // Controller keys the boundary by the Port''s own DataType.
    IReadOnlyList<BoundaryPort> PortsOf(string moduleName);

    // Start a Module by name. Returns null on success, or a reason to report.
    string? Start(string moduleName);

    // Supply boundary inputs and run. Keys are "DataType#PortNumber".
    RunResult Run(string moduleName, IReadOnlyDictionary<string, string> inputs);

    // The same, for a client's session: what the run keeps for the session is that
    // client's, and deleted when it ends (M3245 3.3).
    RunResult Run(string moduleName, IReadOnlyDictionary<string, string> inputs, string session) => Run(moduleName, inputs);

    // A client's session ended.
    void SessionEnded(string session) { }

    void Stop(string moduleName);
}

// A MODULE THAT RUNS CONTINUOUSLY (Execution Continuous in its L3) is not run an exchange at a time: what is written to its Port is written at once
// (MPAI_AIFU_Module_InputWrite), and what it gives at a Port is read from it (MPAI_AIFU_Module_OutputRead), waiting for it up to the time the reader allows.
// A runner that can say which of its Modules are continuous implements this too; the server then relays the Input and Output routes of those Modules as the
// Controller API has them, and runs the others per exchange, as before. No route is added.
public interface IContinuousModuleRunner
{
    bool IsContinuous(string moduleName);

    // Returns null when written, or the reason it was not.
    string? Write(string moduleName, string key, string json);

    // The Json read, or the outcome: NotProduced (nothing within the time, or the Port gave nothing) or an error.
    (string? Json, string Outcome) Read(string moduleName, string key, int timeoutMs);
}

public sealed class RunResult
{
    // Present when the run completed. Keyed "DataType#PortNumber".
    public IReadOnlyDictionary<string, string> Outputs { get; init; } =
        new Dictionary<string, string>();

    // Present when the run failed, for the status the RCA is given.
    public string? Error { get; init; }
}

// Turns the two path segments that name a boundary Port into the Port they address (MPAI-MAS V1.1, API 1).
//
// {DataType} is the Data Type in canonical form, its Header, <Standard>-<Acronym>-V<Major>.<Minor>, or a type of the type system of AIF V3.0 (Metadata, 1.1),
// which a Port may carry as well: a base type (string, int32, float64, boolean, ...) or a struct or variant in braces, each optionally an array ([]) - the
// pattern of AIFType in AIF V3.0 AIMMetadata.json. The MachineID input of the MMC Modules is a string (2026-11-12: refused here, the Apps of the MAS App stopped).
// {PortNumber} is a decimal integer from 1 with no leading zero, always present. "#", which begins a fragment, is never in a URL.
//
//     Input/OSD-BTO-V1.5/1      -> Port 1
//     Input/OSD-BTO-V1.5/2      -> Port 2
//
// MMC-AMQ-V2.5 is why the Port Number matters on the first Module an RCA touches: it declares two Speech inputs, Port 1 for the question and 2 for
// the reply, and two Text inputs the same way.
public static class PortSegment
{
    private static readonly System.Text.RegularExpressions.Regex CanonicalDataType =
        new(@"^[A-Z0-9]{3,4}-[A-Z0-9]{3}-V[0-9]{1,2}\.[0-9]{1,2}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex AifType =
        new(@"^((int8|int16|int32|int64|uint8|byte|uint16|uint32|uint64|float32|float64|number|boolean|string)|\{.+\})(\[\])*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex CanonicalPortNumber =
        new(@"^[1-9][0-9]{0,8}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // The two segments of a Port, as a client writes them.
    public static string Path(string dataType, int portNumber) => dataType + "/" + portNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParse(
        string typeSegment,
        string numberSegment,
        out string dataType,
        out int portNumber)
    {
        dataType   = string.Empty;
        portNumber = 0;

        if (!CanonicalDataType.IsMatch(typeSegment ?? string.Empty) && !AifType.IsMatch(typeSegment ?? string.Empty)) return false;
        if (!CanonicalPortNumber.IsMatch(numberSegment ?? string.Empty)) return false;

        dataType   = typeSegment!;
        portNumber = int.Parse(numberSegment!, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    // The Port a segment addresses, or null if the Module has no such Port.
    //
    // Matching is on DIRECTION, PORT NUMBER and ACCEPTANCE of the named type -
    // so an RCA sending a full Text Object to a Port declared
    // ["OSD-BTO-V1.5", "OSD-TXO-V1.5"] names OSD-TXO-V1.5 in the URL, honestly,
    // and the Port is still found. The boundary key then comes from the Port,
    // not from the URL, which is the whole reason this lookup exists.
    public static BoundaryPort? Resolve(
        IReadOnlyList<BoundaryPort> ports,
        string direction,
        string dataType,
        int portNumber)
    {
        foreach (var port in ports)
            if (string.Equals(port.Direction, direction, StringComparison.Ordinal) &&
                port.PortNumber == portNumber &&
                port.Accepts(dataType))
                return port;

        return null;
    }
}