using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mpai.Aif.Tests;

// THE DATA TYPES OF AIH-HSP V1.1: an instance of each that the Health Secure Platform exchanges, written as the specification's schemas say it,
// and the same with what the schema requires taken away - the Data Type is what the Ports of the AIMs carry. The instances are the ones the AIMs'
// tests and the Health Front End and Back End of the first slice use.
public static class AihSamples
{
    public static JsonObject Time(string id = "t") => new() { ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = id, ["Data"] = "2026-10-14T10:00:00Z" };

    public static JsonObject EcgQualifier() => new()
    {
        ["Header"] = "TFA-ECQ-V1.5", ["ECGQualifierID"] = "ecq", ["SubTypes"] = new JsonObject(), ["Formats"] = new JsonObject()
    };

    public static JsonObject EcgObject(string user = "user-1") => new()
    {
        ["Header"] = "AIH-ECO-V1.1", ["ECGObjectID"] = "ecg-1", ["EndUserID"] = user, ["ECGObjectTime"] = Time("ecg-t"),
        ["ECGData"] = new JsonArray(new JsonObject { ["Data"] = "AAAA" }), ["ECGDataQualifier"] = EcgQualifier()
    };

    public static JsonObject HealthData(string user = "user-1") => new()
    {
        ["Header"] = "AIH-HLD-V1.1", ["HealthDataID"] = "hd-1", ["EndUserID"] = user, ["HealthDataTime"] = Time("hd-t"),
        ["HealthDataData"] = new JsonArray(new JsonObject { ["Data"] = "AAAA" }),
        ["DataQualifier"] = new JsonObject
        {
            ["Header"] = "TFA-HDQ-V1.5", ["HealthDataQualifierID"] = "hdq", ["SpecialisedQualifier"] = EcgQualifier()
        }
    };

    public static JsonObject ModelLicence(string user = "user-1", string data = "ahd-1") => new()
    {
        ["Header"] = "AIH-MDL-V1.1", ["ModelLicenceID"] = "ml-1", ["ModelLicenceTime"] = Time("ml-t"), ["EndUserID"] = user, ["AIHDataID"] = data,
        ["AIHDataType"] = "AIH-ECO",
        ["ModelLicenceData"] = new JsonObject
        {
            ["Common"] = new JsonObject { ["LicenceTime"] = Time("lc-t"), ["EndUserID"] = user, ["AIHDataID"] = data, ["AIHDataUsage"] = "Research", ["AIHDataProcessingType"] = "PhysiologicalRiskPrediction" },
            ["Specific"] = new JsonArray(new JsonObject
            {
                ["BackEndLicence"] = new JsonObject
                {
                    ["EndUserID"] = user, ["BackEndID"] = "hbe-1", ["AIHDataID"] = data, ["AIHDataStatus"] = "Pseudonymised",
                    ["AIHDataProcess"] = "PhysiologicalRiskPrediction", ["AIHDataUsage"] = "Research"
                }
            })
        }
    };

    public static JsonObject AihData(string user = "user-1", string id = "ahd-1") => new()
    {
        ["Header"] = "AIH-AHD-V1.1", ["UserID"] = user, ["AIHDataID"] = id, ["AIHDataTime"] = Time("ahd-t"),
        ["AIHData"] = new JsonObject
        {
            ["HealthData"] = HealthData(user), ["ModelLicence"] = ModelLicence(user, id), ["SmartContractID"] = "sc-1", ["DeIDAndAnonym"] = "Anonymised"
        }
    };

    public static JsonObject Tokens(string user = "user-1") => new()
    {
        ["Header"] = "AIH-TKN-V1.1", ["UserID"] = user, ["TokensID"] = "tk-1", ["TokensTime"] = Time("tk-t"),
        ["Tokens"] = new JsonArray(new JsonObject { ["ProcessType"] = "Store" }, new JsonObject { ["ProcessType"] = "AIHDataProcessing" })
    };

    public static JsonObject UserProfile(string user = "user-1") => new()
    {
        ["Header"] = "AIH-UPR-V1.1", ["UserID"] = user, ["UserProfileID"] = "up-1",
        ["UserProfileData"] = new JsonArray(new JsonObject
        {
            ["Human"] = new JsonObject { ["FirstName"] = "Ada", ["LastName"] = "Lovelace", ["Age"] = 36, ["Nationality"] = "GB", ["Email"] = "ada@example.org" }
        })
    };

