# THALIA'S LOCAL APPS: the seven icons of Thalia's desktop that stand for the Apps on this PC start those Apps.
# This listens on this PC only (http://localhost:5016/) and starts, on POST /launch/{id}, the standalone launcher of that App
# in this folder - and nothing else. It does not touch the Service, the Store or the Thalia browser server.
$Host.UI.RawUI.WindowTitle = 'Thalia local apps'
$apps = @{
    'AMQ' = '..\MMC\AMQ.bat'
    'NPR' = '..\MMC\NPR.bat'
    'MTS' = '..\MMC\MTS.bat'
    'MCA' = '..\MMC\MCA.bat'
    'ACR' = '..\MMC\ACR.bat'
    'MAC' = '..\MMC\MAC.bat'
    'AMC' = '..\MMC\AMC.bat'
}
$port = 5016
if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) {
    Write-Host "Thalia local apps is already running on port $port." -ForegroundColor Yellow
    return
}
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://localhost:$port/")
$listener.Start()
Write-Host "THALIA LOCAL APPS - http://localhost:$port/ - close this window to stop it" -ForegroundColor Cyan
while ($listener.IsListening) {
    $ctx = $listener.GetContext()
    $req = $ctx.Request; $res = $ctx.Response
    # The page is a file on this PC (origin "null"): the answer is allowed to it.
    $res.Headers.Add('Access-Control-Allow-Origin', '*')
    $res.Headers.Add('Access-Control-Allow-Methods', 'POST, OPTIONS')
    $text = ''
    if ($req.HttpMethod -eq 'OPTIONS') { $res.StatusCode = 204 }
    elseif ($req.HttpMethod -eq 'POST' -and $req.Url.AbsolutePath -match '^/launch/(\w+)$') {
        $id = $Matches[1].ToUpperInvariant()
        $bat = if ($apps.ContainsKey($id)) { Join-Path $PSScriptRoot $apps[$id] } else { $null }
        if (-not $bat) { $res.StatusCode = 404; $text = "There is no local App '$id'." }
        elseif (-not (Test-Path $bat)) { $res.StatusCode = 404; $text = "The launcher of '$id' is not on this PC." }
        else { Start-Process cmd.exe -ArgumentList '/c', 'start', "`"$id`"", "`"$bat`"" -WorkingDirectory (Split-Path $bat); $res.StatusCode = 202; $text = "$id is starting."; Write-Host "$(Get-Date -Format HH:mm:ss) started $id" }
    }
    else { $res.StatusCode = 404 }
    $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    $res.ContentType = 'text/plain; charset=utf-8'
    $res.OutputStream.Write($bytes, 0, $bytes.Length)
    $res.Close()
}
