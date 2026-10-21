using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Mpai.Mmm;
using Mpai.Mmm.Client;
using Mpai.Mmm.Server;

namespace Mpai.Aif.Tests;

// THE MMM THROUGH TAILSCALE: nothing to type, no code - whoever Tailscale says is asking (its login, in the headers `tailscale serve` adds), and what the person may do is what the chair
// gave it in the M-Instance. A guest the chair listed has its name; any other login is a visitor; a request that comes from another machine, or by the Funnel, is not answered. A human
// registers as who it is and chooses its Persona from the M-Instance's catalogue - it does not bring one - and, back after closing its browser, is given its User again.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
[Collection("MmmMeeting")]
public class MmmTailscaleTests
{
    private static JsonObject Profile(string name, string? choice) => new()
    {
        ["Header"] = "MMM-PPR-V2.2", ["MInstanceID"] = "MI1", ["humanID"] = $"{name}-human", ["PersonalProfileID"] = $"Profile-{name}-human",
        ["PersonalProfile"] = new JsonObject
        {
            ["Data"] = new JsonArray(new JsonObject { ["FirstName"] = name }),
            ["Processes"] = new JsonArray(new JsonObject
            {
                ["ProcessIDOrProcess"] = new JsonArray(new JsonObject { ["ProcessID"] = name }),
                ["PersonaIDOrPersona"] = new JsonArray(new JsonObject { ["PersonaID"] = $"{name}-Persona" }.Also(p => { if (choice is not null) p["PersonaChoice"] = choice; }))
            })
        }
    };

    private static async Task<(HttpResponseMessage Response, JsonNode? Body)> Register(HttpClient http, string name, string? choice, string? login)
    {
        using var rq = new HttpRequestMessage(HttpMethod.Post, "/api/v2.2/identity/register");
        rq.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (login is not null) rq.Headers.Add("Tailscale-User-Login", login);
        rq.Content = JsonContent.Create(new JsonObject { ["SourceProcessID"] = $"{name}-human", ["Action"] = "", ["Complements"] = new JsonObject { ["With"] = new JsonArray(Profile(name, choice)) } });
        var rs = await http.SendAsync(rq);
        return (rs, JsonNode.Parse(await rs.Content.ReadAsStringAsync()));
    }

    private static async Task<JsonNode?> WhoAmI(HttpClient http, string? login)
    {
        using var rq = new HttpRequestMessage(HttpMethod.Get, "/viewer/whoami");
        if (login is not null) rq.Headers.Add("Tailscale-User-Login", login);
        var rs = await http.SendAsync(rq);
        return rs.IsSuccessStatusCode ? JsonNode.Parse(await rs.Content.ReadAsStringAsync()) : null;
    }

