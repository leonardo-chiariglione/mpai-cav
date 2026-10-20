using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Metadata;
using Mpai.StoreService;

// THE MPAI STORE SERVICE.
//
//   GET  /MPAI/Store/Register                the page where an Implementer registers (workflow step 1)
//   POST /MPAI/Store/Implementer             register: gives the Implementer ID and a registration code
//   POST /MPAI/Store/Implementer/{id}/key    the Implementer gives the identity (CII) of its key, with the code
//   GET  /MPAI/Store/Implementer/{id}        whether that ID has been assigned, its status and credential
//   GET  /MPAI/Store/Approve                 the page where MPAI approves registrations (admin key)
//   GET  /MPAI/Store/Implementer, POST /MPAI/Store/Implementer/{id}/approve   (admin key, header MPAI-Admin)
//   A submission carries MPAI-Signature: the Implementer's signature of the L3 (or MPAI-Admin, for MPAI's own ID 1).
//   POST /MPAI/Store/L3                      submit an L3: published with what was found, or refused
//   GET  /MPAI/Store/L3?name=...             approved L3s, latest versions (workflow step 6)
//   GET  /MPAI/Store/L3/{id}[?version=n]     one L3 (workflow step 6)
//   GET  /MPAI/Store/L3/{id}/versions        its published versions
//   GET  /MPAI/Store/L3/{id}/findings[?version=n]   what the Store found in it
//   GET  /MPAI/Store/L3/{id}/implementations        the binaries it has a fingerprint of (their names)
//   POST /MPAI/Store/L3/{id}/verify          MPAI_AIFS_VerifyImplementation: {BinaryName, Fingerprint} -> Same | Different | NotRecorded
//   POST /MPAI/Store/Licence                 MPAI_AIFS_GetLicence: {AIMName, TrustAnchorID[, ControllerID]} -> the AIM Licence (201 the first time, 200 after: the same one)
//   GET  /MPAI/Store/Licence/{id}            an AIM Licence the Store issued
//   GET  /MPAI/Store/Licence/{id}/status     MPAI_AIFS_VerifyLicence: valid or revoked, and why
//   POST /MPAI/Store/Licence/{id}/revoke     the operator revokes it: {Reason} (admin key, header MPAI-Admin)
//   GET  /MPAI/Store/Key/{keyId}             the public key of the Store that signs AIM Licences (for experiments: a Controller's trust in it comes from its Trust Anchor)
//
//   dotnet run --project MAS\Store\Service -- --Urls https://localhost:5020 --Root <folder> --Packages <folder>
//
// --Root is where the Store keeps its L3s (default: <local application data>\MPAI\Store).
// --Schemas is the published schemas folder (default: the "schemas" folder above this program):
//   the L2s every L3 is checked against.
// --Packages is the folder holding all packages; file: URIs are inspected only inside it.
// --AdminKey is the key of the Store's operator (MPAI); default: made at first start, in <Root>/admin-key.txt.
// --MarkupPercent is the Store's mark-up on the sale price an Implementer proposes (default 0); --StoreId names the Store in the AIM Licences it issues (default mpai-store);
// --StatusMaxAge is how long a status may go unconfirmed (ISO 8601 duration, default P7D); --PublicUrl is where Controllers reach this Store (default: --Urls).
// The Store's own key (the Trust Authority for Implementers) is kept in <Root>/store-key.hex.

var builder = WebApplication.CreateBuilder(args);
var urls    = builder.Configuration["Urls"] ?? "https://localhost:5020";
builder.WebHost.UseUrls(urls);
var root    = builder.Configuration["Root"]
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MPAI", "Store");
var schemas = builder.Configuration["Schemas"] ?? FindSchemas() ?? "schemas";

