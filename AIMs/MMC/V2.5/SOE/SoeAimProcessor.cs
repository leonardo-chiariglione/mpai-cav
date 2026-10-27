using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;
using AIF.SharedStorage;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Mmc.Soe;

// MMC-SOE-V2.5 - Speech Object Editing, as the author's Reference Model of Audio
// Scene Management draws it (CAE Reference Models, 2026/10/26): User Command, User
// PoV and a Speech Object (OSD-SPO) in; the Speech Object as edited, and the User
// Command with its response, out. The Object goes on to Speech Scene Editing. There is
// no Basic Speech Object on a Port: a Speech Object is a collection of Basic Speech
// Objects, and one that holds a single Basic Speech Object is that Object.
//
// OPEN BY PORT, ACT BY COMMAND, as Audio Object Editing does. A Speech Object arriving
// that holds one Basic Speech Object opens that Object when Shared Storage holds it, and
// keeps it when it is new. One that holds more is kept as a Speech Object (its Basic
// Speech Objects as new Objects) and is the Speech Object given back; the open Object
// stays. Then a User Command acts on what is open:
//
//   UserPoV            where it is heard from, when heard on its own
//   ChangedObjects     its Qualifier (a Speech Qualifier)
//   TranslatedObjects  translated by Text and Speech Translation (MMC-TST): the Speech
//                      and a Language Selector go out; the Translation, when it comes
//                      back, replaces the original under its identifier (the author,
//                      2026/10/03) - so every Speech Scene holding it holds the
//                      translation, at its place.
//
// A SPEECH OBJECT IS NAMED BY THE USER (the author, 2026/10/26): in the data, the commands and
// the replies it is identified only by its name - "hello" - the value of its
// BasicSpeechObjectID or SpeechObjectID; the Module's storage keys never leave it. A second
// Object given a name that is taken is "hello 2". An Object arriving with no name is not
// kept: the report says that a name is needed. ListedObjects lists the Speech Objects the
// Module keeps, by name. A placement is the command's amount: a ChangedObjects without
// the Spatial Attitude's position is reported as failed, not guessed.
//
// A Speech Object is kept under its own name, changed in place.
public sealed class SoeAimProcessor : IAimProcessor
{
    private readonly ISharedStorage _storage;

    private readonly string _commandPort;
    private readonly string _povPort;
    private readonly string _objectPort;        // OSD-SPO #1
    private readonly string _translationPort;   // OSD-BSO #2, from Text and Speech Translation (it handles Basic Speech Objects)
    private readonly string _objectOut;         // OSD-SPO #1
    private readonly string _speechOut;         // OSD-BSO #2, to Text and Speech Translation
    private readonly string _languageOut;       // OSD-SEL
    private readonly string _reportOut;

    private string? _open;
    private readonly UndoJournal _undo = new();

    // The translation asked for and not yet arrived, and the report that waits for it:
    // in continuous execution the command and the translation come in different runs.
    private (ObjectTranslation Entry, UserCommand Command)? _pending;

    public string InstanceId { get; }

