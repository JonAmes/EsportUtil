#!/usr/bin/env bash
# Build the production Windows EXE from the Linux dev box.
# Cross-compile is supported because EsportsUtil.csproj sets EnableWindowsTargeting=true.
#
#   ./deploy/publish-win.sh            -> dist/win-x64/esportsutil.exe (R2R, no trim)
#   ./deploy/publish-win.sh --trim     -> additionally IL-trimmed (opt-in; see BUILD.md caveats)
#
# Output is a single self-contained EXE — no .NET install needed on game PCs.
set -eu
cd "$(dirname "$0")/.."

TRIM=false
if [ "${1:-}" = "--trim" ]; then TRIM=true; fi

OUT=dist/win-x64
rm -rf "$OUT"

# WinForms (UseWindowsForms in the Windows TFM) imports Microsoft.NET.Sdk.WindowsDesktop,
# which Debian-packaged /usr/lib/dotnet SDKs do NOT ship. Official dotnet-install SDKs (~/.dotnet)
# do. Prefer one that has it; fail loudly otherwise.
DOTNET=dotnet
if ls "$HOME"/.dotnet/sdk/*/Sdks/Microsoft.NET.Sdk.WindowsDesktop >/dev/null 2>&1; then
  DOTNET="$HOME/.dotnet/dotnet"
fi

"$DOTNET" publish src/EsportsUtil \
  -c Release \
  -f net8.0-windows10.0.19041.0 \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeAllContentForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:ReadyToRun=true \
  -p:PublishTrimmed="$TRIM" \
  -p:TrimMode=partial \
  -p:DebugType=none -p:DebugSymbols=false \
  -p:InvariantGlobalization=true \
  -p:Version="${VER:-0.5.0}" \
  -o "$OUT"

echo
ls -lh "$OUT"/esportsutil.exe
file "$OUT"/esportsutil.exe