var repository = new L3Repository(root);
var implementers = new ImplementerRegistry(root);
var storeKey = StoreIdentity.Load(root);
var adminKey = builder.Configuration["AdminKey"] ?? StoreIdentity.AdminKey(root);
var aimMetadata = AimMetadataSchema.Load(schemas);                      // L1: the form of every L3
var fingerprints = new AIF.Store.ImplementationFingerprints(root);
// An L3 is found for the conformance check of a composite among the L3s the Store holds.
var l2 = new L2Conformance(schemas, id =>
    repository.Text(id, null) is { } text ? JsonDocument.Parse(text).RootElement.Clone() : null);
var packages = new PackageCheck(builder.Configuration["Packages"]);
var licences = new AIF.Store.LicenceBook(Path.Combine(root, "licences"));
var markup = decimal.TryParse(builder.Configuration["MarkupPercent"], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var percent) ? percent : 0m;
var licenceIssuer = new AIF.Store.AimLicences.Issuer(storeKey.Key, StoreIdentity.KeyId, builder.Configuration["StoreId"] ?? "mpai-store", builder.Configuration["StatusMaxAge"] ?? "P7D",
                                                     (builder.Configuration["PublicUrl"] ?? urls).TrimEnd('/') + "/MPAI/Store/Licence", markup);
var app = builder.Build();

