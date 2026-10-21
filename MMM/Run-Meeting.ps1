# A MEETING IN THE MMM WORLD: the guests install nothing, each opens a personal link.
#   MMM\Run-Meeting.ps1 -Chair Leonardo -Guests Anna,Bruno,Carla -Until 19:00
#   MMM\Run-Meeting.ps1 -Chair Leonardo -Guests Anna,Bruno,Carla -Via cloudflare
# It starts the M-Instance with its world (the Metaverse Square, the MPAI building: a public atrium and the wings of rooms) and puts it on the internet at an https address (the microphone needs
# https): -Via funnel (Tailscale Funnel, a stable address on this PC's Tailscale name; needs Tailscale logged in, Serve and Funnel enabled in its console) or -Via cloudflare (the default: a Cloudflare
# quick tunnel: no account, a new address each time; cloudflared.exe).
# THE CHAIR INVITES: each guest gets a personal link (?invite=...) that this script prints. The server turns it into the guest's name; the guests listed are the ones given the Rights to the wing of the
# meeting rooms and to the MMM meeting room, for the meeting - the rights end with it. Without the link nobody is answered; with -Visitors anybody may come into the atrium (public), and no further.
# You, on this PC: the address http://127.0.0.1:7099 - you are the chair. Close the server window when the meeting is over (the address goes with it).
# A GROUP'S MEETING: -Group MMM -When "Thursday 22 October, 17:00 CEST" takes the people of the group from MMM\Registry\people.json (see Registry\README.md): the chair is the one who chairs the group, the others
# are the guests, each is sent an email with the personal link, composed through Outlook. -Mail ask (default): the recipients are shown and you are asked to confirm before anything is sent;
# draft: the emails are saved as Outlook drafts, nothing is sent; none: no emails.
# -Officers A,B: the people, besides the chair, who may register with the MPAI Officer Account Level (the Rights to every place of the building).
param([string]$Chair = '', [string]$Guests = '', [string]$Until = '', [double]$Hours = 3, [int]$Port = 7099, [ValidateSet('funnel', 'cloudflare')][string]$Via = 'cloudflare', [switch]$Visitors,
      [string]$Group = '', [string]$When = '', [string]$Officers = '', [string]$HostName = '', [ValidateSet('ask', 'draft', 'none')][string]$Mail = 'ask', [switch]$Preview)

$here = $PSScriptRoot
$local = "http://127.0.0.1:$Port"
$people = @{}      # the name in the M-Instance -> the person (Email, Name), for the emails
if ($Group) {
    $groups = @(Get-Content "$here\Registry\groups.json" -Raw -Encoding UTF8 | ConvertFrom-Json | ForEach-Object { $_ })
    $grp = $groups | Where-Object { $_.Id -eq $Group } | Select-Object -First 1
    if (-not $grp) { Write-Host "The group $Group is not in Registry\groups.json."; exit 1 }
    if ($grp.Room -ne 'MeetingRoom') { Write-Host "The meeting room of $Group ($($grp.Room)) has no meeting set up yet: only the MMM room has seats and rights. Stopping."; exit 1 }
    $all = @(Get-Content "$here\Registry\people.json" -Raw -Encoding UTF8 | ConvertFrom-Json | ForEach-Object { $_ } | Where-Object { $_.Groups -contains $Group })
    if (-not $all) { Write-Host "Nobody is in the group $Group in Registry\people.json."; exit 1 }
    # The name in the M-Instance: letters only, the first name by default, made unique.
    $used = @{}
    foreach ($p in $all) {
        $id = if ($p.Id) { $p.Id } else { (([string]($p.Name -split '\s+')[0]).Normalize([Text.NormalizationForm]::FormD)) -replace '\p{Mn}', '' -replace '[^A-Za-z]', '' }
        if (-not $id) { $id = 'Guest' }
        $base = $id; $k = 2; while ($used.ContainsKey($id)) { $id = "$base$k"; $k++ }
        $used[$id] = $true; $people[$id] = $p
    }
    # THE HOST is whoever runs the meeting on this PC: the one with Role President in the list (the Officer of every place). THE CHAIR is who convenes this group's meeting: the one marked (C) in
    # the list - a person with a link of their own, if it is not the host - or, if nobody is, the host.
    if (-not $HostName) { $HostName = @($people.Keys | Where-Object { $people[$_].Role -eq 'President' })[0] }
    if (-not $HostName) { $HostName = Read-Host 'Who is the host, on this PC (the President)? The name as in the list' }
    $chairs = @($people.Keys | Where-Object { $people[$_].Chair -contains $Group -and $people[$_].Role -ne 'President' })
    if (-not $Chair) { $Chair = if ($chairs) { $chairs[0] } else { $HostName } }
    if (-not $Guests) { $Guests = ($people.Keys | Where-Object { $_ -ne $HostName } | Sort-Object) -join ',' }
}
if (-not $HostName) { $HostName = $Chair }
if (-not $Chair) { $Chair = Read-Host 'Your name in the meeting (the chair)' }
if (-not $Guests) { $Guests = Read-Host 'The guests, their names separated by commas (for example Anna,Bruno,Carla)' }
$names = @($Guests -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -ne $HostName })
if ($names.Count -lt 1) { Write-Host 'No guests.'; exit 1 }

