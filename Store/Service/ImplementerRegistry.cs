using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mpai.StoreService;

// THE IMPLEMENTERS OF THE STORE. An Implementer is a member of the trusted MPAI ecosystem, and becomes one in three steps (the first actions of the workflow, MPAI Store, 7):
//   Registered  it registered on the page of the Store (name, organisation, e-mail) and received its Implementer ID and a registration code, shown once;
//   KeyBound    it gave the Store the Cryptographic Instance Identity of a key pair of its own, with the registration code;
//   Approved    MPAI approved the registration, and the Store, the Trust Authority for Implementers, issued it a credential (MPAI-PTF Instance Credential) for that identity.
// Only an Approved Implementer can submit (STR-4): each L3 carries its signature, which the Store checks with the credential. ID 1 is MPAI's own; it submits with the key of the
// Store's operator (the admin key), as it is the Store.
//
// Kept in <root>/implementers.json, which is the whole truth. IDs are numbers, in the order of registration.
public sealed class ImplementerRegistry
{
    public const string MpaiId = "1";
    public const string Registered = "Registered", KeyBound = "KeyBound", Approved = "Approved";
    public const string Human = "Human", Machine = "Machine";                      // what the registered entity is: a person, or a machine (a Health Back End, say)

    public sealed record Implementer(string Id, string Name, string Organisation, string Email, DateTimeOffset RegisteredAt, string Status,
                                     string CodeHash, JsonObject? Cii, JsonObject? Credential, string Kind = Human);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object gate = new();
    private readonly string file;
    private readonly List<Implementer> implementers;

    public ImplementerRegistry(string root)
    {
        Directory.CreateDirectory(root);
        file = Path.Combine(root, "implementers.json");
        implementers = File.Exists(file) ? JsonSerializer.Deserialize<List<Implementer>>(File.ReadAllText(file)) ?? [] : [];
        if (!implementers.Any(i => i.Id == MpaiId))
        {
            implementers.Insert(0, new Implementer(MpaiId, "MPAI", "MPAI", "", DateTimeOffset.UtcNow, Approved, "", null, null, Human));
            Save();
        }
    }

    private void Save() => File.WriteAllText(file, JsonSerializer.Serialize(implementers, Json));

    public int Count { get { lock (gate) return implementers.Count; } }                 // MPAI included

    public Implementer? Find(string id)
    {
        lock (gate) return implementers.FirstOrDefault(i => i.Id == id);
    }

    public IReadOnlyList<Implementer> All() { lock (gate) return implementers.ToList(); }