app.MapPost("/MPAI/Store/L3", async (HttpRequest request) =>
{
    var text = await new StreamReader(request.Body).ReadToEndAsync();
    JsonDocument document;
    try { document = JsonDocument.Parse(text); }
    catch (JsonException ex) { return Results.BadRequest(new { refused = "Not JSON: " + ex.Message }); }
    using (document)
    {
        var l3 = document.RootElement;
        var l3Node = JsonNode.Parse(text)!.AsObject();
        if (l3.ValueKind != JsonValueKind.Object || !l3.TryGetProperty("Identifier", out var identifier) ||
            !identifier.TryGetProperty("AIMName", out var nameElement) || nameElement.GetString() is not { Length: > 0 } id)
            return Results.BadRequest(new { refused = "An L3 needs Identifier.AIMName: without it the Store cannot say what it is." });
        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains(".."))
            return Results.BadRequest(new { refused = $"'{id}' cannot name an L3 in the Store." });

        // ONLY AN IMPLEMENTER THE STORE KNOWS MAY SUBMIT (workflow step 1; STR-4): the Implementer ID in the Identifier
        // is one the Store assigned when the Implementer registered.
        var implementerId = identifier.TryGetProperty("ImplementerID", out var ii) && ii.ValueKind == JsonValueKind.String ? ii.GetString() ?? "" : "";
        var who = implementers.Find(implementerId);
        if (who is null)
        {
            Console.WriteLine($"[Store] refused {id}: Implementer ID '{implementerId}' is not registered");
            return Results.Json(new { id, published = false, refused = $"Implementer ID '{implementerId}' has not been assigned by this Store. Register at /MPAI/Store/Register and put the ID in the Identifier of the L3." }, statusCode: 403);
        }

        // THE SUBMITTER IS AUTHENTICATED (STR-4): MPAI (ID 1) by the key of the Store's operator; any other Implementer by its signature of the L3, checked with the credential the
        // Store issued it - which must be current, for the identity it gave, and for this Implementer ID.
        var signature = request.Headers["MPAI-Signature"].ToString();
        if (implementerId == ImplementerRegistry.MpaiId)
        {
            if (!StoreIdentity.IsAdmin(request.Headers["MPAI-Admin"].ToString(), adminKey))
                return Results.Json(new { id, published = false, refused = "MPAI's own Implementer ID 1 submits with the admin key of the Store (header MPAI-Admin)." }, statusCode: 401);
        }
        else
        {
            var why = StoreIdentity.CheckSubmitter(who, signature, l3Node, storeKey);
            if (why is not null)
            {
                Console.WriteLine($"[Store] refused {id}: {why}");
                return Results.Json(new { id, published = false, refused = why }, statusCode: why.StartsWith("Not approved") ? 403 : 401);
            }
        }

        // THE WHOLE L3 IS VALIDATED (STR-1): its form against the AIM Metadata schema (L1) - every field, not only the Topology -
        // then what import will do to it (its names resolved to Data Types), then its L2 (below).
        var violations = aimMetadata.Violations(l3);
        if (violations.Count > 0)
        {
            Console.WriteLine($"[Store] refused {id}: {violations.Count} violation(s) of the AIM Metadata schema");
            return Results.Json(new { id, published = false, refused = "The L3 is not a valid instance of the AIM Metadata schema.", violations }, statusCode: 422);
        }
        try { AIF.Store.TopologyNormaliser.Normalise(l3).Dispose(); }
        catch (InvalidOperationException refusal)
        {
            Console.WriteLine($"[Store] refused {id}: {refusal.Message}");
            return Results.Json(new { id, published = false, refused = refusal.Message }, statusCode: 422);
        }

        // WHAT IS SIGNALLED: the package the L3 names.
        var findings = new List<Finding>();
        findings.AddRange(await packages.CheckAsync(l3));

        // A MISSING SUB-AIM REFUSES: each Sub-AIM's L3 must be in the composite's
        // package or in the Store, or the composite could not be built.
        var bundled = packages.L3sIn(l3);
        var missing = new List<string>();
        if (l3.TryGetProperty("SubAIMs", out var subs) && subs.ValueKind == JsonValueKind.Array)
            foreach (var sub in subs.EnumerateArray())
                if (sub.TryGetProperty("Identifier", out var si) && si.TryGetProperty("AIMName", out var sn) &&
                    sn.GetString() is { Length: > 0 } subId && subId != id &&
                    !bundled.Contains(subId) && repository.Text(subId, null) is null)
                    missing.Add(subId);
        if (missing.Count > 0)
        {
            Console.WriteLine($"[Store] refused {id}: Sub-AIM(s) {string.Join(", ", missing)} in neither its package nor the Store");
            return Results.Json(new
            {
                id, published = false,
                refused = $"The L3 of {string.Join(", ", missing)} is in neither this AIM's package nor the Store. Submit it first, or bundle it in the package.",
                missing, findings
            }, statusCode: 422);
        }

        // AN L3 THAT IS NOT AN INSTANCE OF ITS L2 IS REFUSED: the L2 its Header
        // names is what the Standard allows (AIF.Metadata.L2Conformance).
        var nonconformities = l2.Check(l3);
        if (nonconformities.Count > 0)
        {
            Console.WriteLine($"[Store] refused {id}: {nonconformities.Count} nonconformit(ies) with its L2");
            return Results.Json(new
            {
                id, published = false,
                refused = $"The L3 is not an instance of its L2 ({L2Conformance.TypeOf(l3)}).",
                nonconformities, findings
            }, statusCode: 422);
        }

        // THE FINGERPRINTS (STR-3): what is approved is recorded with its SHA-256, as the Implementer submitted it.
        findings.AddRange(packages.RecordFingerprints(l3, id, fingerprints));

        var entry = repository.Publish(id, text, findings);
        // Who submitted it, and the signature, are kept with the version.
        File.WriteAllText(Path.Combine(repository.Root, "versions", id, $"{entry.Version}.submission.json"),
            new JsonObject { ["ImplementerID"] = implementerId, ["Signature"] = signature.Length > 0 ? signature : null, ["CredentialID"] = who.Credential?["InstanceCredentialID"]?.DeepClone(), ["Admin"] = implementerId == ImplementerRegistry.MpaiId }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[Store] published {id} v{entry.Version}: {entry.Errors} error(s), {entry.Warnings} warning(s)");
        return Results.Json(new { id, version = entry.Version, published = true, findings }, statusCode: 201);
    }
});

app.MapGet("/MPAI/Store/Register", () => Results.Content(ImplementerRegistry.Page, "text/html; charset=utf-8"));
app.MapGet("/MPAI/Store/Approve", () => Results.Content(ImplementerRegistry.ApprovePage, "text/html; charset=utf-8"));

