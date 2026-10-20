using Mpai.Core;
using Mpai.Core.OSD;
namespace Mpai.Cae.AsmApp;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest.Run(args).GetAwaiter().GetResult();
        if (args.Length == 2 && args[0] == "--parse") { File.WriteAllText(Path.Combine(Path.GetTempPath(), "asm-parse.txt"), Intent.Parse(args[1])?.ToString() ?? "(nothing)"); return 0; }   // what the grammar makes of a sentence
        if (args.Contains("--mousetest")) return MouseTest.Run();
        if (args.Contains("--commandspng"))        // the list of commands as the editing space shows it, to a picture (on this thread: a rich text box wants one with a message loop)
        {
            ApplicationConfiguration.Initialize();
            using var list = new CommandsPanel { Height = 640 };
            list.CreateControl(); list.Display("Audio profile commands", CommandsPanel.Profile);
            using var bmp = new System.Drawing.Bitmap(list.Width, list.Height);
            list.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, list.Width, list.Height));
            var folder = Paths.Reports; Directory.CreateDirectory(folder);
            bmp.Save(Path.Combine(folder, "commands.png")); return 0;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}

// THE APP WITHOUT ITS WINDOWS: the library, the commands, the undo, the saving and the rendering, in a workspace of its own - for finding what is wrong
// without a person at the screen. Writes selftest.txt to Downloads\ASM\self-test.
internal static class SelfTest
{
    public static async Task<int> Run(string[] args)
    {
        var lines = new List<string>(); var failed = 0;
        void Check(string what, bool ok, string detail = "") { lines.Add($"{(ok ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? " - " + detail : "")}"); if (!ok) failed++; try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "asm-selftest-progress.txt"), lines[^1] + Environment.NewLine); } catch { } }
        var folder = Path.Combine(Path.GetTempPath(), "asmstudio-self-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("ASMSTUDIO_WORKSPACE", folder);
        Directory.CreateDirectory(folder);
        try
        {
            using var studio = new Studio();
            string Tone(string name, double hz, double seconds)
            {
                const int rate = 22050; var n = (int)(rate * seconds); var data = new byte[n * 2];
                for (var i = 0; i < n; i++) BitConverter.GetBytes((short)(Math.Sin(2 * Math.PI * hz * i / rate) * 12000)).CopyTo(data, i * 2);
                using var ms = new MemoryStream();
                using (var w = new NAudio.Wave.WaveFileWriter(ms, new NAudio.Wave.WaveFormat(rate, 16, 1))) w.Write(data, 0, data.Length);
                var path = Path.Combine(folder, name + ".wav"); File.WriteAllBytes(path, ms.ToArray()); return path;
            }
            // What is asked, when it is plain.
            void Asked(string said, string verb, double? x, double? y, bool relative)
            {
                var i = Intent.Parse(said);
                Check($"\"{said}\"", i is not null && i.Verb == verb && Math.Abs((i.X ?? 0) - (x ?? 0)) < 1e-9 && Math.Abs((i.Y ?? 0) - (y ?? 0)) < 1e-9 && i.Relative == relative,
                      i is null ? "not understood" : $"{i.Verb} x={i.X} y={i.Y} relative={i.Relative}");
            }
            Asked("place an audio object at 2m x, 5my", "add", 2, 5, false);
            Asked("Place an audio object at 2 meters x and 5 meters y.", "add", 2, 5, false);
            Asked("put the bell at x 3 y -2", "add", 3, -2, false);
            Asked("add the drum at 4, 1", "add", 4, 1, false);
            Asked("place the hum 3 metres to the right and 2 ahead", "add", 3, 2, true);
            Asked("move the bell two metres to the left", "move", -2, 0, true);
            Asked("move the drum to 1, 4", "move", 1, 4, false);
            Asked("move bell.auo three meters horizontally", "move", 3, 0, true);
            Asked("move bell.auo three meters down", "move", 0, -3, true);
            Asked("move bell.auo three meters up", "move", 0, 3, true);
            Asked("move bell.auo 2 metres vertically", "move", 0, 2, true);
            Asked("move bell.auo 2 m horizontally to the left", "move", -2, 0, true);
            Asked("raise bell.auo 2 m", "move", 0, 0, true);
            Asked("move the bell to x 2 y 3", "move", 2, 3, false);
            Asked("remove the bell", "remove", 0, 0, false);
            var violin = await studio.ImportSoundAsync(Tone("violin", 440, 1.0));
            var drums = await studio.ImportSoundAsync(Tone("drums", 110, 1.0));
            Check("a sound is imported under its name", violin.File == "violin.auo" && violin.Data is not null && Math.Abs(violin.Seconds - 1.0) < 0.05, $"{violin.File}, {violin.Seconds:0.00} s");
            Check("a second with the same name is named apart", (await studio.ImportSoundAsync(Tone("violin", 330, 0.5))).File == "violin (2).auo");

            var a = await studio.AddAsync(violin, 2, 3); var b = await studio.AddAsync(drums, -3, 2);
            Check("two sounds are put in the scene", a.Ok && b.Ok && studio.Members.Count == 2, string.Join("; ", studio.Members.Select(m => $"{m.Name} {m.X:0.0},{m.Y:0.0}")));
            Check("the scene is named by what the user calls the sounds", studio.Members.Select(m => m.Name).OrderBy(x => x).SequenceEqual(["drums.auo", "violin.auo"]));
            Check("a sound is not put in twice", !(await studio.AddAsync(violin, 1, 1)).Ok);

            var moved = await studio.MoveAsync(studio.Members.First(m => m.Name == "violin.auo"), 1, 1);
            var v = studio.Members.First(m => m.Name == "violin.auo");
            Check("a sound is moved", moved.Ok && Math.Abs(v.X - 1) < 0.01 && Math.Abs(v.Y - 1) < 0.01, $"{v.X:0.00},{v.Y:0.00}");
            var undone = await studio.UndoAsync(); v = studio.Members.First(m => m.Name == "violin.auo");
            Check("the move is undone", undone.Ok && Math.Abs(v.X - 2) < 0.01 && Math.Abs(v.Y - 3) < 0.01, $"{undone.Text}; {v.X:0.00},{v.Y:0.00}");

            var removed = await studio.RemoveAsync(studio.Members.First(m => m.Name == "drums.auo"));
            Check("a sound is taken out", removed.Ok && studio.Members.Count == 1);
            Check("taking it out is undone", (await studio.UndoAsync()).Ok && studio.Members.Count == 2);
            Check("then the add is undone", (await studio.UndoAsync()).Ok && studio.Members.Count == 1);
            Check("and the first", (await studio.UndoAsync()).Ok && studio.Members.Count == 0);
            Check("nothing is left to undo", !(await studio.UndoAsync()).Ok);

            await studio.AddAsync(violin, 2, 3); await studio.AddAsync(drums, -3, 2);
            var wav = studio.Render();
            var (rate, bits, channels) = Studio.Format(wav);
            Check("the scene is rendered", wav.Length > 44 && Studio.Seconds(wav) > 0.5, $"{Studio.Seconds(wav):0.00} s, {rate} Hz, {bits} bit, {channels} channels");

            var saved = studio.SaveScene("concert");
            Check("the scene is saved under a name", saved is { File: "concert.asd" });
            await studio.RemoveAsync(studio.Members[0]);
            Check("the saved scene is opened again", (await studio.OpenSceneAsync(saved!)).Ok && studio.Members.Count == 2);

            // WHEN IT SOUNDS: a start time is a command on the scene, undone like any, heard, and kept with the scene.
            var vi = studio.Members.First(m => m.Name == "violin.auo");
            var timed = await studio.StartAsync(vi, 3, false); vi = studio.Members.First(m => m.Name == "violin.auo");
            Check("a sound is given a start time", timed.Ok && Math.Abs(vi.Start - 3) < 1e-6 && Math.Abs(vi.X - 2) < 0.01, $"start {vi.Start}, x {vi.X}, {timed.Text}");
            var heard = Studio.Seconds(studio.Render());
            Check("the scene is heard from that time", heard > 3.9, $"{heard:0.00} s");
            await studio.StartAsync(vi, 1, true); vi = studio.Members.First(m => m.Name == "violin.auo");
            Check("a later start is added to it", Math.Abs(vi.Start - 4) < 1e-6, $"start {vi.Start}");
            await studio.UndoAsync(); vi = studio.Members.First(m => m.Name == "violin.auo");
            Check("the start is undone", Math.Abs(vi.Start - 3) < 1e-6, $"start {vi.Start}");
            var ended = await studio.EndAsync(vi, 3.5); vi = studio.Members.First(m => m.Name == "violin.auo");
            var heardEnd = Studio.Seconds(studio.Render());
            Check("a sound is given an end time", ended.Ok && vi.End is { } e1 && Math.Abs(e1 - 3.5) < 1e-6 && Math.Abs(vi.Start - 3) < 1e-6, $"{ended.Text}; end {vi.End}");
            Check("the scene is cut at that time", heardEnd < heard - 0.2, $"{heardEnd:0.00} s against {heard:0.00} s");
            Check("a sound cannot end before it starts", !(await studio.EndAsync(vi, 2)).Ok);
            await studio.UndoAsync(); vi = studio.Members.First(m => m.Name == "violin.auo");
            Check("the end is undone", vi.End is null, $"end {vi.End}");
            var ei = Intent.Parse("make bell.auo end at 5 seconds"); var si2 = Intent.Parse("stop the drum at 4 seconds"); var ti = Intent.Parse("start at 1 second and end at 4 seconds");
            Check("\"make bell.auo end at 5 seconds\"", ei is { Verb: "end", End: 5, Start: null }, $"{ei}");
            Check("\"stop the drum at 4 seconds\"", si2 is { Verb: "end", End: 4 }, $"{si2}");
            Check("\"start at 1 second and end at 4 seconds\"", ti is { Verb: "time", Start: 1, End: 4 }, $"{ti}");
            var glued = Intent.Parse("makedrum.au to end at 10 seconds"); var dotted = Intent.Parse("Make drum dot a u o to start at five seconds.");
            Check("\"makedrum.au to end at 10 seconds\" (as the recogniser wrote it)", glued is { Verb: "end", End: 10 }, $"{glued}");
            Check("\"Make drum dot a u o to start at five seconds.\"", dotted is { Verb: "start", Start: 5 }, $"{dotted}");
            var si = Intent.Parse("make bell.auo start at 0 seconds"); var sl = Intent.Parse("start the drum 2 seconds later");
            Check("\"make bell.auo start at 0 seconds\"", si is { Verb: "start", Start: 0, StartRelative: false }, $"{si}");
            Check("\"start the drum 2 seconds later\"", sl is { Verb: "start", Start: 2, StartRelative: true }, $"{sl}");

            // THE OBJECT ON STAGE, AS THE COMMANDS LEFT IT, is kept as an audio object (AUO) and comes back with its place and time.
            var kept = studio.SaveObject(vi, "violin late");
            Check("the object on stage is saved as an audio object", kept is { File: "violin late.auo", Audio: true }, $"{kept?.File}");
            var removedFirst = await studio.RemoveAsync(vi);
            lines.Add($"info removed the object on stage: {removedFirst.Ok} {removedFirst.Text}; members now: {string.Join(", ", studio.Members.Select(m => m.Name))}");
            var back = await studio.AddAsync(kept!, 5, 5); var bm = studio.Members.FirstOrDefault(m => m.Name == "violin late.auo");
            Check("an audio object brings its start time and goes where it is said", back.Ok && bm is not null && Math.Abs(bm.Start - 3) < 1e-6 && Math.Abs(bm.X - 5) < 0.01 && Math.Abs(bm.Y - 5) < 0.01, $"{back.Text}; {bm}");
            await studio.RemoveAsync(bm!);
            await studio.AddAsync(violin, 2, 3);

            // READING FROM A FILE: a Basic Audio Object and an Audio Object, both.
            var baoFile = Path.Combine(folder, "drums-bao.auo"); File.WriteAllText(baoFile, studio.ObjectJson(drums)!);
            var fromBao = await studio.ImportFileAsync(baoFile);
            Check("a Basic Audio Object is read from a file", fromBao is { Audio: false } && Math.Abs(fromBao.Seconds - 1.0) < 0.05, $"{fromBao.File} {fromBao.Seconds:0.00} s");
            // An Audio Object read from the user's external storage is given to the Module, which stores it.
            var pairTone = await studio.ImportSoundAsync(Tone("pairtone", 220, 1.0));
            var inner = MpaiJson.FromJson<BasicAudioObject>(studio.ObjectJson(pairTone)!);
            var auoFile = Path.Combine(folder, "pair.auo");
            File.WriteAllText(auoFile, MpaiJson.ToJson(new AudioObject
            {
                AudioObjectID = "AUO-from-elsewhere", MInstanceID = "test",
                BasicAudioObjects = [new BasicAudioObjectEntry
                {
                    BAObjectIDOrBAObject = inner,
                    BasicAudioObjectSpaceTime = new SpaceTime
                    {
                        SpatialAttitude1 = new SpatialAttitude { Position = new Position { CartPosition = [1, 2, 0] } },
                        Time = new SimpleTime { SimpleTimeData = { new TimeSegment { StartTime = 2, EndTime = 2, TimeType = false, TimeUnit = "00" } } }
                    }
                }]
            }));
            var fromAuo = await studio.ImportFileAsync(auoFile);
            Check("an Audio Object is read from a file", fromAuo is { Audio: true } && Math.Abs(fromAuo.Seconds - 3.0) < 0.1, $"{fromAuo.File} {fromAuo.Seconds:0.00} s");
            var put = await studio.AddAsync(fromAuo, null, null); var pm = studio.Members.FirstOrDefault(m => m.Name == "pair.auo");
            Check("and it is put in the scene with the place and the time it came with", put.Ok && pm is not null && Math.Abs(pm.Start - 2) < 1e-6 && Math.Abs(pm.X - 1) < 0.01 && Math.Abs(pm.Y - 2) < 0.01, $"{put.Text}; {pm}");
            Check("a sound captured and not edited stays a Basic Audio Object", violin is { Audio: false });

            // THE AUDIO PROFILE: the commands are understood, the object is edited by Audio Object Editing and put in the scene in place of the old one, and it is heard.
            var p1 = Intent.Parse("display the audio profile commands"); var p2 = Intent.Parse("make bell.auo 6 dB louder"); var p3 = Intent.Parse("make the drum 3 decibels quieter");
            var p4 = Intent.Parse("set the hum to minus 20 LUFS"); var p5 = Intent.Parse("make the bell cardioid"); var p6 = Intent.Parse("make the drum a figure of eight"); var p7 = Intent.Parse("show the profile of the bell");
            Check("\"display the audio profile commands\"", p1 is { Verb: "profilehelp" }, $"{p1?.Verb}");
            Check("\"display the acoustic profan commands\" (as the recogniser wrote it)", Intent.Parse("display the acoustic profan commands.") is { Verb: "profilehelp" });
            Check("\"move bell.auo to x 5, y 3, z 0\" puts a sound back on the floor", Intent.Parse("move bell.auo to x 5, y 3, z 0") is { Verb: "move", X: 5, Y: 3, Z: 0, HasPlace: true });
            Check("\"move me to 0, 0\" puts the user back at the origin", Intent.Parse("move me to 0, 0") is { Verb: "listener", X: 0, Y: 0, HasPlace: true });
            Check("\"make bell.auo 6 dB louder\"", p2 is { Verb: "loudness", Db: 6 }, $"{p2?.Verb} {p2?.Db}");
            Check("\"make the drum 3 decibels quieter\"", p3 is { Verb: "loudness", Db: -3 }, $"{p3?.Verb} {p3?.Db}");
            Check("\"set the hum to minus 20 LUFS\"", p4 is { Verb: "loudness", Lufs: -20 }, $"{p4?.Verb} {p4?.Lufs}");
            Check("\"make the bell cardioid\"", p5 is { Verb: "direction", Pattern: "cardioid" }, $"{p5?.Verb} {p5?.Pattern}");
            Check("\"make the drum a figure of eight\"", p6 is { Verb: "direction", Pattern: "bidirectional" }, $"{p6?.Verb} {p6?.Pattern}");
            Check("\"show the profile of the bell\"", p7 is { Verb: "profile" }, $"{p7?.Verb}");
            var vm = studio.Members.First(m => m.Name == "violin.auo"); var level0 = studio.LoudnessOf(vm);
            var rms0 = Rms(studio.Render(), 0, 1);
            var louder6 = await studio.LoudnessAsync(vm, 6, null); var vm2 = studio.Members.First(m => m.Name == "violin.auo");
            Check("an object is made louder by the AIM, and keeps its name and its place in the scene", louder6.Ok && vm2.ObjectJson != vm.ObjectJson && Math.Abs(vm2.X - vm.X) < 1e-6 && Math.Abs(studio.LoudnessOf(vm2) - (level0 + 6)) < 0.01, $"{louder6.Text}; {level0:0.0} -> {studio.LoudnessOf(vm2):0.0} LUFS");
            var rms1 = Rms(studio.Render(), 0, 1);
            Check("and it is heard louder", rms1 > rms0 * 1.15, $"{rms0:0.0000} -> {rms1:0.0000}");
            await studio.MoveAsync(vm2, vm2.X, vm2.Y, null, 0);                 // turned away from the listener
            var rmsAway = Rms(studio.Render(), 0, 1); var vm3 = studio.Members.First(m => m.Name == "violin.auo");
            var card = await studio.DirectionAsync(vm3, "cardioid"); var vm4 = studio.Members.First(m => m.Name == "violin.auo");
            var rmsCard = Rms(studio.Render(), 0, 1);
            Check("an object is made cardioid, and turned away it is heard less", card.Ok && rmsCard < rmsAway * 0.9, $"{rmsAway:0.0000} -> {rmsCard:0.0000}; {card.Text}");
            Check("its profile says so", studio.ProfileText(vm4).Contains("cardioid") && studio.ProfileText(vm4).Contains("LUFS"), studio.ProfileText(vm4));
            var rmsFull = Rms(studio.Render(), 0, 1);
            var range = await studio.FrequencyRangeAsync(vm4, 2000, null); var vm5 = studio.Members.First(m => m.Name == "violin.auo"); var rmsRange = Rms(studio.Render(), 0, 1);
            var held = studio.ObjectOf(vm5)!; var heardLufs = Mpai.SpatialAudio.SceneAudio.Lufs(Mpai.SpatialAudio.SceneAudio.WithLevel(held, Mpai.SpatialAudio.SceneAudio.Mono(held.Data, 48000)));
            Check("a frequency range takes away what lies outside it (a 440 Hz tone above 2000 Hz), and keeps the rest of the profile", range.Ok && heardLufs < (held.BasicAudioObjectProperties!.AcousticProfile!.Loudness ?? 0) - 20 && studio.ProfileText(vm5).Contains("cardioid") && studio.ProfileText(vm5).Contains("2000"), $"{heardLufs:0.0} LUFS against {held.BasicAudioObjectProperties!.AcousticProfile!.Loudness:0.0}; {range.Text}");
            var spec = await studio.SpectrogramAsync(vm5); var vm6 = studio.Members.First(m => m.Name == "violin.auo");
            Check("a spectrogram is measured, and the rest is kept", spec.Ok && studio.ProfileText(vm6).Contains("points") && studio.ProfileText(vm6).Contains("2000") && studio.ProfileText(vm6).Contains("cardioid"), spec.Text);
            await studio.UndoAsync(); await studio.UndoAsync();                 // the spectrogram, the range
            Check("the direction is undone", (await studio.UndoAsync()).Ok && studio.Members.First(m => m.Name == "violin.auo").ObjectJson == vm3.ObjectJson);
            await studio.UndoAsync(); await studio.UndoAsync();                 // the turn, the loudness
            Check("the loudness is undone", studio.Members.First(m => m.Name == "violin.auo").ObjectJson == vm.ObjectJson);

            // THE USER IS MOVED, NOT A SOUND: "move me", "move the listener"; a misheard "display" is not "play".
            var lm = Intent.Parse("Move me to the center of the room."); var lu = Intent.Parse("Move me the listener 3 meters up"); var lc = Intent.Parse("move bell.auo to the center of the room");
            var lp = Intent.Parse("play the audio acoustic profile command."); var lt = Intent.Parse("turn me 90 degrees"); var lf = Intent.Parse("make the bell face me");
            Check("\"Move me to the center of the room.\" moves the user", lm is { Verb: "listener", Field: "center", HasPlace: true }, $"{lm?.Verb} {lm?.Field}");
            Check("\"Move me the listener 3 meters up\" moves the user 3 m up the screen", lu is { Verb: "listener", Y: 3, Relative: true }, $"{lu?.Verb} {lu?.Y}");
            Check("\"move bell.auo to the center of the room\" moves the sound", lc is { Verb: "move", Field: "center", HasPlace: true }, $"{lc?.Verb} {lc?.Field}");
            Check("\"play the audio acoustic profile command.\" is the list of commands, not play", lp is { Verb: "profilehelp" }, $"{lp?.Verb}");
            Check("\"turn me 90 degrees\" turns the user; \"make the bell face me\" turns the sound", lt is { Verb: "listener", Yaw: 90 } && lf is { Verb: "turn" }, $"{lt?.Verb} {lf?.Verb}");
            var farRms = Rms(studio.Render(), 0, 1);
            var heardFrom = await studio.ListenerAsync(2, 2.5, 0, 0);
            Check("the user is moved, and the scene is heard from there", heardFrom.Ok && studio.Listener is { X: 2, Y: 2.5 } && Rms(studio.Render(), 0, 1) > farRms * 1.2, $"{heardFrom.Text} {studio.Listener}; {farRms:0.0000} -> {Rms(studio.Render(), 0, 1):0.0000}");
            Check("and moved back", (await studio.UndoAsync()).Ok && studio.Listener is { X: 0, Y: 0 });

            // A ROOM round the objects: the distance asked for is the height; round the sides there is only a margin.
            var ri = Intent.Parse("build a room that encloses the two objects with a minimum distance between the walls of 3 m");
            Check("\"build a room that encloses the two objects with a minimum distance between the walls of 3 m\"", ri is { Verb: "room", Height: 3, Margin: null }, $"{ri}");
            var dry = Studio.Seconds(studio.Render());
            var built = await studio.BuildRoomAsync(studio.Members.ToList(), 1, 3);
            var rm = studio.Room;
            Check("a room is built round the objects", built.Ok && rm is not null && Math.Abs(rm.Height - 3) < 1e-6 && studio.Members.All(m => m.X >= rm.X0 + 0.99 && m.X <= rm.X1 - 0.99 && m.Y >= rm.Y0 + 0.99 && m.Y <= rm.Y1 - 0.99), $"{built.Text} {rm}");
            var resized = await studio.ResizeRoomAsync(rm!.X0 - 1, rm.Y0, rm.X1 + 2, rm.Y1 + 1, 4);
            Check("the room is resized by a command: wider, deeper, higher, the walls kept", resized.Ok && studio.Room is { } rs && Math.Abs(rs.Width - (rm.Width + 3)) < 1e-6 && Math.Abs(rs.Depth - (rm.Depth + 1)) < 1e-6 && rs.Height == 4 && Math.Abs(rs.Absorption - rm.Absorption) < 1e-6, $"{resized.Text}");
            Check("and it is undone", (await studio.UndoAsync()).Ok && studio.Room is { } rb && Math.Abs(rb.Width - rm.Width) < 1e-6 && rb.Height == 3);
            Check("the room is in the scene as a Closed Space", studio.SceneJson!.Contains("OSD-CSP-V1.5") && studio.SceneJson.Contains("OSD-RPP-V1.5"));
            var wet = Studio.Seconds(studio.Render());
            Check("the room is heard: the sound goes on after it", wet > dry + 0.1, $"{dry:0.00} s without, {wet:0.00} s with (tail {rm?.Rt60:0.00} s)");
            double Rms(byte[] w, double from, double to)
            {
                var (r, _, c) = Studio.Format(w); var a = (int)(from * r) * c; var b = Math.Min((w.Length - 44) / 2, (int)(to * r) * c); double s = 0; var n = 0;
                for (var i = Math.Max(0, a); i < b; i++) { var v = BitConverter.ToInt16(w, 44 + 2 * i) / 32768.0; s += v * v; n++; }
                return n == 0 ? 0 : Math.Sqrt(s / n);
            }
            var wetWav = studio.Render(); var body = Rms(wetWav, dry - 1, dry); var afterwards = Rms(wetWav, dry + 0.05, dry + 0.45);
            Check("the tail can be heard (not far under the sound)", afterwards > 0.08 * body, $"tail {afterwards:0.0000} against {body:0.0000}");
            var ra = Intent.Parse("Add reverberation to the room."); var rl = Intent.Parse("less reverberation"); var rt = Intent.Parse("make the reverberation 2 seconds long");
            Check("\"Add reverberation to the room.\" is reverberation, not a new room", ra is { Verb: "reverb", Factor: 0.5 }, $"{ra}");
            Check("\"less reverberation\"", rl is { Verb: "reverb", Factor: 2 }, $"{rl}");
            Check("\"make the reverberation 2 seconds long\"", rt is { Verb: "reverb", Tail: 2 }, $"{rt}");
            var cmds = new[] { "limit the frequency range of bell.auo from 200 Hz to 8 kHz", "measure the spectrogram of bell.auo", "set the early reflection time to 20 ms and the late reflection time to 80 ms",
                               "set the reverberation time (RT60) to 1.5 seconds", "set the early decay time to 0.8 seconds", "set the diffusion to 0.7", "set the absorption to 0.3",
                               "set the Doppler direct sound factor to 0.5 and the indirect sound to 1", "show the scene profile" }.Select(Intent.Parse).ToArray();
            Check("\"limit the frequency range of bell.auo from 200 Hz to 8 kHz\"", cmds[0] is { Verb: "frequency", A: 200, B: 8000 }, $"{cmds[0]?.Verb} {cmds[0]?.A} {cmds[0]?.B}");
            Check("\"measure the spectrogram of bell.auo\"", cmds[1] is { Verb: "spectrogram" });
            Check("\"set the early reflection time to 20 ms and the late reflection time to 80 ms\"", cmds[2] is { Verb: "reflectivity", A: 0.02, B: 0.08 }, $"{cmds[2]?.A} {cmds[2]?.B}");
            Check("\"set the reverberation time (RT60) to 1.5 seconds\"", cmds[3] is { Verb: "reverb", Field: "RT60", Tail: 1.5 }, $"{cmds[3]?.Field} {cmds[3]?.Tail}");
            Check("\"set the early decay time to 0.8 seconds\"", cmds[4] is { Verb: "reverb", Field: "EDT", Tail: 0.8 }, $"{cmds[4]?.Field} {cmds[4]?.Tail}");
            Check("\"set the diffusion to 0.7\" and \"set the absorption to 0.3\"", cmds[5] is { Verb: "diffusion", A: 0.7 } && cmds[6] is { Verb: "absorption", A: 0.3 });
            Check("\"set the Doppler direct sound factor to 0.5 and the indirect sound to 1\"", cmds[7] is { Verb: "doppler", A: 0.5, B: 1 }, $"{cmds[7]?.A} {cmds[7]?.B}");
            Check("\"show the scene profile\"", cmds[8] is { Verb: "sceneprofile" });

            // THE SCENE'S PROFILE: Reverberation (RT60, EDT), Reflectivity, Diffusion, Absorption, Doppler - each a field, each a command of Audio Scene Editing, each keeping the others.
            var rt0 = studio.Acoustics!.Rt60;
            var longer = await studio.ReverbAsync("RT60", null, 0.5); var tail1 = studio.Acoustics!.Rt60;
            Check("more reverberation: the RT60 of the scene is longer", longer.Ok && tail1 > rt0 * 1.5 && studio.SceneProfile?.Reverberation?[0].RT60 is not null, $"{rt0:0.00} s -> {tail1:0.00} s");
            var set = await studio.ReverbAsync("RT60", 2.0, null);
            Check("an RT60 of 2 seconds is set", set.Ok && Math.Abs(studio.Acoustics!.Rt60 - 2.0) < 1e-6, $"{studio.Acoustics!.Rt60:0.00} s");
            Check("the longer tail is heard longer", Studio.Seconds(studio.Render()) > wet + 0.8, $"{Studio.Seconds(studio.Render()):0.00} s against {wet:0.00} s");
            await studio.ReverbAsync("EDT", 0.8, null); await studio.ReflectivityAsync(0.02, 0.08); await studio.DiffusionAsync(0.7); await studio.AbsorptionAsync(0.3); await studio.DopplerAsync(0.5, 1);
            var sp = studio.SceneProfile;
            Check("the EDT, the Reflectivity, the Diffusion, the Absorption and the Doppler are fields of the scene's profile",
                  sp is { Diffusion: 0.7, Absorption: 0.3 } && sp.Reverberation![0].EDT == 0.8 && sp.Reverberation[0].RT60 is not null && sp.Reflectivity![0] is { EarlyReflectionTime: 0.02, LateReflectionTime: 0.08 } && sp.Doppler![0] is { DirectSoundFactor: 0.5, IndirectSound: 1 },
                  studio.SceneProfileText());
            Check("the reflections and the density change what is heard", Studio.Seconds(studio.Render()) > 0.5);
            for (var k = 0; k < 7; k++) await studio.UndoAsync();
            Check("the profile commands are undone one by one", studio.SceneProfile is null && Math.Abs(studio.Acoustics!.Rt60 - rt0) < 1e-6, $"{studio.SceneProfile is null} {studio.Acoustics?.Rt60:0.00} against {rt0:0.00}");
            Check("the room is undone", (await studio.UndoAsync()).Ok && studio.Room is null && studio.Acoustics is null);

            // The editing space, drawn with the scene in it.
            // Drawing needs an interactive desktop: on a locked or unattended one it can block, so it is given a minute and reported as skipped, not failed.
            var drawing = Task.Run(() =>
            {
                using var canvas = new SceneCanvas(studio) { Width = 1300, Height = 760 };
                canvas.CreateControl();
                using var bmp = new System.Drawing.Bitmap(canvas.Width, canvas.Height);
                canvas.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, canvas.Width, canvas.Height));
                var shot = Paths.Reports; Directory.CreateDirectory(shot);
                bmp.Save(Path.Combine(shot, "editing-space.png"));
            });
            if (!drawing.Wait(TimeSpan.FromSeconds(60))) lines.Add("skip the editing space was not drawn - no interactive desktop answered within a minute");
            else if (drawing.IsFaulted) Check("the editing space is drawn", false, drawing.Exception!.GetBaseException().Message);
            // WORDS ABOUT A SPEECH are still understood by the grammar of the User Agent; making the speech needs the vocal interface, which is not part of the Module now.
            {
                var pt = Intent.Parse("make a speech saying bom dia a todos in portuguese called bomdia"); var it2 = Intent.Parse("create an Italian speech saying buongiorno a tutti");
                Check("\"make a speech saying bom dia a todos in portuguese called bomdia\"", pt is { Verb: "speech", Field: "pt", Text: "bom dia a todos", Name: "bomdia" }, $"{pt?.Field} | {pt?.Text} | {pt?.Name}");
                Check("\"create an Italian speech saying buongiorno a tutti\"", it2 is { Verb: "speech", Field: "it", Text: "buongiorno a tutti", Name: null }, $"{it2?.Field} | {it2?.Text}");
            }
        }
        catch (Exception e) { Check("the self-test ran to its end", false, e.ToString()); }
        finally { try { Directory.Delete(folder, true); } catch { } }

        var report = Paths.Reports;
        Directory.CreateDirectory(report);
        File.WriteAllLines(Path.Combine(report, "selftest.txt"), lines.Append($"{lines.Count(l => l.StartsWith("ok"))} ok, {failed} failed"));
        return failed == 0 ? 0 : 1;
    }
}

