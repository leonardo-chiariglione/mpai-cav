using System;
using System.Collections.Generic;
using System.Linq;

using Mpai.Core.OSD;

namespace Mpai.Core;

// THE ANSWERS OF SEVERAL AIMS TO ONE USER COMMAND, ONE RESPONSE under the one UserCommandID: the actions of each, in the order the answers are given. Where several AIMs answer
// one action (the same Action on the same Object), what one did stands over what the others could not: the action is the ones that were Done if any, else the first. An action that an AIM declines (it names no Object, or it says it is not supported: it is another AIM's) is not heard when another did that action. The same
// action twice with the same outcome is said once. The Outcome is what the actions say, as in any response; the lists of ListedObjects are one list.
public static class UserCommandMerge
{
    public static UserCommand Merge(string id, IReadOnlyList<UserCommand> answers)
    {
        var first = answers.FirstOrDefault();
        var actions = new List<ReportAction>();
        foreach (var group in answers.SelectMany(a => a.UserCommandRS?.Actions ?? []).GroupBy(a => (a.Action, a.ObjectID)))
        {
            var chosen = group.Any(a => a.Outcome == "Done") ? group.Where(a => a.Outcome == "Done") : group.Take(1);
            actions.AddRange(chosen.DistinctBy(a => (a.Outcome, a.ResultID, a.Reason)));
        }
        // An AIM that declines a whole action - one that names no Object (an AIM that has nothing to do with it says so) - is not heard when another AIM did that action.
        actions.RemoveAll(a => (a.ObjectID is null && a.Outcome != "Done" || a.Outcome == "NotSupported") && actions.Any(d => d.Action == a.Action && d.Outcome == "Done"));
        var listed = answers.SelectMany(a => a.UserCommandRS?.ListedObjects ?? []).ToList();
        return new UserCommand
        {
            MInstanceID = first?.MInstanceID, UEnvironmentID = first?.UEnvironmentID,
            UserCommandID = id, UserCommandTime = first?.UserCommandTime ?? SimpleTime.At(DateTimeOffset.UtcNow),
            UserCommandRS = new UserCommandRS
            {
                ReportTime = SimpleTime.At(DateTimeOffset.UtcNow),
                Outcome = actions.Count == 0 ? "Ignored" : actions.All(a => a.Outcome == "Done") ? "Done" : actions.Any(a => a.Outcome == "Done") ? "Partly" : "Failed",
                Actions = actions,
                ListedObjects = listed.Count > 0 ? listed : null
            }
        };
    }
}