# A personal token for each guest: 18 hexadecimal characters, from the system's random numbers.
$tokens = @{}
foreach ($n in $names) { $b = New-Object byte[] 9; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); $tokens[$n] = ([BitConverter]::ToString($b) -replace '-', '').ToLower() }
$invites = ($names | ForEach-Object { "$_=$($tokens[$_])" }) -join ','

function Get-Mails($base) {
    $subject = "MPAI $($grp.Id) meeting" + $(if ($When) { " - $When" } else { '' })
    @(foreach ($n in $names) {
        $p = $people[$n]; if (-not $p -or -not $p.Email) { continue }
        $link = "$base/viewer/?world=1&mic=1&invite=$($tokens[$n])"
        $body = @"
Dear $(($p.Name -split '\s+')[0]),

you are invited to the $($grp.Name) meeting$(if ($When) { " on $When" }), in the MMM virtual world: the MPAI building, the meeting room of the group.

$(if ($n -eq $Chair) { 'You chair the meeting: you can share your screen, and end the meeting for everybody.' + [Environment]::NewLine + [Environment]::NewLine })Your personal link (it is yours: please do not pass it on):

$link

What you need: a PC with Chrome or Edge, and a headset (no loudspeakers: the room has its own echo). Open the link. The first time you register: keep the Account Level *Participant*, give your first name, last name and email, and choose your avatar. Allow the microphone when the browser asks.
Walk with W and S, turn with A and D; go through the doors of the MPAI building to the meeting rooms, and into the MMM meeting room: you sit down by yourself. Q stands up, E sits again.
The link works while the meeting is on; the address changes for each meeting.

$($HostName)
"@
        [pscustomobject]@{ To = $p.Email; Name = $n; Subject = $subject; Body = $body }
    })
}

if ($Preview) {
    Write-Host "Group $Group - chair: $Chair; guests: $($names -join ', ')"
    foreach ($m in (Get-Mails 'https://(the address of the meeting)')) { Write-Host ('-' * 70); Write-Host "To: $($m.To)   Subject: $($m.Subject)"; Write-Host $m.Body }
    exit 0
}

