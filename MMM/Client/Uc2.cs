using System.Text.Json.Nodes;

namespace Mpai.Mmm.Client;

// USE CASE 2 OF MMM-TEC V2.2, "FRIENDS MEET IN THE METAVERSE" (Verification Use
// Cases, 2): its M-Instance and its 19 steps, performed by its Processes as clients
// of the MMM-API. Run by the test, at full speed, and by the server (--demo), a step
// at a time, for the viewer to show.
//
// The M-Instance starts with a Seller holding the Parcel and the Room - the Room not
// perceptible until Friend1 makes it so - Metaverse Square (MVS) and MLocFriend2 open
// to all, and the Personae's 3D Models; Friend2 registers and puts its Persona at
// MLocFriend2 before the Use Case begins.
public static class Uc2
{
    // The M-Environment, in metres: MVS at the centre, MLocFriend2 to its left, the
    // Parcel 10 m to the right, the Room on the Parcel once placed. ULocFriend1 is in
    // the Universe.
    public static void Setup(MInstance m)
    {
        m.AddLocation("MVS", isPublic: true, x: 0, z: 0, size: 8);
        m.AddLocation("MLocFriend2", isPublic: true, x: -9, z: 3, size: 3);
        m.AddLocation("ParcelID", x: 10, z: 0, size: 8);
        m.AddLocation("RoomID", x: 0, z: 0, size: 4, perceptible: false);
        m.AddLocation("ULocFriend1", universe: true);
        m.AddUser("Seller", "human0", owns: ["ParcelID", "RoomID"]);
        m.SetModel("Persona1ID", "Persona1.glb");
        m.SetModel("Persona2ID", "Persona2.glb");
    }

    private const string MI = "MI1";

