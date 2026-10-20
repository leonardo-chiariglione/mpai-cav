using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace Mpai.Cae.Usc;

// The video of a tape recorder's playback head, as the video side of CAE-ARP has it: a series of small grayscale frames.
public sealed record VideoFrames(int Width, int Height, double FramesPerSecond, IReadOnlyList<byte[]> Frames)
{
    public double Seconds => FramesPerSecond > 0 ? Frames.Count / FramesPerSecond : 0;

    // "FRM8", width, height, frames a second (32-bit, little endian), the number of frames, then each frame (Width x Height bytes).
    public byte[] ToBytes()
    {
        using var m = new MemoryStream(); using var w = new BinaryWriter(m);
        w.Write(Encoding.ASCII.GetBytes("FRM8")); w.Write(Width); w.Write(Height); w.Write((float)FramesPerSecond); w.Write(Frames.Count);
        foreach (var f in Frames) w.Write(f);
        w.Flush(); return m.ToArray();
    }

    public static VideoFrames FromBytes(byte[] b)
    {
        if (b.Length < 20 || Encoding.ASCII.GetString(b, 0, 4) != "FRM8") throw new FormatException("The video is not a series of grayscale frames (FRM8).");
        int width = BitConverter.ToInt32(b, 4), height = BitConverter.ToInt32(b, 8); var fps = BitConverter.ToSingle(b, 12); var count = BitConverter.ToInt32(b, 16);
        if (width <= 0 || height <= 0 || count < 0 || b.Length < 20 + (long)count * width * height) throw new FormatException("The series of grayscale frames is truncated.");
        return new VideoFrames(width, height, fps, Enumerable.Range(0, count).Select(i => b[(20 + i * width * height)..(20 + (i + 1) * width * height)]).ToArray());
    }
}

// THE STAND-IN FOR THE PRESERVATION AUDIO-VISUAL FILE. A real one is a video file (MP4...) that this machine has no decoder for. Here the Basic Audio-Visual Object (OSD-BMO-V1.5)
// carries two data items: the one with the ID "audio" a WAV file (the audio digitised with the video clock) and the one with the ID "video-frames" the frames of the film (VideoFrames).
// A decoder for a real video replaces this and nothing else of the AIMs that read it.
public static class AudioVisualObject
{
    public const string Header = "OSD-BMO-V1.5";

    public static string Make(string id, Audio audio, VideoFrames? frames = null)
    {
        var items = new JsonArray(new JsonObject { ["ID"] = "audio", ["Data"] = Convert.ToBase64String(audio.ToWav()) });
        if (frames is not null) items.Add(new JsonObject { ["ID"] = "video-frames", ["Data"] = Convert.ToBase64String(frames.ToBytes()) });
        return new JsonObject { ["Header"] = Header, ["BasicAudioVisualObjectID"] = id, ["BasicAudioVisualObjectData"] = items }.ToJsonString();
    }

    public static (Audio? Audio, VideoFrames? Frames) Read(string json)
    {
        Audio? audio = null; VideoFrames? frames = null;
        foreach (var (id, bytes) in AudioObject.Files(json, "BasicAudioVisualObjectData"))
        {
            if (id == "audio") audio = Audio.FromWav(bytes);
            else if (id == "video-frames") frames = VideoFrames.FromBytes(bytes);
        }
        return (audio, frames);
    }
}