    private static string HashOf(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    // Registers an Implementer: its ID, and the registration code that only it has seen.
    public (Implementer Implementer, string Code) Register(string name, string organisation, string email, string kind)
    {
        lock (gate)
        {
            var next = implementers.Select(i => int.TryParse(i.Id, out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
            var added = new Implementer(next.ToString(), name, organisation, email, DateTimeOffset.UtcNow, Registered, HashOf(code), null, null, kind);
            implementers.Add(added);
            Save();
            return (added, code);
        }
    }

    // The Implementer gives the identity of its key, with the registration code it was given. Null if all is well, else why not.
    public string? BindKey(string id, string code, JsonObject cii)
    {
        lock (gate)
        {
            var i = implementers.FirstOrDefault(x => x.Id == id);
            if (i is null || i.Id == MpaiId) return $"Implementer ID {id} has not been assigned.";
            if (i.Status == Approved) return "This Implementer is approved already; its key cannot be changed here.";
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(HashOf(code)), Encoding.UTF8.GetBytes(i.CodeHash))) return "The registration code is not the one given at registration.";
            implementers[implementers.IndexOf(i)] = i with { Cii = cii, Status = KeyBound };
            Save();
            return null;
        }
    }

    // MPAI approves: the credential the Store issued for the identity. Null if all is well, else why not.
    public string? Approve(string id, Func<JsonObject, JsonObject> issue)
    {
        lock (gate)
        {
            var i = implementers.FirstOrDefault(x => x.Id == id);
            if (i is null) return $"Implementer ID {id} has not been assigned.";
            if (i.Cii is null) return "It has given no key yet.";
            implementers[implementers.IndexOf(i)] = i with { Credential = issue(i.Cii), Status = Approved };
            Save();
            return null;
        }
    }

    // The page an Implementer opens to register.
    public const string Page = """
<!doctype html><html lang="en"><head><meta charset="utf-8"><title>MPAI Store - register as an Implementer</title>
<style>body{font:16px system-ui,sans-serif;max-width:36rem;margin:3rem auto;padding:0 1rem}label{display:block;margin:.8rem 0 .2rem}
input,select{width:100%;padding:.4rem;box-sizing:border-box}button{margin-top:1rem;padding:.5rem 1.2rem}#out{margin-top:1.5rem;padding:1rem;background:#eef;display:none}
#err{color:#b00;margin-top:1rem}code{background:#eee;padding:0 .3rem}</style></head><body>
<h1>MPAI Store</h1><h2>Register as an Implementer</h2>
<p>You receive an Implementer ID and a registration code. In the Store App you give the code once; the App makes your key and gives the Store its public part. MPAI then approves your registration and the Store gives you a credential. Every L3 you submit carries your Implementer ID in its <code>Identifier</code> and is signed with your key.</p>
<label for="kind">The entity you register is</label><select id="kind"><option value="Human">a human</option><option value="Machine">a machine (for example a Health Back End)</option></select>
<label for="name">Name of the human or of the machine</label><input id="name" autocomplete="name">
<label for="org">Organisation</label><input id="org" autocomplete="organization">
<label for="email">E-mail</label><input id="email" type="email" autocomplete="email">
<button id="go">Register</button><div id="err"></div>
<div id="out">Your Implementer ID is <strong id="id"></strong>. Your registration code is <strong id="code"></strong>.<br>Keep both: the code is shown only now.</div>
<script>
document.getElementById('go').onclick = async () => {
  const err = document.getElementById('err'); err.textContent = '';
  const body = { kind: kind.value, name: name.value.trim(), organisation: org.value.trim(), email: email.value.trim() };
  if (!body.name) { err.textContent = 'A name is needed.'; return; }
  try {
    const r = await fetch('/MPAI/Store/Implementer', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const j = await r.json();
    if (!r.ok) { err.textContent = j.refused || ('The Store answered ' + r.status); return; }
    document.getElementById('id').textContent = j.implementerId; document.getElementById('code').textContent = j.registrationCode;
    document.getElementById('out').style.display = 'block';
  } catch (e) { err.textContent = 'The Store could not be reached: ' + e.message; }
};
</script></body></html>
""";

    // The page where MPAI approves the registrations that wait: it asks for the admin key of the Store.
    public const string ApprovePage = """
<!doctype html><html lang="en"><head><meta charset="utf-8"><title>MPAI Store - approve Implementers</title>
<style>body{font:16px system-ui,sans-serif;max-width:48rem;margin:3rem auto;padding:0 1rem}input{padding:.4rem;width:20rem}button{padding:.3rem .8rem}
td,th{padding:.3rem .6rem;text-align:left;border-bottom:1px solid #ccc}#err{color:#b00}</style></head><body>
<h1>MPAI Store</h1><h2>Registrations waiting for MPAI's approval</h2>
<p>Admin key: <input id="key" type="password"> <button id="load">Show</button></p><div id="err"></div>
<table id="t"></table>
<script>
const $ = id => document.getElementById(id);
async function load() {
  $('err').textContent = '';
  const r = await fetch('/MPAI/Store/Implementer', { headers: { 'MPAI-Admin': $('key').value } });
  if (!r.ok) { $('err').textContent = 'The Store answered ' + r.status; return; }
  const list = await r.json();
  $('t').innerHTML = '<tr><th>ID</th><th>Name</th><th>Organisation</th><th>Kind</th><th>Status</th><th></th></tr>' + list.map(i =>
    `<tr><td>${i.implementerId}</td><td>${i.name}</td><td>${i.organisation}</td><td>${i.kind}</td><td>${i.status}</td><td>${i.status === 'KeyBound' ? `<button data-id="${i.implementerId}">Approve</button>` : ''}</td></tr>`).join('');
  document.querySelectorAll('button[data-id]').forEach(b => b.onclick = async () => {
    const a = await fetch('/MPAI/Store/Implementer/' + b.dataset.id + '/approve', { method: 'POST', headers: { 'MPAI-Admin': $('key').value } });
    if (!a.ok) $('err').textContent = (await a.json()).refused || ('The Store answered ' + a.status); load();
  });
}
$('load').onclick = load;
</script></body></html>
""";
}