    public static JsonObject RegisterRequest(string user = "user-1") => new()
    {
        ["Header"] = "AIH-REG-V1.1", ["RegisterID"] = "reg-1", ["RegisterTime"] = Time("reg-t"),
        ["RegisterData"] = new JsonArray(new JsonObject
        {
            ["Request"] = new JsonObject { ["UserProfile"] = UserProfile(user), ["RequestedServices"] = new JsonArray(new JsonObject { ["Service"] = "Store" }) }
        })
    };

    public static JsonObject ThirdPartyUserProfile(string id = "tpu-1") => new()
    {
        ["Header"] = "AIH-TPU-V1.1", ["ThirdPartyUserID"] = id, ["ThirdPartyUserProfileID"] = "tpup-1",
        ["ThirdPartyUserProfileData"] = new JsonObject { ["CompanyName"] = "Cardio Research Ltd", ["RegistrationCode"] = "GB-0001", ["Country"] = "GB", ["Email"] = "data@cardio.example.org" },
        ["Entitlements"] = new JsonObject
        {
            ["UserTypes"] = new JsonArray("Non-Profit Entity"), ["DataClasses"] = new JsonArray("AIH-ECO"), ["DataStatus"] = new JsonArray("Pseudonymised"),
            ["ProcessTypes"] = new JsonArray("PhysiologicalRiskPrediction"), ["AnomalyTypes"] = new JsonArray("Point Anomaly"), ["UsageTypes"] = new JsonArray("Research")
        }
    };

    public static JsonObject ThirdPartyRegisterRequest(string id = "tpu-1") => new()
    {
        ["Header"] = "AIH-REG-V1.1", ["RegisterID"] = "reg-2", ["RegisterTime"] = Time("reg-t"),
        ["RegisterData"] = new JsonArray(new JsonObject
        {
            ["Request"] = new JsonObject { ["ThirdPartyUserProfile"] = ThirdPartyUserProfile(id), ["RequestedServices"] = new JsonArray(new JsonObject { ["Service"] = "AIHDataProcessing" }) }
        })
    };

    public static JsonObject RegisterResponse(string user = "user-1") => new()
    {
        ["Header"] = "AIH-REG-V1.1", ["RegisterID"] = "reg-1", ["RegisterTime"] = Time("reg-t"),
        ["RegisterData"] = new JsonArray(new JsonObject { ["Response"] = new JsonObject { ["Success"] = true, ["Tokens"] = Tokens(user) } })
    };

    public static JsonObject ProcessRequest(string user = "user-1") => new()
    {
        ["Header"] = "AIH-AHP-V1.1", ["UserID"] = user, ["AIHDataProcessID"] = "ahp-1", ["AIHDataProcessTime"] = Time("ahp-t"),
        ["AIHDataProcess"] = new JsonArray(new JsonObject
        {
            ["Request"] = new JsonObject
            {
                ["AIHDataOrAIHDataID"] = new JsonArray("ahd-1"), ["ProcessingType"] = "Store", ["Tokens"] = Tokens(user)
            }
        })
    };

    public static JsonObject ProcessResponse(string user = "user-1") => new()
    {
        ["Header"] = "AIH-AHP-V1.1", ["UserID"] = user, ["AIHDataProcessID"] = "ahp-1", ["AIHDataProcessTime"] = Time("ahp-t"),
        ["AIHDataProcess"] = new JsonArray(new JsonObject { ["Response"] = new JsonObject { ["AIHDataOrAIHDataID"] = new JsonArray("ahd-1"), ["ProcessingStatus"] = true } })
    };

    public static JsonObject LicenceConfirmRequest(string user = "user-1") => new()
    {
        ["Header"] = "AIH-LCF-V1.1", ["EndUserID"] = user, ["LicenceConfirmID"] = "lcf-1", ["LicenceConfirmTime"] = Time("lcf-t"),
        ["LicenceConfirmData"] = new JsonArray(new JsonObject { ["Request"] = new JsonObject { ["AIHDataID"] = "ahd-1", ["ProcessType"] = "Store", ["RequesterID"] = "user-1", ["Time"] = Mpai.Aih.AihJson.SimpleTime() } })
    };

    public static JsonObject LicenceConfirmResponse(bool ok, string user = "user-1") => new()
    {
        ["Header"] = "AIH-LCF-V1.1", ["EndUserID"] = user, ["LicenceConfirmID"] = "lcf-1", ["LicenceConfirmTime"] = Time("lcf-t"),
        ["LicenceConfirmData"] = new JsonArray(new JsonObject { ["Response"] = new JsonObject { ["Status"] = ok } })
    };