app.MapPost("/MPAI/Store/Implementer", async (HttpRequest request) =>
{
    JsonDocument body;
    try { body = JsonDocument.Parse(await new StreamReader(request.Body).ReadToEndAsync()); }
    catch (JsonException ex) { return Results.BadRequest(new { refused = "Not JSON: " + ex.Message }); }
    using (body)
    {
        string S(string n) => body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        if (S("name").Length == 0) return Results.BadRequest(new { refused = "A name is needed." });
        if (S("kind") is not (ImplementerRegistry.Human or ImplementerRegistry.Machine))
            return Results.BadRequest(new { refused = "Say what the entity you register is: kind Human or Machine." });
        var (who, code) = implementers.Register(S("name"), S("organisation"), S("email"), S("kind"));
        Console.WriteLine($"[Store] registered Implementer {who.Id}: {who.Name}");
        return Results.Json(new { implementerId = who.Id, name = who.Name, organisation = who.Organisation, kind = who.Kind, registrationCode = code }, statusCode: 201);
    }
});

app.MapGet("/MPAI/Store/Implementer/{id}", (string id) =>
    implementers.Find(id) is { } who ? Results.Json(new { implementerId = who.Id, name = who.Name, organisation = who.Organisation, kind = who.Kind, status = who.Status, credential = who.Credential })
                                     : Results.NotFound(new { missing = $"Implementer ID {id} has not been assigned." }));

app.MapGet("/MPAI/Store/Implementer", (HttpRequest request) =>
    StoreIdentity.IsAdmin(request.Headers["MPAI-Admin"].ToString(), adminKey)
        ? Results.Json(implementers.All().Select(i => new { implementerId = i.Id, name = i.Name, organisation = i.Organisation, email = i.Email, kind = i.Kind, status = i.Status }))
        : Results.Json(new { refused = "The admin key of the Store is needed (header MPAI-Admin)." }, statusCode: 401));

// The Implementer gives the identity of its key (its CII), with the registration code it was given: the Store keeps it for MPAI's approval.
app.MapPost("/MPAI/Store/Implementer/{id}/key", async (HttpRequest request, string id) =>
{
    try
    {
        using var body = JsonDocument.Parse(await new StreamReader(request.Body).ReadToEndAsync());
        var code = body.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
        if (!body.RootElement.TryGetProperty("cii", out var ciiElement) || JsonNode.Parse(ciiElement.GetRawText()) is not JsonObject cii)
            return Results.BadRequest(new { refused = "The identity (cii) of the key is needed." });
        if (AIF.Trust.PtfIdentity.Check(cii, out _) != AIF.Trust.PtfIdentity.Outcome.Valid)
            return Results.BadRequest(new { refused = "The identity is not a valid Cryptographic Instance Identity (it must prove it holds its private key)." });
        var refused = implementers.BindKey(id, code, cii);
        return refused is null ? Results.Json(new { implementerId = id, status = ImplementerRegistry.KeyBound }) : Results.Json(new { refused }, statusCode: 403);
    }
    catch (JsonException ex) { return Results.BadRequest(new { refused = "Not JSON: " + ex.Message }); }
});

// MPAI approves a registration: the Store, the Trust Authority for Implementers, issues the credential for the identity the Implementer gave.
app.MapPost("/MPAI/Store/Implementer/{id}/approve", (HttpRequest request, string id) =>
{
    if (!StoreIdentity.IsAdmin(request.Headers["MPAI-Admin"].ToString(), adminKey))
        return Results.Json(new { refused = "The admin key of the Store is needed (header MPAI-Admin)." }, statusCode: 401);
    var refused = implementers.Approve(id, cii => storeKey.Issuer.IssueCredential(cii, "Implementer", id, "Implementer", DateTimeOffset.UtcNow, TimeSpan.FromDays(365)));
    if (refused is null) Console.WriteLine($"[Store] approved Implementer {id}");
    return refused is null ? Results.Json(new { implementerId = id, status = ImplementerRegistry.Approved }) : Results.Json(new { refused }, statusCode: 409);
});