if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) { Write-Host "Port $Port is in use: a server is already running. Close its window first."; exit 1 }

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }
$when = if ($Until) { "--until $Until" } else { "--hours $Hours" }
$arguments = "run --project `"$here\Server\Mpai.Mmm.Server.csproj`" -c Release -- $local --world --identity --chair $Chair --host $HostName --invites $invites --invite $((@($Chair) + @($HostName) + $names | Select-Object -Unique) -join ',') $when" + $(if ($Visitors) { ' --visitors' } else { '' }) + $(if ($Officers) { " --officers $Officers" } else { '' })
Start-Process -FilePath $dotnet -ArgumentList $arguments -WindowStyle Normal
Write-Host 'The server starts (the first time it compiles, about a minute)...'
$up = $false
for ($i = 0; $i -lt 180 -and -not $up; $i++) {
    Start-Sleep 2
    try { Invoke-WebRequest "$local/viewer/" -UseBasicParsing -TimeoutSec 3 | Out-Null; $up = $true } catch { if ($_.Exception.Response) { $up = $true } }
}
if (-not $up) { Write-Host 'The server did not start. Look at its window.'; exit 1 }

$base = $null
if ($Via -eq 'funnel') {
    $ts = (Get-Command tailscale -ErrorAction SilentlyContinue).Source
    if (-not $ts) { $ts = @('C:\Program Files\Tailscale\tailscale.exe') | Where-Object { Test-Path $_ } | Select-Object -First 1 }
    if (-not $ts) { Write-Host 'Tailscale is not installed. Use -Via cloudflare.'; exit 1 }
    $out = & $ts funnel --bg $Port 2>&1 | Out-String
    $dns = $null
    try { $dns = ((& $ts status --json | ConvertFrom-Json).Self.DNSName).TrimEnd('.') } catch { }
    if (-not $dns -or $out -match 'not enabled') { Write-Host "Funnel is not enabled on the tailnet:`n$out`nEnable it (the address above), or use -Via cloudflare."; exit 1 }
    $base = "https://$dns"
}
else {
    $cf = @("$here\tools\cloudflared.exe", "$here\..\Apps\ABV\AVS\tools\cloudflared.exe", 'D:\DI\ABV\tools\cloudflared.exe') | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $cf) { $cf = (Get-Command cloudflared -ErrorAction SilentlyContinue).Source }
    if (-not $cf) { Write-Host 'cloudflared.exe is not found (MMM\tools\, Apps\ABV\AVS\tools\, D:\DI\ABV\tools\).'; exit 1 }
    $log = Join-Path $env:TEMP 'mmm-tunnel.log'
    Remove-Item $log -ErrorAction SilentlyContinue
    Start-Process -FilePath $cf -ArgumentList 'tunnel', '--no-autoupdate', '--url', $local -RedirectStandardError $log -WindowStyle Minimized
    for ($i = 0; $i -lt 40 -and -not $base; $i++) {
        Start-Sleep 1.5
        if (Test-Path $log) { $m = [regex]::Match((Get-Content $log -Raw -ErrorAction SilentlyContinue), 'https://[a-z0-9-]+\.trycloudflare\.com'); if ($m.Success) { $base = $m.Value } }
    }
    if (-not $base) { Write-Host 'The tunnel did not give an address. Look at the log:' $log; exit 1 }
}

Write-Host ''
Write-Host '  For each guest - send them THEIR OWN link (Chrome or Edge on a PC, a headset, allow the microphone):' -ForegroundColor Green
foreach ($n in $names) { Write-Host ("    {0,-12} {1}/viewer/?world=1&mic=1&invite={2}" -f $n, $base, $tokens[$n]) }
Write-Host "    the meeting ends: $(if ($Until) { 'at ' + $Until } else { 'in ' + $Hours + ' hours' }), or when you press the button (bottom left); the rights of everybody end with it"
Write-Host ''
Write-Host '  For you, on this PC:' -ForegroundColor Green
Write-Host "    $local/viewer/?world=1&mic=1"
Write-Host ''
Write-Host '  The first time, each chooses a Persona. Walk (W/S, A/D) to the MPAI doors, across the atrium to the south wing: the MMM meeting room is the first on the right. Q stands up, E sits.'
# THE EMAILS: one for each guest of the group, with the personal link, through Outlook (the account of this PC): shown first, sent only if you confirm - or saved as drafts.
if ($Group -and $Mail -ne 'none') {
    $mails = Get-Mails $base
    Write-Host ''
    Write-Host "  Emails to send ($($mails.Count)), from your Outlook:" -ForegroundColor Green
    foreach ($m in $mails) { Write-Host ("    {0,-10} {1}" -f $m.Name, $m.To) }
    $do = $Mail
    if ($Mail -eq 'ask') {
        $ans = Read-Host "Send these $($mails.Count) emails now? (y = send, d = save as drafts, anything else = no)"
        $do = if ($ans -match '^[yY]') { 'send' } elseif ($ans -match '^[dD]') { 'draft' } else { 'none' }
    }
    if ($do -ne 'none' -and $mails.Count -gt 0) {
        try {
            $ol = New-Object -ComObject Outlook.Application
            foreach ($m in $mails) {
                $item = $ol.CreateItem(0); $item.To = $m.To; $item.Subject = $m.Subject; $item.Body = $m.Body
                if ($do -eq 'send') { $item.Send() } else { $item.Save() }
            }
            Write-Host "  $($mails.Count) emails $(if ($do -eq 'send') { 'sent' } else { 'saved as drafts in Outlook' })."
        }
        catch { Write-Host "  Outlook could not make the emails: $($_.Exception.Message)`n  The links above are what each guest needs." }
    }
}
Start-Process "$local/viewer/?world=1&mic=1"
Read-Host 'Enter to take the meeting off the internet (the server has its own window)' | Out-Null
if ($Via -eq 'funnel') { & $ts funnel reset | Out-Null } else { Get-Process cloudflared -ErrorAction SilentlyContinue | Stop-Process -Force }
