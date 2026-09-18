using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Mpai.Rca;

// WHICH DEVICE A DATA TYPE COMES FROM, AND WHICH ONE IT GOES TO.
//
// A workflow says "acquire UserFace (OSD-BVO-V1.5)" and nothing about a camera;
// it says "present MachineSpeech, MachineFace" and nothing about a loudspeaker or
// an avatar. Something must know that an OSD-BVO is got by looking, an OSD-BSO by
// listening, an OSD-STM by asking the clock - and this is that something.
//
// It is the parallel of the Port-data serialiser registry, and for the same
// reason: one entry per Data Type, registered once, and no application anywhere
// in it. A Remote Client Application that knew which application it was running
// would not be one.
//
// ACQUISITION RETURNS AN OBJECT, NOT BYTES. Whatever a device produces arrives
// with the Qualifier the device determined - the sampling frequency it captured
// at, the format it wrote - because a consumer that must guess will guess wrong
// and say nothing about it.
public sealed class DeviceRegistry
{
    // Gets a datum of this Data Type from the real world. The flag is the
    // workflow's "via VAD": wait for the speaker to stop, rather than for a fixed
    // interval or a button.
    public delegate Task<string?> Acquire(bool viaVad);

    // Renders a datum of this Data Type. Several may be presented together - an
    // OSD-BSO and a PAF-FDO are one utterance, not two - so a presenter receives
    // everything being presented at once and takes what it recognises.
    public delegate Task Present(IReadOnlyDictionary<string, string> byDataType);

    private readonly Dictionary<string, List<(string Qualifier, Acquire How)>> sources =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<(string Name, Present Render)> presenters = new();

    // A DATA TYPE MAY HAVE SEVERAL SOURCES, AND THE QUALIFIER TELLS THEM APART.
    // A Visual Object may be a webcam frame or a file chosen from disk: the same
    // Data Type, different formats, and the difference is not in the data.
    //
    // The qualifier named here is what this source produces.
    public DeviceRegistry RegisterAcquire(string dataType, string qualifier, Acquire how)
    {
        if (!sources.TryGetValue(dataType, out var list))
            sources[dataType] = list = new List<(string, Acquire)>();
        list.Add((qualifier, how));
        return this;
    }

    public DeviceRegistry RegisterAcquire(string dataType, Acquire how) =>
        RegisterAcquire(dataType, "", how);

    // What this client can produce for a Data Type. A refusal says this, so that
    // an App naming a format nobody has can be corrected rather than guessed at.
    public IReadOnlyList<string> QualifiersFor(string dataType) =>
        sources.TryGetValue(dataType, out var list)
            ? list.Select(s => s.Qualifier).Where(q => q.Length > 0).ToList()
            : new List<string>();

    // A presenter is not keyed by Data Type, because rendering is not one datum at
    // a time. The avatar wants the speech and the face descriptors together; a
    // screen wants the text. Each is offered everything and takes what it knows.
    public DeviceRegistry RegisterPresent(string name, Present render)
    {
        presenters.Add((name, render));
        return this;
    }

    public bool CanAcquire(string dataType) => sources.ContainsKey(dataType);

    // ASKED WHEN THE WORKFLOW DID NOT SAY AND MORE THAN ONE IS POSSIBLE. The
    // client puts the choice to the person; a client that cannot ask uses the
    // first it has.
    public Func<string, IReadOnlyList<string>, Task<string?>>? Ask { get; set; }

    // WAITING FOR THE PERSON. The word is the App's and the client shows it on a
    // button; a client that cannot wait proceeds, which is what a console does.
    public Func<string, Task>? Await { get; set; }

    public async Task<string?> AcquireAsync(string dataType, bool viaVad, string? qualifier = null)
    {
        if (!sources.TryGetValue(dataType, out var list) || list.Count == 0)
            throw new NotSupportedException(
                $"This client acquires no {dataType}.");

        // NAMED, AND EITHER SATISFIED OR REFUSED WITH WHAT IS AVAILABLE. A
        // workflow that asks for a format nobody has should be correctable,
        // which means the refusal must say what there is.
        if (!string.IsNullOrWhiteSpace(qualifier))
        {
            foreach (var s in list)
                if (string.Equals(s.Qualifier, qualifier, StringComparison.OrdinalIgnoreCase))
                    return await s.How(viaVad);

            var have = QualifiersFor(dataType);
            throw new NotSupportedException(
                $"This client cannot acquire a {dataType} as {qualifier}. " +
                (have.Count == 0
                    ? "It states no format for what it can acquire."
                    : "It can acquire: " + string.Join(", ", have) + "."));
        }

        if (list.Count == 1) return await list[0].How(viaVad);

        // Unstated, and more than one possible: the person decides.
        if (Ask is not null)
        {
            var chosen = await Ask(dataType, QualifiersFor(dataType));
            foreach (var s in list)
                if (string.Equals(s.Qualifier, chosen, StringComparison.OrdinalIgnoreCase))
                    return await s.How(viaVad);
            return null;                       // the person declined
        }

        return await list[0].How(viaVad);
    }

    public async Task PresentAsync(IReadOnlyDictionary<string, string> byDataType)
    {
        foreach (var (_, render) in presenters)
            await render(byDataType);
    }

    public IReadOnlyCollection<string> KnownAcquisitions => sources.Keys;
}