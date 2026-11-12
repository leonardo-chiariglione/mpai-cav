using AIF.Channels;
using AIF.Controller;

namespace Mpai.Aif.Tests;

// EACH COMPOSITE ITS OWN POOL (M3194; N-16 of the night list, agreed 2026-11-10). The Controller resolves each Topology connection only inside the composite that
// declares it, and a Sub-AIM in each composite is an instance of its own. Audio Scene Management holds Text and Speech Translation (MMC-TST: ASR, TTT, TTS) and
// Voice Command Interpretation (MMC-VCI: ASR, JSI, EDP, TTS): MMC-ASR and MMC-TTS are in both, and must run as two instances each, never one.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class ControllerCompositePoolTests
{
    private const string Asm = "1CAE-ASM-V1.0-I01", Tst = "1MMC-TST-V2.5-I01", Vci = "1MMC-VCI-V2.5-I01", Asr = "1MMC-ASR-V2.5-I01", Tts = "1MMC-TTS-V2.5-I01", Ttt = "1MMC-TTT-V2.5-I01";

    private static DescriptorGraph Graph()
    {
        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        return new AIF.Controller.Controller(store).RegisterAim(store.FindByAimName(Asm)!);
    }

    private static IEnumerable<DescriptorNode> Leaves(DescriptorNode node) => node.Children.SelectMany(c => c.IsComposite ? Leaves(c) : [c]);

    [Fact]
    public void AnAimInTwoCompositesIsTwoInstancesAndAnAimAloneKeepsItsName()
    {
        var leaves = Leaves(Graph().Root).ToList();
        Assert.Equal([$"{Tst}/{Asr}", $"{Vci}/{Asr}"], leaves.Where(l => l.AIMName == Asr).Select(l => l.Instance).Order(StringComparer.Ordinal));
        Assert.Equal([$"{Tst}/{Tts}", $"{Vci}/{Tts}"], leaves.Where(l => l.AIMName == Tts).Select(l => l.Instance).Order(StringComparer.Ordinal));
        Assert.All(leaves.Where(l => l.AIMName is not (Asr or Tts)), l => Assert.Equal(l.AIMName, l.Instance));   // one of its name: as before
        Assert.Equal(leaves.Count, leaves.Select(l => l.Instance).Distinct().Count());
    }

    [Fact]
    public void NoChannelCrossesFromOneCompositesPoolToTheOthers()
    {
        var executor = new ContinuousExecutor(Graph(), new AimHost(), new Dictionary<string, IChannelTransport>
        {
            ["Controller"] = new ControllerTransport(), ["Bus"] = new BusTransport()
        }, Asm);
        static bool In(string composite, string aim) => aim.StartsWith(composite + "/", StringComparison.Ordinal);

        foreach (var channel in executor.Channels)
        {
            var writer = channel.Writer.Aim;
            foreach (var reader in channel.Readers.Select(r => r.Reader.Aim))
            {
                Assert.False(In(Tst, writer) && In(Vci, reader), $"{channel.Writer} (TST) reaches {reader} (VCI)");
                Assert.False(In(Vci, writer) && In(Tst, reader), $"{channel.Writer} (VCI) reaches {reader} (TST)");
            }
        }

        // TST: the speech to translate reaches TST's ASR only; what TST's ASR hears goes to TTT; what TTT gives is spoken by TST's TTS.
        var toTranslate = executor.Channels.Where(c => c.Readers.Any(r => r.Reader.Aim == $"{Tst}/{Asr}")).ToList();
        Assert.NotEmpty(toTranslate);
        Assert.All(toTranslate, c => Assert.DoesNotContain(c.Readers, r => r.Reader.Aim == $"{Vci}/{Asr}"));
        Assert.Contains(executor.Channels, c => c.Writer.Aim == $"{Tst}/{Asr}" && c.Readers.Any(r => r.Reader.Aim == Ttt));
        Assert.Contains(executor.Channels, c => c.Writer.Aim == Ttt && c.Readers.Any(r => r.Reader.Aim == $"{Tst}/{Tts}"));
        Assert.DoesNotContain(executor.Channels, c => c.Writer.Aim == Ttt && c.Readers.Any(r => r.Reader.Aim == $"{Vci}/{Tts}"));

        // VCI: the User's speech at the Module's boundary reaches VCI's ASR, not TST's.
        Assert.Contains(executor.Channels, c => c.Writer.IsBoundary && c.Readers.Any(r => r.Reader.Aim == $"{Vci}/{Asr}"));
        Assert.DoesNotContain(executor.Channels, c => c.Writer.IsBoundary && c.Readers.Any(r => r.Reader.Aim == $"{Tst}/{Asr}"));
    }

    // Each instance its own implementation: a provider is asked once per instance, and knows which instance it makes.
    [Fact]
    public void EachInstanceIsMadeOnceAndAsItself()
    {
        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        var controller = new AIF.Controller.Controller(store);
        var graph = controller.RegisterAim(store.FindByAimName(Asm)!);
        var made = new List<(string Aim, string? Instance)>();
        using var host = new AimHost();
        controller.Instantiate(graph, new Recording(made), AIF.Store.AimSettings.Empty, host);
        Assert.Equal(2, made.Count(m => m.Aim == Asr));
        Assert.Equal([$"{Tst}/{Asr}", $"{Vci}/{Asr}"], made.Where(m => m.Aim == Asr).Select(m => m.Instance!).Order(StringComparer.Ordinal));
        Assert.True(host.Contains($"{Tst}/{Asr}") && host.Contains($"{Vci}/{Asr}") && host.Contains($"{Tst}/{Tts}") && host.Contains($"{Vci}/{Tts}"));
        Assert.NotSame(host.ProcessorOf($"{Tst}/{Asr}"), host.ProcessorOf($"{Vci}/{Asr}"));
    }

    private sealed class Recording(List<(string Aim, string? Instance)> made) : IAimProvider
    {
        public bool CanCreate(string aimName) => true;
        public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage)
        {
            made.Add((aimName, AIF.Controller.Controller.CreatingInstance));
            return new Nothing(aimName);
        }
        private sealed class Nothing(string name) : IAimProcessor
        {
            public string InstanceId => name;
            public Task<Message> ProcessAsync(Message message) => Task.FromResult(new Message { MessageId = message.MessageId });
        }
    }
}
