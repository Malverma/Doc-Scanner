# Receipt Scanner: Build Plan

Companion to [`spec.md`](spec.md). The spec remains the source of truth for requirements. This file records the decisions made on 2026-11-02 and where they differ from it.

## Decisions from planning

| Topic | Decision |
|---|---|
| Test environment | No Windows PC is available yet. Everything except real scanning is built and tested on Arch against a simulated scanner |
| Folder UI | Clickable tiles with file counts. Click selects where scans go, double-click opens subfolders |
| After a scan | Clean scans save automatically, and the preview offers rotate, move, rescan and delete. Suspect scans stop for review |

## Where this differs from the spec, and why

| Spec | Plan | Why |
|---|---|---|
| WPF | **Avalonia** | WPF can't run on this PC, so its UI could never be tested here. Avalonia runs on both, and on Windows 11 it uses a native-looking Fluent theme |
| `NAPS2.Sdk` | **`NAPS2.NTwain` directly** | From the SDK source: `ConfigureSource` is private with no hook for custom capabilities, the `ImageLayout.Set` result is ignored so a clamped frame goes unnoticed, and images are held in memory as RGB24, with buffers doubling as they grow. Options A and B and requirement F4 all need lower-level access |
| Spec 5.1: frame may be limited to the physical size | Not a limit in the SDK | Lines 387-407 only read physical **width**, for alignment. Height isn't clamped there, though the driver may still clamp it |
| F8: preview before saving | Auto-save, then review | Chosen for large batches. F6 still applies: failed scans never reach the folder, and suspect scans wait for review |
| F7: PDF and PNG | Format setting: PDF (default), PNG, or both | Avoids two files per receipt unless wanted |

License effect: dropping `NAPS2.Sdk` removes the LGPL dependency. NTwain and Avalonia are both MIT.

## Architecture

```text
ReceiptScanner.App (x64, Avalonia UI)
   | starts one worker per operation, reads JSON events from stdout
   v
ReceiptScanner.TwainWorker (x86, because TWAIN sources are usually 32-bit)
   | streams rows to a temp file, so a 32-bit process never holds the whole image
   v
Brother TWAIN driver -> DS-640
```

- **One worker per scan.** Each scan opens a fresh TWAIN session, which gives reconnection after unplugging or idle drop-off (F9) with no extra state. A hung driver call can be ended by killing the process.
- **`ReceiptScanner.Core`** holds everything testable without hardware: event protocol, image analysis, PNG and PDF writers, folder operations, settings and logging.
- **Simulated scanner.** Enabled only by `--simulate`, or automatically on non-Windows systems. It reproduces clean scans, jams, no paper, disconnects and over-length paper. The UI shows a permanent banner while it's active, so a simulated scan can't be mistaken for a real one.

## Long Paper strategy

Which option works can only be learned on real hardware, so the app supports all three and picks at run time. Each result is logged.

1. **A, capabilities:** set a 72 in frame, undefined image size and length detection, with deskew and border detection off. Then read the values back.
2. **B, saved driver state:** the user selects Long Paper once in Brother's dialog, and the app saves and restores `DAT_CUSTOMDSDATA`, tied to the driver version.
3. **C, Brother's dialog:** shown for each scan as the fallback.

If none can be confirmed, Scan warns and requires an explicit override (F4). Option D (direct USB) stays out of scope.

A readback only proves the driver accepted the values. It doesn't prove the scanner will feed a long receipt, so **this stays unproven until acceptance tests A4 to A7 pass on Windows.**

## "Scan the whole page" safeguards

- No time limit ends a scan. After 5 minutes without data, the UI offers Stop Waiting, which warns that a replug may be needed.
- Rows go to a temp file, and output files appear only through an atomic move after a clean end of transfer.
- A scan is flagged as suspect if its height is within 2 mm of the 1828.8 mm limit, or if printed content runs into the bottom edge.
- Blank tail and side trimming are conservative and can be turned off.

## UI

```text
+---------------------------------------------------+-------------------+
| Receipts > 2026                        [+ Folder] |  Latest scan      |
|  +---------+ +---------+ +---------+              |  +-------------+  |
|  | Grocery | | Gas     | | Meals   |              |  |   preview   |  |
|  | 42    * | | 18      | | 27      |              |  +-------------+  |
|  +---------+ +---------+ +---------+              |  Rotate  Move     |
|                                                   |  Rescan  Delete   |
|  [            SCAN to Grocery  (Space)          ] |                   |
|  DPI [300]  Color [Gray]  Format [PDF]  (gear)    |                   |
+---------------------------------------------------+-------------------+
```

The gear menu holds the base folder, trimming, Long Paper setup, the driver check and the log folder. DPI choices are 200, 300 and 600, and 1200 is never offered.

## Build order

1. Core: protocol, image analysis, writers, folders, settings. Unit tests.
2. Worker: TWAIN scan, Long Paper strategies, capability discovery (spec Phase 1).
3. App: tile UI, scan flow, review actions. Tested headless against the simulator, with screenshots.
4. Publish script for a Windows build, plus `docs/windows-test-guide.md` covering spec Phase 0, discovery and acceptance tests.

## What's been verified

| Area | How | Result |
|---|---|---|
| Image analysis, writers, folders, settings, view model | `dotnet test`, with injected bugs confirming the tests catch failures | Passing |
| PDF and PNG output | Checked with `qpdf`, `pdfinfo` and an independent PNG checker | Valid |
| Worst-case scan (621 MB raw, 1800 mm at 600 dpi in color) | Synthetic image | 2.2 MB allocated while encoding |
| UI | Screenshots of each state, light and dark, against the simulator | Reviewed |
| TWAIN worker | Published x86 build under Wine against a fake driver (`tests/wine-twain`): pixels, jams, disconnects, cancels, Long Paper methods, driver check | Passing |
| Full app chain | Published x64 app, x86 worker and fake driver under Wine | Reports the scanner as ready |
| Recycle Bin call | Published x64 build under Wine | Works, and refuses a missing file |
| Installer | Silent install and uninstall under Wine, with the fake-driver checks run on the installed worker | Passing. Not yet run on real Windows |

## What can't be verified until Windows is available

The fake driver follows the TWAIN protocol, but it isn't Brother's driver, and Wine isn't Windows. See `docs/windows-test-guide.md`.

- Whether option A, B or C enables Long Paper (spec Q1 to Q5). **This is the app's central requirement, and it's still unproven.**
- Whether Brother ships a 32-bit TWAIN source. The worker is x86 and uses Microsoft's `twain_32.dll` (spec Q2).
- Real driver status codes, timing, its own error dialogs, and every acceptance test that uses paper.
- Trimming thresholds on real scans. They've only seen synthetic images.
- Avalonia's look and behavior on real Windows 11.
