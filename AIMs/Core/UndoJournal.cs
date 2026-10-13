using System;
using System.Collections.Generic;

namespace Mpai.Core;

// WHAT AN AIM HAS DONE, TO UNDO IT (the author, 2026/10/11: the AIMs involved in a User Command must be able to undo). A User Command may revoke an earlier one by
// its ID (UserCommandData.RevokedUserCommandID); the AIM that executed the earlier one undoes it, and its report says whether it did. Each AIM keeps one journal: a
// command that changed something is recorded with how to put it back; a revocation finds it by its ID.
//   - THE LAST FIRST. A command is undone only when it is the last that this AIM executed and not yet undone, and what it changed has not been changed by anything
//     else since: a later command must be revoked before an earlier one. Otherwise the revocation is reported as failed, with the reason.
//   - An AIM that keeps every edit as a new version puts the version it had before back (RecordVersions); one that edits in place keeps what it overwrote.
public sealed class UndoJournal
{
    private const int Remembered = 200;
    private sealed record Entry(string CommandId, Func<bool> Unchanged, Func<string?> Undo);
    private readonly List<Entry> entries = [];

    public int Count => entries.Count;

    // A command that changed something: unchanged says that nothing else has changed it since; undo puts it back and returns what is then in force, if anything.
    public void Record(string? commandId, Func<bool> unchanged, Func<string?> undo)
    {
        if (string.IsNullOrWhiteSpace(commandId)) return;
        entries.Add(new Entry(commandId, unchanged, undo));
        if (entries.Count > Remembered) entries.RemoveAt(0);
    }

    // An AIM that keeps versions: the open one was before, and is now after. Nothing is recorded when the command left it as it was.
    public void RecordVersions(string? commandId, string? before, string? after, Func<string?> open, Action<string?> restore)
    {
        if (before == after) return;
        Record(commandId, () => open() == after, () => { restore(before); return before; });
    }

    // The revocation of a command, reported: Done with what is in force after, or Failed with why not.
    public void Revoke(string revokedId, CommandReport report)
    {
        const string action = "RevokedUserCommandID";
        var at = entries.FindLastIndex(e => e.CommandId == revokedId);
        if (at < 0) { report.Failed(action, revokedId, "this AIM executed no such User Command, or has already undone it"); return; }
        if (at != entries.Count - 1) { report.Failed(action, revokedId, "later User Commands were executed after it: revoke them first"); return; }
        if (!entries[at].Unchanged()) { report.Failed(action, revokedId, "what it changed has been changed since"); return; }
        var entry = entries[at];
        entries.RemoveAt(at);
        report.Done(action, revokedId, entry.Undo());
    }
}
