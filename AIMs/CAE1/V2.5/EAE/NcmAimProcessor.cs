using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Cae.Usc.Eae;

// CAE-NCM-V2.5, Noise Cancellation Module: the coefficients of the sound field (Audio Object, port 1), the sources in the transform domain (Audio Object, port 2), where the sources are (Audio
// Scene Geometry) and the Audio Scene Reference in; the sources in the transform domain, with less noise, out.
//
// The noise is MEASURED where there is only noise: in the blocks in which no source is active (all the channels of the sources are silent, as Speech Detection and Separation leaves them).
// There the mean power of each coefficient of the field, bin by bin, is the noise of the field; the noise of a source is what that gives through the beam that picks the source (the beam
// weights are those Speech Detection and Separation used, found again from the directions of the Audio Scene Geometry), the coefficients taken as not related. The channel of a source then
// loses the noise in each of the bins of the blocks where it is active, by spectral subtraction on the power (Over times the noise taken away, at most down to Floor of the power).
// Where no block is silent the noise cannot be measured and the AIM is a passthrough, which the page allows. The page also says the module cancels the REVERBERATION: this stand-in does
// not. The Audio Scene Reference is read for what the page says it is for and is not used: see Speech Detection and Separation.
public sealed class NcmAimProcessor(string instanceId, string shdKey, string transformKey, string geometryKey, string asrKey, string outputKey) : IAimProcessor
{
    public string InstanceId { get; } = instanceId;
    public string AudioSceneReferenceKey { get; } = asrKey;
    public double Over { get; init; } = 1.5;
    public double Floor { get; init; } = 0.05;

    public Task<Message> ProcessAsync(Message message)
    {
        string? Get(string key) => message.Ports.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        var shdJson = Get(shdKey); var transformJson = Get(transformKey); var geometryJson = Get(geometryKey);
        // No source, no sources to clean: the sound field came and nothing was separated from it (a quiet room). That is not a fault: nothing is given.
        if (shdJson is not null && transformJson is null && geometryJson is null)
            return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "NothingToClean", DataType = AudioObject.Header, Payload = "", Ports = new Dictionary<string, string>() });
        if (shdJson is null || transformJson is null || geometryJson is null)
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-NCM-V2.5: it needs the Spherical Harmonic Decomposition Coefficients, the Transform Audio and the Audio Scene Geometry."));
        try
        {
            var (shd, _) = TransformObject.Read(shdJson); var (sources, geometry) = TransformObject.Read(transformJson);
            var directions = SceneDescriptors.Read(geometryJson).Select(s => (s.Azimuth, s.Elevation)).ToList();
            if (shd.Blocks.Count != sources.Blocks.Count || shd.BlockSize != sources.BlockSize) throw new FormatException("The coefficients and the sources are not of the same blocks.");
            if (directions.Count != sources.Channels) throw new FormatException($"The Audio Scene Geometry has {directions.Count} sources and the Transform Audio {sources.Channels} channels.");
            var k = shd.Channels; var order = (int)Math.Round(Math.Sqrt(k)) - 1;
            if (k < 4 || (order + 1) * (order + 1) != k) throw new FormatException($"The coefficients are {k}: it needs (N + 1)^2 of them, N at least one.");

            var silent = Enumerable.Range(0, sources.Blocks.Count).Where(n => sources.Blocks[n].All(ch => ch.All(v => v == Complex.Zero))).ToArray();
            var blocks = sources.Blocks;
            if (silent.Length > 0)
            {
                var weights = SdsAimProcessor.BeamWeights(directions, order); var bins = sources.Bins; var s = sources.Channels;
                var power = Enumerable.Range(0, k).Select(c => Enumerable.Range(0, bins).Select(b => silent.Average(n => shd.Blocks[n][c][b].Real * shd.Blocks[n][c][b].Real + shd.Blocks[n][c][b].Imaginary * shd.Blocks[n][c][b].Imaginary)).ToArray()).ToArray();
                var noise = new double[s][];                                                                    // the noise power of each source, each bin
                for (var i = 0; i < s; i++)
                {
                    noise[i] = new double[bins];
                    for (var b = 0; b < bins; b++)
                        for (var c = 0; c < k; c++) noise[i][b] += weights[c, i].Magnitude * weights[c, i].Magnitude * power[c][b];
                }
                blocks = sources.Blocks.Select(block => block.Select((ch, i) =>
                {
                    var result = new Complex[bins];
                    for (var b = 0; b < bins; b++)
                    {
                        var p = ch[b].Real * ch[b].Real + ch[b].Imaginary * ch[b].Imaginary;
                        result[b] = p <= 0 ? Complex.Zero : ch[b] * Math.Sqrt(Math.Max(Floor, 1 - Over * noise[i][b] / p));
                    }
                    return result;
                }).ToArray()).ToList();
            }
            var text = TransformObject.Make("ncm-" + Guid.NewGuid().ToString("N")[..12], new TransformBlocks(sources.Rate, sources.BlockSize, sources.Samples, blocks), "Transform", geometry);
            return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "BasicAudioObject", DataType = AudioObject.Header, Payload = text, Ports = new Dictionary<string, string> { [outputKey] = text } });
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-NCM-V2.5: " + ex.Message));
        }
    }
}