    [Fact]
    public async Task WhoeverTailscaleSaysAsksAndTheChairBrowsesOnThisMachine()
    {
        var m = new MInstance(Repository.Schemas);
        World.Setup(m, Repository.Root);
        m.Catalogue = new HashSet<string> { "Female_Adult_01", "Male_Adult_02" };
        m.CatalogueEntries = new JsonArray(new JsonObject { ["Name"] = "Female_Adult_01", ["Group"] = "Adults", ["Gender"] = "Female" }, new JsonObject { ["Name"] = "Male_Adult_02", ["Group"] = "Adults", ["Gender"] = "Male" });
        var identity = new Identity("Leonardo", new Dictionary<string, string> { ["anna@example.com"] = "Anna" }, null, visitors: true);
        var app = MmmServer.Build(m, "http://127.0.0.1:0", Path.Combine(Repository.Root, "MMM", "Viewer"), identity: identity);
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            Assert.Equal("Leonardo", (string?)(await WhoAmI(http, null))!["name"]);                         // this machine, no Tailscale in between: the chair
            Assert.Equal("host", (string?)(await WhoAmI(http, null))!["kind"]);
            var anna = (await WhoAmI(http, "ANNA@example.com"))!;                                            // a guest the chair listed (the login is not case-sensitive)
            Assert.Equal("Anna", (string?)anna["name"]); Assert.Equal("guest", (string?)anna["kind"]); Assert.Null(anna["resume"]);
            var visitor = (await WhoAmI(http, "someone.else@example.org"))!;                                // any other login: a visitor
            Assert.Equal("visitor", (string?)visitor["kind"]); Assert.StartsWith("Visitor-", (string?)visitor["name"]);

            // A human that is not registered yet is shown what the M-Instance offers: the catalogue of Personae. No token, no User: there is none yet.
            var offered = JsonNode.Parse(await http.GetStringAsync("/api/v2.2/register/personae"))!.AsArray();
            Assert.Equal(["Female_Adult_01", "Male_Adult_02"], offered.Select(e => (string)e!["Name"]!).ToArray());

            // A person registers as who it is, and only as that.
            Assert.Equal(403, (int)(await Register(http, "Bruno", null, "anna@example.com")).Response.StatusCode);
            // A Persona is chosen from the catalogue, not brought: one that is not in it is refused, and the human is not registered by the refusal.
            Assert.Equal(422, (int)(await Register(http, "Anna", "Not_In_The_Catalogue", "anna@example.com")).Response.StatusCode);
            var ok = await Register(http, "Anna", "Female_Adult_01", "anna@example.com");
            Assert.Equal(201, (int)ok.Response.StatusCode);
            Assert.Equal("catalogue/Female_Adult_01.glb", m.Get("Anna-Persona")!.Model);

            // Back after closing the browser: the same person is given its User again (a new token; the old one is dead), and told where its Persona stands.
            var firstToken = ok.Response.Headers.GetValues("MMM-Tokens").Single().Split('=')[1];
            var back = (await WhoAmI(http, "anna@example.com"))!["resume"]!;
            Assert.Equal("Anna-Persona", (string?)back["persona"]!["Persona"]);
            Assert.NotEqual(firstToken, (string?)back["token"]);
            Assert.Null(m.ProcessOf(firstToken));
            Assert.Equal("Anna", m.ProcessOf((string)back["token"]!));
        }
        finally { await app.DisposeAsync(); Meeting.Reset(); }
    }

    [Fact]
    public async Task WhatIsNotThroughTailscaleIsNotAnswered()
    {
        var m = new MInstance(Repository.Schemas);
        var identity = new Identity("Leonardo", new Dictionary<string, string>());
        var app = MmmServer.Build(m, "http://127.0.0.1:0", Path.Combine(Repository.Root, "MMM", "Viewer"), identity: identity);
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            using var funnel = new HttpRequestMessage(HttpMethod.Get, "/viewer/whoami");
            funnel.Headers.Add("X-Forwarded-For", "203.0.113.9");                                           // a proxy that passed it on, with no Tailscale login: not the chair
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await http.SendAsync(funnel)).StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }

    // The MPAI Officer Account Level is offered to the chair, and to the people the chair designates (--officers); the others are not offered it, and are refused (403) if they ask.
    [Fact]
    public async Task TheOfficerLevelIsOfferedOnlyToTheChairAndTheDesignated()
    {
        var m = new MInstance(Repository.Schemas);
        World.Setup(m, Repository.Root);
        var identity = new Identity("Leonardo", new Dictionary<string, string>(), new Dictionary<string, string> { ["tok-anna"] = "Anna", ["tok-pres"] = "President" });
        var app = MmmServer.Build(m, "http://127.0.0.1:0", Path.Combine(Repository.Root, "MMM", "Viewer"), identity: identity, officers: new HashSet<string> { "President" });
        await app.StartAsync();
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
            async Task<string[]> Levels(string? cookie, bool proxied)
            {
                using var rq = new HttpRequestMessage(HttpMethod.Get, "/api/v2.2/register/options");
                if (proxied) rq.Headers.Add("X-Forwarded-For", "203.0.113.9");
                if (cookie is not null) rq.Headers.Add("Cookie", cookie);
                return JsonNode.Parse(await (await http.SendAsync(rq)).Content.ReadAsStringAsync())!.AsArray().Select(l => (string)l!["id"]!).ToArray();
            }
            Assert.Equal(["Visitor", "Participant", "Officer"], await Levels(null, proxied: false));                // the chair, on this machine
            Assert.Equal(["Visitor", "Participant"], await Levels("mmm-id=tok-anna", proxied: true));               // a guest
            Assert.Equal(["Visitor", "Participant", "Officer"], await Levels("mmm-id=tok-pres", proxied: true));    // one the chair designated

            async Task<int> Register(string name, string level, string cookie)
            {
                using var rq = new HttpRequestMessage(HttpMethod.Post, "/api/v2.2/identity/register");
                rq.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString()); rq.Headers.Add("X-Forwarded-For", "203.0.113.9"); rq.Headers.Add("Cookie", cookie);
                var p = Profile(name, null); p["PersonalProfile"]!["AccountLevel"] = level;
                rq.Content = JsonContent.Create(new JsonObject { ["SourceProcessID"] = $"{name}-human", ["Action"] = "", ["Complements"] = new JsonObject { ["With"] = new JsonArray(p) } });
                return (int)(await http.SendAsync(rq)).StatusCode;
            }
            Assert.Equal(403, await Register("Anna", "Officer", "mmm-id=tok-anna"));
            Assert.Equal(201, await Register("President", "Officer", "mmm-id=tok-pres"));
        }
        finally { await app.DisposeAsync(); Meeting.Reset(); }
    }
}

