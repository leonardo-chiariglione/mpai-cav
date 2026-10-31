using System.Linq;
using System.Text.Json.Nodes;

namespace Mpai.Aih.Hdp;

// THE PROFILES OF THE HEALTH FRONT END AND THE HEALTH BACK END: there is one Front End, and what it does depends on the User Profile it ingests. An End User - an individual - works
// on its own AIH Data. A Third-Party User - a Third-Party User Profile, or an individual who is a member of an organisation - asks for AIH Data to be processed as its licence prescribes.
internal static class Profiles
{
    public static bool IsThirdParty(JsonObject? registerRequest)
    {
        if (registerRequest is null) return false;
        if (registerRequest["ThirdPartyUserProfile"] is JsonObject) return true;
        return (registerRequest["UserProfile"]?["UserProfileData"] as JsonArray)?.OfType<JsonObject>()
                   .Any(entry => entry["Human"]?["Membership"] is JsonArray { Count: > 0 }) == true;
    }
}