    public SoeAimProcessor(string instanceId, ISharedStorage storage, AimPortReader ports)
    {
        InstanceId       = instanceId;
        _storage         = storage;
        _commandPort     = ports.Input("CAE-UCM-V1.0");
        _povPort         = ports.InputOrDefault("OSD-OPV-V1.5", "");
        _objectPort      = ports.InputOrDefault("OSD-SPO-V1.5", 1, "");
        _translationPort = ports.InputOrDefault("OSD-BSO-V1.5", 2, "");
        _objectOut       = ports.Output("OSD-SPO-V1.5", 1);
        _speechOut       = ports.OutputOrDefault("OSD-BSO-V1.5", 2, "");
        _languageOut     = ports.OutputOrDefault("OSD-SEL-V1.5", "");
        _reportOut       = ports.OutputOrDefault("CAE-UCM-V1.0", "");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        var outputs = new Dictionary<string, string>();
        string? In(string port) =>
            port.Length > 0 && message.Ports.TryGetValue(port, out var json) && !string.IsNullOrWhiteSpace(json) ? json : null;

        // 1. opening, or keeping, by Port: the Object the Module keeps under the name, when it is the same speech; any other is a new one, under that name or the next
        // free one. An Object with no name is not kept.
        string? several = null;
        string? unnamed = null;     // why an arriving Object was not taken, to be reported
        string? acquired = null;    // the name of the Object the User Command gave (AcquiredObject), taken
        var arrivedCommand = In(_commandPort) is { } givenCommand ? CommandReader.Read(givenCommand, out _) : null;
        var objectJson = In(_objectPort);

        // The Object the User Command gives (AcquiredObject), in full or by its name: a Speech Object is taken as an Object that arrives; a name opens the Object the Module
        // keeps under it. An Audio or Visual Object is not for this AIM.
        if (objectJson is null && arrivedCommand?.UserCommandRQ?.AcquiredObject is { } acquiring)
        {
            if (acquiring.SpeechObject is { } speechObject) { objectJson = MpaiJson.ToJson(speechObject); acquired = speechObject.BasicSpeechObjectID; }
            else if (acquiring.AudioObject is null && acquiring.BasicAudioObject is null && acquiring.VisualObject is null && acquiring.ObjectID is { Length: > 0 } wanted)
            {
                if (SpeechStore.Get(_storage, wanted) is not null) { _open = wanted; acquired = wanted; }
                else unnamed = $"the Module keeps no Speech Object named {wanted}";
            }
        }
        if (objectJson is not null)
        {
            try
            {
                var arrived = SpeechStore.Parse(objectJson);
                if (SpeechStore.Alone(arrived) is { } speech)
                {
                    if (NameBook.Clean(speech.BasicSpeechObjectID) is null) throw new NameNeededException("the Basic Speech Object");
                    _open = SpeechStore.Get(_storage, speech.BasicSpeechObjectID) is not null && SpeechStore.IsTheSpeech(_storage, speech) ? speech.BasicSpeechObjectID.Trim() : SpeechStore.Keep(_storage, speech);
                    Console.WriteLine($"[MMC-SOE-V2.5] open {_open}");
                }
                else
                {
                    several = SpeechStore.KeepObject(_storage, arrived);
                    Console.WriteLine($"[MMC-SOE-V2.5] speech object {several}");
                }
            }
            catch (NameNeededException e) { unnamed = $"{e.Message}: the Object is named in its identifier (BasicSpeechObjectID, SpeechObjectID)"; }
            catch (InvalidOperationException e) { unnamed = e.Message; }
        }

        // 2. a User PoV on its Port.
        if (In(_povPort) is { } povJson && Open() is { } heard)
            SpeechStore.Put(_storage, SpeechStore.With(heard, userPoV: MpaiJson.FromJson<PointOfView>(povJson)));

        // 3. a translation come back: it replaces its original, and the command is reported.
        if (In(_translationPort) is { } translationJson && _pending is { } pending)
        {
            _pending = null;
            var report = new CommandReport(pending.Command);
            var id = IdOf(pending.Entry.ObjectID);
            if (id is not null && SpeechStore.Get(_storage, id) is { } original && MpaiJson.FromJson<BasicSpeechObject>(translationJson) is { } translation)
            {
                SpeechStore.Put(_storage, SpeechStore.Replacing(original, translation, pending.Entry.SpeechQualifier));
                _open = id;
                // what the translation replaced is kept: revoking the command puts the original back, in every Scene that holds it
                _undo.Record(pending.Command.UserCommandID, () => true, () => { SpeechStore.Put(_storage, original); return id; });
                report.Done("TranslatedObjects", id, id);
                Console.WriteLine($"[MMC-SOE-V2.5] {id} replaced by its translation into {pending.Entry.TargetLanguage}");
            }
            else report.Failed("TranslatedObjects", id, "no such Speech Object to replace");
            if (_reportOut.Length > 0) outputs[_reportOut] = MpaiJson.ToJson(report.Report());
        }

        // 4. acting, by Command.
        if (In(_commandPort) is { } commandJson)
        {
            var command = CommandReader.Read(commandJson, out var rejection);
            if (command is null) { if (_reportOut.Length > 0) outputs[_reportOut] = MpaiJson.ToJson(rejection); goto opened; }
            var report = new CommandReport(command);
            if (unnamed is not null) report.Failed("AcquiredObject", null, unnamed);
            else if (acquired is not null) report.Done("AcquiredObject", _open ?? acquired);
            if (command.UserCommandRQ?.RevokedUserCommandID is { } revoked) { _undo.Revoke(revoked, report); outputs[_reportOut] = MpaiJson.ToJson(report.Report()); goto opened; }
            var waiting = Apply(command, report, outputs);
            if (_overwritten.Count > 0)
            {
                var kept = _overwritten.ToList(); _overwritten.Clear();
                _undo.Record(command.UserCommandID, () => true, () => { for (var i = kept.Count - 1; i >= 0; i--) SpeechStore.Put(_storage, kept[i]); return _open; });
            }
            // A translation is reported when it comes back; anything else now.
            if (!waiting && _reportOut.Length > 0) outputs[_reportOut] = MpaiJson.ToJson(report.Report());
        }
        else if (unnamed is not null && _reportOut.Length > 0)
        {
            // An Object that came by its Port and was not taken: no command to report under, so a report of its own.
            var report = new CommandReport(new UserCommand { UserCommandID = Guid.NewGuid().ToString() });
            report.Failed("AcquiredObject", null, unnamed);
            outputs[_reportOut] = MpaiJson.ToJson(report.Report());
        }

        // 5. the Speech Object: one stored in this run, else the open Object as a Speech Object of one.
        opened:
        var given = several is not null ? SpeechStore.GetObject(_storage, several) : Open() is { } open ? SpeechStore.Wrap(open) : null;
        if (given is not null) outputs[_objectOut] = MpaiJson.ToJson(given);
        return Task.FromResult(new Message
        {
            MessageId   = Guid.NewGuid().ToString(),
            MessageType = given is null ? "NoOpenObject" : "SpeechObject",
            DataType    = given is null ? "" : "OSD-SPO-V1.5",
            Ports       = outputs
        });
    }

