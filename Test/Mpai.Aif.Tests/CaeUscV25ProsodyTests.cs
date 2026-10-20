using Mpai.Cae.Usc;
using Mpai.Cae.Usc.Ees;

namespace Mpai.Aif.Tests;

// CAE-USC V2.5, Emotion Enhanced Speech: what the SubAIMs measure of speech and change in it - the pitch, the level and the speaking rate. A voiced "syllable" is a tone of 200 Hz
// whose level rises and falls four times a second.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CaeUscV25ProsodyTests
{
    private static Audio Syllables(double seconds = 3, double hertz = 200, double perSecond = 4, double level = 8000)
    {
        const int rate = 16000;
        var s = new short[(int)(seconds * rate)];
        for (var i = 0; i < s.Length; i++)
        {
            var envelope = Math.Pow(Math.Sin(Math.PI * perSecond * i / rate), 2);               // perSecond humps in each second, silence between them
            s[i] = (short)(level * envelope * Math.Sin(2 * Math.PI * hertz * i / rate));
        }
        return new Audio(s, rate);
    }

    [Fact]
    public void ThePitchTheLevelAndTheRateOfSpeechAreMeasured()
    {
        var f = Prosody.Measure(Syllables());
        Assert.InRange(f.MeanF0Hz, 195, 205);
        Assert.InRange(f.SpeakingRate, 3.0, 5.0);                                           // four syllables a second
        var louder = Prosody.Measure(Syllables(level: 16000));
        Assert.InRange(louder.RmsDb - f.RmsDb, 5.5, 6.5);                                    // twice the amplitude: 6 dB
    }

    [Fact]
    public void ThePitchIsMultipliedAndTheLengthIsKept()
    {
        var audio = Syllables();
        var higher = Prosody.Modify(audio, 1.25, 1.0, 1.0);
        Assert.InRange(higher.Seconds, 2.9, 3.1);
        Assert.InRange(Prosody.Measure(higher).MeanF0Hz, 240, 260);
        var lower = Prosody.Modify(audio, 0.8, 1.0, 1.0);
        Assert.InRange(Prosody.Measure(lower).MeanF0Hz, 155, 165);
    }

    [Fact]
    public void TheRateIsMultipliedAndThePitchIsKept()
    {
        var audio = Syllables();
        var faster = Prosody.Modify(audio, 1.0, 1.25, 1.0);
        Assert.InRange(faster.Seconds, 2.3, 2.5);                                            // 3 s / 1.25
        var f = Prosody.Measure(faster);
        Assert.InRange(f.MeanF0Hz, 195, 205);
        Assert.InRange(f.SpeakingRate, 4.0, 6.0);                                            // five syllables a second
    }

    [Fact]
    public void TheLevelIsMultiplied()
    {
        var audio = Syllables();
        var louder = Prosody.Modify(audio, 1.0, 1.0, 2.0);
        Assert.InRange(Prosody.Measure(louder).RmsDb - Prosody.Measure(audio).RmsDb, 5.5, 6.5);
    }
}
