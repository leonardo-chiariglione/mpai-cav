using System.Globalization;
using System.Text.Json.Nodes;

namespace Mpai.Mmm.Client;

// THE WORLD OF THE MMM, AS THE M-INSTANCE HAS IT (MMM/World/world.json, written by make_world.py): the Metaverse Square, the buildings on it and the rooms in them.
// A building is a Location of three dimensions - a Location Object with its Sub-Locations, the rooms - rendered by a 3D Model Object (glTF); the Square is a Location of two.
// A door is a Right: a Process that holds the Right to move its Persona to the Location behind the door may enter, and the others are refused (403). The buildings and
// their rooms are the AVS Service's: not public. The Town Hall is the meeting's: its meeting room holds the table and the seats.
public static class World
{
    // The Rights the AVS Service gives a User it invites, and takes back when it takes the User out: to move any of its Personae through the doors of the Town Hall.
    public static string DoorID(string user, string location) => $"PA-Door-{user}-{location}";
    public static JsonObject DoorPA(string user, string location) =>
        Uc2.PA("MMM-2MP-V2.2", "MMMove", DoorID(user, location), ("RQ3", "To", "PointOfView2", Uc2.PointOfView(location)));

    public static JsonObject Load(string repo) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(repo, "MMM", "Viewer", "world", "world.json")))!.AsObject();

    // The Square, the buildings and the rooms as Locations, and the seats of the meeting room; the Services of the meeting. Returns the world as read.
    public static JsonObject Setup(MInstance m, string repo)
    {
        var world = Load(repo);
        var sq = world["square"]!;
        m.AddLocation((string)sq["id"]!, isPublic: true, x: (double)sq["x"]!, y: (double)sq["y"]!, size: (double)sq["size"]!);
        m.AddUser("Seller", "human0");
        foreach (var b in world["buildings"]!.AsArray().OfType<JsonObject>())
        {
            var fp = b["footprint"]!.AsArray().Select(v => (double)v!).ToArray();
            m.AddLocation((string)b["id"]!, owner: Meeting.Avs, x: (double)b["x"]!, y: (double)b["y"]!, size: Math.Max(fp[2] - fp[0], fp[3] - fp[1]));
            m.Get((string)b["id"]!)!.Model = (string)b["model"]!;
            foreach (var r in b["rooms"]!.AsArray().OfType<JsonObject>())
            {
                var rf = r["footprint"]!.AsArray().Select(v => (double)v!).ToArray();
                m.AddLocation((string)r["id"]!, owner: Meeting.Avs, x: (double)r["x"]!, y: (double)r["y"]!, size: Math.Max(rf[2] - rf[0], rf[3] - rf[1]), at: (string)b["id"]!);
                m.Get((string)r["id"]!)!.Body = RoomLocationObject(r);
                if ((string)r["id"]! != Meeting.Room) continue;
                Meeting.ParcelX = (double)b["x"]!; Meeting.ParcelY = (double)b["y"]!;
                Meeting.Seats = r["seats"]!.AsArray().OfType<JsonObject>().Select(s => ((string)s["id"]!, (double)s["x"]!, (double)s["y"]!, (double)s["yaw"]!)).ToArray();
                foreach (var s in Meeting.Seats) m.AddLocation(s.ID, owner: Meeting.Avs, x: s.X, y: s.Y, size: 0.8, perceptible: false, at: Meeting.Room);
            }
        }
        Meeting.DoorsNeedRights = true;
        Meeting.Doors = [Meeting.Parcel, Meeting.Room];
        m.AddService(Meeting.Avs);
        m.AddService(Meeting.Asr);
        m.AddService(Meeting.Secretary, Meeting.SecretaryPersona, "Persona2.glb");
        return world;
    }

    // The meeting room as an OSD Location Object: a Location of the M-Instance, Virtual (Location Qualifier), whose Attributes carry its Acoustic Profile (CAE3: the Scene's
    // reflectivity, reverberation, diffusion and absorption), which a client renders the speech of the room with.
    public static JsonObject RoomLocationObject(JsonObject room)
    {
        var f = room["footprint"]!.AsArray().Select(v => (double)v!).ToArray();
        var id = (string)room["id"]!;
        return new JsonObject
        {
            ["Header"] = "OSD-BLC-V1.5", ["MInstanceID"] = "MI1", ["BasicLocationObjectID"] = id,
            ["BasicLocationObjectSpaceTime"] = new JsonObject { ["Extent"] = new JsonArray(f[2] - f[0], f[3] - f[1], (double)room["height"]!) },
            ["LocationQualifier"] = new JsonObject
            {
                ["Header"] = "TFA-LCQ-V1.5", ["LocationQualifierID"] = id + "-Q", ["SubTypes"] = new JsonObject { ["Categories"] = new JsonArray("Virtual") },
                ["Attributes"] = new JsonObject { ["AcousticProfile"] = room["acoustics"]!.DeepClone() }
            }
        };
    }

    public static string Invariant(double v) => v.ToString(CultureInfo.InvariantCulture);
}
