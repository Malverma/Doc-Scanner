#!/bin/sh
# Builds dist/ReceiptScanner-Setup-<version>.exe with Inno Setup 7.
# On Windows (Git Bash), ISCC.exe must be on PATH.
# On Linux, set INNO_PREFIX to a Wine prefix with Inno Setup installed in C:\Inno.
set -eu
root="$(cd "$(dirname "$0")/.." && pwd)"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props")"

"$root/scripts/publish.sh"

if [ -n "${INNO_PREFIX:-}" ]; then
    export WINEPREFIX="$INNO_PREFIX" WINEDEBUG=-all WINEDLLOVERRIDES="mshtml=;winemenubuilder.exe=d"
    ln -sfn "$root" "$INNO_PREFIX/dosdevices/p:"
    trap 'rm -f "$INNO_PREFIX/dosdevices/p:"' EXIT
    wine 'C:\Inno\ISCC.exe' /Q "/DAppVersion=$version" 'P:\installer\ReceiptScanner.iss'
else
    ISCC.exe /Q "/DAppVersion=$version" "$root/installer/ReceiptScanner.iss"
fi
echo "Built $root/dist/ReceiptScanner-Setup-$version.exe"
