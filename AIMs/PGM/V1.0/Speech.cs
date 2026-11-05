using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Pgm;

// What CXT needs when the human spoke: the words of a Speech Object. Whisper in the real implementation; a test supplies its own.
public interface ISpeechRecogniser
{
    Task<string> RecogniseAsync(JsonObject speechObject);
}

// What AUF needs to say the Final Response: a Speech Object from a text.
public interface ISpeechSynthesiser
{
    Task<JsonObject> SynthesiseAsync(string text);
}

// The speech synthesiser used when no voice is configured: a Speech Object of silence as long as the words take to say. It is valid Data, and it is a stub.
public sealed class SilentSynthesiser : ISpeechSynthesiser
{
    public Task<JsonObject> SynthesiseAsync(string text)
    {
        var seconds = Math.Max(0.5, text.Length / 15.0);
        var speech = BasicSpeechObject.FromData(new byte[(int)(16000 * 2 * seconds)], new SpeechQualifier
        {
            SpeechQualifierID = PgmAim.NewId("sq"),
            Format = new SpeechFormat
            {
                ContentFormats = new SpeechContentFormats { RawData = new Pcm { SamplingFrequency = 16000, Precision = 16 } },
                TransportFormats = new SpeechTransportFormats { FileFormat = SpeechFileFormat.Wav }
            }
        });
        return Task.FromResult((JsonObject)JsonNode.Parse(MpaiJson.ToJson(speech))!);
    }
}
