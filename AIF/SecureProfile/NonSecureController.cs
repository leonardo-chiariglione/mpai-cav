namespace AIF.SecureProfile;

// THE NON-SECURE CONTROLLER (Zero Trust and Profiles 6.2): it interfaces with the User Agent and accesses the non-secure parts of the AIF. It relays the frames
// of the User Agent to the Secure Controller and their replies back. The data of the Module they carry is sealed with a key it does not have: it relays bytes
// it cannot read and cannot alter without the Secure Controller - or the User Agent - refusing them.
//
// What it saw is counted (Relayed, Bytes); Tamper, for tests, lets a test act as a Non-Secure Controller that is not honest.
public sealed class NonSecureController : IFrameLink
{
    private readonly IFrameLink secure;

    public NonSecureController(IFrameLink secure) => this.secure = secure;

    public int Relayed { get; private set; }
    public long Bytes { get; private set; }

    // Every frame it relayed, both ways, as it saw them (for tests: what a Non-Secure Controller can see).
    public List<byte[]> Seen { get; } = [];

    public Func<byte[], byte[]>? Tamper { get; set; }

    public byte[] Send(byte[] frame)
    {
        lock (Seen)
        {
            Relayed++;
            Bytes += frame.Length;
            Seen.Add(frame);
        }
        var reply = secure.Send(Tamper is { } t ? t(frame) : frame);
        lock (Seen) Seen.Add(reply);
        return reply;
    }
}
