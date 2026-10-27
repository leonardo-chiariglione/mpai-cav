using AIF.Controller;
using AIF.SharedStorage;

namespace Mpai.Aif.Tests;

// THE VOICE COMMAND INTERFACE OF A MODULE THAT IS NOT BEING JUDGED THROUGH SPEECH: the AIMs of the Voice Command Interface that Audio Scene Management holds (the speech recogniser, the
// JSON Interface, the dialogue, the speech synthesiser) are built, as the Module needs them to start, as AIMs that give nothing. The tests of the editing of Objects and Scenes through
// the Controller use them; the Voice Command Interface itself is judged by VciTests, AsmVciIntegrationTests and AsmVoiceWorkflowTests, with its real AIMs.
public sealed class VciStandIn : IAimProvider
{
    private static readonly string[] Aims = ["1MMC-ASR-V2.5-I01", "1MMC-JSI-V2.5-I02", "1MMC-EDP-V2.5-I03", "1MMC-TTS-V2.5-I01"];

    public bool CanCreate(string aimName) => Aims.Contains(aimName);

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage) => new Silent(aimName);

    private sealed class Silent(string name) : IAimProcessor
    {
        public string InstanceId => name;
        public Task<Message> ProcessAsync(Message message) => Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "Nothing", Ports = new Dictionary<string, string>() });
    }
}
