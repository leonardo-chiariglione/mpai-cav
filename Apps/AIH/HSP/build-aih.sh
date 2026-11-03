#!/usr/bin/env bash
# Builds the AIH-HSP demo - and with it the nine AIH AIMs, the Chain, the User Agents and what they rest on (the Controller, the Trust, the Store, the Controller API).
#   ./build-aih.sh            Debug
#   ./build-aih.sh Release
# Needs the .NET 10 SDK (https://dotnet.microsoft.com/download; or, without root: https://dot.net/v1/dotnet-install.sh --channel 10.0). Tried on Ubuntu 26.04, .NET 10.0.402, x86-64.
# If the system has no ICU library (apt install libicu-dev), give dotnet DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
configuration="${1:-Debug}"
if ! command -v dotnet >/dev/null 2>&1; then
    echo "The .NET 10 SDK is not installed (or not on the PATH)." >&2
    exit 1
fi
dotnet build "$here/Demo/AihDemo.csproj" -c "$configuration" -nologo -v q
echo "Built: $here/Demo/bin/$configuration/net10.0/AihDemo.dll"
