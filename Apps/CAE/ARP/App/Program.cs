using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using Mpai.Cae.Apps;
using Mpai.Cae.Usc;
using Mpai.Ua;

namespace Mpai.Cae.Arp;

// AUDIO RECORDING PRESERVATION, the App.
//
//   ArpApp --tape tape.wav [--film-audio film.wav --film-frames <folder of PNG frames> --fps 10] --out <folder>
//
// --tape         the digitised audio of the tape (16-bit PCM WAV; the Module keeps the file as it is)
// --film-audio   the audio of the film of the playback head (WAV)
// --film-frames  the frames of the film: a folder of grey-scale PNG files of one size, in the order of their names (the video analysis is a stand-in for one that reads a film)
// --fps          the frames a second of the film (default 10)
// --out          the folder to write into: master\ (every file of the Preservation Master) and access-copy.wav
internal static class Program
{
    private const string Module = "1CAE-ARP-V2.5-I01";

    private static int Main(string[] args)
    {
        try
        {
            var o = new Options(args, "--tape", "--film-audio", "--film-frames", "--fps", "--out");
            var tape = o.Need("--tape"); var output = o.Need("--out");
            if (!File.Exists(tape)) throw new FileNotFoundException($"There is no file {tape}.");
            if (o.Has("--film-frames") && !o.Has("--film-audio")) throw new ArgumentException("--film-frames needs --film-audio: the audio of the film is what the film is placed on the tape by.");
            Directory.CreateDirectory(output);

            Console.WriteLine("Starting the Module (a first start takes a while)...");
            using var module = ModuleHost.Start(Module, null, Console.WriteLine);
            using var ua = new ModuleSession(module.Api, Path.Combine(ModuleHost.Root, "Apps", "CAE", "ARP", "ARP.orch"),
                ("TapeIn", "OSD-BAO-V1.5"), ("FilmIn", "OSD-BMO-V1.5"), ("AccessCopyOut", "OSD-BAO-V1.5"), ("MasterOut", "OSD-BAO-V1.5"));
            ua.Start();

            Console.WriteLine("Analysing the tape...");
            ua.Send("TapeIn", AudioObject.MakeFiles("tape", [(Path.GetFileName(tape), File.ReadAllBytes(tape))]));
            if (o.One("--film-audio") is { } filmAudio)
            {
                var frames = o.One("--film-frames") is { } folder ? Frames(folder, double.Parse(o.One("--fps") ?? "10", System.Globalization.CultureInfo.InvariantCulture)) : null;
                ua.Send("FilmIn", AudioVisualObject.Make("film", Inputs.Speech16(filmAudio), frames));
            }

            // The Module settles as the analyses exchange their files: the Master is read until it has not changed for a while.
            string? master = null; var quiet = Stopwatch.StartNew(); var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromMinutes(10))
            {
                var next = ua.Receive("MasterOut", 3_000);
                if (next is not null && next != master) { master = next; quiet.Restart(); }
                else if (master is not null && quiet.Elapsed > TimeSpan.FromSeconds(15)) break;
            }
            if (master is null) throw new InvalidOperationException("The Module gave no Preservation Master.");
            var access = ua.Receive("AccessCopyOut", 5_000);

            var folderMaster = Path.Combine(output, "master"); Directory.CreateDirectory(folderMaster);
            foreach (var (id, bytes) in AudioObject.Files(master, "BasicAudioObjectData"))
            {
                var name = Path.Combine(folderMaster, id ?? "file"); File.WriteAllBytes(name, bytes); Console.WriteLine("Preservation Master: " + name);
            }
            if (access is not null)
                foreach (var (id, bytes) in AudioObject.Files(access, "BasicAudioObjectData")) { var name = Path.Combine(output, "access-copy" + Path.GetExtension(id ?? ".wav")); File.WriteAllBytes(name, bytes); Console.WriteLine("Access Copy: " + name); }
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static VideoFrames Frames(string folder, double fps)
    {
        var files = Directory.EnumerateFiles(folder, "*.png").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) throw new FileNotFoundException($"There are no PNG frames in {folder}.");
        int w = 0, h = 0; var frames = new List<byte[]>();
        foreach (var f in files)
        {
            var (fw, fh, pixels) = Png.Decode(File.ReadAllBytes(f));
            if (w == 0) (w, h) = (fw, fh); else if (fw != w || fh != h) throw new FormatException($"{Path.GetFileName(f)} is {fw}x{fh}, the first frame {w}x{h}: the frames are of one size.");
            frames.Add(pixels);
        }
        return new VideoFrames(w, h, fps, frames);
    }
}