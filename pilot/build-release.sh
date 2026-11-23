#!/usr/bin/env bash
# Builds the WinDictate Restricted Pilot submission package on macOS or Linux.
# Same steps as pilot/build-release.ps1 (see there); the Windows program is
# cross-compiled. The official package is built and tested on Windows by
# .github/workflows/pilot-release.yml.
#
#   pilot/build-release.sh --out <folder> [--model <file>] [--deny-file <file>] [--allow-dirty] [--skip-tests]
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
cd "$root"
exec dotnet run --project pilot/tools/ReleaseKit -c Release -- build "$@"
