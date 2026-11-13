using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.SharedStorage;
using AIF.Store;
using Mpai.Aih;

namespace Mpai.Aif.Tests;

// THE FIRST AIMs OF AIH-HSP V1.1, each on its own: Anomaly and Risk Alerting, Model Licensing and Storage. They are built by the provider from the AIM Metadata,
// given what their Ports take in the form their Data Types have, and judged by what leaves their Ports (valid against the schemas).
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AihAimsTests : IDisposable
{
    private readonly AmdStore store = new(Repository.Amds);
    private readonly string work = Path.Combine(Path.GetTempPath(), "aih-aims-" + Guid.NewGuid().ToString("N"));
    private static readonly IReadOnlyDictionary<string, Json.Schema.JsonSchema> Schemas = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);

    public AihAimsTests() { store.Scan(); Directory.CreateDirectory(work); }
    public void Dispose() { try { Directory.Delete(work, true); } catch { } }

    private IAimProcessor Build(string aim) =>
        new AihProvider(store).Create(aim, new Dictionary<string, string>(), new FileSharedStorage(work, "aih-test", aim), new FileSharedStorage(Path.Combine(work, "own", aim), "aih-test", aim),
            new AIF.SharedStorage.RuledStore(() => Path.Combine(work, "private", "module"), () => DateTimeOffset.UtcNow, everyoneReads: false, centralControl: null)
                .For(new AIF.SharedStorage.StorageHolder("m", aim), "m", "s"));

    private string In(string aim, string dataType, int ordinal = 1) => AimPortReader.Load(store, aim).Input(dataType, ordinal);
    private string Out(string aim, string dataType, int ordinal = 1) => AimPortReader.Load(store, aim).Output(dataType, ordinal);

    // A10 (2026-11-13): NO PSEUDONYM KEY IN THE CODE. Without the setting PseudonymKey, De-Identification and Anonymisation makes a random key once and keeps it in its Private
    // Storage - built again on that storage, it uses the same key (the same End User keeps the same pseudonym); without the setting and without its storage, it is not built.
    [Fact]
    public void TheKeyOfThePseudonymsIsMadeOnceAndKeptNotWrittenInTheCode()
    {
        var own = new FileSharedStorage(Path.Combine(work, "dia-own"), "aih-test", AihProvider.Dia);
        new AihProvider(store).Create(AihProvider.Dia, new Dictionary<string, string>(), null, own, null);
        var key = Encoding.UTF8.GetString(own.MPAI_AIFM_SharedStorage_Get("pseudonym-key"));
        Assert.Equal(64, key.Length);
        new AihProvider(store).Create(AihProvider.Dia, new Dictionary<string, string>(), null, own, null);
        Assert.Equal(key, Encoding.UTF8.GetString(own.MPAI_AIFM_SharedStorage_Get("pseudonym-key")));
        var other = new FileSharedStorage(Path.Combine(work, "dia-other"), "aih-test", AihProvider.Dia);
        new AihProvider(store).Create(AihProvider.Dia, new Dictionary<string, string>(), null, other, null);
        Assert.NotEqual(key, Encoding.UTF8.GetString(other.MPAI_AIFM_SharedStorage_Get("pseudonym-key")));
        Assert.Throws<InvalidOperationException>(() => new AihProvider(store).Create(AihProvider.Dia, new Dictionary<string, string>(), null, null, null));
        Assert.DoesNotContain("mpai-aih-test-pseudonym-key", File.ReadAllText(Path.Combine(Repository.Root, "AIMs", "AIH", "V1.1", "Provider", "AihProvider.cs")));
    }

    // AIF V3.0 MET-3 (A5, 2026-11-13): an L3 declares only what its Implementation supports. For every AIH-HSP V1.1 L3: the folder its ImplementationURI names is in this
    // repository, and the binary the provider reports for the AIM is the BinaryName the L3 gives (a Controller that measures what it runs compares the two). A Composite AIM is an
    // AIM too: it names the binary through which its Sub-AIMs are provided (Mpai.Aih).
    [Fact]
    public void EveryAihL3NamesAnImplementationThatExists()
    {
        var provider = new AihProvider(store);
        var problems = new List<string>();
        const string Repo = "https://github.com/leonardo-chiariglione/mpai-cav/tree/main/";
        foreach (var file in Directory.GetFiles(Repository.Amds, "1AIH-*-V1.1-I01.json"))
        {
            var aim = Path.GetFileNameWithoutExtension(file);
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var impl = doc.RootElement.GetProperty("Implementations")[0];
            var uri = impl.GetProperty("ImplementationURI").GetString() ?? "";
            var named = impl.GetProperty("BinaryName").GetString();
            if (!uri.StartsWith(Repo, StringComparison.Ordinal) || !Directory.Exists(Path.Combine(Repository.Root, uri[Repo.Length..])))
                problems.Add($"{aim}: its ImplementationURI {uri} is not a folder of this repository.");
            if (provider.CanCreate(aim))
            {
                var reported = provider.ImplementationOf(aim) is { } path ? Path.GetFileNameWithoutExtension(path) : null;
                if (reported != named) problems.Add($"{aim}: the provider reports {reported ?? "no binary"}, its L3 names {named}.");
            }
            else if (named != "Mpai.Aih") problems.Add($"{aim}: a Composite AIM of AIH-HSP names {named}, not the binary that provides its Sub-AIMs (Mpai.Aih).");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static string Valid(string schema, string json)
    {
        var s = Schemas[Path.GetFullPath(Path.Combine(Repository.Schemas, "AIH1", "V1.1", "data", schema + ".json"))];
        using var doc = JsonDocument.Parse(json);
        lock (AIF.Metadata.PublishedSchemas.Lock) return s.Evaluate(doc.RootElement).IsValid ? "valid" : "not valid";
    }

    // An electrocardiogram of 16-bit samples at 250 Hz: a spike at each beat, the beats at the given intervals (in seconds), a little noise between.
    private static string Ecg(params double[] intervals)
    {
        const int rate = 250;
        var samples = new short[(int)(intervals.Sum() * rate) + rate];
        var random = new Random(1);
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)random.Next(-30, 30);
        var at = 0.5;
        foreach (var interval in intervals.Prepend(0))
        {
            at += interval;
            var i = (int)(at * rate);
            if (i < samples.Length - 2) { samples[i - 1] = 6000; samples[i] = 12000; samples[i + 1] = 6000; }
        }
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++) { bytes[2 * i] = (byte)(samples[i] & 0xFF); bytes[2 * i + 1] = (byte)((samples[i] >> 8) & 0xFF); }
        return Convert.ToBase64String(bytes);
    }

    private static JsonObject AihDataWith(string ecg, string id = "ahd-1")
    {
        var data = AihSamples.AihData(id: id);
        data["AIHData"]!["HealthData"]!["HealthDataData"] = new JsonArray(new JsonObject { ["Data"] = ecg });
        data["AIHData"]!.AsObject().Remove("SmartContractID"); data["AIHData"]!.AsObject().Remove("DeIDAndAnonym");
        return data;
    }

    private async Task<JsonObject> Alert(JsonObject data)
    {
        var ara = Build(AihProvider.Ara);
        var reply = await ara.ProcessAsync(new Message { MessageId = "m", Ports = new Dictionary<string, string> { [In(AihProvider.Ara, AihJson.AihData)] = data.ToJsonString() } });
        Assert.False(reply.IsError, reply.Payload);
        var json = reply.Ports[Out(AihProvider.Ara, AihJson.AraData)];
        Assert.Equal("valid", Valid("ARAData", json));
        return JsonNode.Parse(json)!.AsObject();
    }

    [Theory]
    [InlineData("Green", 0.8, 0.8, 0.8, 0.8, 0.8, 0.8, 0.8, 0.8, 0.8, 0.8, 0.8, 0.8)]    // 75 beats a minute, regular
    [InlineData("Red", 2.0, 2.0, 2.0, 2.0, 2.0, 2.0)]                                       // 30 a minute
    [InlineData("Red", 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3)] // 200 a minute
    [InlineData("Yellow", 0.8, 0.4, 0.8, 1.2, 0.8, 0.4, 0.8, 1.2, 0.8, 0.4, 0.8, 1.2)]       // the beats are not regular
    public async Task TheRiskFollowsTheHeartRate(string risk, params double[] intervals)
    {
        var result = await Alert(AihDataWith(Ecg(intervals)));
        Assert.Equal(risk, result["ARAData"]!["Risk"]!.ToString());
        Assert.Equal("ahd-1", result["ARAData"]!["AIHDataID"]!.ToString());
        if (risk != "Green") Assert.NotNull(result["ARAData"]!["Anomalies"]!["Anomaly"]);
    }

    [Fact]
    public async Task WithNothingToJudgeTheRiskIsBlue()
    {
        var flat = Convert.ToBase64String(new byte[2000]);
        Assert.Equal("Blue", (await Alert(AihDataWith(flat)))["ARAData"]!["Risk"]!.ToString());
    }

    [Fact]
    public async Task ModelLicensingMakesAihDataThatTheSchemaAccepts()
    {
        var mdl = Build(AihProvider.Mdl);
        var licence = AihSamples.ModelLicence(data: "ahd-9");
        var reply = await mdl.ProcessAsync(new Message
        {
            MessageId = "m",
            Ports = new Dictionary<string, string>
            {
                [In(AihProvider.Mdl, AihJson.HealthData)] = AihSamples.HealthData().ToJsonString(),
                [In(AihProvider.Mdl, AihJson.ModelLicence)] = licence.ToJsonString()
            }
        });
        Assert.False(reply.IsError, reply.Payload);
        var json = reply.Ports[Out(AihProvider.Mdl, AihJson.AihData)];
        Assert.Equal("valid", Valid("AIHData", json));
        var data = JsonNode.Parse(json)!;
        Assert.Equal("ahd-9", data["AIHDataID"]!.ToString());
        Assert.Null(data["AIHData"]!["SmartContractID"]);                                  // the Back End makes the Smart Contract later
    }

    [Fact]
    public async Task ModelLicensingRefusesALicenceOfAnotherEndUser()
    {
        var mdl = Build(AihProvider.Mdl);
        var reply = await mdl.ProcessAsync(new Message
        {
            MessageId = "m",
            Ports = new Dictionary<string, string>
            {
                [In(AihProvider.Mdl, AihJson.HealthData)] = AihSamples.HealthData("user-1").ToJsonString(),
                [In(AihProvider.Mdl, AihJson.ModelLicence)] = AihSamples.ModelLicence("user-2").ToJsonString()
            }
        });
        Assert.True(reply.IsError);
    }

    private static string Request(string user, string type, params string[] ids) => new JsonObject
    {
        ["Header"] = "AIH-AHP-V1.1", ["UserID"] = user, ["AIHDataProcessID"] = "ahp-1", ["AIHDataProcessTime"] = AihSamples.Time("t"),
        ["AIHDataProcess"] = new JsonArray(new JsonObject
        {
            ["Request"] = new JsonObject
            {
                ["AIHDataOrAIHDataID"] = new JsonArray(ids.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()), ["ProcessingType"] = type, ["Tokens"] = AihSamples.Tokens(user)
            }
        })
    }.ToJsonString();


}
