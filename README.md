# Receipt Scanner

A Windows 11 app for the Brother DS-640 that scans long receipts in one pass and sorts them into folders.

**Status:** built and tested against simulated and fake scanners. **It has not yet run on real Windows or with a real DS-640**, so Long Paper is unproven. Start with [`docs/windows-test-guide.md`](docs/windows-test-guide.md).

## Install

1. Open the [Releases page](https://github.com/Malverma/Doc-Scanner/releases) and download `ReceiptScanner-Setup-<version>.exe`.
2. Double-click it and follow the prompts. It doesn't need administrator rights, and it adds a Start menu entry and an uninstall entry in Windows Settings.

The installer isn't code-signed yet, so Windows SmartScreen may warn about it. If it does, click **More info**, then **Run anyway**.

Uninstalling leaves your scanned receipts and `%LOCALAPPDATA%\ReceiptScanner` in place.

## Using it

1. Click a folder tile to choose where scans go. Double-click a tile to open its subfolders.
2. Insert one receipt and click **Scan**, or press Space.
3. Clean scans save automatically. Rotate, Move, Rescan or Delete the latest one from the panel on the right.

Scans that might be cut off wait for you to choose **Save anyway** or **Discard**. Saved ones get `_UNVERIFIED` in the file name. Failed scans are never saved.

Files are named like `receipt_2026-11-02_14-30-05.pdf`. App data and logs are in `%LOCALAPPDATA%\ReceiptScanner`.

## Requirements

- Windows 11 x64
- Brother's full DS-640 driver package, which includes the TWAIN driver

## Building

Needs the .NET 10 SDK.

```sh
./scripts/publish.sh           # app only: dist/ReceiptScanner-win-x64.zip
./scripts/build-installer.sh   # installer: dist/ReceiptScanner-Setup-<version>.exe, also needs Inno Setup 7
```

The script's header explains how it finds Inno Setup. The version number is set in `Directory.Build.props`.

## Testing

```sh
dotnet test                 # logic, process handling and UI, using the simulator
tests/wine-twain/run.sh     # the real TWAIN worker against a fake driver under Wine (after publishing)
```

To try the UI without a scanner on Linux, run `dotnet run --project src/ReceiptScanner.App`. On Windows, add `-- --simulate`, or `--simulate=jam`, `nopaper`, `disconnect`, `overlength`, `unconfirmed` or `stall`.

`dotnet test` writes UI screenshots to a folder if `RS_SCREENSHOTS` is set, and runs a multi-gigabyte worst-case test if `RS_LARGE` is set.

## Layout

| Path | Purpose |
|---|---|
| `src/ReceiptScanner.App` | x64 Avalonia UI |
| `src/ReceiptScanner.TwainWorker` | x86 process that talks to Brother's TWAIN driver, one run per scan |
| `src/ReceiptScanner.Core` | Image analysis, PNG and PDF writers, folders, settings, simulator |
| `tests/ReceiptScanner.Tests` | Automated tests |
| `tests/wine-twain` | Fake TWAIN driver and test script |
| `installer` | Inno Setup script for the Windows installer |

See [`spec.md`](spec.md) for requirements and [`PLAN.md`](PLAN.md) for design decisions.
