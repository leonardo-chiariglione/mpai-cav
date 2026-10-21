using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Mpai.Mmm.Server;

// WHO IS ASKING, without a meeting code. The server is put on the internet by Tailscale Funnel (or a tunnel) at an https address; nobody has to install anything. A person is named by:
//  - an INVITATION: the chair gives each guest a personal link (?invite=<token>), the server turns it into the guest's name, once, and keeps it in a cookie of this site (12 hours); a guest is
//    whoever holds its invitation, and only the invited names are given the Rights to the wing and the room - the chair's decision, made in the M-Instance, not here;
//  - the chair's own browser: a request made on this machine directly (no proxy in between: no X-Forwarded-For, no Tailscale login) is the chair;
//  - Tailscale's headers (Tailscale-User-Login), when the person comes through `tailscale serve` on the tailnet: a login listed with --guests login=Name is that name;
//  - nobody else, unless the chair allows visitors (--visitors): the atrium of the building is public, so a visitor - named at random, kept in a cookie - may come in, look, talk and ask, and
//    no further. Without --visitors a request with no invitation is not answered (401).
public sealed class Identity(string chair, IReadOnlyDictionary<string, string> loginGuests, IReadOnlyDictionary<string, string>? invites = null, bool visitors = false)
{
    public sealed record Who(string Name, string Kind, string Login);          // Kind: chair, guest, visitor

    public const string Cookie = "mmm-id", VisitorCookie = "mmm-visitor";
    public string Chair { get; } = chair;
    public bool Visitors { get; } = visitors;
    private readonly Dictionary<string, string> loginGuests = new(loginGuests, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> invites = new(invites ?? new Dictionary<string, string>(), StringComparer.Ordinal);       // token -> name

    public string? NameOfInvitation(string? token) => token is { Length: > 0 } && invites.TryGetValue(token, out var n) ? n : null;
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(9)).ToLowerInvariant();

    // The chair's own browser: it reaches the server at a loopback address - the Host it asked for is this machine's - with nothing in between. A tunnel or the Funnel also connects from this machine,
    // but the Host of what it passes on is its public name, and it may add X-Forwarded-For: either is enough to say it is not the chair.
    private static bool Direct(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip) && !ctx.Request.Headers.ContainsKey("X-Forwarded-For") && !ctx.Request.Headers.ContainsKey("Tailscale-User-Login")
        && ctx.Request.Host.Host is "127.0.0.1" or "localhost" or "::1" or "[::1]";

    public Who? Of(HttpContext ctx)
    {
        var login = ctx.Request.Headers["Tailscale-User-Login"].ToString();
        if (login.Length > 0)                                                  // through `tailscale serve`: the headers are Tailscale's only when it sent them, from this machine
        {
            if (ctx.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)) return null;
            if (loginGuests.TryGetValue(login, out var guest)) return new Who(guest, guest == Chair ? "chair" : "guest", login);
            if (!Visitors) return null;                                       // somebody on the tailnet the chair did not list: only a visitor, if visitors are allowed
            var local = Regex.Replace(login.Split('@')[0], "[^A-Za-z0-9]", "");
            return new Who("Visitor-" + (local.Length > 0 ? local : "x"), "visitor", login);
        }
        if (Direct(ctx)) return new Who(Chair, "chair", "");
        if (NameOfInvitation(ctx.Request.Cookies[Cookie]) is { } invited) return new Who(invited, invited == Chair ? "chair" : "guest", "");
        if (Visitors && ctx.Request.Cookies[VisitorCookie] is { Length: >= 6 } v && Regex.IsMatch(v, "^[0-9a-f]+$")) return new Who("Visitor-" + v[..6], "visitor", "");
        return null;
    }
}
