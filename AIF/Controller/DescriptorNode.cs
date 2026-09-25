namespace AIF.Controller;

// A node in an AIM hierarchy.
//
// An AIM may have hierarchical structure: a node with no children is a Basic
// AIM; a node with children is a Composite AIM, and its Connections are that
// composite's own Topology. The structure nests to any depth.
//
// InternalTypes maps an InternalType name (as used in Topology PortName fields)
// to its DataType identifier (e.g. "OSD-AUO-V1.5"). This lets the executor
// route data between AIMs by DataType rather than by port name, so each AIM
// can use its own port names without the Controller needing to know them.
public sealed class DescriptorNode
{
    public string AIMName { get; init; } =
        string.Empty;

    public string ImplementerID { get; init; } =
        string.Empty;

    public string ImplementationID { get; init; } =
        string.Empty;

    public List<RuntimePort> Ports { get; } =
        new();

    // WHERE THIS SUB-AIM RUNS, as the composite that contains it declares
    // (Identifier.Relation in its SubAIMs entry): "Internal" - within the machine
    // provisioned for the Module, as every AIM does today - or "External",
    // "Private", "Public": on its own machine, reached over MPAI-MAS. Empty when
    // the L3 does not say.
    public string Relation { get; set; } = string.Empty;

    // InternalType name -> DataType identifier.
    // Populated from the "InternalTypes" array in the composite's AMD.
    public Dictionary<string, string> InternalTypes { get; } =
        new();

    // InternalType name -> the Output number (M3194 Number 3) declared on that
    // flow, for the flows that are sent on to a child composite's Input group.
    public Dictionary<string, int> InternalTypeOutputs { get; } =
        new();

    public List<DescriptorNode> Children { get; } =
        new();

    // What this composite does when one of its AIMs fails (M3213 3.5):
    // StopModule, the default; StopAIM; or Continue.
    public string OnDegraded { get; set; } =
        "StopModule";

    // The AIM Instance that holds the central control of this composite's Private
    // Storage (M3219 3.2); null where each writer sets the rules of its data.
    public string? StorageControl { get; set; }

    // "Always" where the Controller records the boundary from Start to Stop
    // (M3219 3.4); null where the User Agent decides.
    public string? Record { get; set; }

    // How the Controller executes this composite: Exchange, the default, or
    // Continuous (M3205 Section 5).
    public string Execution { get; set; } =
        "Exchange";

    public bool IsContinuous =>
        Execution == "Continuous";

    // How many times this AIM is started again when it throws (M3215 3.3), and its
    // Period and Deadline in milliseconds (3.5); null where not declared.
    public int     RestartLimit { get; set; }
    public double? Period       { get; set; }
    public double? Deadline     { get; set; }

    public List<TopologyConnection> Connections { get; } =
        new();

    public bool IsComposite =>
        Children.Count > 0;

    // Resolve an InternalType name to a DataType.
    // Returns null if the name is not found in InternalTypes.
    public string? ResolveInternalType(string internalTypeName)
    {
        return InternalTypes.TryGetValue(internalTypeName, out var dataType)
            ? dataType
            : null;
    }
}
