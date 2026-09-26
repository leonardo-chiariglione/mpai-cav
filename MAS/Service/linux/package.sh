#!/bin/bash
# PACKAGE MPAI-MAS FOR A LINUX SERVER. Run from the repository root, on the build
# machine (Git Bash on Windows, or Linux), with the .NET 10 SDK and the Models folder:
#
#   MAS/Service/linux/package.sh <folder> [install-root]
#
# <folder> receives everything the server needs, laid out as it will be installed
# under install-root (default /opt/mpai): the MAS Service and the browser client's
# host, published for linux-x64; the four Apps, their L3s and the schemas; the avatar
# and the client's workflow; the model files the Apps use - and nothing else from
# Models; the configuration, the settings, the systemd units, setup.sh and README.md.
# Copy <folder> to install-root on the server, then run setup.sh there.
set -e
out="${1:?the folder to package into}"
root="${2:-/opt/mpai}"
here="MAS/Service/linux"
[ -f "$here/package.sh" ] || { echo "Run from the repository root."; exit 1; }
mkdir -p "$out"

echo "== the MAS Service and the browser client's host, for linux-x64"
dotnet publish MAS/Service/src/MasService.csproj -c Release -r linux-x64 --self-contained false -o "$out/service" -v quiet -nologo
dotnet publish UserAgent/Clients/Browser/Host/RcaWeb.Host.csproj -c Release -r linux-x64 --self-contained false -o "$out/client" -v quiet -nologo

echo "== the Apps, their L3s, the schemas; the avatar and the client's workflow"
mkdir -p "$out/Apps" "$out/UserAgent"
for app in MAD AMQ MAT MPD; do cp -r "Apps/$app" "$out/Apps/"; done
rm -rf "$out/AMDs" "$out/schemas"
cp -r AIMs/AMDs "$out/AMDs"
cp -r schemas "$out/schemas"
cp -r UserAgent/Assets UserAgent/Orchestration "$out/UserAgent/"

echo "== the models the Apps use"
grep -o '"/opt/mpai/Models/[^"]*"' "$here/aim-settings.json" | tr -d '"' | sed 's#^/opt/mpai/##' | sort -u | while read -r model; do
    mkdir -p "$out/$(dirname "$model")"
    [ -f "$out/$model" ] && [ "$(stat -c %s "$out/$model")" = "$(stat -c %s "$model")" ] || cp "$model" "$out/$model"
done
du -sh "$out/Models"

echo "== the configuration, for $root"
for f in mas-server.json aim-settings.json; do sed "s#/opt/mpai#$root#g" "$here/$f" > "$out/$f"; done
mkdir -p "$out/systemd"
for f in "$here"/systemd/*.service; do sed "s#/opt/mpai#$root#g" "$f" > "$out/systemd/$(basename "$f")"; done
cp "$here/setup.sh" "$here/README.md" "$out/"
chmod +x "$out/setup.sh" "$out/service/MasService" "$out/client/RcaWeb.Host" 2> /dev/null || true
echo "Packaged in $out, for $root. Copy it there on the server, and run setup.sh."
