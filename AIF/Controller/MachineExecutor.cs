namespace AIF.Controller;

// Runs an AIM hierarchy.
//
// ROUTING IS BY DATA TYPE AND PORT NUMBER, FROM OUTPUT TO INPUT. A Topology
// connection joins two TYPED endpoints (Endpoint = AimName?, DataType,
// PortNumber), resolved by the loader from the AMD's ExternalPorts and
// InternalTypes, the Input and Output groups of M3194 included. What an AIM
// produces is kept with the Data Type and Port Number of the Port it was
// produced on, and a connection takes exactly the output its producing end
// names - nothing is found by Data Type alone (M3213 3.1). Port NAMES do not
// exist here: a boundary datum is keyed by its Endpoint.Key =
// "DataType#PortNumber".
//
// SUSPEND / RESUME: a composite suspends when a required boundary input
// (DataType, PortNumber) has not been supplied for a consumer, and resumes when
// the User Agent supplies it. The UA deals only in typed data.
public sealed class MachineExecutor
{
    private readonly AimHost host;

    private readonly ExecutionPlanner planner =
        new();

    public MachineExecutor(
        AimHost host)
    {
        this.host = host;
    }

    public IReadOnlyList<string> Plan(
        DescriptorGraph graph)
    {
        return planner.BuildPlan(graph.Root);
    }

    // Single-pass entry point (throws if the run would suspend).
    public async Task<Message> ExecuteAsync(
        DescriptorGraph graph,
        Message message)
    {
        var result = await ExecuteNodeResumableAsync(
            graph.Root,
            planner.BuildPlan(graph.Root),
            0,
            new Dictionary<string, Dictionary<string, RoutedObject>>(),
            new Dictionary<string, string>(message.Ports),
            message);

        if (result.IsSuspended)
            throw new InvalidOperationException(
                "Composite suspended waiting for boundary input " +
                $"'{result.Suspended!.WaitingPort}'. Use ExecuteResumableAsync.");

        return result.Completed!;
    }

    // Resumable entry point.
    public Task<ExecutionResult> ExecuteResumableAsync(
        DescriptorGraph graph,
        Message message)
    {
        return ExecuteNodeResumableAsync(
            graph.Root,
            planner.BuildPlan(graph.Root),
            0,
            new Dictionary<string, Dictionary<string, RoutedObject>>(),
            new Dictionary<string, string>(message.Ports),
            message);
    }

    // Resume with more boundary input, keyed by (DataType, PortNumber) i.e. the
    // Endpoint.Key of the boundary port.
    public Task<ExecutionResult> ResumeAsync(
        SuspendedExecution suspended,
        IReadOnlyDictionary<string, string> addedBoundary)
    {
        var boundary =
            new Dictionary<string, string>(suspended.Boundary);

        foreach (var kv in addedBoundary)
            boundary[kv.Key] = kv.Value;

        return ExecuteNodeResumableAsync(
            suspended.Node,
            suspended.Plan,
            suspended.Position,
            suspended.Outputs,
            boundary,
            suspended.Envelope);
    }

