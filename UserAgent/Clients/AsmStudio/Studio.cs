using Orientation = Mpai.Core.Orientation;
using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.SpatialAudio;

namespace Mpai.AsmStudio;

public static class Paths
{
    public static readonly string Root = FindRoot();
    public static string Amds => Path.Combine(Root, "AIMs", "AMDs");
    public static string Settings => Path.Combine(Root, "AIMs", "aim-settings.json");
    public static string Workspace { get; } = Environment.GetEnvironmentVariable("ASMSTUDIO_WORKSPACE") is { Length: > 0 } w ? w : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ASM Studio");

    private static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "AIMs", "aim-settings.json"))) return d.FullName;
        return @"D:\DI";
    }

    // The settings of one AIM, from the settings file of the repository: a file named is given by its full path.
    public static Dictionary<string, string> SettingsOf(string aim)
    {
        var all = JsonNode.Parse(File.ReadAllText(Settings))!;
        return all[aim]!.AsObject().ToDictionary(p => p.Key, p =>
        {
            var v = (string)p.Value!;
            return File.Exists(Path.Combine(Root, v)) ? Path.Combine(Root, v) : v;
        });
    }
}

// What the user calls a thing: a name and a kind, as in a directory - violin.auo, hello.bso, concert.asd.
public sealed class Item
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "auo";            // auo: an audio object; bso: speech; asd: a scene
    public string? Id { get; set; }                       // the Basic Audio Object (auo, bso) or the Basic Audio Scene (asd) in Shared Storage
    public double Seconds { get; set; }
    public string? Text { get; set; }                     // what a speech says
    public string? Language { get; set; }                 // the language it is spoken in (BCP 47, as the voice states it), for a speech
    public string File => Name + "." + Kind;
    // An object held as it was edited - placed, timed, composed - is an Audio Object (AUO); one that was only captured or read is a Basic Audio Object (BAO).
    public bool Audio => Id?.StartsWith("AUO", StringComparison.Ordinal) == true;
    public override string ToString() => File;
}

public sealed class Catalog
{
    public List<Item> Items { get; set; } = [];
    public string? OpenScene { get; set; }

    private static string FilePath => Path.Combine(Paths.Workspace, "catalog.json");
    public static Catalog Load()
    {
        try { if (System.IO.File.Exists(FilePath)) return JsonSerializer.Deserialize<Catalog>(System.IO.File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        return new();
    }
    public void Save()
    {
        Directory.CreateDirectory(Paths.Workspace);
        System.IO.File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
    public Item? ByFile(string file) => Items.FirstOrDefault(i => i.File.Equals(file, StringComparison.OrdinalIgnoreCase));
    public Item? ByName(string name) => Items.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && i.Kind != "asd")
                                     ?? Items.FirstOrDefault(i => i.File.Equals(name, StringComparison.OrdinalIgnoreCase));
    public Item? ByObject(string? id) => id is null ? null : Items.FirstOrDefault(i => i.Kind != "asd" && i.Id == id);
    public string Unique(string name, string kind)
    {
        var candidate = name; var n = 2;
        while (Items.Any(i => i.Kind == kind && i.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))) candidate = $"{name} ({n++})";
        return candidate;
    }
}

// Start: when it begins to sound, in seconds from the start of the scene. Group: the audio object it came from, when that holds more than one sound.
public sealed record Member(string Id, string Name, double X, double Y, double Z, double Yaw, double Start = 0, string? Group = null, double? End = null);
public sealed record Outcome(bool Ok, string Text);

// The user in the scene: where the scene is heard from, and which way the user faces (yaw, degrees: 0 faces ahead, +y).
public sealed record Listener(double X, double Y, double Z, double Yaw);

// THE USER AGENT'S SIDE OF AUDIO SCENE MANAGEMENT. The Module (CAE-ASM-V1.0) does the editing, the understanding of the words, the recognising and the speaking: the
// Controller runs its AIMs and keeps its Shared Storage. This is a User Agent: it gives the Module data and asks it for data, by Data Type and Port Number (ModuleLink),
// and it keeps the names the user gives, the order of the commands, and what the user hears. Every edit of the scene is a User Command, and the Command to revoke is a
// User Command too. It touches no AIM and no storage.
public sealed class Studio : IDisposable
{
    private readonly ModuleLink link;
    private readonly List<string> done = [];                 // the User Commands that can be revoked, the last last
    private readonly SemaphoreSlim turn = new(1, 1);          // one exchange with the editing AIMs at a time
    private readonly SemaphoreSlim voiceTurn = new(1, 1);     // one with the voice and the dialogue
    private readonly Dictionary<string, Item> placedAs = [];     // the sounds that came into the scene inside an audio object, by that audio object
    private readonly Dictionary<string, string> held = [];       // the objects the Module gave, by their identifiers: a version never changes

    public Catalog Catalog { get; }
    public IReadOnlyList<Member> Members { get; private set; } = [];
    public Room? Room { get; private set; }                  // the room the scene is in, if it has one
    public Listener Listener { get; private set; } = new(0, 0, 0, 0);   // where the scene is heard from (its User PoV): the origin, facing ahead, until it is moved
    public AcousticProfile? SceneProfile { get; private set; }   // the acoustics of the scene (the Scene part of the Acoustic Profile), if it has any
    public Acoustics? Acoustics => Mpai.AsmStudio.Acoustics.From(SceneProfile, Room);   // what the scene does to sound: none, unless something says there is reverberation
    public string? SceneJson { get; private set; }
    public bool CanUndo => done.Count > 0;
    public string Model { get; set; } = "llama3.2:3b";
    public event Action? SceneChanged;
    public event Action? CatalogChanged;

    public Studio()
    {
        Directory.CreateDirectory(Paths.Workspace);
        Catalog = Catalog.Load();
        link = ModuleLink.InProcess();
        link.Start(Path.Combine(Paths.Workspace, "storage"));      // the User says where the Module's Shared Storage is; the Controller keeps it there
    }

    // ---- the library ---------------------------------------------------------------------------

    // A sound the user has: a WAV file, named as the file is unless a name is given.
    public async Task<Item> ImportSoundAsync(string wavPath, string? name = null)
    {
        var wav = await File.ReadAllBytesAsync(wavPath);
        var item = new Item { Kind = "auo", Name = Catalog.Unique(name ?? Path.GetFileNameWithoutExtension(wavPath), "auo") };
        item.Id = await CreateObjectAsync(wav, item.Name);
        item.Seconds = Seconds(wav);
        Catalog.Items.Add(item); Catalog.Save(); CatalogChanged?.Invoke();
        return item;
    }

    public async Task<Item> ImportSoundAsync(byte[] wav, string name)
    {
        var item = new Item { Kind = "auo", Name = Catalog.Unique(name, "auo") };
        item.Id = await CreateObjectAsync(wav, item.Name);
        item.Seconds = Seconds(wav);
        Catalog.Items.Add(item); Catalog.Save(); CatalogChanged?.Invoke();
        return item;
    }

