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
// A RUN COMPLETES. An AIM whose inputs are absent does not run, and nothing
// waits for a User Agent (M3205 5.1).
public sealed class MachineExecutor
{
    private readonly AimHost host;

    private readonly ExecutionPlanner planner =
        new();

    // The Module being run: a run begins when its root does.
    private DescriptorNode? root;

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

    // Runs the Module once on the boundary inputs the message carries.
    public Task<ExecutionResult> RunAsync(
        DescriptorGraph graph,
        Message message)
    {
        root = graph.Root;
        return ExecuteNodeAsync(graph.Root, new Dictionary<string, string>(message.Ports), message);
    }

    // One composite, its AIMs in the order of its Topology.
    private async Task<ExecutionResult> ExecuteNodeAsync(
        DescriptorNode node,
        Dictionary<string, string> boundary,
        Message message)
    {
        // WHAT WAS STARTED MAY BE A BASIC AIM. A Remote Client may start any AIM
        // (MPAI-MAS action 6), and a Service that offers one AIM runs that AIM. It
        // is not a composite and nothing contains it: its own Ports are the
        // boundary, so there is no Topology to walk and no routing to do.
        if (node.Children.Count == 0)
            return await ExecuteAimAsync(node, boundary, message);

        if (ReferenceEquals(node, root)) host.BeginRun();

        var children =
            node.Children.ToDictionary(
                child => child.AIMName,
                child => child);

        var plan    = planner.BuildPlan(node);
        var outputs = new Dictionary<string, Dictionary<string, RoutedObject>>();
        Message last = message;

        for (int position = 0; position < plan.Count; position++)
        {
            var aimName = plan[position];
            var child   = children[aimName];

            // A Module its policy stopped runs no further; a stopped AIM, never again.
            if (host.IsStopped)
                return ExecutionResult.Complete(Empty(message));
            if (!child.IsComposite && host.IsDead(aimName))
            {
                Console.WriteLine($"[AIF] {aimName}: stopped, skipped");
                continue;
            }

            // AN EXCHANGE COMPLETES. The User Agent gives what it has and names what
            // it wants; it never promises to supply more, so there is nothing to wait
            // for. An AIM whose inputs are absent has no business in this exchange -
            // Text and Image Query when the words are a reply rather than a question
            // about an image - and is skipped below like any other.
            //
            // This held the whole exchange open instead, and the outputs that had
            // been produced surfaced on the next one: every answer arrived a turn
            // late, and the welcome was heard when an answer was expected.

            // This AIM may have nothing to work on - e.g. an optional boundary
            // input that was not supplied. Skip it.
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
                // One AIM stopped - by a policy, by the User Agent, by another AIM
                // - produced nothing, and the others go on. The Module stopped:
                // the run ends.
                if (!host.IsStopped && !child.IsComposite && host.IsDead(aimName))
                {
                    Console.WriteLine($"[AIF] {aimName}: stopped while running, produced no output");
                    continue;
                }
                return ExecutionResult.Complete(
                    Message.Cancelled(message.MessageId, aimName, cancelled.Message));
            }
            catch (Exception failure)
            {
                // A leaf that THROWS is DEGRADED, and has produced nothing; what
                // follows is the policy of the composite containing it.
                if (Degraded(node, aimName, $"it threw: {failure.Message}"))
                    return ExecutionResult.Complete(Empty(message));
                continue;
            }

            // A user CANCEL aborts the whole run. A leaf ERROR - a recogniser that
            // saw no face or heard no speaker - is a failure like a throw: the AIM
            // is DEGRADED, produced nothing, and the policy decides.
            if (result.IsCancelled)
                return ExecutionResult.Complete(result);
            if (result.IsError)
            {
                if (Degraded(node, aimName, $"it returned an error: {result.Payload}"))
                    return ExecutionResult.Complete(Empty(message));
                last = result;
                continue;
            }

            if (!child.IsComposite) host.Succeeded(aimName);

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
        host.BeginRun();

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
            // An AIM started on its own is not in a composite and has no policy:
            // it is DEGRADED, and what to do is the User Agent's decision.
            host.Degrade(aim.AIMName, $"it threw: {failure.Message}");
            Console.WriteLine($"[AIF] {aim.AIMName}: threw, produced no output: {failure.Message}");
            return ExecutionResult.Complete(Empty(message));
        }

        if (result.IsCancelled) return ExecutionResult.Complete(result);
        if (result.IsError)
        {
            host.Degrade(aim.AIMName, $"it returned an error: {result.Payload}");
            Console.WriteLine($"[AIF] {aim.AIMName}: error, produced no output: {result.Payload}");
            return ExecutionResult.Complete(Empty(message));
        }
        host.Succeeded(aim.AIMName);

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

    // An AIM of 'node' failed. It is DEGRADED, and the composite's policy is
    // applied (M3213 3.5): StopModule - the Module is stopped and the run ends
    // (true); StopAIM - that AIM is stopped and the others go on; Continue - the
    // others go on, and it runs again at the next exchange.
    private bool Degraded(DescriptorNode node, string aimName, string reason)
    {
        host.Degrade(aimName, reason);
        Console.WriteLine($"[AIF] {aimName}: DEGRADED ({reason}); {node.AIMName} OnDegraded {node.OnDegraded}");

        switch (node.OnDegraded)
        {
            case "Continue":
                return false;
            case "StopAIM":
                host.StopAim(aimName, $"{reason}; OnDegraded StopAIM");
                return false;
            default:
                host.StopModule();
                return true;
        }
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
        var result = await ExecuteNodeAsync(child, new Dictionary<string, string>(input.Ports), input);
        return result.Completed;
    }

    // ---- Type-based routing helpers ----------------------------------------

    // The AIM's OWN input port name that carries (dataType, portNumber). Used to
    // key a leaf's inbox, because a leaf reads its Message.Ports by its own port
    // names (which it resolves from DataType via AimPortReader).
    private static string? InputPortForDataType(
        DescriptorNode aim, string dataType, int ordinal = 1) =>
        PortForDataType(aim, "Input", dataType, ordinal);

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