    // Core resumable loop.
    private async Task<ExecutionResult> ExecuteNodeResumableAsync(
        DescriptorNode node,
        IReadOnlyList<string> plan,
        int startPosition,
        Dictionary<string, Dictionary<string, RoutedObject>> outputs,
        Dictionary<string, string> boundary,
        Message message)
    {
        // WHAT WAS STARTED MAY BE A BASIC AIM. A Remote Client may start any AIM
        // (MPAI-MAS action 6), and a Service that offers one AIM runs that AIM. It
        // is not a composite and nothing contains it: its own Ports are the
        // boundary, so there is no Topology to walk and no routing to do.
        if (node.Children.Count == 0)
            return await ExecuteAimAsync(node, boundary, message);

        var children =
            node.Children.ToDictionary(
                child => child.AIMName,
                child => child);

        Message last = message;


        for (int position = startPosition; position < plan.Count; position++)
        {
            var aimName = plan[position];
            var child   = children[aimName];

            // AN EXCHANGE COMPLETES. The User Agent gives what it has and names what
            // it wants; it never promises to supply more, so there is nothing to wait
            // for. An AIM whose inputs are absent has no business in this exchange -
            // Text and Image Query when the words are a reply rather than a question
            // about an image - and is skipped below like any other.
            //
            // This held the whole exchange open instead, and the outputs that had
            // been produced surfaced on the next one: every answer arrived a turn
            // late, and the welcome was heard when an answer was expected.

            // Nothing suspended us, but this AIM may have nothing to work on -
            // e.g. an optional boundary input that was not supplied. Skip it.
            System.Console.WriteLine($"[EXEC] {aimName}: boundary has {string.Join(", ", boundary.Keys)}");
            if (HasNoInputAvailable(node, child, boundary, outputs))
            {
                Console.WriteLine($"[AIF] {aimName}: skipped (no input available)");
                continue;
            }

            var inbox =
                BuildInbox(node, child, outputs, boundary);

            var input =
                new Message
                {
                    MessageId   = message.MessageId,
                    MessageType = message.MessageType,
                    Ports       = inbox
                };

            input.Inputs.AddRange(
                BuildInputs(node, child, outputs));

            Console.WriteLine(
                $"[AIF] {aimName}: Ports={input.Ports.Count}, Inputs={input.Inputs.Count}");

            Message result;
            try
            {
                result =
                    child.IsComposite
                    ? await RunCompositeChildAsync(child, input)
                    : await host.ProcessAsync(aimName, input);
            }
            catch (OperationCanceledException cancelled)
            {
                return ExecutionResult.Complete(
                    Message.Cancelled(message.MessageId, aimName, cancelled.Message));
            }
            catch (Exception failure)
            {
                // A leaf that THROWS is isolated just like one that returns an
                // error: log it, treat it as having produced nothing, and let the
                // Module continue (graceful degradation, e.g. a recogniser that
                // threw on empty/garbled input must not blank the whole graph).
                Console.WriteLine($"[AIF] {aimName}: threw, skipped (produced no output): {failure.Message}");
                continue;
            }

            // A user CANCEL aborts the whole run. But a single leaf ERROR is
            // isolated: it means that AIM produced nothing (e.g. a recogniser
            // that saw no face or heard no speaker). The Module continues so the
            // rest of the graph - notably ID Reconciliation - can proceed with
            // whichever modalities DID succeed. Graceful degradation, not abort.
            if (result.IsCancelled)
                return ExecutionResult.Complete(result);
            if (result.IsError)
            {
                Console.WriteLine($"[AIF] {aimName}: error, skipped (produced no output): {result.Payload}");
                last = result;
                continue;
            }

            // EVERY OBJECT EVERY AIM PRODUCES PASSES THROUGH HERE. Asked, once
            // per AIM and Data Type, whether it says what its Data is. It reports
            // and does not refuse: the defects this finds are months old, and a
            // check that stopped a Module would turn a quiet fault into an outage.
            var routed = Produced(child, result);
            foreach (var produced in routed.Values)
                ObjectInspector?.Invoke(aimName, produced.DataType, produced.Payload);

            outputs[aimName] = routed;

            last = result;
        }

        return ExecutionResult.Complete(
            new Message
            {
                MessageId   = message.MessageId,
                MessageType = last.MessageType,
                DataType    = last.DataType,
                Payload     = last.Payload,
                Ports       = CollectOutputs(node, outputs, last)
            });
    }

    // THE AIM ITSELF, ASKED DIRECTLY. What the boundary carries is keyed by Data
    // Type and Port Number; an AIM reads its Message by its own Port names, and
    // answers by them. Here is the one place the two are matched, for an AIM that
    // was started on its own.
    private async Task<ExecutionResult> ExecuteAimAsync(
        DescriptorNode aim,
        IReadOnlyDictionary<string, string> boundary,
        Message message)
    {
        var inbox = new Dictionary<string, string>();
        foreach (var supplied in boundary)
        {
            var parts    = supplied.Key.Split('#');
            var dataType = parts[0];
            var number   = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 1;
            if (InputPortForDataType(aim, dataType, number) is { } port)
                inbox[port] = supplied.Value;
        }

        Console.WriteLine($"[AIF] {aim.AIMName}: started on its own, Ports={inbox.Count}");

        Message result;
        try
        {
            result = await host.ProcessAsync(
                aim.AIMName,
                new Message
                {
                    MessageId   = message.MessageId,
                    MessageType = message.MessageType,
                    Ports       = inbox
                });
        }
        catch (OperationCanceledException cancelled)
        {
            return ExecutionResult.Complete(Message.Cancelled(message.MessageId, aim.AIMName, cancelled.Message));
        }
        catch (Exception failure)
        {
            Console.WriteLine($"[AIF] {aim.AIMName}: threw, produced no output: {failure.Message}");
            return ExecutionResult.Complete(Empty(message));
        }

        if (result.IsCancelled) return ExecutionResult.Complete(result);
        if (result.IsError)
        {
            Console.WriteLine($"[AIF] {aim.AIMName}: error, produced no output: {result.Payload}");
            return ExecutionResult.Complete(Empty(message));
        }

        // Its outputs, back on the boundary they belong to.
        var produced = new Dictionary<string, string>();
        foreach (var output in result.Ports)
        {
            if (aim.Ports.FirstOrDefault(p => p.Direction == "Output" && p.Name == output.Key) is not { } port) continue;
            var number = NumberOf(aim, port);
            produced[new Endpoint(null, port.DataType, number).Key] = output.Value;
            Console.WriteLine($"[COLLECT] {aim.AIMName}.{port.DataType} -> {port.DataType}#{number}");
        }

        return ExecutionResult.Complete(new Message
        {
            MessageId   = message.MessageId,
            MessageType = result.MessageType,
            DataType    = result.DataType,
            Payload     = result.Payload,
            Ports       = produced
        });
    }