    private static JsonObject SimpleTime(string id) => new()
    {
        ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = id,
        ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 3, ["StartTime"] = 1_767_254_400_000, ["EndTime"] = 1_767_254_400_000, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 1 })
    };

    private static JsonObject PointOfView(string id) => new()
    {
        ["Header"] = "OSD-OPV-V1.5", ["PointOfViewID"] = id,
        ["CartPosition"] = new JsonArray(0.0, 0.0, 0.0), ["Orientation"] = new JsonArray(0.0, 0.0, 0.0)
    };

    // x, z: where the Item stands within the Location it is placed at, in metres (0, 0: the centre).
    private static JsonObject SpatialAttitude(string id, double x = 0, double z = 0) => new()
    {
        ["Header"] = "OSD-OSA-V1.5", ["ObjectSpatialAttitudeID"] = id,
        ["Position"] = new JsonObject { ["Header"] = "OSD-OPS-V1.5", ["PositionID"] = $"{id}-P", ["CartPosition"] = new JsonArray(x, 0.0, z) },
        ["Orientation"] = new JsonObject { ["Header"] = "OSD-OOR-V1.5", ["OrientationID"] = $"{id}-O", ["Orientation"] = new JsonArray(0.0, 0.0, 0.0) }
    };

    // A concrete Process Action the M-Instance keeps, for a Right to name: its Header
    // says the Action, its Request Complements the Items.
    // rq: the slot of each Complement in the PA's schema (RQ1 is Nil, ...), its Complement, its value.
    private static JsonObject PA(string header, string name, string id, params (string Slot, string Complement, string Key, JsonNode Value)[] rq)
    {
        var pa = new JsonObject
        {
            ["Header"] = header, ["MInstanceID"] = MI, [$"{name}PAID"] = id,
            ["Time"] = SimpleTime($"{id}-T")
        };
        var parq = new JsonObject();
        foreach (var (slot, complement, key, value) in rq)
            parq[slot] = new JsonObject { ["Complement"] = complement, [key] = value };
        pa[$"{name}PARQ"] = parq;
        return pa;
    }

    private static JsonObject Rights(string id, string verb, string level, params string[] pas) => new()
    {
        ["Header"] = "MMM-RGT-V2.2", ["MInstanceID"] = MI, ["RightsID"] = id,
        ["RightsData"] = new JsonArray(new JsonObject
        {
            ["DeonticVerb"] = verb, ["Level"] = level,
            ["PAIDOrPA"] = new JsonArray(pas.Select(p => (JsonNode)new JsonObject { ["ProcessActionID"] = p }).ToArray())
        })
    };

    private static JsonObject Profile(string human, string user, string persona) => new()
    {
        ["Header"] = "MMM-PPR-V2.2", ["MInstanceID"] = MI, ["humanID"] = human, ["PersonalProfileID"] = $"Profile-{human}",
        ["PersonalProfile"] = new JsonObject
        {
            ["Data"] = new JsonArray(new JsonObject { ["FirstName"] = human }),
            ["Processes"] = new JsonArray(new JsonObject
            {
                ["ProcessIDOrProcess"] = new JsonArray(new JsonObject { ["ProcessID"] = user }),
                ["PersonaIDOrPersona"] = new JsonArray(new JsonObject { ["PersonaID"] = persona })
            })
        }
    };

    private static JsonObject Transaction(string id, string buyer, string seller, string asset) => new()
    {
        ["Header"] = "MMM-TRA-V2.2", ["MInstanceID"] = MI, ["MEnvironmentID"] = "ME1", ["TransactionID"] = id,
        ["AssetID"] = new JsonArray(new JsonObject { ["ItemID"] = asset }),
        ["SenderData"] = new JsonObject { ["SenderID"] = buyer, ["SenderWalletID"] = $"Wallet-{buyer}" },
        ["ReceiverData"] = new JsonObject { ["ReceiverID"] = seller, ["ReceiverWalletID"] = $"Wallet-{seller}" },
        ["TransactionStatus"] = "Model"
    };

    private static JsonObject Message(string id, string text) => new()
    {
        ["Header"] = "MMM-MSG-V2.2", ["MInstanceID"] = MI, ["MessageID"] = id, ["MessageData"] = new JsonArray(new JsonObject { ["MessageData"] = text })
    };

    private static Complements C(string? nil = null, string? at = null, string? from = null, string? to = null, params JsonNode[] with) =>
        new() { Nil = nil, At = at, From = from, To = to, With = with.Length == 0 ? null : with.Select(w => (JsonNode?)w).ToList() };

    public static string Said(MmmClient.Result r) =>
        $"{r.Http} {r.Response.PaStatus.Code}" + (r.Response.Complements?.With is { Count: > 0 } w
            ? " " + string.Join(" ", w.Select(x => x is JsonObject o && o["itemID"] is not null ? $"{o["itemID"]}({o["status"]?.GetValue<string>()[0]})" : x?.ToString()))
            : "") + (r.Response.Complements?.Nil is { } n ? $" Nil {n}" : "") + (r.Response.PaStatus.Detail is { } d ? $" ({d})" : "");


    // The steps, in order. after: called after each step of the workflow, with its
    // name and result - to see the M-Instance then, or to wait before the next.
    public static async Task<Dictionary<string, string>> RunAsync(HttpClient http, MInstance m,
        Func<string, MmmClient.Result, Task>? after = null)
    {
        var result = new Dictionary<string, string>();
        var step = 0;
        async Task<MmmClient.Result> Do(string what, MmmClient who, string action, Complements c, string? key = null)
        {
            var r = await who.PerformAsync(action, c, key);
            result[$"{++step:00} {what}"] = Said(r);
            if (after is not null) await after($"{step:00} {what}", r);
            return r;
        }

        // Before the Use Case: Friend2 registered, its Persona at MLocFriend2.
        var human2 = new MmmClient(http, "human2");
        var reg2 = await human2.PerformAsync("Register", C(with: Profile("human2", "Friend2", "Persona2ID")));
        var friend2 = new MmmClient(http, "Friend2", reg2.Tokens.GetValueOrDefault("Friend2"));
        result["00 Friend2 registered, its Persona at MLocFriend2"] =
            $"{reg2.Http} / {(await friend2.PerformAsync("MM-Add", C("Persona2ID", at: "MLocFriend2", with: SpatialAttitude("SA-P2")))).Http}";

        // 2.3 Workflow
        var human1 = new MmmClient(http, "human1");
        var reg = await Do("human1 registers", human1, "Register", C(with: Profile("human1", "Friend1", "Persona1ID")));
        var friend1 = new MmmClient(http, "Friend1", reg.Tokens.GetValueOrDefault("Friend1"));

        // The Process Actions the Rights name, kept by the M-Instance with them.
        var addAtParcel = PA("MMM-2DP-V2.2", "MMAdd", "PA-AddAtParcel", ("RQ2", "At", "MLocationID", "ParcelID"));
        var moveToParcel = PA("MMM-2MP-V2.2", "MMMove", "PA-MoveToParcel", ("RQ3", "To", "PointOfView2", PointOfView("ParcelID")));
        var land = new[] { "PA-AddAtParcel", "PA-MoveToParcel" };
        var buyLand = C(with: [Transaction("Land_Transaction", "Friend1", "Seller", "ParcelID"), addAtParcel, moveToParcel,
                               Rights("Land_Rights", "May", "Acquired", land)]);
        var bought = await Do("Friend1 buys land", friend1, "Transact", buyLand, key: "buy-land");
        result["retried: the same Response, the land not bought twice"] =
            Said(await friend1.PerformAsync("Transact", buyLand, "buy-land")) == Said(bought) ? "yes" : "no";

        var enterRoom = PA("MMM-2MP-V2.2", "MMMove", "PA-EnterRoom", ("RQ3", "To", "PointOfView2", PointOfView("RoomID")));
        await Do("Friend1 buys the room", friend1, "Transact", C(with: [Transaction("Room_Transaction", "Friend1", "Seller", "RoomID"), enterRoom,
                                                                         Rights("Room_Rights", "May", "Acquired", "PA-EnterRoom")]));
        var addPersona = PA("MMM-2DP-V2.2", "MMAdd", "PA-AddPersona1", ("RQ1", "Nil", "Item", "Persona1ID"), ("RQ2", "At", "MLocationID", "MVS"));
        await Do("Friend1 adds its Persona at MVS", friend1, "MM-Add", C("Persona1ID", at: "MVS",
            with: [SpatialAttitude("SA1"), addPersona, Rights("Persona1_Rights", "May", "Internal", "PA-AddPersona1")]));
        await Do("Friend1 captures data at ULocFriend1", friend1, "UM-Capture", C("DataID", at: "ULocFriend1", with: JsonValue.Create("Qualifier")!));
        var identified = await Do("Friend1 identifies the stream", friend1, "Identify", C("DataID", with: JsonValue.Create("Qualifier")!));
        var stream = identified.Response.Complements?.Nil ?? "";
        var animate = PA("MMM-2AP-V2.2", "MMAnimate", "PA-AnimatePersona1", ("RQ1", "Nil", "ItemID", "Persona1ID"));
        await Do("Friend1 animates its Persona", friend1, "MM-Animate", C("Persona1ID",
            with: [JsonValue.Create(stream)!, animate, Rights("Animate_Rights", "May", "Internal", "PA-AnimatePersona1")]));
        var usePR = PA("MMM-MSP-V2.2", "MMSend", "PA-MessagePR", ("RQ1", "Nil", "Message", Message("MessagePRID", "")));
        await Do("Friend1 signals its presence", friend1, "MM-Send", C("MessagePRID", to: "PRSrvc",
            with: [Message("MessagePRID", "Friend1 is present"), usePR, Rights("MessagePR_Rights", "May", "Acquired", "PA-MessagePR")]));
        await Do("Friend1 moves to the parcel", friend1, "MM-Move", C("Persona1ID", from: "MVS", to: "ParcelID", with: SpatialAttitude("SA2", x: -3, z: 3)));   // at the Parcel's edge, outside the Room-to-be
        var addRoom = PA("MMM-2DP-V2.2", "MMAdd", "PA-AddRoom", ("RQ1", "Nil", "Item", "RoomID"), ("RQ2", "At", "MLocationID", "ParcelID"));
        await Do("Friend1 places the room", friend1, "MM-Add", C("RoomID", at: "ParcelID",
            with: [SpatialAttitude("SA3"), addRoom, Rights("AddRoom_Rights", "May", "Acquired", "PA-AddRoom")]));
        var perceptible = PA("MMM-PCP-V2.2", "PropertyChange", "PA-RoomPerceptible", ("RQ1", "Nil", "ItemID", "RoomID"));
        await Do("Friend1 makes the room perceptible", friend1, "Property Change", C("RoomID",
            with: [perceptible, Rights("Property_Rights", "May", "Internal", "PA-RoomPerceptible")]));
        await Do("Friend1 enters the room", friend1, "MM-Move", C("Persona1ID", from: "ParcelID", to: "RoomID", with: SpatialAttitude("SA4")));
        var actuate = PA("MMM-3CP-V2.2", "MUActuate", "PA-ActuateRoom", ("RQ1", "Nil", "ItemID", "RoomID"), ("RQ3", "At", "ULocation", "ULocFriend1"));
        await Do("Friend1 renders the room to human1", friend1, "MU-Actuate", C("RoomID", at: "ULocFriend1",
            with: [JsonValue.Create("RItemType")!, SpatialAttitude("SA5"), actuate, Rights("Actuate_Rights", "May", "Internal", "PA-ActuateRoom")]));
        var use12 = PA("MMM-MSP-V2.2", "MMSend", "PA-Message12", ("RQ1", "Nil", "Message", Message("Message12ID", "")));
        await Do("Friend1 invites Friend2", friend1, "MM-Send", C("Message12ID", to: "Friend2",
            with: [Message("Message12ID", "Come to my room"), use12, Rights("Message12_Rights", "May", "Acquired", "PA-Message12")]));
        result["Friend2 reads the invitation"] = (await friend2.ReadAsync("Message12ID")).Http.ToString();
        result["Seller reads the invitation"] = (await new MmmClient(http, "Seller", m.TokenOf("Seller")).ReadAsync("Message12ID")).Http.ToString();
        var use21 = PA("MMM-MSP-V2.2", "MMSend", "PA-Message21", ("RQ1", "Nil", "Message", Message("Message21ID", "")));
        await Do("Friend2 accepts", friend2, "MM-Send", C("Message21ID", to: "Friend1",
            with: [Message("Message21ID", "Coming"), use21, Rights("Message21_Rights", "May", "Acquired", "PA-Message21")]));

        var p2EntersRoom = PA("MMM-2MP-V2.2", "MMMove", "PA-Persona2EntersRoom", ("RQ1", "Nil", "ItemID", "Persona2ID"), ("RQ3", "To", "PointOfView2", PointOfView("RoomID")));
        Complements moveIn() => C("Persona2ID", from: "MLocFriend2", to: "RoomID",
            with: [SpatialAttitude("SA6"), p2EntersRoom, Rights("Move_Rights", "May", "Acquired", "PA-Persona2EntersRoom")]);
        result["Friend2 enters the room before access is granted"] = Said(await friend2.PerformAsync("MM-Move", moveIn()));
        await Do("Friend1 grants Friend2 access", friend1, "Rights Change", C("Friend2",
            with: [p2EntersRoom, Rights("RoomAccess_Rights", "May", "Granted", "PA-Persona2EntersRoom")]));
        await Do("Friend2 enters the room", friend2, "MM-Move", moveIn());
        await Do("Friend2 leaves the room", friend2, "MM-Move", C("Persona2ID", from: "RoomID", to: "MLocFriend2", with: SpatialAttitude("SA7")));
        await Do("Friend1 revokes the access", friend1, "Rights Change", C("Friend2",
            with: [p2EntersRoom, Rights("Revoke_Rights", "May Not", "Granted", "PA-Persona2EntersRoom")]));
        result["Friend2 enters the room after the revoke"] = Said(await friend2.PerformAsync("MM-Move", moveIn()));

        result["where Persona1ID is"] = m.Get("Persona1ID")?.Location ?? "";
        result["who owns the Room"] = m.Get("RoomID")?.Owner ?? "";
        result["Activity Data, Process Actions recorded"] = m.ActivityData.Count.ToString();
        return result;
    }
}