app.MapGet("/MPAI/Store/L3", (string? name) => Results.Json(repository.Latest(name)));

app.MapGet("/MPAI/Store/L3/{id}", (HttpResponse response, string id, int? version) =>
{
    var text = repository.Text(id, version);
    if (text is null) return Results.NotFound(new { missing = $"{id}{(version is null ? "" : " v" + version)} is not in the Store." });
    response.Headers["MPAI-Store-Version"] = (version ?? repository.Versions(id).Last().Version).ToString();
    return Results.Text(text, "application/json");
});

app.MapGet("/MPAI/Store/L3/{id}/versions", (string id) =>
{
    var list = repository.Versions(id);
    return list.Count == 0 ? Results.NotFound(new { missing = $"{id} is not in the Store." }) : Results.Json(list);
});

app.MapGet("/MPAI/Store/L3/{id}/findings", (string id, int? version) =>
{
    var findings = repository.Findings(id, version);
    return findings is null ? Results.NotFound(new { missing = $"{id} is not in the Store." }) : Results.Json(findings);
});

app.MapGet("/MPAI/Store/L3/{id}/implementations", (string id) =>
    repository.Versions(id).Count == 0 ? Results.NotFound(new { missing = $"{id} is not in the Store." }) : Results.Json(new { binaries = fingerprints.Binaries(id) }));

// MPAI_AIFS_VerifyImplementation: a Controller measured what it obtained; the Store says whether that is the fingerprint it approved. The fingerprint is never given out.
app.MapPost("/MPAI/Store/L3/{id}/verify", async (HttpRequest request, string id) =>
{
    if (repository.Versions(id).Count == 0) return Results.NotFound(new { missing = $"{id} is not in the Store." });
    try
    {
        using var body = JsonDocument.Parse(await new StreamReader(request.Body).ReadToEndAsync());
        string S(string n) => body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        if (S("BinaryName").Length == 0 || S("Fingerprint").Length == 0) return Results.BadRequest(new { refused = "BinaryName and Fingerprint are needed." });
        return Results.Json(new { result = fingerprints.Verify(id, S("BinaryName"), S("Fingerprint")).ToString() });
    }
    catch (JsonException ex) { return Results.BadRequest(new { refused = "Not JSON: " + ex.Message }); }
});

// MPAI_AIFS_GetLicence: the Store sublicenses the AIM to the holder of the Trust Anchor, under the Implementer's AIMLicence in the L3 it published, at the price with its mark-up, signed
// with its key. The first time it is issued (201); asked again, the same one is given (200): what is paid for once is not paid for again. A refusal says why.
app.MapPost("/MPAI/Store/Licence", async (HttpRequest request) =>
{
    try
    {
        using var body = JsonDocument.Parse(await new StreamReader(request.Body).ReadToEndAsync());
        string S(string n) => body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        var aim = S("AIMName"); var anchor = S("TrustAnchorID"); var controller = S("ControllerID");
        if (aim.Length == 0 || anchor.Length == 0) return Results.BadRequest(new { refused = "AIMName and TrustAnchorID are needed." });
        if (repository.Text(aim, null) is not { } text) return Results.NotFound(new { refused = $"{aim} is not in the Store." });
        var l3 = JsonNode.Parse(text)!.AsObject();
        if (l3["AIMLicence"] is not JsonObject offer) return Results.NotFound(new { refused = $"{aim} has no AIMLicence in its Metadata: it needs no AIM Licence." });
        var approved = new Dictionary<string, string>();
        foreach (var item in l3["Implementations"] as JsonArray ?? [])
            if ((string?)item?["BinaryName"] is { } binary && fingerprints.Approved(aim, binary) is { } fingerprint) approved[binary] = fingerprint;
        var (licence, created) = licences.GetOrIssue(aim, anchor, controller.Length > 0 ? controller : null, () =>
            AIF.Store.AimLicences.Issue(offer.DeepClone().AsObject(), aim, AIF.Store.AimRepository.VersionIn(JsonDocument.Parse(text).RootElement), approved, anchor,
                                        controller.Length > 0 ? controller : null, licenceIssuer, DateTimeOffset.UtcNow, (string?)l3["Identifier"]?["ImplementerID"] ?? ""));
        if (created) Console.WriteLine($"[Store] issued an AIM Licence for {aim} to the Trust Anchor {anchor}");
        return Results.Text(licence, "application/json", statusCode: created ? 201 : 200);
    }
    catch (JsonException ex) { return Results.BadRequest(new { refused = "Not JSON: " + ex.Message }); }
    catch (InvalidOperationException refusal) { return Results.Json(new { refused = refusal.Message }, statusCode: 403); }
});