    private static Message Empty(Message message) => new()
    {
        MessageId   = message.MessageId,
        MessageType = message.MessageType,
        Ports       = new Dictionary<string, string>()
    };

    private async Task<Message> RunCompositeChildAsync(
        DescriptorNode child,
        Message input)
    {
        var result = await ExecuteNodeResumableAsync(
            child,
            planner.BuildPlan(child),
            0,
            new Dictionary<string, Dictionary<string, RoutedObject>>(),
            new Dictionary<string, string>(input.Ports),
            input);

        if (result.IsSuspended)
            throw new InvalidOperationException(
                $"Nested composite '{child.AIMName}' suspended; " +
                "nested suspension is not yet supported.");

        return result.Completed!;
    }

    // ---- Type-based routing helpers ----------------------------------------

    // The AIM's OWN input port name that carries (dataType, portNumber). Used to
    // key a leaf's inbox, because a leaf reads its Message.Ports by its own port
    // names (which it resolves from DataType via AimPortReader).
    private static string? InputPortForDataType(
        DescriptorNode aim, string dataType, int ordinal = 1) =>
        PortForDataType(aim, "Input", dataType, ordinal);

    private static string? OutputPortForDataType(
        DescriptorNode aim, string dataType, int ordinal = 1) =>
        PortForDataType(aim, "Output", dataType, ordinal);

    // Routing is by DataType; when one AIM declares several ports of the same
    // Direction and DataType the PortNumber decides (the port whose AMD
    // PortNumber equals it, else the n-th such port in declaration order).
    private static string? PortForDataType(
        DescriptorNode aim, string direction, string dataType, int ordinal)
    {
        var candidates = aim.Ports
            .Where(p => p.Direction == direction && p.Accepts(dataType))
            .ToList();

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0].Name;

        var declared = candidates.FirstOrDefault(p => p.PortNumber == ordinal);
        if (declared is not null) return declared.Name;

