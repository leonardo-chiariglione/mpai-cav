using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using AIF.Channels;
using AIF.Controller;
using AIF.SharedStorage;
using AIF.Store;

namespace AIF.RemoteHost;

// WHAT AN AIM HOST DOES FOR A CONTROLLER (M3217 3.1, 3.3). On the Controller's
// orders it instantiates an AIM of one of its Module instances, runs it, pauses,
// resumes and stops it, and reports its status. It runs no graph, decides
// nothing, and keeps no Module: what it holds for a Module instance goes when the
// Controller releases it, or the link goes.
public sealed class AimHostServer
{
    private readonly AmdStore store;
    private readonly AimSettings settings;
    private readonly IAimProvider provider;
    private readonly string storageRoot;
    private readonly ConcurrentDictionary<string, Hosted> modules = new();

    // The AIMs of one Module instance held here, and their lifecycle.
    private sealed class Hosted
    {
        public required AimHost Host { get; init; }
        public HashSet<string> Aims { get; } = new();
    }

    public AimHostServer(AmdStore store, AimSettings settings, IAimProvider provider, string storageRoot)
    {
        this.store = store;
        this.settings = settings;
        this.provider = provider;
        this.storageRoot = storageRoot;
    }

    // A Controller admitted: its requests served; its Module instances released
    // when its link goes.
    public void Serve(RemoteLink link)
    {
        var mine = new ConcurrentBag<string>();
        link.OnRequest = frame => HandleAsync(frame, mine);
        link.Lost += _ =>
        {
            foreach (var module in mine) Release(module);
        };
    }

    private async Task<JsonObject?> HandleAsync(JsonObject frame, ConcurrentBag<string> mine)
    {
        var module = frame["Module"]?.GetValue<string>() ?? "";
        switch (frame["Kind"]?.GetValue<string>())
        {
            case "Place":
            {
                var aim = frame["Aim"]!.GetValue<string>();
                var identifier = store.FindByAimName(aim);
                if (identifier is null) return Refused($"this host holds no L3 of {aim}");
                if (!provider.CanCreate(aim)) return Refused($"this host has no implementation of {aim}");
                var hosted = modules.GetOrAdd(module, _ => { mine.Add(module); return new Hosted { Host = new AimHost() }; });
                if (!hosted.Aims.Add(aim)) return new JsonObject { ["Ok"] = true };   // placed already
                var scope = Path.Combine(storageRoot, Uri.EscapeDataString(module));
                var processor = provider.Create(aim, settings.For(aim),
                    new FileSharedStorage(scope, $"{module}/{aim}", "remote"),
                    new FileSharedStorage(Path.Combine(scope, "private", Uri.EscapeDataString(aim)), $"{module}/{aim}", "remote"));
                hosted.Host.RegisterRuntime(processor);
                Console.WriteLine($"[AIM host] {aim} placed for {module}");
                return new JsonObject { ["Ok"] = true };
            }

            case "Process":
            {
                if (!modules.TryGetValue(module, out var hosted)) return Refused($"{module} holds nothing here");
                var aim = frame["Aim"]!.GetValue<string>();
                var ports = frame["Ports"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>());
                hosted.Host.BeginRun();
                try
                {
                    var result = await hosted.Host.ProcessAsync(aim, new Message
                    {
                        MessageId = frame["MessageId"]?.GetValue<string>() ?? "", MessageType = module, Ports = ports
                    });
                    return new JsonObject
                    {
                        ["Ok"]          = true,
                        ["MessageType"] = result.MessageType,
                        ["Payload"]     = result.Payload,
                        ["Ports"]       = new JsonObject(result.Ports.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
                        ["Reports"]     = Reports(hosted, aim)
                    };
                }
                catch (OperationCanceledException stopped)
                {
                    return new JsonObject { ["Ok"] = true, ["Cancelled"] = stopped.Message, ["Reports"] = Reports(hosted, aim) };
                }
                catch (Exception failure)
                {
                    return new JsonObject { ["Ok"] = true, ["Threw"] = failure.Message, ["Reports"] = Reports(hosted, aim) };
                }
            }

            case "Pause":  return Each(module, h => h.PauseModule());
            case "Resume": return Each(module, h => h.ResumeModule());
            case "Stop":   return Each(module, h => h.StopModule());

            case "StopAim":
                return modules.TryGetValue(module, out var one) && one.Host.StopAim(frame["Aim"]!.GetValue<string>(), "stopped by its Controller")
                    ? new JsonObject { ["Ok"] = true } : Refused("no such AIM here");

            case "Status":
            {
                if (!modules.TryGetValue(module, out var hosted)) return Refused($"{module} holds nothing here");
                return new JsonObject
                {
                    ["Ok"] = true,
                    ["Aims"] = new JsonArray(hosted.Host.Status().Select(a => (JsonNode)new JsonObject
                    {
                        ["Aim"] = a.Aim, ["Status"] = a.Status.ToString(), ["Reason"] = a.Reason,
                        ["Reports"] = new JsonArray(a.Reports.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray())
                    }).ToArray())
                };
            }

            case "Release":
                Release(module);
                return new JsonObject { ["Ok"] = true };

            default:
                return Refused($"'{frame["Kind"]}' is not asked of an AIM host");
        }
    }

    private JsonObject Each(string module, Action<AimHost> act)
    {
        if (!modules.TryGetValue(module, out var hosted)) return Refused($"{module} holds nothing here");
        act(hosted.Host);
        return new JsonObject { ["Ok"] = true };
    }

    private static JsonArray Reports(Hosted hosted, string aim) =>
        new(hosted.Host.Status().Where(a => a.Aim == aim).SelectMany(a => a.Reports).Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());

    private void Release(string module)
    {
        if (!modules.TryRemove(module, out var hosted)) return;
        hosted.Host.Dispose();
        Console.WriteLine($"[AIM host] {module} released");
    }

    private static JsonObject Refused(string why) => new() { ["Ok"] = false, ["Error"] = why };
}
