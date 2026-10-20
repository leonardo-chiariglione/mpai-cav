using System.Text.Json;
using AIF.Metadata;
using Json.Schema;

namespace Mpai.Aif.Tests;

// OSD Simple Time: the accuracy fields follow AccuracyMode, and a Mode that is left out is the default, single (it used to require the fields of both modes).
[Trait("Group", "Fast")]
public class SimpleTimeSchemaTests
{
    private static bool Valid(string segment)
    {
        var root = Path.GetFullPath(Repository.Schemas);
        var compiled = PublishedSchemas.At(root)[Path.Combine(root, "OSD", "V1.5", "data", "SimpleTime.json")];
        using var document = JsonDocument.Parse("{\"Header\":\"OSD-STM-V1.5\",\"SimpleTimeID\":\"t\",\"SimpleTimeData\":[{\"FlagsByte\":0,\"StartTime\":0,\"EndTime\":1" + segment + "}]}");
        lock (PublishedSchemas.Lock) return compiled.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
    }

    [Fact]
    public void AModeLeftOutIsSingleAndNeedsOnlyTheOneAccuracy()
    {
        Assert.True(Valid(",\"AccuracyPlusMinus\":0.01"));
        Assert.False(Valid(""));                                                                                        // no accuracy at all
        Assert.False(Valid(",\"AccuracyStartPlusMinus\":0.01,\"AccuracyEndPlusMinus\":0.01"));                          // the fields of separate, no mode: single is meant
    }

    [Fact]
    public void TheModesStillNeedTheirOwnFields()
    {
        Assert.True(Valid(",\"AccuracyMode\":\"single\",\"AccuracyPlusMinus\":0.01"));
        Assert.False(Valid(",\"AccuracyMode\":\"single\""));
        Assert.True(Valid(",\"AccuracyMode\":\"separate\",\"AccuracyStartPlusMinus\":0.01,\"AccuracyEndPlusMinus\":0.02"));
        Assert.False(Valid(",\"AccuracyMode\":\"separate\",\"AccuracyPlusMinus\":0.01"));
    }
}