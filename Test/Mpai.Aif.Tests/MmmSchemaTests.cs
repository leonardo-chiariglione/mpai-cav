using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mpai.Aif.Tests;

// RIGHTS IS A FIRST-CLASS ITEM (the author, 2026/10/01): its RightsData lists Rights,
// each with its Deontic Verb, its Process Actions and its Level - Internal, from
// registration; Acquired, from Process Actions with other Processes; Granted, by a
// User to another for a time, as in Use Case 2. Penalties is its copy, where a May
// becomes a May Not. Each admits an instance of what it describes and refuses one
// without what it requires.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class MmmSchemaTests
{
    private static JsonObject Rights(string name, string header, string verb, string? level, bool withId = true)
    {
        var entry = new JsonObject
        {
            ["DeonticVerb"] = verb,
            ["PAIDOrPA"] = new JsonArray(new JsonObject { ["ProcessActionID"] = "MM-Move-Room1", ["Status"] = "Final" })
        };
        if (level is not null) entry["Level"] = level;
        var o = new JsonObject { ["Header"] = header };
        if (withId) o[$"{name}ID"] = $"{name}-Friend2-Room1";
        o[$"{name}Data"] = new JsonArray(entry);
        return o;
    }

    [Fact]
    public void RightsAndPenalties()
    {
        var schemas = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);
        string Check(string file, JsonObject instance)
        {
            var schema = schemas[Path.GetFullPath(Path.Combine(Repository.Schemas, "MMM4", "V2.2", "data", file + ".json"))];
            using var doc = JsonDocument.Parse(instance.ToJsonString());
            lock (AIF.Metadata.PublishedSchemas.Lock) return schema.Evaluate(doc.RootElement).IsValid ? "valid" : "invalid";
        }
        var result = new Dictionary<string, string>
        {
            ["Rights granted to Friend2 on Room1"] = Check("Rights", Rights("Rights", "MMM-RGT-V2.2", "May", "Granted")),
            ["Rights without the Level of a Right"] = Check("Rights", Rights("Rights", "MMM-RGT-V2.2", "May", null)),
            ["Rights without its identifier"] = Check("Rights", Rights("Rights", "MMM-RGT-V2.2", "May", "Granted", withId: false)),
            ["Rights with the Header as a pattern"] = Check("Rights", Rights("Rights", "^MMM-RGT-V2.2$", "May", "Granted")),
            ["Penalties, a May become a May Not"] = Check("Penalties", Rights("Penalties", "MMM-PNL-V2.2", "May Not", "Acquired")),
            ["Penalties with the Header of Rights"] = Check("Penalties", Rights("Penalties", "MMM-RGT-V2.2", "May Not", "Acquired")),
        };
        Expected.Match("mmm-schemas.json", result);
    }

    // THE ITEMS CORRECTED WITH THE AUTHOR (2026/10/01), one decision each: a Transaction
    // has a sender and a receiver (TransactionData, defined nowhere, no longer required);
    // a Simple Contract has a Status, Model or Final; a Licence is the root one unless it
    // says otherwise (IsRootLicence, default true, so not required; IsFirstLicence is
    // defined nowhere); a Basic Object needs no Trace; an
    // Object has its own ObjectSpaceTime, an OSD Space Time.
    private static JsonObject SimpleTime(string id) => new()
    {
        ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = id,
        ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 3, ["StartTime"] = 1_767_254_400_000, ["EndTime"] = 1_767_254_400_000, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 1 })
    };

    private static JsonObject SpaceTime(string id) => new() { ["Header"] = "OSD-SPT-V1.5", ["SpaceTimeID"] = id };

    private static JsonObject Transaction(bool sender, bool receiver)
    {
        var o = new JsonObject { ["Header"] = "MMM-TRA-V2.2", ["MInstanceID"] = "MI1", ["MEnvironmentID"] = "ME1", ["TransactionID"] = "Buy-Room1" };
        if (sender) o["SenderData"] = new JsonObject { ["SenderID"] = "Friend1", ["SenderWalletID"] = "W-Friend1" };
        if (receiver) o["ReceiverData"] = new JsonObject { ["ReceiverID"] = "Seller", ["ReceiverWalletID"] = "W-Seller" };
        return o;
    }

    private static JsonObject SimpleContract(string? status)
    {
        var o = new JsonObject { ["Header"] = "MMM-SCT-V2.2", ["MInstanceID"] = "MI1", ["SimpleContractID"] = "SC1", ["SimpleContractTime"] = SimpleTime("SC1-T") };
        if (status is not null) o["Status"] = status;
        return o;
    }

    private static JsonObject Licence(string? root, bool value = true)
    {
        var o = new JsonObject
        {
            ["Header"] = "MMM-LIC-V2.2", ["LicenceID"] = "L1", ["LicensorID"] = "Friend1", ["LicenseeID"] = "Friend2",
            ["LicensorRights"] = Rights("Rights", "MMM-RGT-V2.2", "May", "Internal"),
            ["LicenseeRights"] = Rights("Rights", "MMM-RGT-V2.2", "May", "Granted")
        };
        if (root is not null) o[root] = value;
        return o;
    }

    [Fact]
    public void CorrectedItems()
    {
        var schemas = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);
        string Check(string path, JsonObject instance)
        {
            var schema = schemas[Path.GetFullPath(Path.Combine(Repository.Schemas, path + ".json"))];
            using var doc = JsonDocument.Parse(instance.ToJsonString());
            lock (AIF.Metadata.PublishedSchemas.Lock) return schema.Evaluate(doc.RootElement).IsValid ? "valid" : "invalid";
        }
        const string mmm = "MMM4/V2.2/data/", osd = "OSD/V1.5/data/";
        var result = new Dictionary<string, string>
        {
            ["Transaction with its sender and receiver"] = Check(mmm + "Transaction", Transaction(true, true)),
            ["Transaction without its sender"] = Check(mmm + "Transaction", Transaction(false, true)),
            ["Transaction without its receiver"] = Check(mmm + "Transaction", Transaction(true, false)),
            ["Simple Contract at Status Final"] = Check(mmm + "SimpleContract", SimpleContract("Final")),
            ["Simple Contract without its Status"] = Check(mmm + "SimpleContract", SimpleContract(null)),
            ["Simple Contract at a Status other than Model or Final"] = Check(mmm + "SimpleContract", SimpleContract("Pending")),
            ["Licence, the root one"] = Check(mmm + "Licence", Licence("IsRootLicence")),
            ["Licence saying nothing: the root one by default"] = Check(mmm + "Licence", Licence(null)),
            ["Licence, a subsequent one"] = Check(mmm + "Licence", Licence("IsRootLicence", false)),
            ["Licence saying IsFirstLicence"] = Check(mmm + "Licence", Licence("IsFirstLicence")),
            ["Basic Object without a Trace"] = Check(osd + "BasicObject", new JsonObject { ["Header"] = "OSD-BOB-V1.5", ["BasicObjectID"] = "BO1", ["BasicObjectSpaceTime"] = SpaceTime("BO1-ST") }),
            ["Object with its ObjectSpaceTime"] = Check(osd + "Object", new JsonObject { ["Header"] = "OSD-OBJ-V1.5", ["ObjectID"] = "O1", ["ObjectSpaceTime"] = SpaceTime("O1-ST") }),
            ["Object without its ObjectSpaceTime"] = Check(osd + "Object", new JsonObject { ["Header"] = "OSD-OBJ-V1.5", ["ObjectID"] = "O1" }),
        };
        Expected.Match("mmm-corrected-items.json", result);
    }
}