    public static JsonObject BlockchainLicenceRequest(string user = "user-1") => new()
    {
        ["Header"] = "AIH-BCL-V1.1", ["UserID"] = user, ["BlockchainLicenceID"] = "bcl-1", ["AIHDataID"] = "ahd-1", ["BlockchainLicenceTime"] = Time("bcl-t"),
        ["BlockchainLicenceData"] = new JsonArray(new JsonObject { ["Request"] = new JsonObject { ["ModelLicence"] = ModelLicence(user) } })
    };

    public static JsonObject BlockchainLicenceResponse(string user = "user-1") => new()
    {
        ["Header"] = "AIH-BCL-V1.1", ["UserID"] = user, ["BlockchainLicenceID"] = "bcl-1", ["AIHDataID"] = "ahd-1", ["BlockchainLicenceTime"] = Time("bcl-t"),
        ["BlockchainLicenceData"] = new JsonArray(new JsonObject { ["Response"] = new JsonObject { ["BlockchainLicenceID"] = "sc-1", ["LicenceStatus"] = true, ["AIHDataID"] = "ahd-1" } })
    };

    public static JsonObject AraData() => new()
    {
        ["Header"] = "AIH-ARD-V1.1", ["ARADataID"] = "ara-1", ["ARADataTime"] = Time("ara-t"),
        ["ARAData"] = new JsonObject { ["AIHDataID"] = "ahd-1", ["Risk"] = "Yellow", ["Anomalies"] = new JsonObject { ["Anomaly"] = "Point Anomaly" } }
    };

    public static JsonObject DeIdRequest(string user = "user-1") => new()
    {
        ["Header"] = "AIH-DIA-V1.1", ["DeIDAndAnonymID"] = "dia-1", ["DeIDAndAnonymTime"] = Time("dia-t"), ["EndUserID"] = user,
        ["DeIDAndAnonymData"] = new JsonArray(new JsonObject
        {
            ["Request"] = new JsonObject { ["AIHDataID"] = "ahd-1", ["DeIDAndAnonymType"] = "Anonymise", ["DeIDAndAnonymAlgoID"] = "K-Anonymity", ["Tokens"] = Tokens(user) }
        })
    };

    public static JsonObject FederatedLearnRequest(string user = "user-1") => new()
    {
        ["Header"] = "AIH-FDL-V1.1", ["EndUserID"] = user, ["FederatedLearnID"] = "fdl-1", ["FederatedLearnTime"] = Time("fdl-t"),
        ["FederatedLearnData"] = new JsonArray(new JsonObject { ["Request"] = new JsonObject { ["HFEID"] = "hfe-1", ["PreLearnNNModelID"] = "model-0", ["PreLearnAIMID"] = "aim-nn-0" } })
    };

    public static JsonObject AuditRequest(string user = "user-1") => new()
    {
        ["Header"] = "AIH-ADT-V1.1", ["UserID"] = user, ["AuditID"] = "adt-1", ["AuditTime"] = Time("adt-t"),
        ["Audit"] = new JsonObject
        {
            ["Request"] = new JsonObject
            {
                ["Tokens"] = Tokens(user), ["AuditPeriod"] = Time("adt-p"),
                ["ProcessingOperations"] = new JsonArray(new JsonObject { ["ProcessingOperation"] = "PhysiologicalRiskPrediction", ["DataClass"] = "AIH-ECO" })
            }
        }
    };
}