// THE MOUSE, ON ITS OWN: the editing space is given the presses, moves and releases a hand would make, and says what it makes of them. It runs on this thread, which
// has a message loop's kind of apartment, as a control wants.
internal static class MouseTest
{
    public static int Run()
    {
        ApplicationConfiguration.Initialize();
        var lines = new List<string>(); var failed = 0;
        void Check(string what, bool ok, string detail = "") { lines.Add($"{(ok ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? " - " + detail : "")}"); if (!ok) failed++; }
        var folder = Path.Combine(Path.GetTempPath(), "asmstudio-mouse-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("ASMSTUDIO_WORKSPACE", folder); Directory.CreateDirectory(folder);
        try
        {
            using var studio = new Studio();
            var wav = Path.Combine(folder, "tone.wav");
            using (var ms = new MemoryStream())
            {
                var data = new byte[22050 * 2]; for (var i = 0; i < 22050; i++) BitConverter.GetBytes((short)(Math.Sin(2 * Math.PI * 440 * i / 22050.0) * 9000)).CopyTo(data, i * 2);
                using (var w = new NAudio.Wave.WaveFileWriter(ms, new NAudio.Wave.WaveFormat(22050, 16, 1))) w.Write(data, 0, data.Length);
                File.WriteAllBytes(wav, ms.ToArray());
            }
            var item = studio.ImportSoundAsync(wav).GetAwaiter().GetResult();
            studio.AddAsync(item, 2, 3).GetAwaiter().GetResult();

            using var canvas = new SceneCanvas(studio) { Width = 800, Height = 600 };
            canvas.CreateControl();
            (double X, double Y)? listenerMoved = null; double? listenerTurned = null; (Member M, double X, double Y)? moved = null; (Member M, double Yaw)? turned = null;
            canvas.ListenerMoved += (x, y) => listenerMoved = (x, y); canvas.ListenerTurned += y => listenerTurned = y;
            canvas.Moved += (m, x, y) => moved = (m, x, y); canvas.Turned += (m, y) => turned = (m, y);

            void Mouse(string what, int x, int y, MouseButtons button = MouseButtons.Left)
            {
                var args = new MouseEventArgs(button, 1, x, y, 0);
                typeof(SceneCanvas).GetMethod(what, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(canvas, [args]);
            }
            // the editing space is 800 by 600; the origin is at (400, 432); 60 pixels to the metre
            Mouse("OnMouseDown", 400, 432); Mouse("OnMouseMove", 460, 400); Mouse("OnMouseMove", 520, 372); Mouse("OnMouseUp", 520, 372);
            Check("the user is dragged with the mouse", listenerMoved is { } lm && Math.Abs(lm.X - 2) < 0.05 && Math.Abs(lm.Y - 1) < 0.05, $"{listenerMoved}");

            // the user is at the origin as far as the studio knows (nothing carried the command out): the nose is 24 pixels ahead of it
            Mouse("OnMouseDown", 400, 408); Mouse("OnMouseMove", 460, 432); Mouse("OnMouseUp", 500, 432);
            Check("the user is turned by the nose (the pointer to the right: yaw -90)", listenerTurned is { } lt && Math.Abs(lt - (-90)) < 1, $"{listenerTurned}");

            // the sound is at (2, 3): its dot at (520, 252)
            Mouse("OnMouseDown", 520, 252); Mouse("OnMouseMove", 550, 252); Mouse("OnMouseMove", 580, 252); Mouse("OnMouseUp", 580, 252);
            Check("a sound is dragged with the mouse", moved is { } mv && Math.Abs(mv.X - 3) < 0.05 && Math.Abs(mv.Y - 3) < 0.05, $"{moved}");

            // it faces the user (yaw 180): the tip of its arrow is 30 pixels below its dot
            Mouse("OnMouseDown", 520, 282); Mouse("OnMouseMove", 560, 252); Mouse("OnMouseUp", 600, 252);
            Check("a sound is turned by the tip of its arrow (the pointer to its right: yaw -90)", turned is { } tn && Math.Abs(tn.Yaw - (-90)) < 1, $"{turned}");

            // the room: its right wall dragged, then the room moved by its label
            studio.BuildRoomAsync(studio.Members.ToList(), 1, 3).GetAwaiter().GetResult();           // x 1 to 3, y 2 to 4: its right wall at (580, 252), its label inside (460, 192)
            (double, double, double, double)? room = null; canvas.RoomChanged += (a, b, c, d) => room = (a, b, c, d);
            Mouse("OnMouseDown", 580, 252); Mouse("OnMouseMove", 640, 252); Mouse("OnMouseMove", 700, 252); Mouse("OnMouseUp", 700, 252);
            Check("a wall of the room is dragged (the right wall to x = 5)", room is { } r1 && Math.Abs(r1.Item1 - 1) < 0.05 && Math.Abs(r1.Item2 - 2) < 0.05 && Math.Abs(r1.Item3 - 5) < 0.05 && Math.Abs(r1.Item4 - 4) < 0.05, $"{room}");
            room = null;
            Mouse("OnMouseDown", 480, 202); Mouse("OnMouseMove", 510, 232); Mouse("OnMouseMove", 540, 262); Mouse("OnMouseUp", 540, 262);
            Check("the room is moved by its label (1 m to the right, 1 m down)", room is { } r2 && Math.Abs(r2.Item1 - 2) < 0.05 && Math.Abs(r2.Item2 - 1) < 0.05 && Math.Abs(r2.Item3 - 4) < 0.05 && Math.Abs(r2.Item4 - 3) < 0.05, $"{room}");
            room = null;
            Mouse("OnMouseDown", 460, 192); Mouse("OnMouseMove", 440, 172); Mouse("OnMouseMove", 430, 162); Mouse("OnMouseUp", 430, 162);
            Check("a corner of the room is dragged (the top-left corner out by 0.5 m)", room is { } r3 && Math.Abs(r3.Item1 - 0.5) < 0.05 && Math.Abs(r3.Item4 - 4.5) < 0.05 && Math.Abs(r3.Item3 - 3) < 0.05, $"{room}");

            // a click on the empty space moves nothing
            listenerMoved = null; moved = null;
            Mouse("OnMouseDown", 100, 100); Mouse("OnMouseMove", 140, 130); Mouse("OnMouseUp", 140, 130);
            Check("the empty space is panned, and nothing is moved", listenerMoved is null && moved is null);
        }
        catch (Exception e) { Check("the mouse test ran to its end", false, e.ToString()); }
        finally { try { Directory.Delete(folder, true); } catch { } }
        var report = Paths.Reports; Directory.CreateDirectory(report);
        File.WriteAllLines(Path.Combine(report, "mousetest.txt"), lines.Append($"{lines.Count(l => l.StartsWith("ok"))} ok, {failed} failed"));
        return failed == 0 ? 0 : 1;
    }
}