app.MapGet("/MPAI/Store/Licence/{id}", (string id) =>
    licences.Text(id) is { } licence ? Results.Text(licence, "application/json") : Results.NotFound(new { missing = $"The Store issued no AIM Licence {id}." }));

// MPAI_AIFS_VerifyLicence: whether the AIM Licence is valid or was revoked, and why.
app.MapGet("/MPAI/Store/Licence/{id}/status", (string id) =>
    licences.Status(id) is { } s ? Results.Json(new { aimLicenceId = id, status = s.Status, reason = s.Reason, time = s.Time }) : Results.NotFound(new { missing = $"The Store issued no AIM Licence {id}." }));

// The Store's operator revokes an AIM Licence (for example for a security finding, a breach of its terms or a reversed payment).
app.MapPost("/MPAI/Store/Licence/{id}/revoke", async (HttpRequest request, string id) =>
{
    if (!StoreIdentity.IsAdmin(request.Headers["MPAI-Admin"].ToString(), adminKey))
        return Results.Json(new { refused = "The admin key of the Store is needed (header MPAI-Admin)." }, statusCode: 401);
    var reason = "";
    try { using var body = JsonDocument.Parse(await new StreamReader(request.Body).ReadToEndAsync()); reason = body.RootElement.TryGetProperty("Reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : ""; }
    catch (JsonException) { }
    if (reason.Length == 0) return Results.BadRequest(new { refused = "A Reason is needed." });
    if (!licences.Revoke(id, reason, DateTimeOffset.UtcNow)) return Results.NotFound(new { missing = $"The Store issued no AIM Licence {id}." });
    Console.WriteLine($"[Store] revoked the AIM Licence {id}: {reason}");
    return Results.Json(new { aimLicenceId = id, status = "revoked", reason });
});

// The public key the Store signs AIM Licences with, by the KeyID the AIM Licence names. A Controller's trust in it comes from the Trust Anchor of the Store; this route lets a Controller
// of an experiment fetch it.
app.MapGet("/MPAI/Store/Key/{keyId}", (string keyId) =>
    keyId == StoreIdentity.KeyId ? Results.Json(new { keyId, algorithm = "ECDSA-P256", publicKey = Convert.ToHexString(storeKey.Key.ExportSubjectPublicKeyInfo()) })
                                 : Results.NotFound(new { missing = $"The Store has no key {keyId}." }));

Console.WriteLine("=== MPAI Store ===");
Console.WriteLine($"  Listen:   {urls}");
Console.WriteLine($"  Root:     {repository.Root}");
Console.WriteLine($"  Schemas:  {schemas}   ({l2.Count} L2s)");
Console.WriteLine($"  Packages: {packages.Where}");
Console.WriteLine($"  L3s:      {repository.Count}");
Console.WriteLine($"  AIM Licences issued: {licences.Count}   (mark-up {markup}%)");
Console.WriteLine($"  Implementers registered: {implementers.Count}   (register at {urls}/MPAI/Store/Register; MPAI approves at {urls}/MPAI/Store/Approve)");
app.Run();

static string? FindSchemas()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (Directory.Exists(Path.Combine(dir.FullName, "schemas", "MMC")))
            return Path.Combine(dir.FullName, "schemas");
    return null;
}
