using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Tests;

// THE FORMS OF THE PORTS OF AUDIO SCENE MANAGEMENT (CAE Reference Models, 2026/10/26). No Port of the Module or of Audio Object Editing, Audio Scene Editing, Speech Object
// Editing and Speech Scene Editing has a Basic Data Type: they take and give the Audio Object (OSD-AUO), the Audio Scene Descriptors (OSD-ASD), the Speech Object (OSD-SPO)
// and the Speech Scene Descriptors (OSD-SSD). The tests judge the editing of ONE Basic Object or Scene at a time, so they give a Basic Object as the Object that holds it
// alone, and read the Basic Object an Object holds alone, with these: the Basic case, which is the case of one part of a Full Object or Scene (AsmFullTests judges the Full ones).
internal static class Forms
{
    // An Object brought in by a User Command (AcquiredObject): the User Command the editing AIM takes it from, since no Port of the editing AIMs takes an Object.
    public static string Acquire(string objectJson) =>
        MpaiJson.ToJson(AsmRig.Command(new UserCommandRQ { AcquiredObject = MpaiJson.FromJson<ManagedObject>(objectJson) }));

    public static string Auo(BasicAudioObject bao) => MpaiJson.ToJson(new AudioObject
    {
        MInstanceID = bao.MInstanceID ?? "", AudioObjectID = bao.BasicAudioObjectID, AudioObjectSpaceTime = new SpaceTime(),
        BasicAudioObjectCount = 1, BasicAudioObjects = [new BasicAudioObjectEntry { BAObjectIDOrBAObject = bao }]
    });
    public static string Auo(string baoJson) => Auo(MpaiJson.FromJson<BasicAudioObject>(baoJson));

    // The Basic Audio Object an Audio Object holds alone.
    public static string Bao(string auoJson) =>
        MpaiJson.FromJson<AudioObject>(auoJson) is { BasicAudioObjects: { Count: 1 } one } && one[0].BAObjectIDOrBAObject is { } bao ? MpaiJson.ToJson(bao)
        : throw new InvalidOperationException("The Audio Object does not hold one Basic Audio Object.");

    public static string Asd(BasicAudioSceneDescriptors bas) => MpaiJson.ToJson(new AudioSceneDescriptors
    {
        MInstanceID = bas.MInstanceID, AudioSceneDescriptorsID = bas.BasicAudioSceneDescriptorsID, AudioSceneDescriptorsSpaceTime = bas.BASSpaceTime ?? new SpaceTime(),
        UserPoV = bas.UserPoV, SubAudioSceneCount = 1, SubAudioScenes = [new SubAudioSceneEntry { BasicScene = bas }]
    });
    public static string Asd(string basJson) => Asd(MpaiJson.FromJson<BasicAudioSceneDescriptors>(basJson));

    public static string Bas(string asdJson) =>
        MpaiJson.FromJson<AudioSceneDescriptors>(asdJson).SubAudioScenes?.FirstOrDefault(s => s.BasicScene is not null)?.BasicScene is { } bas ? MpaiJson.ToJson(bas)
        : throw new InvalidOperationException("The Audio Scene does not hold a Basic Audio Scene.");

    public static string Spo(BasicSpeechObject bso) => MpaiJson.ToJson(new SpeechObject
    {
        MInstanceID = bso.MInstanceID, SpeechObjectID = bso.BasicSpeechObjectID, SpeechObjectSpaceTime = new SpaceTime(),
        BasicSpeechObjectCount = 1, BasicSpeechObjects = [new BasicSpeechObjectEntry { BSObjectIDOrBSObject = bso }]
    });
    public static string Spo(string bsoJson) => Spo(MpaiJson.FromJson<BasicSpeechObject>(bsoJson));

    public static string Bso(string spoJson) =>
        MpaiJson.FromJson<SpeechObject>(spoJson) is { BasicSpeechObjects: { Count: 1 } one } && one[0].BSObjectIDOrBSObject is { } bso ? MpaiJson.ToJson(bso)
        : throw new InvalidOperationException("The Speech Object does not hold one Basic Speech Object.");

