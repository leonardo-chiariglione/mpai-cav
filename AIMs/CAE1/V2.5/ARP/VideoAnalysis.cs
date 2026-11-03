using System;
using System.Collections.Generic;
using System.Linq;

namespace Mpai.Cae.Usc.Arp;

// What Video Analysis for Preservation sees in the film of the playback head: a sudden change of brightness between two frames (a different tape - leader, another colour - or a
// splice passing the head: it changes the brightness twice) or an abrupt change of what is in the frame. The changes close to one another (within a second and a half: a splice of some centimetres takes as long to pass the head at the slower speeds) are one irregularity.
public static class VideoAnalysis
{
    public const double BrightnessStep = 25, FrameChange = 40;

    public static IReadOnlyList<(double Start, double End)> Detect(VideoFrames video)
    {
        var frames = video.Frames; var events = new List<int>();
        double previousMean = 0;
        for (var t = 0; t < frames.Count; t++)
        {
            var mean = frames[t].Select(b => (double)b).Average();
            if (t > 0)
            {
                double diff = 0; for (var i = 0; i < frames[t].Length; i++) diff += Math.Abs(frames[t][i] - frames[t - 1][i]); diff /= frames[t].Length;
                if (Math.Abs(mean - previousMean) >= BrightnessStep || diff >= FrameChange) events.Add(t);
            }
            previousMean = mean;
        }
        var result = new List<(double, double)>();
        for (var i = 0; i < events.Count;)
        {
            var j = i; while (j + 1 < events.Count && events[j + 1] - events[j] <= video.FramesPerSecond * 1.5) j++;
            result.Add((Math.Max(0, (events[i] - 1) / video.FramesPerSecond), (events[j] + 1) / video.FramesPerSecond));
            i = j + 1;
        }
        return result;
    }

    // The frame at a time, as a PNG.
    public static byte[] ImageAt(VideoFrames video, double seconds)
    {
        var index = Math.Clamp((int)Math.Round(seconds * video.FramesPerSecond), 0, video.Frames.Count - 1);
        return Png.Encode(video.Frames[index], video.Width, video.Height);
    }
}
