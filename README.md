# Receipt Scanner

A Windows 11 app for the Brother DS-640 and Epson RapidReceipt RR-70W that scans long receipts in one pass and sorts them into folders.

**Status:** on Linux, a real RR-70W has scanned a 536 mm receipt in one pass through the app's scanning and trimming code. **It has not yet run on real Windows, or with a real DS-640**, so Long Paper is unproven there. Start with [`docs/windows-test-guide.md`](docs/windows-test-guide.md).

## Install

1. Open the [Releases page](https://github.com/Malverma/Doc-Scanner/releases) and download `ReceiptScanner-Setup-<version>.exe`.
2. Double-click it and follow the prompts. It doesn't need administrator rights, and it adds a Start menu entry and an uninstall entry in Windows Settings.

The installer isn't code-signed yet, so Windows SmartScreen may warn about it. If it does, click **More info**, then **Run anyway**.

Uninstalling leaves your scanned receipts and `%LOCALAPPDATA%\ReceiptScanner` in place.

## Using it

1. Click a folder tile to choose where scans go. Double-click a tile to open its subfolders.
2. Insert one receipt and click **Scan**, or press Space.
3. Clean scans save automatically as real files. The panel on the right shows the full path, with **Show in File Explorer**, and offers Rotate, Move, Rescan and Delete.
4. The receipts in the selected folder are listed under the folder tiles. Drag one out to File Explorer or an email to copy it, or onto a folder tile or the path at the top to move it. Double-click to open it, or right-click for more.

A scan runs until the driver reports the whole page is done. There's no time limit, and **Stop waiting** only appears after 5 silent minutes. **Settings > Receipt size** can set a width to scan and an expected length. Neither ends a scan early, and a scan shorter than the expected length is held for review.

Scans that might be cut off wait for you to choose **Save anyway** or **Discard**. That includes scans that end with paper still in the scanner. Saved ones get `_UNVERIFIED` in the file name. Failed scans are never saved.

Files are named like `receipt_2026-11-02_14-30-05.pdf`. The default folder is `Receipts` in your Documents folder. If OneDrive backs up Documents, that's `OneDrive\Documents\Receipts`. App data and logs are in `%LOCALAPPDATA%\ReceiptScanner`.

## Requirements

- Windows 11 x64
- One supported scanner and its TWAIN driver:
  - Brother DS-640: Brother's full DS-640 driver package
  - Epson RapidReceipt RR-70W: Epson Scan 2 and the RR-70W driver from Epson's support site. Over Wi-Fi, pick the scanner in Epson Scan 2 Utility first

With both installed, **Settings > Scanner** picks one. **Automatic** uses the first supported scanner found.

### Linux

On Linux the app scans through SANE's `scanimage` instead of TWAIN. Only the Epson RR-70W is set up for this. Run `scripts/setup-epson-linux.sh` once. It installs Epson Scan 2 from the AUR (`epsonscan2-bin`) and adds the two links Arch needs, so the driver can find its modules and its image decoder. Long Paper is turned on with the driver's own `--long-paper-mode` option, so no setup step is needed.

The RR-70W scans on a black background. The app detects that and trims to the paper's edges. The **Width** setting isn't applied on Linux.


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

To try the UI without a scanner, run `dotnet run --project src/ReceiptScanner.App -- --simulate`, or add `--simulate=jam`, `nopaper`, `disconnect`, `overlength`, `unconfirmed`, `stall` or `paperleft`.

`dotnet test` writes UI screenshots to a folder if `RS_SCREENSHOTS` is set, and runs a multi-gigabyte worst-case test if `RS_LARGE` is set.

## Layout

| Path | Purpose |
|---|---|
| `src/ReceiptScanner.App` | x64 Avalonia UI |
| `src/ReceiptScanner.TwainWorker` | x86 process that talks to the scanner's TWAIN driver, one run per scan |
| `src/ReceiptScanner.Core` | Image analysis, PNG and PDF writers, folders, settings, simulator |
| `tests/ReceiptScanner.Tests` | Automated tests |
| `tests/wine-twain` | Fake TWAIN driver and test script |
| `installer` | Inno Setup script for the Windows installer |

See [`spec.md`](spec.md) for requirements and [`PLAN.md`](PLAN.md) for design decisions.