    public static string Ssd(BasicSpeechSceneDescriptors bss) => MpaiJson.ToJson(new SpeechSceneDescriptors
    {
        MInstanceID = bss.MInstanceID, SpeechSceneDescriptorsID = bss.BasicSpeechSceneDescriptorsID, SpeechSceneDescriptorsSpaceTime = bss.BasicSpeechSceneDescriptorsSpaceTime,
        UserPoV = bss.UserPoV, SubSpeechSceneCount = 1, SubSpeechScenes = [new SubSpeechSceneEntry { BasicScene = bss }]
    });
    public static string Ssd(string bssJson) => Ssd(MpaiJson.FromJson<BasicSpeechSceneDescriptors>(bssJson));

    public static string Bss(string ssdJson) =>
        MpaiJson.FromJson<SpeechSceneDescriptors>(ssdJson).SubSpeechScenes?.FirstOrDefault(s => s.BasicScene is not null)?.BasicScene is { } bss ? MpaiJson.ToJson(bss)
        : throw new InvalidOperationException("The Speech Scene does not hold a Basic Speech Scene.");

    // ---- THE FULL FORMS (the author, 2026/11/08: everything in ASM is Full, a Basic Object or Scene is the case of one part) -----------------------------------------------------
    // The helpers above judge ONE Basic Object or Scene held alone: the case of one part. These make the Object or Scene of several parts.

    // A Full Audio Object of Basic Audio Objects, the part n standing at x = n inside the Object.
    public static string FullAuo(string name, params BasicAudioObject[] parts) => MpaiJson.ToJson(new AudioObject
    {
        AudioObjectID = name, AudioObjectSpaceTime = new SpaceTime(), BasicAudioObjectCount = parts.Length,
        BasicAudioObjects = parts.Select((p, n) => new BasicAudioObjectEntry
        {
            BasicAudioObjectSpaceTime = new SpaceTime { SpatialAttitude1 = AsmRig.At(n, 0) }, BAObjectIDOrBAObject = p
        }).ToList()
    });

    // A Full Audio Scene of Basic Audio Scenes (JSON of each).
    public static string FullAsd(string name, params string[] basicScenes) => MpaiJson.ToJson(new AudioSceneDescriptors
    {
        AudioSceneDescriptorsID = name, AudioSceneDescriptorsSpaceTime = new SpaceTime(), SubAudioSceneCount = basicScenes.Length,
        SubAudioScenes = basicScenes.Select(b => new SubAudioSceneEntry { SubAudioSceneSpaceTime = new SpaceTime(), BasicScene = MpaiJson.FromJson<BasicAudioSceneDescriptors>(b) }).ToList()
    });

    // A Speech Object of Basic Speech Objects, the part n standing at x = n inside the Object.
    public static string FullSpo(string name, params BasicSpeechObject[] parts) => MpaiJson.ToJson(new SpeechObject
    {
        SpeechObjectID = name, SpeechObjectSpaceTime = new SpaceTime(), BasicSpeechObjectCount = parts.Length,
        BasicSpeechObjects = parts.Select((p, n) => new BasicSpeechObjectEntry
        {
            BasicSpeechObjectSpaceTime = new SpaceTime { SpatialAttitude1 = AsmRig.At(n, 0) }, BSObjectIDOrBSObject = p
        }).ToList()
    });

    // A Full Speech Scene of Basic Speech Scenes (JSON of each).
    public static string FullSsd(string name, params string[] basicScenes) => MpaiJson.ToJson(new SpeechSceneDescriptors
    {
        SpeechSceneDescriptorsID = name, SpeechSceneDescriptorsSpaceTime = new SpaceTime(), SubSpeechSceneCount = basicScenes.Length,
        SubSpeechScenes = basicScenes.Select(b => new SubSpeechSceneEntry { SubSpeechSceneSpaceTime = new SpaceTime(), BasicScene = MpaiJson.FromJson<BasicSpeechSceneDescriptors>(b) }).ToList()
    });
}
