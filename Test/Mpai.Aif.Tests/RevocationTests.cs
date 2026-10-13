using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Cav.Ess;

namespace Mpai.Aif.Tests;

// A USER COMMAND THAT REVOKES AN EARLIER ONE (the author, 2026/10/11): every User Command has an ID; one can name an earlier command by it. The AIM that executed
// that command undoes it if it can, and its report says whether it did - under the revoking command's own ID.
public class RevocationTests
{
    private static JsonObject Command(string id, JsonObject data) => new()
    {
        ["Header"] = "CAE-UCM-V1.0", ["UserCommandID"] = id, ["UserCommandTime"] = EssJson.SimpleTime(id + "-T", 1000), ["UserCommandData"] = data
    };

    private static bool Valid(JsonNode json)
    {
        var schema = AIF.Metadata.PublishedSchemas.At(Repository.Schemas)[Path.GetFullPath(Path.Combine(Repository.Schemas, "CAE3", "V1.0", "data", "UserCommand.json"))];
        using var doc = JsonDocument.Parse(json.ToJsonString());
        lock (AIF.Metadata.PublishedSchemas.Lock) return schema.Evaluate(doc.RootElement).IsValid;
    }

    [Fact]
    public void ACommandRevokesAnEarlierOneByItsID() =>
        Assert.True(Valid(Command("UCM000007", new JsonObject { ["RevokedUserCommandID"] = "UCM000005" })));

    [Theory]
    [InlineData("Done")]
    [InlineData("Failed")]
    [InlineData("NotSupported")]
    public void TheReportSaysWhetherTheAIMUndidIt(string outcome)
    {
        var report = Command("UCM000007", new JsonObject { ["RevokedUserCommandID"] = "UCM000005" });
        report["UserCommandReport"] = new JsonObject
        {
            ["Outcome"] = outcome == "Done" ? "Done" : outcome == "Failed" ? "Failed" : "Failed",
            ["Actions"] = new JsonArray(new JsonObject { ["Action"] = "RevokedUserCommandID", ["ObjectID"] = "UCM000005", ["Outcome"] = outcome })
        };
        Assert.True(Valid(report));
    }

    [Fact]
    public void AFieldOfTheCommandThatDoesNotExistIsStillRefused() =>
        Assert.False(Valid(Command("UCM000008", new JsonObject { ["UndoUserCommandID"] = "UCM000005" })));
}