public class MmmInvitationTests
{
    private static HttpRequestMessage Get(string path, string? cookie = null, bool proxied = true)
    {
        var rq = new HttpRequestMessage(HttpMethod.Get, path);
        if (proxied) rq.Headers.Add("X-Forwarded-For", "203.0.113.9");            // what comes through the Funnel (or a tunnel): not this machine
        if (cookie is not null) rq.Headers.Add("Cookie", cookie);
        return rq;
    }

    // The chair gives each guest a personal link; the server turns it into the guest's name once, and keeps it in a cookie. Nobody else is answered, unless the chair allows visitors.
    [Fact]
    public async Task AGuestIsWhoeverHoldsItsInvitation()
    {
        var m = new MInstance(Repository.Schemas);
        var identity = new Identity("Leonardo", new Dictionary<string, string>(), new Dictionary<string, string> { ["tok-anna"] = "Anna", ["tok-leo"] = "Leonardo" });
        var app = MmmServer.Build(m, "http://127.0.0.1:0", Path.Combine(Repository.Root, "MMM", "Viewer"), identity: identity);
        await app.StartAsync();
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await http.SendAsync(Get("/viewer/whoami"))).StatusCode);                  // no invitation
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await http.SendAsync(Get("/viewer/whoami", "mmm-id=guess"))).StatusCode); // a guess
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await http.SendAsync(Get("/viewer/?world=1&invite=wrong"))).StatusCode);

            var landing = await http.SendAsync(Get("/viewer/?world=1&mic=1&invite=tok-anna"));                                             // the link: the cookie is given, the address cleaned
            Assert.Equal(System.Net.HttpStatusCode.Redirect, landing.StatusCode);
            Assert.DoesNotContain("invite", landing.Headers.Location!.ToString());
            Assert.Contains(landing.Headers.GetValues("Set-Cookie"), c => c.StartsWith("mmm-id=tok-anna") && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));

            var me = JsonNode.Parse(await (await http.SendAsync(Get("/viewer/whoami", "mmm-id=tok-anna"))).Content.ReadAsStringAsync())!;
            Assert.Equal("Anna", (string?)me["name"]); Assert.Equal("guest", (string?)me["kind"]);
            Assert.Equal("chair", (string?)JsonNode.Parse(await (await http.SendAsync(Get("/viewer/whoami", "mmm-id=tok-leo"))).Content.ReadAsStringAsync())!["kind"]);
            Assert.Equal("host", (string?)JsonNode.Parse(await (await http.SendAsync(Get("/viewer/whoami", proxied: false))).Content.ReadAsStringAsync())!["kind"]);   // this machine, directly: the host
            using var tunnel = Get("/viewer/whoami", proxied: false); tunnel.Headers.Host = "something.trycloudflare.com";                // a tunnel that passes no X-Forwarded-For: its Host is its public name
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await http.SendAsync(tunnel)).StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task AVisitorMayComeInOnlyIfTheChairAllowsIt()
    {
        var m = new MInstance(Repository.Schemas);
        var identity = new Identity("Leonardo", new Dictionary<string, string>(), null, visitors: true);
        var app = MmmServer.Build(m, "http://127.0.0.1:0", Path.Combine(Repository.Root, "MMM", "Viewer"), identity: identity);
        await app.StartAsync();
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
            var first = await http.SendAsync(Get("/viewer/whoami"));
            Assert.Equal(System.Net.HttpStatusCode.OK, first.StatusCode);
            var cookie = first.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("mmm-visitor=")).Split(';')[0];
            var name = (string?)JsonNode.Parse(await first.Content.ReadAsStringAsync())!["name"];
            Assert.StartsWith("Visitor-", name);
            Assert.Equal("visitor", (string?)JsonNode.Parse(await first.Content.ReadAsStringAsync())!["kind"]);
            Assert.Equal(name, (string?)JsonNode.Parse(await (await http.SendAsync(Get("/viewer/whoami", cookie))).Content.ReadAsStringAsync())!["name"]);   // the same visitor again
        }
        finally { await app.DisposeAsync(); }
    }
}

internal static class AlsoExtension
{
    public static T Also<T>(this T value, Action<T> act) { act(value); return value; }
}
