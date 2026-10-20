using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using AIF.Store;

namespace MPAIApps.StoreApp;

// The window an Implementer sees. It holds no Store logic: every check and every
// publication is the Store Service's, reached over its API, so this window and any
// other client agree on what "published" and "refused" mean.
// It asks for the Implementer ID and asks the Service whether it has been assigned; an Implementer who has none registers
// on the page of the Service (Register), which gives it with a registration code. The first time, the App makes the Implementer's key (kept in its folder), gives the Store its
// identity with the code, and waits for MPAI to approve: from then on each L3 is signed with the key, and the Store checks it. The ID is kept for the next time.
public sealed class StoreForm : Form
{
    private static readonly string StoreUrl =
        (Environment.GetEnvironmentVariable("MPAI_STORE") is { Length: > 0 } s ? s : "https://localhost:5020/").TrimEnd('/') + "/";

    private static readonly string AppFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MPAI", "StoreApp");
    private static readonly string IdFile = Path.Combine(AppFolder, "implementer-id.txt");
    private ImplementerKey? key;                     // the Implementer's key, once the Store has approved the Implementer

    private readonly HttpClient http = new() { BaseAddress = new Uri(StoreUrl), Timeout = TimeSpan.FromSeconds(60) };

    private readonly ListBox publishedList = new() { Dock = DockStyle.Fill };
    private readonly TextBox statusBox = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new System.Drawing.Font("Consolas", 9)
    };

    public StoreForm()
    {
        Text = "MPAI Store";
        Width = 900;
        Height = 560;
        StartPosition = FormStartPosition.CenterScreen;

        var submitButton = new Button { Text = "Submit an L3...", Dock = DockStyle.Top, Height = 36 };
        submitButton.Click += async (_, _) => await SubmitFileAsync();

        var registerButton = new Button { Text = "Register as an Implementer...", Dock = DockStyle.Top, Height = 28 };
        registerButton.Click += (_, _) => OpenRegistration();

        var refreshButton = new Button { Text = "Refresh", Dock = DockStyle.Top, Height = 28 };
        refreshButton.Click += async (_, _) => await RefreshListAsync();

        var leftPanel = new Panel { Dock = DockStyle.Left, Width = 320 };
        leftPanel.Controls.Add(publishedList);
        leftPanel.Controls.Add(new Label { Text = $"Published in the Store at {StoreUrl}", Dock = DockStyle.Top, Height = 20 });
        leftPanel.Controls.Add(refreshButton);
        leftPanel.Controls.Add(registerButton);
        leftPanel.Controls.Add(submitButton);

        Controls.Add(statusBox);
        Controls.Add(leftPanel);

        Shown += async (_, _) => await RefreshListAsync();
    }

    private async Task RefreshListAsync()
    {
        publishedList.Items.Clear();
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync("MPAI/Store/L3"));
            foreach (var e in doc.RootElement.EnumerateArray())
                publishedList.Items.Add($"{e.GetProperty("id").GetString()}  v{e.GetProperty("version").GetInt32()}" +
                                        $"   ({e.GetProperty("errors").GetInt32()} err, {e.GetProperty("warnings").GetInt32()} warn)");
        }
        catch (Exception failure)
        {
            Log($"Could not reach the Store at {StoreUrl}: {failure.Message}");
        }
    }

    // The one entry point an Implementer uses: pick an L3, submit it, read the verdict.
    private async Task SubmitFileAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Submit an L3 (AIM Metadata instance)",
            Filter = "AIM Metadata (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        string json;
        try { json = File.ReadAllText(dialog.FileName); }
        catch (Exception failure) { Log($"Could not read {dialog.FileName}: {failure.Message}"); return; }

        Log($"--- {Path.GetFileName(dialog.FileName)} ---");
        if (!await ImplementerIsApprovedAsync(json)) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "MPAI/Store/L3") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.Add("MPAI-Signature", key!.Sign(JsonNode.Parse(json)!.AsObject()));       // the Store checks it with the credential it issued
            using var answer = await http.SendAsync(request);
            using var doc = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
            var r = doc.RootElement;

            if (r.TryGetProperty("findings", out var findings))
                foreach (var f in findings.EnumerateArray())
                    Log($"    {f.GetProperty("severity").GetString(),-7} [{f.GetProperty("source").GetString()}] {f.GetProperty("text").GetString()}");

            if (r.TryGetProperty("published", out var p) && p.GetBoolean())
            {
                Log($"    PUBLISHED {r.GetProperty("id").GetString()} as version {r.GetProperty("version").GetInt32()}");
                await RefreshListAsync();
            }
            else
            {
                var why = r.TryGetProperty("refused", out var reason) ? reason.GetString() : $"The Store answered {(int)answer.StatusCode}.";
                Log($"    REFUSED   {why}");
                MessageBox.Show(this, why, "Refused", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception failure)
        {
            Log($"    Could not submit to the Store at {StoreUrl}: {failure.Message}");
        }
    }

    // The Implementer ID: the one kept, or asked for; the Service says whether it has been assigned, and the L3 must carry it.
    private async Task<bool> ImplementerIsApprovedAsync(string l3Json)
    {
        string? inL3 = null;
        try { inL3 = JsonDocument.Parse(l3Json).RootElement.GetProperty("Identifier").GetProperty("ImplementerID").GetString(); } catch { }

        var id = File.Exists(IdFile) ? File.ReadAllText(IdFile).Trim() : "";
        string status = "";
        for (;;)
        {
            if (id.Length == 0)
            {
                id = Ask("Your Implementer ID (the Store gave it when you registered):").Trim();
                if (id.Length == 0)
                {
                    Log("    NOT SUBMITTED: an Implementer ID is needed. Register first, then submit.");
                    if (MessageBox.Show(this, "You need an Implementer ID. Open the registration page now?", "Implementer ID", MessageBoxButtons.YesNo) == DialogResult.Yes)
                        OpenRegistration();
                    return false;
                }
            }
            try
            {
                using var answer = await http.GetAsync("MPAI/Store/Implementer/" + Uri.EscapeDataString(id));
                if (answer.IsSuccessStatusCode) { status = (string?)JsonNode.Parse(await answer.Content.ReadAsStringAsync())?["status"] ?? ""; break; }
                Log($"    The Store has not assigned Implementer ID {id}.");
                if (MessageBox.Show(this, $"The Store has not assigned Implementer ID {id}. Open the registration page?", "Implementer ID", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    OpenRegistration();
                return false;
            }
            catch (Exception failure) { Log($"    Could not reach the Store at {StoreUrl}: {failure.Message}"); return false; }
        }
        Directory.CreateDirectory(AppFolder);
        File.WriteAllText(IdFile, id);
        if (inL3 != id)
        {
            Log($"    NOT SUBMITTED: the L3 names Implementer ID '{inL3}'; yours is {id}. The Identifier of the L3 must carry your ID.");
            return false;
        }

        // The key: made here, the first time; its identity is given to the Store with the registration code; MPAI then approves.
        key = ImplementerKey.LoadOrCreate(Path.Combine(AppFolder, $"implementer-{id}.key"));
        if (status == "Registered")
        {
            var code = Ask("The registration code the Store gave you (shown once, at registration):").Trim();
            if (code.Length == 0) { Log("    NOT SUBMITTED: the registration code is needed to give the Store your key."); return false; }
            try
            {
                using var content = new StringContent(new JsonObject { ["code"] = code, ["cii"] = key.Cii(id, DateTimeOffset.UtcNow) }.ToJsonString(), Encoding.UTF8, "application/json");
                using var bound = await http.PostAsync($"MPAI/Store/Implementer/{Uri.EscapeDataString(id)}/key", content);
                if (!bound.IsSuccessStatusCode) { Log("    The Store refused the key: " + await bound.Content.ReadAsStringAsync()); return false; }
                Log("    Your key is with the Store. MPAI must now approve your registration.");
                status = "KeyBound";
            }
            catch (Exception failure) { Log($"    Could not reach the Store at {StoreUrl}: {failure.Message}"); return false; }
        }
        if (status != "Approved")
        {
            Log($"    NOT SUBMITTED: MPAI has not yet approved Implementer {id} ({status}). Try again when it has.");
            return false;
        }
        return true;
    }

    private void OpenRegistration() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(new Uri(new Uri(StoreUrl), "MPAI/Store/Register").ToString()) { UseShellExecute = true });

    private string Ask(string prompt)
    {
        using var form = new Form { Text = "MPAI Store", Width = 440, Height = 150, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        var box = new TextBox { Left = 12, Top = 36, Width = 400 };
        var ok = new Button { Text = "OK", Left = 336, Top = 70, Width = 76, DialogResult = DialogResult.OK };
        form.Controls.AddRange(new Control[] { new Label { Text = prompt, Left = 12, Top = 12, Width = 400 }, box, ok });
        form.AcceptButton = ok;
        return form.ShowDialog(this) == DialogResult.OK ? box.Text : "";
    }

    private void Log(string line) => statusBox.AppendText(line + Environment.NewLine);
}
