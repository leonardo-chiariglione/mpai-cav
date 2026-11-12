namespace AIF.Controller;

// A node in an AIM hierarchy.
//
// An AIM may have hierarchical structure: a node with no children is a Basic
// AIM; a node with children is a Composite AIM, and its Connections are that
// composite's own Topology. The structure nests to any depth.
//
// Built from the L3 as normalised at import (AIF.Store.TopologyNormaliser):
// there is no name in it, so nothing here can route by one.
public sealed class DescriptorNode
{
    public string AIMName { get; init; } =
        string.Empty;

    // THE INSTANCE THIS NODE IS AT RUN TIME (M3194: each composite has its own pool). The same AIM may be a
    // Sub-AIM of two composites of one Module (MMC-ASR in MMC-VCI and in MMC-TST): each is an instance of its
    // own, with its own Ports, its own Channels and its own state. The Controller names them when it builds the
    // graph: the AIM name where it is the only one of its name in the Module, else the names of the composites
    // that hold it and its own, joined by '/'. What runs, writes and is read is keyed by it; what the AIM is
    // (its L3, settings, Access, implementation) is AIMName.
    public string Instance { get => instance ?? AIMName; set => instance = value; }
    private string? instance;

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

    public List<DescriptorNode> Children { get; } =
        new();

    // A PACKAGE (M3223 3.3): the AIMs its L3 names with the Relation "Packaged" -
    // inside its one binary. The Controller builds none of them; the package is
    // one AIM to it, and presents them for verification.
    public List<string> Packaged { get; } =
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

    // THE ACCESS THIS AIM READS (AIM Metadata, AccessSources): kinds of data, by the Data Type of their items, with the
    // Writer they must have and, where stated, the Keys. Empty: the AIM reads no Access.
    public List<AccessEntry> AccessSources { get; } =
        new();

    // How many times this AIM is started again when it throws (M3215 3.3), and its
    // Period and Deadline in milliseconds (3.5); null where not declared.
    public int     RestartLimit { get; set; }
    public double? Period       { get; set; }
    public double? Deadline     { get; set; }

    public List<TopologyConnection> Connections { get; } =
        new();

    public bool IsComposite =>
        Children.Count > 0;
}
