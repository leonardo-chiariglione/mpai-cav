using System.Text.Json.Nodes;

namespace Mpai.Pgm;

// The AUC-XXX Data Types (PGM-UCC, UCR, UCK, UCD, UCU, UCP, UCF, UCS): one type per partner of A-User Control, Mode RQ or RS. They differ in the Header and in the names of the
// ID and Time properties, which follow the pattern AUC<partner>ID and AUC<partner>Time.
public static class Auc
{
    public const string Request = "RQ", Response = "RS";

    // The Data Type of each partner: its Header and the acronym its properties use.
    public const string Cxt = "PGM-UCC-V1.0", Prc = "PGM-UCR-V1.0", Bkn = "PGM-UCK-V1.0", Dac = "PGM-UCD-V1.0", Usr = "PGM-UCU-V1.0", Pal = "PGM-UCP-V1.0", Auf = "PGM-UCF-V1.0", Aus = "PGM-UCS-V1.0";

    public static string Acr(string header) => header switch
    {
        Cxt => "CXT", Prc => "PRC", Bkn => "BKN", Dac => "DAC", Usr => "USR", Pal => "PAL", Auf => "AUF", Aus => "AUS", _ => throw new System.ArgumentException(header)
    };

    // A Request (Mode RQ) or a Response (Mode RS) of the partner the Header names, with the properties its Data Type requires of that Mode. For a Response, requestId is the ID of the
    // Request it answers. The Instruction code of a Request is GLA in this slice: the Data Type does not say what the codes mean (open question).
    public static JsonObject Make(string header, string mode, PgmAim.Session session, string? requestId = null, string objective = "Serve the human command")
    {
        var acr = Acr(header);
        var o = new JsonObject { ["Header"] = header, ["Mode"] = mode };
        session.Stamp(o);
        o[$"AUC{acr}ID"] = PgmAim.NewId("auc" + acr.ToLowerInvariant());
        o[$"AUC{acr}Time"] = PgmAim.Time();
        var six = header is Cxt or Prc or Bkn or Dac or Usr or Pal;
        if (mode == Request && six)
        {
            o["Instruction"] = "GLA";
            o["Exchange"] = new JsonObject { ["Phase"] = "new" };
            o["Goal"] = new JsonObject { ["Objective"] = objective };
            switch (header)
            {
                case Prc: o["Operation"] = "generate"; break;
                case Usr: o["Operation"] = "estimate"; break;
                case Pal: o["Operation"] = "align"; break;
                case Dac: o["DomainContext"] = new JsonObject { ["DomainType"] = "building", ["DomainInstance"] = "demo-building" }; break;
            }
        }
        if (mode == Response)
        {
            o["RequestID"] = requestId ?? "";
            if (six) o["GoalUnderstood"] = new JsonObject { ["Objective"] = objective };
            switch (header)
            {
                case Prc: o["PromptState"] = "ready"; break;
                case Bkn: o["DeliberationState"] = "concluded"; break;
                case Usr: o["Engagement"] = new JsonObject { ["Level"] = 0.5, ["Mode"] = "interactive" }; break;
                case Pal: o["AdoptedStance"] = new JsonObject { ["Headline"] = "default A-User state" }; break;
            }
        }
        return o;
    }

    public static string IdOf(JsonObject o) => (string?)o[$"AUC{Acr((string)o["Header"]!)}ID"] ?? "";

    public static bool IsRequest(JsonObject? o) => (string?)o?["Mode"] == Request;
}
