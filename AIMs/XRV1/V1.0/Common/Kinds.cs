using System.Collections.Generic;
using System.Linq;

namespace Mpai.Xrv;

public enum StatusKind { REPerformance, VEPerformance, REParticipant, VEParticipant, REOperator, VEOperator }
public enum Family { Performance, Participant, Operator }

// WHAT THE SIX KINDS OF STATUS (and of Descriptors) ARE CALLED in their schemas: the names follow one pattern, so they are given by it. The Data Type of each is told by its Header.
public sealed record KindInfo(StatusKind Kind)
{
    public bool IsRE => Kind.ToString().StartsWith("RE");
    public string Side => IsRE ? "RE" : "VE";
    public Family Family => Kind.ToString()[2..] switch { "Performance" => Family.Performance, "Participant" => Family.Participant, _ => Family.Operator };
    private string F => Family.ToString();
    private char S => IsRE ? 'R' : 'V';
    private char L => Family switch { Family.Performance => 'F', Family.Participant => 'T', _ => 'O' };

    public string StatusKey => $"{Side}{F}Status";                         // the property of the Cue Conditions, and the root name of the Status
    public string StatusHeader => $"XRV-{S}{L}S-V1.0";
    public string StatusFile => $"XRV1/V1.0/data/{StatusKey}.json";
    public string StatusDataKey => Family == Family.Operator ? StatusKey : StatusKey + "Data";
    public string StatusIdKey => StatusKey + "ID";
    public string StatusTimeKey => StatusKey + "Time";

    public string DescriptorsKey => $"{Side}{F}Descriptors";
    public string DescriptorsHeader => $"XRV-{S}{L}D-V1.0";
    public string DescriptorsFile => $"XRV1/V1.0/data/{DescriptorsKey}.json";
    public string DescriptorsDataKey => Family == Family.Operator ? DescriptorsKey : DescriptorsKey + "Data";
    public string DescriptorsIdKey => DescriptorsKey + "ID";
    public string DescriptorsTimeKey => DescriptorsKey + "Time";

    public static IReadOnlyList<KindInfo> All { get; } = System.Enum.GetValues<StatusKind>().Select(k => new KindInfo(k)).ToList();
    public static KindInfo Of(StatusKind kind) => All[(int)kind];
    public static KindInfo OfStatusHeader(string header) => All.First(k => k.StatusHeader == header);
    public static KindInfo OfDescriptorsHeader(string header) => All.First(k => k.DescriptorsHeader == header);
}

// The Data Types, as the Ports of the AIMs name them.
public static class Types
{
    public const string Script = "XRV-SCR-V1.0", ActionTable = "XRV-ATB-V1.0", ScriptVenueAssociations = "XRV-SVA-V1.0", ParticipantData = "XRV-PTD-V1.0",
                        BasicTextObject = "OSD-BTO-V1.5", REVenueSpecification = "XRV-RVS-V1.0", VEVenueSpecification = "XRV-VVS-V1.0";
}