    // A file: a WAV sound; or an object as the standards write it - a Basic Audio Object (BAO) or an Audio Object (AUO), whichever the file holds.
    public async Task<Item> ImportFileAsync(string path)
    {
        if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)) return await ImportSoundAsync(path);
        // TEXT TO BE SPOKEN: a plain text file is the text (its language is found out); a Basic Text Object file is the object, its Text Qualifier giving the language.
        if (Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            var words = (await File.ReadAllTextAsync(path)).Trim();
            if (words.Length == 0) throw new InvalidDataException($"{Path.GetFileName(path)} holds no text.");
            return await NewSpeechAsync(words.Length > 4000 ? words[..4000] : words, Path.GetFileNameWithoutExtension(path));
        }
        var json = await File.ReadAllTextAsync(path);
        var header = JsonNode.Parse(json)?["Header"]?.GetValue<string>();
        if (header == "OSD-BTO-V1.5") return await NewSpeechAsync(MpaiJson.FromJson<BasicTextObject>(json), Path.GetFileNameWithoutExtension(path));
        var name = Catalog.Unique(Path.GetFileNameWithoutExtension(path), "auo");
        Item item;
        switch (header)
        {
            case "OSD-BAO-V1.5":
                var bao = MpaiJson.FromJson<BasicAudioObject>(json);
                item = new Item { Kind = "auo", Name = name, Id = await StoreBaoAsync(json), Seconds = Seconds(bao.Data) };
                break;
            case "OSD-AUO-V1.5":
                // An Audio Object read from the user's external storage is given to the Module at its Audio Object Port; the Module stores it (its sounds one by one) and
                // names it. How long it lasts is read from the file, which holds the sounds.
                var read = MpaiJson.FromJson<AudioObject>(json);
                var seconds = SceneAudio.Members(read).Select(m => m.EndSeconds ?? m.StartSeconds + Seconds(m.Object.Data)).DefaultIfEmpty(0).Max();
                var stored = await AudioObjectExchangeAsync(json);
                item = new Item { Kind = "auo", Name = name, Id = (string)JsonNode.Parse(stored)!["AudioObjectID"]!, Seconds = seconds };
                break;
            default: throw new InvalidDataException($"{Path.GetFileName(path)} holds {header ?? "no Header"}: a Basic Audio Object or an Audio Object is read.");
        }
        Catalog.Items.Add(item); Catalog.Save(); CatalogChanged?.Invoke();
        return item;
    }

    // A Basic Audio Object read from a file is a new one here: whatever identifier it came with is another workspace's.
    private async Task<string> StoreBaoAsync(string baoJson)
    {
        var node = JsonNode.Parse(baoJson)!; node["BasicAudioObjectID"] = "";
        return (string)JsonNode.Parse(await ObjectExchangeAsync(node.ToJsonString()))!["BasicAudioObjectID"]!;
    }

    // A few sounds to begin with, made here: a bell, a drum, a hum - and, if the synthesiser is there, someone saying a word.
    public async Task SeedAsync()
    {
        const int rate = 22050;
        byte[] Wav(Func<int, double> sample, double seconds)
        {
            var n = (int)(rate * seconds); var data = new byte[n * 2];
            for (var i = 0; i < n; i++) BitConverter.GetBytes((short)Math.Clamp(sample(i) * 14000, -32000, 32000)).CopyTo(data, i * 2);
            using var ms = new MemoryStream();
            using (var w = new NAudio.Wave.WaveFileWriter(ms, new NAudio.Wave.WaveFormat(rate, 16, 1))) w.Write(data, 0, data.Length);
            return ms.ToArray();
        }
        var noise = new Random(7);
        await ImportSoundAsync(Wav(i => (Math.Sin(2 * Math.PI * 880 * i / rate) + 0.5 * Math.Sin(2 * Math.PI * 1320 * i / rate)) * Math.Exp(-(i % (rate * 2)) / (rate * 0.35)), 4), "bell");
        await ImportSoundAsync(Wav(i => (noise.NextDouble() * 2 - 1) * Math.Exp(-(i % (rate / 2)) / (rate * 0.05)) * 1.4 + Math.Sin(2 * Math.PI * 70 * (i % (rate / 2)) / rate) * Math.Exp(-(i % (rate / 2)) / (rate * 0.12)), 3), "drum");
        await ImportSoundAsync(Wav(i => (Math.Sin(2 * Math.PI * 110 * i / rate) + 0.4 * Math.Sin(2 * Math.PI * 220 * i / rate)) * 0.7, 3), "hum");
        try { await NewSpeechAsync("Welcome. This is a voice in your scene.", "welcome"); } catch { }
    }

    // SPEECH THE USER HAS TYPED (or read from a file): the User Agent acquires the text and the language the user says it is in, and gives them to the Text to Speech
    // AIM as a Basic Text Object whose Text Qualifier states the language; the AIM chooses the voice by it and makes the Basic Speech Object. It is kept as a Speech
    // Object (.bso) and, to be heard in a scene, as the Audio Object of its sound. Where the user states no language the AIM speaks with its default voice.
    public Task<Item> NewSpeechAsync(string text, string name, string? language = null)
    {
        var textObject = BasicTextObject.FromText(text);
        if (!string.IsNullOrWhiteSpace(language))
            textObject = BasicTextObject.FromText(text, new TextQualifier
            {
                TextQualifierID = Guid.NewGuid().ToString(),
                Attributes = new TextAttributes { Language = new Language { LanguageCode = language, LanguageFormat = LanguageFormat.Bcp47 } }
            });
        return NewSpeechAsync(textObject, name);
    }

    // The same, from a Basic Text Object as it comes: its Text Qualifier says the language.
    public async Task<Item> NewSpeechAsync(BasicTextObject textObject, string name)
    {
        var voice = await SpeakAsync(textObject);
        if (voice.Data.Length < 100) throw new InvalidOperationException("The voice made no sound.");
        var spoken = voice.SpeechQualifier?.Attributes?.Metadata?.Language?.LanguageCode ?? textObject.TextQualifier?.Attributes?.Language?.LanguageCode;
        var item = new Item { Kind = "bso", Name = Catalog.Unique(name, "bso"), Text = textObject.GetText(), Language = spoken };
        item.Id = await CreateObjectAsync(voice.Data, item.Name);
        item.Seconds = Seconds(voice.Data);
        Catalog.Items.Add(item); Catalog.Save(); CatalogChanged?.Invoke();
        return item;
    }

    public void Rename(Item item, string name)
    {
        item.Name = Catalog.Items.Any(i => i != item && i.Kind == item.Kind && i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? Catalog.Unique(name, item.Kind) : name;
        Catalog.Save(); CatalogChanged?.Invoke();
        Refresh();
    }

    public void Forget(Item item)
    {
        Catalog.Items.Remove(item); Catalog.Save(); CatalogChanged?.Invoke();
    }

    // The object as the Module holds it (a Basic Audio Object, as JSON), for whoever wants to give it on: asked of the Module, by its identifier.
    public string? ObjectJson(Item item) => item.Id is null || item.Audio ? null : ObjectJsonById(item.Id);

    public byte[]? SoundOf(Item item)
    {
        if (ObjectJson(item) is not { } json) return null;
        try { return MpaiJson.FromJson<BasicAudioObject>(json).Data; }
        catch { return null; }
    }

    // Opening an Object by its identifier is how the Module is asked for it: the Object comes back at the same Port.
    private string? ObjectJsonById(string id)
    {
        lock (held) if (held.TryGetValue(id, out var known)) return known;
        turn.Wait();
        try
        {
            link.DrainEditing();
            link.Write(ModuleLink.Bao, 1, new JsonObject { ["Header"] = "OSD-BAO-V1.5", ["BasicAudioObjectID"] = id }.ToJsonString());
            var json = link.Read(ModuleLink.Bao, 1, 20_000);
            if (json is not null && (string?)JsonNode.Parse(json)?["BasicAudioObjectID"] == id) lock (held) held[id] = json;
            return json;
        }
        finally { turn.Release(); }
    }

    // One exchange with Audio Object Editing at its Audio Object Port: an Audio Object read from outside given, the Audio Object as the Module keeps it back.
    private async Task<string> AudioObjectExchangeAsync(string auoJson)
    {
        await turn.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                link.DrainEditing();
                link.Write(ModuleLink.Auo, 1, auoJson);
                return link.Read(ModuleLink.Auo, 1) ?? throw new InvalidOperationException("Audio Object Editing gave nothing back.");
            });
        }
        finally { turn.Release(); }
    }

    // The Audio Object as the Shared Storage holds it (JSON), for whoever wants to give it on.
    public string? AudioObjectJson(Item item) => item.Id is not null && item.Audio ? AudioObjectJsonById(item.Id) : null;

    // An Audio Object the Module keeps, asked for by its identifier at the Audio Object Port, as a Basic Audio Object is: the Module gives it back as it keeps it.
    private string? AudioObjectJsonById(string id)
    {
        lock (held) if (held.TryGetValue(id, out var known)) return known;
        try
        {
            var json = AudioObjectExchangeAsync(new JsonObject { ["Header"] = "OSD-AUO-V1.5", ["AudioObjectID"] = id }.ToJsonString()).GetAwaiter().GetResult();
            lock (held) held[id] = json;
            return json;
        }
        catch { return null; }
    }

    private AudioObject? AudioObjectIn(string id) => AudioObjectJsonById(id) is { } json ? MpaiJson.FromJson<AudioObject>(json) : null;

    private double SecondsOf(AudioObject auo) =>
        SceneAudio.Members(auo).Select(m => m.EndSeconds ?? m.StartSeconds + (ItemOf(m.Object.BasicAudioObjectID)?.Seconds ?? 0)).DefaultIfEmpty(0).Max();

    // One exchange with Audio Object Editing: a Basic Audio Object given, the Object as the Module holds it back.
    private async Task<string> ObjectExchangeAsync(string baoJson)
    {
        await turn.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                link.DrainEditing();
                link.Write(ModuleLink.Bao, 1, baoJson);
                return link.Read(ModuleLink.Bao, 1) ?? throw new InvalidOperationException("Audio Object Editing gave nothing back.");
            });
        }
        finally { turn.Release(); }
    }

    private async Task<string> CreateObjectAsync(byte[] wav, string name)
    {
        var (rate, bits, channels) = Format(wav);
        var qualifier = new AudioQualifier
        {
            AudioQualifierID = Guid.NewGuid().ToString(), SubTypes = channels == 1 ? "Mono" : "Stereo",
            Formats = new AudioFormats
            {
                ContentFormat = new AudioContentFormat { RawData = new AudioRawData { SampleSpace = new Pcm { SamplingFrequency = rate, Precision = bits } } },
                TransportFormat = new AudioTransportFormat { FileFormats = AudioFileFormat.Wav }
            }
        };
        var made = BasicAudioObject.FromData(wav, qualifier);
        var bao = new BasicAudioObject { BasicAudioObjectID = made.BasicAudioObjectID, BasicAudioObjectData = made.BasicAudioObjectData, AudioQualifier = made.AudioQualifier, DescrMetadata = name, MInstanceID = "ASM" };
        return (string)JsonNode.Parse(await ObjectExchangeAsync(MpaiJson.ToJson(bao)))!["BasicAudioObjectID"]!;
    }

    public static (int Rate, int Bits, int Channels) Format(byte[] wav)
    {
        if (wav.Length < 44) return (48000, 16, 1);
        return (BitConverter.ToInt32(wav, 24), BitConverter.ToInt16(wav, 34), BitConverter.ToInt16(wav, 22));
    }
    public static double Seconds(byte[] wav)
    {
        var (rate, bits, channels) = Format(wav);
        return rate <= 0 || bits <= 0 || channels <= 0 ? 0 : Math.Max(0, wav.Length - 44) / (double)(rate * bits / 8 * channels);
    }

    // ---- the scene: commands ---------------------------------------------------------------------

    // A time in the scene's own seconds (relative): when the member begins to sound.
    // From the second it begins to the second it ends; where it states no end, the end is the start (an instant) and the sound plays to its own end.
    private static SimpleTime StartTime(double seconds, double? end = null) => new()
    {
        SimpleTimeID = Guid.NewGuid().ToString(),
        SimpleTimeData = { new TimeSegment { FlagsByte = 0, StartTime = seconds, EndTime = end is { } e && e > seconds ? e : seconds, AccuracyMode = "single", AccuracyPlusMinus = 0.0, TimeType = false, TimeUnit = "00" } }
    };

    private static SpatialAttitude At(double x, double y, double z, double yaw, double? start = null, double? end = null) => new()
    {
        ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
        SpatialAttitudeTime = start is { } s ? StartTime(s, end) : null,
        Position = new Position { PositionID = Guid.NewGuid().ToString(), CartPosition = [x, y, z] },
        Orientation = new Orientation { OrientationID = Guid.NewGuid().ToString(), EulerAngles = [0, 0, yaw] }
    };

    private static SpaceTime PlacedAt(double x, double y, double z, double yaw, double start, double? end = null) => new() { SpatialAttitude1 = At(x, y, z, yaw), Time = StartTime(start, end) };

    private static UserCommand Command(UserCommandData data) => new()
    {
        UserCommandID = "C" + Guid.NewGuid().ToString("N")[..10], UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow), UserCommandData = data
    };

    // Puts the object of the library in the scene - facing the listener - where the user says.
    // A basic audio object is put where it is said (x, y; or in front of the listener where nothing is said). An audio object (AUO) brings its own
    // places and times: where it is said, the first of its sounds goes there and the others keep their places around it.
    public Task<Outcome> AddAsync(Item item, double? x, double? y, double z = 0, double yaw = 180)
    {
        if (item.Id is null) return Task.FromResult(new Outcome(false, $"{item.File} is not a sound."));
        if (!item.Audio)
        {
            if (Members.Any(m => m.Id == item.Id || (placedAs.TryGetValue(m.Id, out var held) && held == item))) return Task.FromResult(new Outcome(false, $"{item.File} is already in the scene."));
            double px = x ?? 0, py = y ?? 2;
            placedAs.Remove(item.Id);                            // put in as itself, not as part of an audio object it was once in
            return RunAsync(Command(new UserCommandData
            {
                AddedObjects = new ObjectPlacements { Objects = [new ObjectPlacement { ObjectID = new ManagedObject { ObjectID = item.Id }, SpatialAttitude = At(px, py, z, yaw, 0) }] }
            }), $"{item.File} is in the scene, {Describe(px, py)}.");
        }

        List<SceneAudio.Member> parts;
        try { parts = AudioObjectIn(item.Id) is { } held ? SceneAudio.Members(held).ToList() : []; }
        catch (Exception e) { return Task.FromResult(new Outcome(false, $"{item.File} cannot be read: {e.Message}")); }
        if (parts.Count == 0) return Task.FromResult(new Outcome(false, $"{item.File} holds no sound, or is not in the Shared Storage."));
        if (parts.Any(p => Members.Any(m => m.Id == p.Object.BasicAudioObjectID))) return Task.FromResult(new Outcome(false, $"{item.File} is already in the scene."));
        double dx = x is null ? 0 : x.Value - parts[0].Position[0], dy = y is null ? 0 : y.Value - parts[0].Position[1];
        foreach (var p in parts) placedAs[p.Object.BasicAudioObjectID] = item;
        var placed = parts.Select(p => new ObjectPlacement
        {
            ObjectID = new ManagedObject { ObjectID = p.Object.BasicAudioObjectID },
            SpatialAttitude = At(p.Position[0] + dx, p.Position[1] + dy, p.Position[2], p.YawDeg, p.StartSeconds, p.EndSeconds)
        }).ToList();
        var first = placed[0].SpatialAttitude!.Position.CartPosition;
        return RunAsync(Command(new UserCommandData { AddedObjects = new ObjectPlacements { Objects = placed } }), $"{item.File} is in the scene, {Describe(first[0], first[1])}.");
    }

    // The sounds that move together: the member, or all the sounds of the audio object it came from.
    private List<Member> GroupOf(Member m) => m.Group is null ? [m] : Members.Where(o => o.Group == m.Group).ToList();

    // Moves, turns, or times the member (and with it the sounds of its audio object): to (x, y, z), turned to yaw, beginning at start seconds.
    public Task<Outcome> MoveAsync(Member m, double x, double y, double? z = null, double? yaw = null, double? start = null, string? said = null, double? end = null, bool noEnd = false)
    {
        double dx = x - m.X, dy = y - m.Y, dz = (z ?? m.Z) - m.Z, dyaw = (yaw ?? m.Yaw) - m.Yaw, dstart = (start ?? m.Start) - m.Start;
        // the end stays where it is in the scene's time, unless said (or cleared); an end that would come before the start is dropped
        var moved = GroupOf(m).Select(o =>
        {
            var begins = Math.Max(0, o.Start + dstart);
            var ends = noEnd ? null : end ?? o.End;
            if (ends is { } e && e <= begins) ends = null;
            return new ObjectMovement
            {
                OldSpatialAttitude = At(o.X, o.Y, o.Z, o.Yaw, o.Start, o.End),          // the Object is the one at its PoV: no identifier
                NewSpatialAttitude = At(o.X + dx, o.Y + dy, o.Z + dz, o.Yaw + dyaw, begins, ends)
            };
        }).ToList();
        return RunAsync(Command(new UserCommandData { MovedObjects = new ObjectMovements { Objects = moved } }), said ?? $"{m.Name} moved, {Describe(x, y)}.");
    }

    // WHEN IT SOUNDS: from the second it starts (or that many seconds later than now, when relative) to the second it ends; the sound is cut there.
    public Task<Outcome> TimeAsync(Member m, double? start, bool startRelative, double? end, bool noEnd = false)
    {
        var begins = start is null ? m.Start : Math.Max(0, startRelative ? m.Start + start.Value : start.Value);
        if (end is { } e && e <= begins) return Task.FromResult(new Outcome(false, $"{m.Name} cannot end at {e:0.##} s: it starts at {begins:0.##} s."));
        var text = (start, end, noEnd) switch
        {
            (not null, not null, _) => $"{m.Name} sounds from {begins:0.##} s to {end:0.##} s.",
            (not null, null, _) => $"{m.Name} starts at {begins:0.##} s.",
            (null, not null, _) => $"{m.Name} ends at {end:0.##} s.",
            _ => $"{m.Name} plays to its end."
        };
        return MoveAsync(m, m.X, m.Y, m.Z, null, begins, text, end, noEnd);
    }

    public Task<Outcome> StartAsync(Member m, double seconds, bool relative) => TimeAsync(m, seconds, relative, null);
    public Task<Outcome> EndAsync(Member m, double seconds) => TimeAsync(m, null, false, seconds);

    public Task<Outcome> RemoveAsync(Member m) =>
        RunAsync(Command(new UserCommandData { RemovedObjects = new ObjectPlacements { Objects = GroupOf(m).Select(o => new ObjectPlacement { SpatialAttitude = At(o.X, o.Y, o.Z, o.Yaw, o.Start, o.End) }).ToList() } }),
                 $"{m.Name} is out of the scene.");

    // A ROOM ROUND THE OBJECTS: the box of their places with a margin of floor all round, and a height - the distance between floor and ceiling - of at least minHeight
    // (higher where a sound is higher). The room replaces the one the scene had. The distance asked for is the height; round the sides there is only the margin.
    public Task<Outcome> BuildRoomAsync(IReadOnlyList<Member> around, double margin, double minHeight)
    {
        if (around.Count == 0) return Task.FromResult(new Outcome(false, "There is nothing in the scene to enclose."));
        double x0 = around.Min(m => m.X) - margin, x1 = around.Max(m => m.X) + margin, y0 = around.Min(m => m.Y) - margin, y1 = around.Max(m => m.Y) + margin;
        if (x1 - x0 < 2) { var c = (x0 + x1) / 2; (x0, x1) = (c - 1, c + 1); }
        if (y1 - y0 < 2) { var c = (y0 + y1) / 2; (y0, y1) = (c - 1, c + 1); }
        var room = new Room(x0, y0, x1, y1, Math.Max(minHeight, around.Max(m => m.Z) + 0.5), 0.15);
        return RunAsync(Command(new UserCommandData { ClosedSpace = new JsonArray(room.ToClosedSpace()) }),
                        $"The room is built: {room.Width:0.#} m by {room.Depth:0.#} m, {room.Height:0.#} m high.");
    }

    // ---- the Acoustic Profile: what its fields enable, field by field ------------------------------------------------------
    //
    // THE OBJECT'S (Audio Object Editing: ModifiedObjects):  FrequencyRange, Spectrogram, Loudness, DirectionalPatterns.
    // THE SCENE'S (Audio Scene Editing: ModifiedScene):      Reflectivity, Reverberation, Diffusion, Absorption, Doppler.
    // A command changes one field and keeps the others; the profile sent is the whole of the new one.

    private Item? ItemOf(string id) => placedAs.GetValueOrDefault(id) ?? Catalog.ByObject(id);

    // The Basic Audio Object a member is, as it is kept.
    public BasicAudioObject? ObjectOf(Member m) => ObjectById(m.Id);

    private BasicAudioObject? ObjectById(string id) => ObjectJsonById(id) is { } json ? MpaiJson.FromJson<BasicAudioObject>(json) : null;

    // WHAT AUDIO OBJECT EDITING DOES, AND THE SCENE FOLLOWS: the Module opens the object and edits it (a new version of it); the scene then has the new version in
    // the place of the old one, in one command - so that undo gives the old one back.
    private async Task<Outcome> EditObjectAsync(Member m, UserCommandData data, string said)
    {
        string newId;
        await turn.WaitAsync();
        try
        {
            var (reportJson, objectJson) = await Task.Run(() =>
            {
                link.DrainEditing();
                link.Write(ModuleLink.Bao, 1, new JsonObject { ["Header"] = "OSD-BAO-V1.5", ["BasicAudioObjectID"] = m.Id }.ToJsonString());
                link.Read(ModuleLink.Bao, 1);                                         // the Object, open
                link.Write(ModuleLink.Ucm, 1, MpaiJson.ToJson(Command(data)));
                var report = link.Read(ModuleLink.Ucm, 1);
                return (report, report is null ? null : link.Read(ModuleLink.Bao, 1));
            });
            var report = reportJson is null ? null : MpaiJson.FromJson<UserCommand>(reportJson).UserCommandReport;
            if (report is not { Outcome: "Done" or "Partly" } || objectJson is null) return new Outcome(false, report?.Actions.FirstOrDefault(a => a.Outcome != "Done")?.Reason ?? "That could not be done.");
            newId = (string)JsonNode.Parse(objectJson)!["BasicAudioObjectID"]!;
            lock (held) held[newId] = objectJson;
        }
        finally { turn.Release(); }
        if (ItemOf(m.Id) is { } via) placedAs[newId] = via;
        return await RunAsync(Command(new UserCommandData
        {
            AddedObjects = new ObjectPlacements { Objects = [new ObjectPlacement { ObjectID = new ManagedObject { ObjectID = newId }, SpatialAttitude = At(m.X, m.Y, m.Z, m.Yaw, m.Start, m.End) }] },
            RemovedObjects = new ObjectPlacements { Objects = [new ObjectPlacement { ObjectID = new ManagedObject { ObjectID = m.Id } }] }
        }), said);
    }

    // The profile an object has, or one to begin with: the fields the schema requires.
    private AcousticProfile ProfileOf(Member m)
    {
        var bao = ObjectById(m.Id);
        return bao?.BasicAudioObjectProperties?.AcousticProfile ?? new AcousticProfile
        {
            AcousticProfileID = Guid.NewGuid().ToString(), FrequencyRange = new FrequencyRange { MinFrequencyHz = 20, MaxFrequencyHz = 20000 },
            Loudness = bao?.BasicAudioObjectProperties?.Level ?? (bao is null ? -23 : SceneAudio.Lufs(SceneAudio.Mono(bao.Data, 48000)))
        };
    }

    // The profile with one field changed, a new identifier, and the required fields there.
    private static AcousticProfile Changed(AcousticProfile p, FrequencyRange? range = null, Plot? spectrogram = null, double? loudness = null, Plot? directional = null,
                                           List<Reflectivity>? reflectivity = null, List<Reverberation>? reverberation = null, double? diffusion = null, double? absorption = null, List<Doppler>? doppler = null) => new()
    {
        AcousticProfileID = Guid.NewGuid().ToString(), MInstanceID = p.MInstanceID, DescrMetadata = p.DescrMetadata,
        FrequencyRange = range ?? p.FrequencyRange ?? new FrequencyRange { MinFrequencyHz = 20, MaxFrequencyHz = 20000 },
        Spectrogram = spectrogram ?? p.Spectrogram, Loudness = loudness ?? p.Loudness ?? -23, DirectionalPatterns = directional ?? p.DirectionalPatterns,
        Reflectivity = reflectivity ?? p.Reflectivity, Reverberation = reverberation ?? p.Reverberation, Diffusion = diffusion ?? p.Diffusion, Absorption = absorption ?? p.Absorption, Doppler = doppler ?? p.Doppler
    };

    private Task<Outcome> ObjectProfileAsync(Member m, Func<AcousticProfile, AcousticProfile> change, string said) =>
        EditObjectAsync(m, new UserCommandData { ModifiedObjects = new ObjectChanges { Objects = [new ObjectChange { ObjectID = new ManagedObject { ObjectID = m.Id }, AcousticProfile = change(ProfileOf(m)) }] } }, said);

    private Task<Outcome> SceneProfileAsync(Func<AcousticProfile, AcousticProfile> change, string said)
    {
        if (SceneJson is null) return Task.FromResult(new Outcome(false, "There is no scene yet."));
        var current = SceneProfile ?? new AcousticProfile { AcousticProfileID = Guid.NewGuid().ToString() };
        return RunAsync(Command(new UserCommandData { ModifiedScene = new SceneChange { AcousticProfile = change(current) } }), said);
    }

    // --- the Object part ---

    // Loudness: what the object is, or is to be, in LUFS - louder or quieter by so many dB, or set to so many.
    public double LoudnessOf(Member m) => ProfileOf(m).Loudness ?? -23;

    public Task<Outcome> LoudnessAsync(Member m, double? change, double? lufs)
    {
        var target = lufs ?? LoudnessOf(m) + (change ?? 0);
        var said = lufs is not null ? $"{m.Name} is set to {target:0.#} LUFS." : $"{m.Name} is {Math.Abs(change ?? 0):0.#} dB {(change >= 0 ? "louder" : "quieter")}, at {target:0.#} LUFS.";
        return ObjectProfileAsync(m, p => Changed(p, loudness: target), said);
    }

    public static readonly string[] Patterns = ["omnidirectional", "cardioid", "bidirectional"];

    // DirectionalPatterns: how an object radiates - gain in dB by azimuth and elevation, relative to where the object faces: the same in every direction, mostly forward
    // (cardioid), or forward and back (bidirectional, a figure of eight).
    public static Plot PatternPlot(string name)
    {
        static double Db(double x) => 20 * Math.Log10(Math.Max(0.01, x));
        double Gain(double az, double el)
        {
            double a = az * Math.PI / 180, e = el * Math.PI / 180;
            return name switch
            {
                "cardioid" => Db((1 + Math.Cos(a) * Math.Cos(e)) / 2),
                "bidirectional" => Db(Math.Abs(Math.Cos(a) * Math.Cos(e))),
                _ => 0
            };
        }
        var rows = new List<List<double>>();
        foreach (var el in new[] { -60.0, 0.0, 60.0 })
            for (var az = -180; az < 180; az += 30) rows.Add([az, el, Math.Round(Gain(az, el), 1)]);
        return new Plot { PlotID = Guid.NewGuid().ToString(), DescrMetadata = name, Data = new PlotData { TwoD = rows } };
    }

    public Task<Outcome> DirectionAsync(Member m, string pattern)
    {
        var said = pattern == "omnidirectional" ? $"{m.Name} radiates in every direction." : $"{m.Name} radiates {(pattern == "cardioid" ? "mostly forward (cardioid)" : "forward and back (bidirectional)")}.";
        return ObjectProfileAsync(m, p => Changed(p, directional: PatternPlot(pattern)), said);
    }

    // FrequencyRange: what lies outside it is taken away when the object is heard. One bound may be left as it is.
    public Task<Outcome> FrequencyRangeAsync(Member m, double? min, double? max)
    {
        var current = ProfileOf(m).FrequencyRange ?? new FrequencyRange { MinFrequencyHz = 20, MaxFrequencyHz = 20000 };
        var range = new FrequencyRange { MinFrequencyHz = min ?? current.MinFrequencyHz, MaxFrequencyHz = max ?? current.MaxFrequencyHz };
        if (range.MaxFrequencyHz <= range.MinFrequencyHz) return Task.FromResult(new Outcome(false, $"The range {range.MinFrequencyHz:0} to {range.MaxFrequencyHz:0} Hz is empty."));
        return ObjectProfileAsync(m, p => Changed(p, range: range), $"{m.Name} is limited to {range.MinFrequencyHz:0} to {range.MaxFrequencyHz:0} Hz.");
    }

    // Spectrogram: measured from the sound - its level (dB) in octave bands, every half second: rows of [time (s), frequency (Hz), level (dB)].
    public static Plot SpectrogramPlot(float[] mono, int rate)
    {
        var rows = new List<List<double>>(); var hop = rate / 2; double[] bands = [125, 250, 500, 1000, 2000, 4000, 8000, 16000];
        for (var start = 0; start < mono.Length && rows.Count < 8 * 40; start += hop)
        {
            var n = Math.Min(hop, mono.Length - start); if (n < 256) break;
            foreach (var f in bands)
            {
                if (f >= rate / 2.0) continue;
                double re = 0, im = 0, w = 2 * Math.PI * f / rate;
                for (var i = 0; i < n; i++) { var window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)); re += mono[start + i] * window * Math.Cos(w * i); im -= mono[start + i] * window * Math.Sin(w * i); }
                var magnitude = 2 * Math.Sqrt(re * re + im * im) / n;
                rows.Add([Math.Round((double)start / rate, 2), f, Math.Round(20 * Math.Log10(magnitude + 1e-6), 1)]);
            }
        }
        return new Plot { PlotID = Guid.NewGuid().ToString(), DescrMetadata = "level in octave bands, every half second: [seconds, Hz, dB]", Data = new PlotData { TwoD = rows } };
    }

    public Task<Outcome> SpectrogramAsync(Member m)
    {
        var bao = ObjectById(m.Id);
        if (bao is null) return Task.FromResult(new Outcome(false, $"{m.Name} cannot be read."));
        var plot = SpectrogramPlot(SceneAudio.Mono(bao.Data, 48000), 48000);
        return ObjectProfileAsync(m, p => Changed(p, spectrogram: plot), $"The spectrogram of {m.Name} is measured: {plot.Data!.TwoD!.Count} points.");
    }

    // --- the Scene part ---

    // Reflectivity: when the early and the late reflections come (seconds).
    public Task<Outcome> ReflectivityAsync(double? early, double? late)
    {
        var current = SceneProfile?.Reflectivity?.FirstOrDefault();
        var one = new Reflectivity { EarlyReflectionTime = early ?? current?.EarlyReflectionTime ?? 0.02, LateReflectionTime = late ?? current?.LateReflectionTime ?? 0.08 };
        return SceneProfileAsync(p => Changed(p, reflectivity: [one]), $"The early reflections come at {one.EarlyReflectionTime * 1000:0.#} ms, the late ones at {one.LateReflectionTime * 1000:0.#} ms.");
    }

    // THE ROOM RESIZED, MOVED, OR MADE HIGHER OR LOWER: the same room with another box (and height), as a command - a Closed Space with the same walls.
    public Task<Outcome> ResizeRoomAsync(double x0, double y0, double x1, double y1, double? height = null)
    {
        if (Room is not { } room) return Task.FromResult(new Outcome(false, "There is no room yet."));
        if (x1 - x0 < 0.5 || y1 - y0 < 0.5) return Task.FromResult(new Outcome(false, "A room cannot be smaller than half a metre."));
        var changed = room with { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Height = Math.Clamp(height ?? room.Height, 0.5, 100) };
        return RunAsync(Command(new UserCommandData { ClosedSpace = new JsonArray(changed.ToClosedSpace()) }),
                        $"The room is {changed.Width:0.#} m by {changed.Depth:0.#} m, {changed.Height:0.#} m high.");
    }

    // THE USER MOVED: the scene is heard from the new place and the new way of facing - a User PoV, a command of Audio Scene Editing like the rest.
    public Task<Outcome> ListenerAsync(double x, double y, double z, double yaw)
    {
        if (SceneJson is null) return Task.FromResult(new Outcome(false, "There is no scene yet."));
        var pov = new PointOfView { PointOfViewID = Guid.NewGuid().ToString(), CartPosition = [x, y, z], Orientation = [0, 0, yaw] };
        return RunAsync(Command(new UserCommandData { UserPoV = pov }), $"You are at {x:0.##}, {y:0.##} m, facing {yaw:0}°.");
    }

    // Reverberation: the time the sound takes to die away - RT60, RT30 or RT20 (a plot of seconds by frequency; one value here, for every frequency), or the EDT (seconds).
    // "More" or "less" reverberation is the RT60 made longer or shorter.
    public Task<Outcome> ReverbAsync(string kind, double? seconds, double? factor)
    {
        if (seconds is null && factor is not null)
        {
            if (Acoustics is not { } now) return Task.FromResult(new Outcome(false, "Say the reverberation time, for example: set the reverberation time to 1.5 seconds."));
            seconds = now.Rt60 / factor.Value; kind = "RT60";
        }
        if (seconds is null) return Task.FromResult(new Outcome(false, "Say how long, for example: set the reverberation time to 1.5 seconds."));
        var value = Math.Clamp(seconds.Value, 0.1, 8);
        var current = SceneProfile?.Reverberation?.FirstOrDefault();
        Plot P() => new() { PlotID = Guid.NewGuid().ToString(), DescrMetadata = "seconds by frequency: [Hz, s]", Data = new PlotData { TwoD = [[500, value]] } };
        var next = new Reverberation
        {
            RT60 = kind == "RT60" ? P() : current?.RT60, RT30 = kind == "RT30" ? P() : current?.RT30, RT20 = kind == "RT20" ? P() : current?.RT20, EDT = kind == "EDT" ? value : current?.EDT
        };
        return SceneProfileAsync(p => Changed(p, reverberation: [next]), $"The {kind} of the scene is {value:0.##} s.");
    }

    public Task<Outcome> DiffusionAsync(double value) =>
        SceneProfileAsync(p => Changed(p, diffusion: Math.Max(0, value)), $"The diffusion of the scene is {Math.Max(0, value):0.##}.");

    public Task<Outcome> AbsorptionAsync(double value) =>
        SceneProfileAsync(p => Changed(p, absorption: Math.Max(0, value)), $"The absorption of the scene is {Math.Max(0, value):0.##}.");

    // Doppler: the factor of the direct sound (0 to 1) and of the indirect sound. Kept in the profile; nothing moves in the scene, so nothing is heard of it.
    public Task<Outcome> DopplerAsync(double? direct, double? indirect)
    {
        var current = SceneProfile?.Doppler?.FirstOrDefault();
        var one = new Doppler { DirectSoundFactor = Math.Clamp(direct ?? current?.DirectSoundFactor ?? 1, 0, 1), IndirectSound = Math.Max(0, indirect ?? current?.IndirectSound ?? 1) };
        return SceneProfileAsync(p => Changed(p, doppler: [one]), $"The Doppler effect of the scene: direct sound {one.DirectSoundFactor:0.##}, indirect sound {one.IndirectSound:0.##}.");
    }

    // The profile of an object, or of the scene, in words: what the AIMs keep of it.
    public string ProfileText(Member m)
    {
        var bao = ObjectById(m.Id); var profile = bao?.BasicAudioObjectProperties?.AcousticProfile;
        var measured = bao is null ? double.NaN : SceneAudio.Lufs(SceneAudio.Mono(bao.Data, 48000));
        var direction = profile?.DirectionalPatterns?.DescrMetadata ?? (profile?.DirectionalPatterns is null ? "omnidirectional" : "a pattern of its own");
        var span = m.End is { } e ? $"sounds from {m.Start:0.##} s to {e:0.##} s" : $"starts at {m.Start:0.##} s";
        return $"{m.Name}: Loudness {(profile?.Loudness is { } l ? $"{l:0.#} LUFS (it is {measured:0.#} LUFS as it is)" : $"{measured:0.#} LUFS (as it is)")}; DirectionalPatterns {direction}; FrequencyRange {profile?.FrequencyRange?.MinFrequencyHz ?? 20:0} to {profile?.FrequencyRange?.MaxFrequencyHz ?? 20000:0} Hz; Spectrogram {(profile?.Spectrogram is { } s ? $"{s.Data?.TwoD?.Count ?? 0} points" : "not measured")}; {span}.";
    }

    public string SceneProfileText()
    {
        var p = SceneProfile; var r = p?.Reverberation?.FirstOrDefault(); var rf = p?.Reflectivity?.FirstOrDefault(); var d = p?.Doppler?.FirstOrDefault();
        static string Seconds(Plot? plot) => plot?.Data?.TwoD is { Count: > 0 } rows && rows[0].Count >= 2 ? $"{rows[0][1]:0.##} s" : "not set";
        return $"The scene: Reflectivity {(rf is null ? "not set" : $"early {rf.EarlyReflectionTime * 1000:0.#} ms, late {rf.LateReflectionTime * 1000:0.#} ms")}; Reverberation RT60 {Seconds(r?.RT60)}, RT30 {Seconds(r?.RT30)}, RT20 {Seconds(r?.RT20)}, EDT {(r?.EDT is { } e ? $"{e:0.##} s" : "not set")}; "
             + $"Diffusion {(p?.Diffusion is { } df ? $"{df:0.##}" : "not set")}; Absorption {(p?.Absorption is { } ab ? $"{ab:0.##}" : "not set")}; Doppler {(d is null ? "not set" : $"direct {d.DirectSoundFactor:0.##}, indirect {d.IndirectSound:0.##}")}."
             + (Acoustics is { } a ? $" Heard with a tail of {a.Rt60:0.0} s." : " No reverberation is heard.");
    }

    // THE OBJECT ON STAGE, AS THE COMMANDS LEFT IT, kept under a name: where it is and when it begins, with its sound(s) - an Audio Object. The original is untouched.
    // The Module makes the Audio Object (Audio Object Editing: AddedObjects, each with its place and time) and names it in its report; the User Agent keeps the name.
    public Item? SaveObject(Member m, string name)
    {
        var group = GroupOf(m);
        var command = Command(new UserCommandData
        {
            AddedObjects = new ObjectPlacements { Objects = group.Select(o => new ObjectPlacement { ObjectID = new ManagedObject { ObjectID = o.Id }, SpatialAttitude = At(o.X, o.Y, o.Z, o.Yaw, o.Start, o.End) }).ToList() }
        });
        string? reportJson;
        turn.Wait();
        try
        {
            link.DrainEditing();
            link.Write(ModuleLink.Ucm, 1, MpaiJson.ToJson(command));
            reportJson = link.Read(ModuleLink.Ucm, 1);
        }
        finally { turn.Release(); }
        var action = reportJson is null ? null : MpaiJson.FromJson<UserCommand>(reportJson).UserCommandReport?.Actions.FirstOrDefault(a => a.Action == "AddedObjects");
        if (action is not { Outcome: "Done", ResultID: { Length: > 0 } made }) throw new InvalidOperationException(action?.Reason ?? "The Module did not make the Audio Object.");
        var seconds = group.Max(o => o.End ?? (ItemOf(o.Id)?.Seconds ?? 0) + o.Start);
        var item = new Item { Kind = "auo", Name = Catalog.Unique(name, "auo"), Id = made, Seconds = seconds };
        Catalog.Items.Add(item); Catalog.Save(); CatalogChanged?.Invoke();
        return item;
    }

    // A command of the dialogue AIM, as it understood it.
    public Task<Outcome> ExecuteAsync(UserCommand command) => RunAsync(command, null);

    // Revokes the last command the AIMs did. Whether it could be undone is in the report.
    public async Task<Outcome> UndoAsync()
    {
        if (done.Count == 0) return new Outcome(false, "There is nothing to undo.");
        var last = done[^1];
        var result = await RunAsync(Command(new UserCommandData { RevokedUserCommandID = last }), null, record: false);
        if (result.Ok) done.RemoveAt(done.Count - 1);
        return result.Ok ? new Outcome(true, "Undone.") : result;
    }

    private async Task<Outcome> RunAsync(UserCommand command, string? said, bool record = true)
    {
        await turn.WaitAsync();
        try
        {
            // The command at the Port of Audio Scene Editing's User Command (2); the report comes back at the same number, and the Scene at Basic Audio Scene 1.
            var (reportJson, scene) = await Task.Run(() =>
            {
                link.DrainEditing();
                link.Write(ModuleLink.Ucm, 2, MpaiJson.ToJson(command));
                var report = link.Read(ModuleLink.Ucm, 2);
                var parsed = report is null ? null : MpaiJson.FromJson<UserCommand>(report).UserCommandReport;
                var ok = parsed is { Outcome: "Done" or "Partly" };
                return (report, SceneFrom(ok ? 60_000 : 1_500, parsed?.Actions.LastOrDefault(a => a.ResultID is { Length: > 0 })?.ResultID));
            });
            ReadScene(scene);
            var report = reportJson is null ? null : MpaiJson.FromJson<UserCommand>(reportJson).UserCommandReport;
            var ok = report is { Outcome: "Done" or "Partly" };
            if (ok && record) done.Add(command.UserCommandID);
            if (record && ok) Catalog.OpenScene = SceneId(); Catalog.Save();
            var reason = report?.Actions.FirstOrDefault(a => a.Outcome != "Done")?.Reason;
            return new Outcome(ok, ok ? (said ?? "Done.") : (reason ?? (reportJson is null ? "The Module did not answer." : "That could not be done.")));
        }
        finally { turn.Release(); }
    }

    // The Scene the Module gave after the exchange. Audio Scene Editing also gives the open Scene when Audio Object Editing gives it an Object, so Scenes may be waiting
    // that are not of this exchange: the one wanted is the one the report names; failing it, the last there is.
    private string? SceneFrom(int milliseconds, string? wanted)
    {
        string? last = null;
        for (var i = 0; i < 8; i++)
        {
            var json = link.Read(ModuleLink.Bas, 1, i == 0 ? milliseconds : 1_500);
            if (json is null) break;
            last = json;
            if (wanted is null || (string?)JsonNode.Parse(json)?["BasicAudioSceneDescriptorsID"] == wanted) break;
        }
        return last;
    }

    private string? SceneId() => SceneJson is null ? null : (string?)JsonNode.Parse(SceneJson)?["BasicAudioSceneDescriptorsID"];

    private void ReadScene(string? json)
    {
        SceneJson = json;
        Room = json is null ? null : Room.From(JsonNode.Parse(json));
        SceneProfile = json is null ? null : MpaiJson.FromJson<BasicAudioSceneDescriptors>(json).AcousticProfile;
        var pov = json is null ? null : MpaiJson.FromJson<BasicAudioSceneDescriptors>(json).UserPoV;
        double[] earAt = pov?.CartPosition is { Length: >= 3 } earPlace ? earPlace : new double[3];
        Listener = new Listener(earAt[0], earAt[1], earAt[2], pov?.Orientation is { Length: >= 3 } earTurn ? earTurn[2] : 0);
        if (json is null) Members = [];
        else
        {
            var bas = MpaiJson.FromJson<BasicAudioSceneDescriptors>(json);
            var list = new List<Member>(); var seen = new Dictionary<string, int>();
            foreach (var e in bas.BasicAudioSceneDescriptorsEntries)
            {
                if (e.AudioObjectIDOrAudioObject is not { } bao) continue;
                var sa = e.AudioObjectSpaceTime?.SpatialAttitude1;
                var p = sa?.Position?.CartPosition is { Length: >= 3 } c ? c : new double[3];
                var yaw = sa?.Orientation?.EulerAngles is { Length: >= 3 } a ? a[2] : 0;
                var start = SceneAudio.StartOf(e.AudioObjectSpaceTime); var end = SceneAudio.EndOf(e.AudioObjectSpaceTime);
                if (placedAs.TryGetValue(bao.BasicAudioObjectID, out var via))
                {
                    var k = seen[via.File] = seen.GetValueOrDefault(via.File) + 1;
                    list.Add(new Member(bao.BasicAudioObjectID, k == 1 ? via.File : $"{via.File} ({k})", p[0], p[1], p[2], yaw, start, via.File, end));
                }
                else
                {
                    var item = Catalog.ByObject(bao.BasicAudioObjectID);
                    list.Add(new Member(bao.BasicAudioObjectID, item?.File ?? bao.DescrMetadata ?? bao.BasicAudioObjectID, p[0], p[1], p[2], yaw, start, null, end));
                }
            }
            Members = list;
        }
        SceneChanged?.Invoke();
    }

    // Names may have changed: the same scene, named again.
    public void Refresh() => ReadScene(SceneJson);

    // ---- the scene: saving and opening ------------------------------------------------------------

    public Item? SaveScene(string name)
    {
        var id = SceneId();
        if (id is null) return null;
        var existing = Catalog.Items.FirstOrDefault(i => i.Kind == "asd" && i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var item = existing ?? new Item { Kind = "asd", Name = name };
        item.Id = id;
        if (existing is null) Catalog.Items.Add(item);
        Catalog.Save(); CatalogChanged?.Invoke();
        return item;
    }

    public async Task<Outcome> OpenSceneAsync(Item scene)
    {
        if (scene.Id is null) return new Outcome(false, $"{scene.File} is not a scene of the Module.");
        await turn.WaitAsync();
        try
        {
            // The Scene is given by its identifier at Basic Audio Scene 1; the Module opens it and gives it back.
            var json = await Task.Run(() =>
            {
                link.DrainEditing();
                link.Write(ModuleLink.Bas, 1, new JsonObject { ["Header"] = "OSD-BAS-V1.5", ["BasicAudioSceneDescriptorsID"] = scene.Id }.ToJsonString());
                return SceneFrom(20_000, scene.Id);
            });
            if (json is null) return new Outcome(false, $"{scene.File} is not in the storage.");
            done.Clear();
            ReadScene(json);
            Catalog.OpenScene = scene.Id; Catalog.Save();
            return new Outcome(true, $"{scene.File} is open.");
        }
        finally { turn.Release(); }
    }

    public async Task RestoreAsync()
    {
        if (Catalog.OpenScene is { } id)
            await OpenSceneAsync(new Item { Kind = "asd", Name = "(last)", Id = id });
    }

    // ---- hearing ------------------------------------------------------------------------------------

    // The scene as the user hears it: Steam Audio, binaural, where it is installed; else each sound panned by where it is.
    public byte[] Render()
    {
        if (SceneJson is null) throw new InvalidOperationException("The scene is empty.");
        var rendering = new AudioRendering(Path.Combine(Paths.Root, "Models", "SteamAudio"));
        if (rendering.Available) return WithRoom(rendering.Render(SceneJson).Data);

        const int rate = 48000;
        var members = SceneAudio.Members(MpaiJson.FromJson<BasicAudioSceneDescriptors>(SceneJson)).ToList();
        var sounds = members.Select(m => SceneAudio.Shape(SceneAudio.WithLevel(m.Object, SceneAudio.Mono(m.Object.Data, rate), rate), m.StartSeconds, m.EndSeconds, rate)).ToList();
        var length = sounds.Count == 0 ? 0 : sounds.Max(s => s.Length);
        var mix = new float[length * 2];
        for (var k = 0; k < members.Count; k++)
        {
            // where it is for the listener: from the listener's place, turned to the way the listener faces (x to the right, y ahead)
            var ear = Listener; var facing = ear.Yaw * Math.PI / 180;
            double dx = members[k].Position[0] - ear.X, dy = members[k].Position[1] - ear.Y;
            double x = dx * Math.Cos(facing) + dy * Math.Sin(facing), y = -dx * Math.Sin(facing) + dy * Math.Cos(facing), d = Math.Sqrt(x * x + y * y);
            var pan = Math.Sin(Math.Atan2(x, y));                       // -1 left .. +1 right
            var gain = 1 / (1 + d / 1.5);
            // how it radiates: its directional pattern, as the listener at the origin sees the source
            if (DirectionalPattern.FromPlot(members[k].Object.BasicAudioObjectProperties?.AcousticProfile?.DirectionalPatterns) is { } pattern)
            {
                var (az, el) = SpatialRenderer.Seen(new SpatialRenderer.Source([], members[k].Position, members[k].YawDeg, members[k].PitchDeg), [ear.X, ear.Y, ear.Z]);
                gain *= pattern.Gain(az, el);
            }
            double l = Math.Cos((pan + 1) * Math.PI / 4) * gain, r = Math.Sin((pan + 1) * Math.PI / 4) * gain;
            for (var i = 0; i < sounds[k].Length; i++) { mix[2 * i] += (float)(sounds[k][i] * l); mix[2 * i + 1] += (float)(sounds[k][i] * r); }
        }
        var peak = mix.Length == 0 ? 1 : mix.Max(Math.Abs);
        if (peak > 0.95f) for (var i = 0; i < mix.Length; i++) mix[i] *= 0.95f / peak;
        return WithRoom(SceneAudio.Wav(mix, 2, rate));
    }

    private byte[] WithRoom(byte[] wav) => Acoustics is { } acoustics ? RoomReverb.ApplyToWav(wav, acoustics) : wav;

    // ---- understanding words, speaking, listening: the Module's (Automatic Speech Recognition, Entity Dialogue Processing, Text to Speech) ----

    public bool DialogueReady()
    {
        try { using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(2) }; return h.GetAsync("http://127.0.0.1:11434/api/tags").Result.IsSuccessStatusCode; }
        catch { return false; }
    }

    // What the Module made of words or of a voice: what was heard (a voice), the command, what it says back, what it asks of the User Agent, and the reply spoken.
    public sealed record Heard(string? Text, UserCommand? Command, string? Reply, string? ForUA, byte[]? ReplyVoice);

    // Words, typed: given at Basic Text Object 1 with the Scene being edited at Basic Audio Scene 2; the command comes out at User Command 6, the reply at Basic Text Object 2.
    public async Task<(UserCommand? Command, string? Reply, string? ForUA)> UnderstandAsync(string words)
    {
        var heard = await ConverseAsync(() => link.Write(ModuleLink.Bto, 1, MpaiJson.ToJson(BasicTextObject.FromText(words))), listened: false);
        return (heard.Command, heard.Reply, heard.ForUA);
    }

    // A voice (a WAV, as the microphone gave it): given at Basic Speech Object 2; the Module recognises it (Basic Text Object 1) and goes on as for typed words.
    public Task<Heard> ListenAsync(byte[] wav) => ListenAsync(new BasicSpeechObject { BasicSpeechObjectID = Guid.NewGuid().ToString(), Data = wav });

    public Task<Heard> ListenAsync(BasicSpeechObject speech) =>
        ConverseAsync(() => link.Write(ModuleLink.Bso, 2, MpaiJson.ToJson(speech)), listened: true);

    private async Task<Heard> ConverseAsync(Action give, bool listened)
    {
        await voiceTurn.WaitAsync();
        try
        {
            var scene = SceneJson;
            return await Task.Run(() =>
            {
                link.DrainVoice();
                if (scene is not null) link.Write(ModuleLink.Bas, 2, scene);
                give();
                string? heardText = null;
                if (listened)
                {
                    heardText = link.Read(ModuleLink.Bto, 1, 60_000) is { } h ? MpaiJson.FromJson<BasicTextObject>(h).GetText() : null;
                    if (string.IsNullOrWhiteSpace(heardText)) return new Heard(heardText, null, null, null, null);
                }
                var reply = link.Read(ModuleLink.Bto, 2, 180_000) is { } r ? MpaiJson.FromJson<BasicTextObject>(r).GetText() : null;
                var command = link.Read(ModuleLink.Ucm, 6, 1_500) is { } c ? MpaiJson.FromJson<UserCommand>(c) : null;
                var forUa = link.Read(ModuleLink.Tfu, 1, 300) is { } u ? MpaiJson.FromJson<TextForUA>(u).Text : null;
                var voice = link.Read(ModuleLink.Bso, 2, 30_000) is { } v ? MpaiJson.FromJson<BasicSpeechObject>(v).Data : null;
                return new Heard(heardText, command, reply, forUa, voice);
            });
        }
        finally { voiceTurn.Release(); }
    }

    // Text to be spoken, given at Basic Text Object 2 (its Text Qualifier states the language); the speech comes out at Basic Speech Object 2.
    public Task<BasicSpeechObject> SpeakAsync(string text) => SpeakAsync(BasicTextObject.FromText(text));

    public async Task<BasicSpeechObject> SpeakAsync(BasicTextObject text)
    {
        await voiceTurn.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                link.DrainVoice();
                link.Write(ModuleLink.Bto, 2, MpaiJson.ToJson(text));
                return link.Read(ModuleLink.Bso, 2, 60_000) is { } v ? MpaiJson.FromJson<BasicSpeechObject>(v) : throw new InvalidOperationException("The Module gave no speech.");
            });
        }
        finally { voiceTurn.Release(); }
    }

    // The languages a person names, and the code a voice is chosen by.
    public static readonly (string Name, string Code)[] Languages = [("English", "en"), ("Italian", "it"), ("French", "fr"), ("German", "de"), ("Spanish", "es"), ("Portuguese", "pt"), ("Japanese", "ja"), ("Chinese", "zh"), ("Korean", "ko"), ("Russian", "ru"), ("Arabic", "ar")];
    public static string LanguageName(string? code)
    {
        var head = (code ?? "").Split('-', '_')[0].ToLowerInvariant();
        return Languages.FirstOrDefault(l => l.Code == head).Name ?? (head.Length > 0 ? head : "an unknown language");
    }


    // "2 m to the right and 3 ahead", for a place.
    public static string Describe(double x, double y)
    {
        string Part(double v, string plus, string minus) => Math.Abs(v) < 0.05 ? "" : $"{Math.Abs(v):0.#} m {(v > 0 ? plus : minus)}";
        var parts = new[] { Part(x, "to the right", "to the left"), Part(y, "ahead", "behind") }.Where(p => p.Length > 0).ToList();
        return parts.Count == 0 ? "at your position" : string.Join(" and ", parts);
    }

    public void Dispose() => link.Dispose();
}