        return ordinal >= 1 && ordinal <= candidates.Count
            ? candidates[ordinal - 1].Name
            : null;
    }

    // The boundary (Endpoint.Key, DataType) that 'aim' requires but which is not
    // yet present, or null if all its boundary-sourced inputs are ready.
    private (string Key, string DataType)? MissingBoundaryInput(
        DescriptorNode node,
        DescriptorNode aim,
        IReadOnlyDictionary<string, string> boundary,
        IReadOnlyDictionary<string, Dictionary<string, RoutedObject>> outputs)
    {
        foreach (var connection in node.Connections)
        {
            if (connection.Input.AimName != aim.AIMName)
                continue;

            var source = connection.Output;
            if (source.AimName is not null)
                continue;   // AIM-to-AIM inputs are produced within the run

            if (!boundary.ContainsKey(source.Key))
            {
                var dt = source.DataType;

                if (InternallySatisfied(node, aim, dt, outputs))
                    continue;   // fed by an AIM that has produced this DataType

                if (BoundaryPortIsOptional(node, dt, source.PortNumber))
                    continue;   // nobody is coming; skip rather than wait

                return (source.Key, dt);
            }
        }

        return null;
    }

    // True if the composite boundary INPUT of (dataType, portNumber) is optional.
    private static bool BoundaryPortIsOptional(DescriptorNode node, string dataType, int portNumber) =>
        node.Ports.Any(p =>
            p.Direction == "Input" && p.Accepts(dataType) &&
            (p.PortNumber ?? 1) == portNumber && p.IsOptional);

    // True if 'aim' would run with NO input at all: every input is either an
    // unsupplied optional boundary port, or an internal connection whose producer
    // has not produced. Such an AIM is skipped.
    private bool HasNoInputAvailable(
        DescriptorNode node,
        DescriptorNode aim,
        IReadOnlyDictionary<string, string> boundary,
        IReadOnlyDictionary<string, Dictionary<string, RoutedObject>> outputs)
    {
        var any = false;

        foreach (var connection in node.Connections)
        {
            if (connection.Input.AimName != aim.AIMName) continue;

            var source = connection.Output;

            if (source.AimName is null)
            {
                if (boundary.ContainsKey(source.Key)) { any = true; break; }
                continue;
            }

            if (outputs.TryGetValue(source.AimName, out var produced) &&
                FindProduced(produced, source) is not null)
            {
                any = true;
                break;
            }
        }

        return !any;
    }

    // True if 'aim' has an AIM-to-AIM input connection carrying 'dataType' whose
    // source AIM has already produced an output of that DataType.
    private bool InternallySatisfied(
        DescriptorNode node,
        DescriptorNode aim,
        string dataType,
        IReadOnlyDictionary<string, Dictionary<string, RoutedObject>> outputs)
    {
        if (string.IsNullOrEmpty(dataType)) return false;

        foreach (var connection in node.Connections)
        {
            if (connection.Input.AimName != aim.AIMName)
                continue;

            var source = connection.Output;
            if (source.AimName is null)
                continue;   // boundary source, not internal

            if (source.DataType != dataType)
                continue;

            if (outputs.TryGetValue(source.AimName, out var producedPorts) &&
                FindProduced(producedPorts, source) is not null)
                return true;
        }

        return false;
    }

    // Structured inputs (DataObjectMessage list) for AIM-to-AIM connections.
    private List<DataObjectMessage> BuildInputs(
        DescriptorNode node,
        DescriptorNode aim,
        IReadOnlyDictionary<string, Dictionary<string, RoutedObject>> outputs)
    {
        var inputs = new List<DataObjectMessage>();

        foreach (var connection in node.Connections)
        {
            if (connection.Input.AimName != aim.AIMName)
                continue;

            var source = connection.Output;
            if (source.AimName is null)
                continue;   // boundary handled in BuildInbox

            var dataType = source.DataType;

            if (!outputs.TryGetValue(source.AimName, out var producedPorts))
                continue;

            var routed = FindProduced(producedPorts, source);
            if (routed is null)
                continue;

            inputs.Add(new DataObjectMessage { DataType = dataType, Payload = routed.Payload });
        }

        return inputs;
    }

    // The inbox for 'aim'. A LEAF is keyed by its own input port NAMES (the leaf
    // reads Message.Ports by name, resolved from DataType via AimPortReader). A
    // COMPOSITE child is keyed by the boundary Endpoint.Key ("DataType#n"),
    // because the nested executor reads its boundary by (DataType, PortNumber).
    //
    // When a destination is fed by BOTH a boundary source and an internal AIM
    // source, the BOUNDARY value wins when present.
    private Dictionary<string, string> BuildInbox(
        DescriptorNode node,
        DescriptorNode aim,
        IReadOnlyDictionary<string, Dictionary<string, RoutedObject>> outputs,
        IReadOnlyDictionary<string, string> boundary)
    {
        var inbox  = new Dictionary<string, string>();
        var filled = new HashSet<string>();

        string DestKey(Endpoint consumer) =>
            aim.IsComposite
                ? consumer.Key
                : (InputPortForDataType(aim, consumer.DataType, consumer.PortNumber) ?? consumer.Key);

        // Pass 1: boundary sources (explicit human inputs) - highest priority.
        foreach (var connection in node.Connections)
        {
            if (connection.Input.AimName != aim.AIMName) continue;

            var source = connection.Output;
            if (source.AimName is not null) continue;   // internal handled in pass 2

            var destKey = DestKey(connection.Input);
            if (boundary.TryGetValue(source.Key, out var supplied))
            {
                inbox[destKey] = supplied;
                filled.Add(destKey);
            }
        }

        // Pass 2: internal AIM sources - fill only destinations not set above.
        foreach (var connection in node.Connections)
        {
            if (connection.Input.AimName != aim.AIMName) continue;

            var source = connection.Output;
            if (source.AimName is null) continue;   // boundary handled in pass 1

            var destKey = DestKey(connection.Input);
            if (filled.Contains(destKey)) continue;

            if (outputs.TryGetValue(source.AimName, out var producedPorts))
            {
                var routed = FindProduced(producedPorts, source);
                if (routed is not null)
                    inbox[destKey] = routed.Payload;
            }
        }

        return inbox;
    }

    // What the composite exposes on its boundary outputs, keyed by the boundary
    // output Endpoint.Key ("DataType#n"), so the User Agent reads outputs by
    // (DataType, PortNumber).
    // SOMEWHERE TO LOOK, WITHOUT KNOWING WHAT IS BEING LOOKED AT. The Framework
    // routes Data Types and payloads; it does not know what an MPAI Object is, and
    // it must not - a Controller that depended on the Data Type library would stop
    // being generic. So it offers the place and whoever knows both worlds installs
    // the inspector, exactly as AimLog offers a sink and a host installs it.
    //
    // (AIM name, Data Type, payload). No sink, no cost beyond a null check.
    public static Action<string, string, string>? ObjectInspector { get; set; }

    private Dictionary<string, string> CollectOutputs(
        DescriptorNode node,
        IReadOnlyDictionary<string, Dictionary<string, RoutedObject>> outputs,
        Message last)
    {
        var composite = new Dictionary<string, string>();

        foreach (var connection in node.Connections)
        {
            var source = connection.Output;   // producing AIM
            var dest   = connection.Input;    // boundary

            if (dest.AimName is not null || source.AimName is null)
                continue;   // only AIM -> boundary

            var had = outputs.TryGetValue(source.AimName, out var producedPorts);
            var routed = had ? FindProduced(producedPorts!, source) : null;
            System.Console.WriteLine($"[COLLECT] {source.AimName}.{source.Key} -> {dest.Key}: ran={had} found={routed is not null}");
            if (routed is not null)
                composite[dest.Key] = routed.Payload;
        }

        return composite;
    }

    // What an AIM produced on the Port a Topology end names: that Data Type, on
    // that Port Number. Nothing else - not the first output of the Data Type, not
    // an AIM's only output whatever it is.
    private static RoutedObject? FindProduced(
        Dictionary<string, RoutedObject> producedPorts,
        Endpoint source) =>
        producedPorts.Values.FirstOrDefault(r => r.IsFrom(source));

    // What an AIM produced, each output with the Data Type and Port Number of its
    // Port. A leaf answers by its own Output Port names, which its Metadata
    // declares; a composite child by its boundary Endpoint.Key ("DataType#n").
    private static Dictionary<string, RoutedObject> Produced(DescriptorNode child, Message result)
    {
        var routed = new Dictionary<string, RoutedObject>();
        foreach (var produced in result.Ports)
        {
            if (child.IsComposite)
            {
                var hash = produced.Key.LastIndexOf('#');
                if (hash <= 0) continue;
                routed[produced.Key] = new RoutedObject
                {
                    DataType   = produced.Key[..hash],
                    PortNumber = int.TryParse(produced.Key[(hash + 1)..], out var n) ? n : 1,
                    Payload    = produced.Value
                };
                continue;
            }

            var port = child.Ports.FirstOrDefault(p => p.Direction == "Output" && p.Name == produced.Key);
            if (port is null)
            {
                Console.WriteLine($"[AIF] {child.AIMName}: produced '{produced.Key}', which is not one of its Output Ports; dropped");
                continue;
            }

            routed[produced.Key] = new RoutedObject
            {
                DataType   = port.DataType,
                DataTypes  = port.DataTypes,
                PortNumber = NumberOf(child, port),
                Payload    = produced.Value
            };
        }
        return routed;
    }

    // A Port's number: the one its Metadata declares, else its place among the
    // AIM's Ports of that Direction and Data Type (1 where the type occurs once).
    private static int NumberOf(DescriptorNode aim, RuntimePort port) =>
        port.PortNumber ??
        aim.Ports.Where(p => p.Direction == port.Direction && p.DataType == port.DataType).ToList().IndexOf(port) + 1;
}
