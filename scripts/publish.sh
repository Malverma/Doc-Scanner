#!/bin/sh
# Builds the Windows 11 app into dist/ReceiptScanner and zips it. Works from Linux or Git Bash.
# The app is x64. The worker is x86 because TWAIN drivers are usually 32-bit.
set -eu
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/dist/ReceiptScanner"
rm -rf "$root/dist"

dotnet publish "$root/src/ReceiptScanner.App" -c Release -r win-x64 --self-contained true -o "$out"
dotnet publish "$root/src/ReceiptScanner.TwainWorker" -c Release -r win-x86 --self-contained true -o "$out/worker"

(cd "$root/dist" && python3 -m zipfile -c ReceiptScanner-win-x64.zip ReceiptScanner)
echo "Built $root/dist/ReceiptScanner-win-x64.zip"
