#!/usr/bin/env bash
# The first slice of AIH-HSP V1.1, narrated: two End Users with their Health Front Ends, the Health Back End (the Trust Authority) with a test Blockchain, and a Third-Party User.
# Each Front End and the Back End is a Controller with its Module, and the Module's AIMs run on an AIM host - a process of its own. It builds first.
#   ./run-aih-demo.sh            Debug
#   ./run-aih-demo.sh Release
# AIH_DEMO_TRACE=1 ./run-aih-demo.sh   shows the stack of a failure.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
configuration="${1:-Debug}"
"$here/build-aih.sh" "$configuration"
exec dotnet "$here/Demo/bin/$configuration/net10.0/AihDemo.dll"