    private const string SpeechQualifierHeader = "TFA-SPQ-V1.5";

    // What the command being executed overwrites, in the order it does: put back in the reverse order when it is revoked.
    private readonly List<BasicSpeechObject> _overwritten = new();
    private void Keep(BasicSpeechObject speech) => _overwritten.Add(speech);

    private BasicSpeechObject? Open() => _open is null ? null : SpeechStore.Get(_storage, _open);

    // True when the command waits for a translation, which then reports it.
    private bool Apply(UserCommand command, CommandReport report, Dictionary<string, string> outputs)
    {
        var data = command.UserCommandRQ;
        if (data is null) return false;
        if (data.ListedObjects is { } listing) List(listing, report);
        const string inScene = "where an Object is in a Scene is Speech Scene Editing's";
        foreach (var (action, any) in new[] { ("AddedObjects", data.AddedObjects is { Objects.Count: > 0 }),
                                              ("MovedObjects", data.MovedObjects is { Objects.Count: > 0 }),
                                              ("RemovedObjects", data.RemovedObjects is { Objects.Count: > 0 }) })
            if (any) report.NotSupported(action, _open, inScene);
        if (data.ModifiedObjects is { Objects.Count: > 0 })
            report.NotSupported("ModifiedObjects", _open, "a Speech Object has no Acoustic Profile to modify");

        if (data.UserPoV is not null)
        {
            if (Open() is { } speech) { Keep(speech); SpeechStore.Put(_storage, SpeechStore.With(speech, userPoV: data.UserPoV)); report.Done("UserPoV", _open, _open); }
            else report.Failed("UserPoV", null, "no Basic Speech Object is open");
        }

        if (data.ChangedObjects is { Objects.Count: > 0 } changed)
            foreach (var entry in changed.Objects)
            {
                var id = IdOf(entry.ObjectID) ?? _open;
                var speech = id is null ? null : SpeechStore.Get(_storage, id);
                if (speech is null) { report.Failed("ChangedObjects", id, "no such Basic Speech Object"); continue; }
                SpeechQualifier? qualifier = null;
                if (entry.Qualifier is { } q && System.Text.Json.JsonSerializer.Serialize(q) is var qJson &&
                    System.Text.Json.Nodes.JsonNode.Parse(qJson)?["Header"]?.GetValue<string>() == SpeechQualifierHeader)
                    qualifier = MpaiJson.FromJson<SpeechQualifier>(qJson);
                if (qualifier is null && entry.SpatialAttitude is null) { report.Failed("ChangedObjects", id, "nothing to change: a Speech Qualifier or a Spatial Attitude is required"); continue; }
                if (entry.SpatialAttitude is not null && Amounts.PlaceProblem(entry.SpatialAttitude) is { } problem) { report.Failed("ChangedObjects", id, problem); continue; }
                Keep(speech);
                SpeechStore.Put(_storage, SpeechStore.With(speech, qualifier: qualifier,
                    spaceTime: entry.SpatialAttitude is null ? null : new SpaceTime { SpatialAttitude1 = entry.SpatialAttitude }));
                report.Done("ChangedObjects", id, id);
            }

        if (data.TranslatedObjects is { Objects.Count: > 0 } translated)
        {
            var entry = translated.Objects[0];
            var id = IdOf(entry.ObjectID);
            var speech = id is null ? null : SpeechStore.Get(_storage, id);
            if (_speechOut.Length == 0 || _languageOut.Length == 0) report.Failed("TranslatedObjects", id, "this AIM has no Port to Text and Speech Translation");
            else if (speech is null) report.Failed("TranslatedObjects", id, "no such Basic Speech Object");
            else if (entry.TargetLanguage is not { Length: > 0 } target) report.Failed("TranslatedObjects", id, "no target language");
            else
            {
                var source = speech.SpeechQualifier?.Attributes?.Metadata?.Language?.LanguageCode;
                outputs[_speechOut]   = MpaiJson.ToJson(speech);
                outputs[_languageOut] = MpaiJson.ToJson(BasicSelectorObject.Languages(source, target));
                Console.WriteLine($"[MMC-SOE-V2.5] translating {id} from {source ?? "its language"} into {target}");
                for (var i = 1; i < translated.Objects.Count; i++)
                    report.NotSupported("TranslatedObjects", IdOf(translated.Objects[i].ObjectID), "one Speech Object at a time");
                _pending = (entry, command);
                return true;
            }
        }
        return false;
    }

