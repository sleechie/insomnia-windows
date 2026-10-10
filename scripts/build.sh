#!/usr/bin/env bash
# Cross-compiles Insomnia.exe from Linux or macOS (needs the .NET 8 SDK).
# ./scripts/build.sh            -> dist/release/Insomnia.exe
# ./scripts/build.sh --lid-sim  -> dist/sim/Insomnia.exe (accepts --simulate-lid)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
if [[ "${1:-}" == "--lid-sim" ]]; then
  dotnet publish "$root/src/Insomnia/Insomnia.csproj" -c Release -p:LidSimulation=true -o "$root/dist/sim"
else
  dotnet publish "$root/src/Insomnia/Insomnia.csproj" -c Release -o "$root/dist/release"
fi