[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AihSchemaTests
{
    private static readonly IReadOnlyDictionary<string, Json.Schema.JsonSchema> All = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);

    private static string Check(string schema, JsonNode value)
    {
        var s = All[Path.GetFullPath(Path.Combine(Repository.Schemas, "AIH1", "V1.1", "data", schema + ".json"))];
        using var doc = JsonDocument.Parse(value.ToJsonString());
        Json.Schema.EvaluationResults r;
        lock (AIF.Metadata.PublishedSchemas.Lock)
            r = s.Evaluate(doc.RootElement, new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List });
        if (r.IsValid) return "valid";
        return "not valid: " + string.Join(" | ", r.Details.Where(d => d.Errors is { Count: > 0 }).OrderByDescending(d => d.InstanceLocation.ToString().Length).Take(3).SelectMany(d => d.Errors!.Select(e => d.InstanceLocation + " " + e.Value)));
    }

    public static IEnumerable<object[]> Instances() =>
    [
        ["HealthData", AihSamples.HealthData()], ["ECGObject", AihSamples.EcgObject()], ["ModelLicence", AihSamples.ModelLicence()], ["AIHData", AihSamples.AihData()],
        ["Tokens", AihSamples.Tokens()], ["UserProfile", AihSamples.UserProfile()], ["Register", AihSamples.RegisterRequest()], ["Register", AihSamples.RegisterResponse()],
        ["AIHDataProcess", AihSamples.ProcessRequest()], ["AIHDataProcess", AihSamples.ProcessResponse()],
        ["LicenceConfirm", AihSamples.LicenceConfirmRequest()], ["LicenceConfirm", AihSamples.LicenceConfirmResponse(true)],
        ["BlockchainLicence", AihSamples.BlockchainLicenceRequest()], ["BlockchainLicence", AihSamples.BlockchainLicenceResponse()],
        ["ARAData", AihSamples.AraData()], ["DeIDAndAnonym", AihSamples.DeIdRequest()], ["FederatedLearn", AihSamples.FederatedLearnRequest()], ["Audit", AihSamples.AuditRequest()]
    ];

    [Theory]
    [MemberData(nameof(Instances))]
    public void AnInstanceValidatesAgainstItsSchema(string schema, JsonNode instance) => Assert.Equal("valid", Check(schema, instance));

    // What a schema requires, taken away: no longer valid.
    [Theory]
    [MemberData(nameof(Instances))]
    public void WithoutItsHeaderItIsNotValid(string schema, JsonNode instance)
    {
        var copy = instance.DeepClone().AsObject();
        copy.Remove("Header");
        Assert.StartsWith("not valid", Check(schema, copy));
    }

    // The ID of the End User: the AIH Data is the End User's, and says whose.
    [Fact]
    public void AihDataSaysWhoseItIs()
    {
        var copy = AihSamples.AihData().DeepClone().AsObject();
        copy.Remove("UserID");
        Assert.StartsWith("not valid", Check("AIHData", copy));
    }

    // The taxonomies are closed lists: a Risk that is not a colour, a Process Type that is not a service.
    [Fact]
    public void TheTaxonomiesAreClosedLists()
    {
        var risk = AihSamples.AraData(); risk["ARAData"]!["Risk"] = "Purple";
        Assert.StartsWith("not valid", Check("ARAData", risk));
        var service = AihSamples.ProcessRequest(); service["AIHDataProcess"]![0]!["Request"]!["ProcessingType"] = "Everything";
        Assert.StartsWith("not valid", Check("AIHDataProcess", service));
    }

    // The Smart Contract's ID and the state of de-identification are optional in AIH Data (the author's decision): the Model Licensing AIM makes
    // AIH Data from Health Data and a Model Licence, and the Back End makes the Smart Contract and the de-identified copy later.
    [Fact]
    public void AihDataMadeByTheFrontEndHasNoSmartContractYetAndIsValid()
    {
        var made = AihSamples.AihData(); made["AIHData"]!.AsObject().Remove("SmartContractID"); made["AIHData"]!.AsObject().Remove("DeIDAndAnonym");
        Assert.Equal("valid", Check("AIHData", made));
    }

    // THE THIRD-PARTY USER (AIH-TPU) is a class of user next to the End User, with a profile of its own: an organisation that states, in the
    // terms of the Taxonomies, what it asks to be allowed to do. It registers with the same Register Data Type, carrying that profile in place of a
    // User Profile - never both.
    [Fact]
    public void AThirdPartyUserRegistersWithItsOwnProfile()
    {
        Assert.Equal("valid", Check("ThirdPartyUserProfile", AihSamples.ThirdPartyUserProfile()));
        Assert.Equal("valid", Check("Register", AihSamples.ThirdPartyRegisterRequest()));
    }

    [Fact]
    public void ARegisterRequestCarriesOneProfileNotTwoNorNone()
    {
        var both = AihSamples.ThirdPartyRegisterRequest();
        both["RegisterData"]![0]!["Request"]!.AsObject()["UserProfile"] = AihSamples.UserProfile();
        Assert.StartsWith("not valid", Check("Register", both));
        var none = AihSamples.ThirdPartyRegisterRequest();
        none["RegisterData"]![0]!["Request"]!.AsObject().Remove("ThirdPartyUserProfile");
        Assert.StartsWith("not valid", Check("Register", none));
    }

    [Fact]
    public void AThirdPartyUserStatesWhatItAsksToDo()
    {
        var nothing = AihSamples.ThirdPartyUserProfile(); nothing.Remove("Entitlements");
        Assert.StartsWith("not valid", Check("ThirdPartyUserProfile", nothing));
        var odd = AihSamples.ThirdPartyUserProfile(); odd["Entitlements"]!["UserTypes"] = new JsonArray("Anybody");
        Assert.StartsWith("not valid", Check("ThirdPartyUserProfile", odd));
    }
}