    private void List(ObjectListing listing, CommandReport report)
    {
        if (!ListKinds.Known(listing.Kind)) { report.Failed("ListedObjects", null, $"the Kind is required: one of {string.Join(", ", ListKinds.Every)}"); return; }
        if (listing.Kind != ListKinds.All && ListKinds.ListedBy(listing.Kind) != "Speech Object Editing")
        { report.NotSupported("ListedObjects", null, $"the {listing.Kind}s are listed by {ListKinds.ListedBy(listing.Kind)}"); return; }
        if (listing.ObjectID is not null && listing.Kind == ListKinds.All) { report.Failed("ListedObjects", listing.ObjectID, "ObjectID names one thing of one Kind: the Kind is required, not All"); return; }
        var listed = SpeechStore.Listing(_storage, listing.Kind!, NameBook.Clean(listing.ObjectID));
        if (listing.ObjectID is not null && listed.Count == 0) { report.Failed("ListedObjects", listing.ObjectID, $"the Module keeps no {listing.Kind} of that name"); return; }
        report.Listed(listed);
    }

    private static string? IdOf(ManagedObject? managed) =>
        managed is null ? null
        : !string.IsNullOrWhiteSpace(managed.ObjectID) ? managed.ObjectID
        : !string.IsNullOrWhiteSpace(managed.SpeechObject?.BasicSpeechObjectID) ? managed.SpeechObject!.BasicSpeechObjectID
        : null;
}
