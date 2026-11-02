# Receipt Scanner for Windows 11: Specification

| | |
|---|---|
| **Target hardware** | Brother DSmobile DS-640 (USB `04f9:0468`) |
| **Target platform** | Windows 11 x64 |
| **Goal** | Scan one receipt of any length up to the scanner's 72 in (1828.8 mm) limit in one pass, without stopping early |
| **Written** | 2026-11-02, from diagnosis on an Arch Linux PC |
| **Implementation status** | Nothing built. No Windows behavior has been tested on this hardware |

Evidence labels used throughout:

- **[Confirmed]**: Brother documentation, or read directly from this PC's logs and files
- **[Reported]**: a third party's claim that has not been reproduced here
- **[Inferred]**: a conclusion drawn from the evidence, not directly proven
- **[Unknown]**: must be tested. See section 6

---

## 1. Summary

The scanner is not broken, and its hardware is rated well beyond the length where scans stop. Brother's firmware and drivers treat a long document as a jam unless **Long Paper** mode is selected. Brother's Linux driver gives no way to select it.

Brother documents Long Paper for Windows, through both iPrint&Scan and the TWAIN driver. That makes Windows a sound target. The main implementation risk is whether an app can **set Long Paper itself** or has to go through Brother's own settings dialog. Phases 0 and 1 in section 7 answer that before any app is built.

"Never stop until the receipt is finished" has one hard limit: **72 in (1828.8 mm)**, the longest document Brother rates the DS-640 for. The app must treat anything longer as a reported failure, and must never save a cut-off scan as if it were complete.

---

## 2. Problem and cause

### 2.1 Symptom

On Linux, using GNOME Document Scanner, the scanner pulls a receipt partway through and stops. Usually the app reports `Document feeder jammed` and produces no image.

### 2.2 What the log recorded

Source: `~/.cache/simple-scan/simple-scan.log`, session of 2026-11-01. **[Confirmed]**

All attempts used `brscan5` 1.7.0, `simple-scan` 50.0, and `AutoDocumentSize=TRUE`. Length and width are calculated from the reported pixel size.

| Result | Count | DPI | Time from option setup to result | Output |
|---|---|---|---|---|
| `SANE_STATUS_JAMMED` | 8 | 300 | About 10.6 s every time | No image |
| `SANE_STATUS_INVAL` | 3 | 1200 | About 0.1 s | No image |
| Finished | 1 | 300 | 9.42 s | 909 x 3475 px, or 77.0 x 294.2 mm |
| Finished | 1 | 300 | 10.94 s | 1894 x 3661 px, or 160.4 x 310.0 mm |
| Finished | 1 | 300 | 8.23 s | 905 x 2916 px, or 76.6 x 246.9 mm |

Notes:

- The log does not record how long each physical receipt was.
- It also does not show whether every attempt used the same receipt.
- The 294 mm image visibly ends at a payment line, so it may have been cut off. It might also have been the real end of that receipt.
- The 10.94 s scan finished without a jam, so 10.6 s is not a simple time limit on all scans.

### 2.3 Cause, from strongest evidence to weakest

1. **[Confirmed] Brother documents this behavior.** The DS-640 Online User's Guide says a document can jam "if it is not inserted or fed correctly, or if it is too long." Its error table says: "If scanning a long document, select Long Paper from the Scan Size or Document Size drop-down list."
2. **[Confirmed] Brother gives a threshold.** The DS-640 FAQ says documents longer than **37.5 cm (14.76 in)** need Document Size set to **Long Paper**.
3. **[Confirmed] The hardware is rated far past that.** Brother's specifications give 86.4 x 50.8 mm to **1828.8 x 215.9 mm**.
4. **[Confirmed] The Linux driver offers no way to choose it.**
   - `scanimage -A` lists no long-paper option.
   - The `-y` and `br-y` scan height stops at 355.6 mm (14 in, US Legal).
   - Brother's core library does contain the code: `CCmdString_v2::MakeLongPaperModeString(bool)`, `CAdjustScanSetting::ConvertUserSetting_LongPaper`, `CDeviceCapsForPreparation::GetLongPaperLengthMax`, `CScanArea::GetLongPaperSizePixel`, and the strings `LONG=`, `LONGPAPER_NARROW`, and `LONGPAPER_WIDE`.
5. **[Reported] The USB traffic matches.** The open-source `excellentplans/brscan` protocol notes come from USB captures of the Brother driver:
   - The settings command contains `LONG=OFF`.
   - The device pulls the sheet **before** responding to the scan-start command, typically in 8.8 to 9.3 s.
   - A jam is about 10 s of silence followed by response `91 00`. That closely matches the 10.6 s failures above.
6. **[Reported] A longer scan area alone may be enough in some modes.** Merged PR #2 on that project raised the DS-640 scan height to 1828.8 mm while still sending `LONG=OFF`.
   - Its author reports a 420 mm receipt scanning in one pass at 600 dpi.
   - A 90 mm card still produced a 90 mm image.
   - Only those two lengths are reported, and the project's author says it is AI-written and physically tested on one DS-640 with a Raspberry Pi.

**[Inferred] Most likely sequence on Linux:**

1. Document Scanner asks for auto-size at the normal maximum length.
2. The firmware feeds the sheet without long-paper handling.
3. A receipt that is too long for that mode gets reported as a jam, leaving the paper in the feed path.

**[Unknown]** The exact length at which this happens on this unit. Finished scans reach 310 mm, while Brother's stated threshold is 375 mm. It is also unknown whether `LONG=ON` or the larger scan area is what actually controls it.

### 2.4 Secondary finding

**[Inferred]** The 1200 dpi `INVAL` errors have a separate cause. The guide says Auto document size and Auto Deskew are unavailable above 400 dpi on the DS-640, and Document Scanner forces auto-size on. 1200 dpi is also interpolated, because optical resolution is 600 dpi, so it offers no real benefit.

---

## 3. Windows driver facts

Source: DS-640 Online User's Guide and FAQ. **[Confirmed]** unless marked.

| Topic | Fact |
|---|---|
| TWAIN source name | Follows the pattern `TW-Brother XX-XXXXX`. Expect `TW-Brother DS-640` **[Inferred]** |
| Preferred driver | Brother's guide uses TWAIN as its recommended option in the PaperPort example |
| Long Paper via TWAIN | Scanner Setup dialog, then **Document Size**, then **Long Paper** |
| Long Paper via iPrint&Scan | **Machine Scan Settings**, then **Document Size**, then **Long Paper** |
| WIA | The guide's WIA settings list **no** Document Size or Long Paper control. Do not build on WIA |
| Detect End of Page | Trims the output when a document is shorter than the selected Document Size |
| Auto Document Size | Turns Auto Deskew on automatically. Unavailable above 400 dpi |
| Long Paper and deskew | Brother's ADS-2400N FAQ says Auto Deskew must be off to select Long Paper. That is another model, so verify here **[Reported]** |
| Duplex | Not available with Long Paper, and the DS-640 is single-sided anyway |
| Thin paper | Rated thickness is 0.04 to 0.28 mm. Brother recommends a carrier sheet for thinner paper, and that sheet limits length unless the receipt is folded |
| Windows 11 support | Brother's download page lists iPrint&Scan for Windows 11, but its specification page lists TWAIN and WIA only through Windows 10. Confirm the driver package in Phase 0 **[Unknown]** |
| USB | USB 3.0 Micro-B, bus powered (reports 498 mA). Brother specifies a cable no longer than 2 m and does not support third-party USB ports |

Also observed in the open-source project's notes, not by Brother. **[Reported]**

- After a **cancel**, an immediate rescan can fail until the scanner is replugged.
- After a **jam**, a new scan works without replugging.
- The device drops off USB after about 10 minutes idle.
- These notes come from Linux, so verify them on Windows.

---

## 4. Requirements

### 4.1 Functional

| ID | Requirement |
|---|---|
| F1 | Scan one receipt in one pass with Long Paper active, from the rated minimum of 86.4 mm up to 1828.8 mm |
| F2 | Trim the output to the receipt's true end, so short receipts do not get a long blank tail |
| F3 | One-click scanning with receipt defaults (see 4.2). The user should not have to set Long Paper by hand on each scan |
| F4 | Verify Long Paper before scanning. If the app cannot confirm it, warn clearly and do not scan silently in normal mode |
| F5 | Detect jam, no-paper, cover-open, and transfer errors, and show plain-language recovery steps |
| F6 | **Never present a failed or partial scan as complete.** Failed scans must not reach the output folder |
| F7 | Save as PDF and PNG, with one receipt per file, a timestamped name, and a user-selectable folder |
| F8 | Show a preview before saving, with rotate and rescan |
| F9 | Handle the scanner being unplugged or dropping off USB, and reconnect without restarting the app |
| F10 | Write a diagnostic log containing driver version, capabilities, settings sent, status codes, timings, and final image size. Never log receipt contents |

### 4.2 Receipt scan defaults

| Setting | Value | Why |
|---|---|---|
| Document Size | Long Paper | Required by Brother above 37.5 cm |
| Source | Feeder | The DS-640's only source |
| Sides | One | The DS-640 is single-sided |
| Resolution | 300 dpi, with 200 and 600 optional | Readable for OCR. 600 is the optical limit, so never offer 1200 |
| Color | True Gray default, 24-bit Color optional | Good for faded thermal print, with smaller files |
| Auto Deskew | Off | Brother's FAQ for another model says it conflicts with Long Paper |
| Detect End of Page | On, if available with Long Paper | Satisfies F2 |
| Blank page skip | Off | A pale receipt must not be discarded |
| Width | Crop in software after the scan, about 77 mm for the receipts seen so far | Keeps the scanner's capture area simple |

### 4.3 Non-functional

- The app must handle a 72 in scan at 600 dpi: 5100 x 43200 px, or about 660 MB as uncompressed RGB. Use streaming or memory-mapped buffers and do not assume the image is small.
- Keep the UI responsive during the wait before data arrives. The device may send nothing for 9 s or more on ordinary documents, and possibly far longer on long ones.
- **Scale timeouts to length.** Measured feed rate is roughly 30 mm/s at 300 dpi, so a full 1828.8 mm sheet could take about 60 s or more. A fixed 15 s timeout would create the same bug in a new place.
- Do not cancel by default on a slow scan, since cancel may leave the device needing a replug.
- No administrator rights in daily use, no network access, and no telemetry. Receipts are financial records and stay local.

### 4.4 User guidance in the app

- Insert one receipt at a time, flat and straight, with the guide against its edge.
- Support long receipts by hand so they do not curl or drag.
- **After a jam, open the Top Cover before pulling the paper out.** Brother warns that pulling first can damage the document.

---

## 5. Technical approach

### 5.1 Recommended base

| Item | Choice | Notes |
|---|---|---|
| Language and UI | C#, .NET 8 or later, WPF | Matches NAPS2.Sdk, whose targets include `net8`, `net10.0`, and `net462` |
| Scanning library | [`NAPS2.Sdk`](https://github.com/cyanfish/naps2/tree/master/NAPS2.Sdk) 1.6.0 with `NAPS2.Images.Wpf` | LGPL-2.1-or-later per the NAPS2 README. It is actively maintained |
| 32-bit TWAIN support | `NAPS2.Sdk.Worker.Win32` and `scanningContext.SetUpWin32Worker()` | TWAIN usually requires a 32-bit caller, so this lets the app itself be x64 |
| Driver | Brother's Windows TWAIN driver, unmodified | Do not redistribute it. Users install it from Brother |

Why NAPS2.Sdk saves work, from reading its source at `NAPS2.Sdk/Scan/Internal/Twain/TwainScanRunner.cs`:

- It sets the TWAIN image layout from a custom `PageSize`, so `new PageSize(8.5m, 72m, PageSizeUnit.Inch)` is possible.
- It has `ScanOptions.UseNativeUI`, which opens Brother's own dialog.
- It also supplies device listing, memory transfers, PDF export, and image handling.

**Known gaps in NAPS2.Sdk**

- It has no long-paper setting. It sets a size and generic capabilities only.
- The code reads the physical size and may limit the frame to it. Check lines 387 to 407 of that file against what the DS-640 reports.
- A larger frame alone may not switch on Long Paper. Phase 1 tests this.
- If custom capabilities or `DAT_CUSTOMDSDATA` are needed, use NAPS2's underlying `NAPS2.NTwain` directly or a small companion wrapper. Forking NAPS2.Sdk is a last resort.

### 5.2 How to enable Long Paper, in order of preference

**Option A: TWAIN capabilities (best)**

The app sets everything itself. Try these in the order listed in Phase 1:

- `DAT_IMAGELAYOUT` frame height of 72 in
- `ICAP_UNDEFINEDIMAGESIZE`
- `ICAP_AUTOMATICLENGTHDETECTION`
- `ICAP_SUPPORTEDSIZES` with `TWSS_NONE`
- Any Brother custom capabilities the dump reveals, numbered `CAP_CUSTOMBASE` (`0x8000`) and above

Turn `ICAP_AUTOMATICBORDERDETECTION` and `ICAP_AUTOMATICDESKEW` off. Forum reports for other scanners say auto-border detection can override length detection. **[Reported]**

**Option B: Saved driver state**

The user chooses Long Paper once in Brother's dialog. The app then saves the driver's settings with `DAT_CUSTOMDSDATA` `MSG_GET`, and restores them before each scan with `MSG_SET`. This needs `CAP_CUSTOMDSDATA` to be TRUE. The saved data must be tied to the driver version, and must be recaptured when that changes.

**Option C: Brother's dialog**

Use `UseNativeUI = true` so the user selects Long Paper in Brother's own dialog. It's reliable but works against F3, so use it only as a setup step or fallback. Whether the driver remembers the selection between sessions is **[Unknown]**.

**Option D: Direct USB (last resort)**

Talk to the scanner over WinUSB without Brother's driver, sending `LONG=ON` and a long scan area. Avoid this unless A to C all fail.

- It requires replacing the driver on interface 1 with WinUSB, which can break Brother's own software.
- It means reimplementing session setup, framing, JPEG and run-length data, and error handling.
- `LONG=ON` is unverified, since all published captures show `LONG=OFF`.
- The protocol notes and fork are GPL-2.0-or-later. Copying from them makes the app GPL as well.
- Capture the real traffic first using USBPcap and Brother's Long Paper mode.

### 5.3 Scan flow

```text
Open TWAIN source -> Confirm DS-640 -> Apply receipt settings
       |
Long Paper confirmed? -- no --> Option B/C, or warn and stop (F4)
       | yes
Check paper loaded -> Start scan -> Wait, with a length-scaled timeout
       |
Result:
  complete -> trim -> preview -> save as PDF and PNG
  jam      -> discard, show Top Cover steps, allow rescan
  error    -> discard, log the status, offer reconnect
```

### 5.4 Completeness check

The driver may not be able to tell the app that a page was cut off, so use several signals:

- Require a successful end-of-transfer with no error status.
- Flag the result if its height is within 2 mm of the frame's limit, since the paper may have continued.
- Check whether the last rows are content or white background. Content running right up to the bottom edge is a warning sign, which the preview should show.
- Let the user record the receipt as verified or rescan it.

---

## 6. Open questions

| # | Question | Answered in |
|---|---|---|
| Q1 | Does Brother's own Long Paper mode scan a real long receipt on Windows 11 on this unit? | Phase 0 |
| Q2 | Is the TWAIN driver available and working on Windows 11, and is it 32-bit, 64-bit, or both? | Phase 0 |
| Q3 | What does `ICAP_PHYSICALHEIGHT` report? Is 72 in accepted as a frame height? | Phase 1 |
| Q4 | Can Long Paper be set without Brother's dialog? Which of Options A to C works? | Phase 1 |
| Q5 | Does Detect End of Page work with Long Paper, and can the app control it? | Phase 1 |
| Q6 | Is a true 72 in scan achievable at each DPI, or is there a memory or time limit? `LONGPAPER_NARROW` and `LONGPAPER_WIDE` hint at width-related modes | Phase 3 |
| Q7 | At what length does the jam occur without Long Paper on this unit? | Phase 0 |
| Q8 | Do receipts below 0.04 mm feed reliably, without a carrier sheet? | Phase 3 |
| Q9 | Do the cancel, jam, and idle-disconnect behaviors from Linux also occur on Windows? | Phase 3 |

---

## 7. Delivery plan

### Phase 0: Prove the hardware (no code)

1. Install Brother's full Windows package for the DS-640.
2. Scan a receipt over 37.5 cm using iPrint&Scan with the default size, and record the failure.
3. Scan it again with **Long Paper** selected.
4. Repeat through the TWAIN dialog, using NAPS2's desktop app with its native-UI option.

**Stop if Long Paper fails here.** No app can fix that, so check the cable, port, driver, and paper first, then contact Brother.

### Phase 1: Capability discovery tool

Build a small console tool that:

1. Opens the TWAIN source and dumps every supported capability, its current value, and its allowed values, including custom IDs from `0x8000` up.
2. Runs that dump with the driver at default settings, and again after the user selects Long Paper in Brother's dialog. **The difference shows which setting controls it.**
3. Tests `DAT_CUSTOMDSDATA` save and restore.
4. Tries each Option A setting with a real long receipt, and records the resulting image size.

Save all output to `docs/twain-capabilities.md`, and record the chosen option there.

If Phase 1 is inconclusive, capture USB traffic with USBPcap and compare a default scan against a Long Paper scan.

### Phase 2: Minimum working app

One button, one receipt, Long Paper via the option chosen in Phase 1, saving to a PDF and PNG, with honest error handling.

### Phase 3: Hardening

Add the rest of section 4, then run the acceptance tests.

---

## 8. Acceptance tests

Measure each physical document with a ruler first. If real receipts of a given length aren't available, tape printer paper or receipt rolls together, keeping joins flat and secure. Test at 300 dpi unless noted.

| ID | Document | Expected |
|---|---|---|
| A1 | 150 mm receipt | Complete, no long blank tail |
| A2 | 294 to 310 mm receipt | Complete |
| A3 | 355.6 mm (14 in) | Complete. This is the Linux driver's maximum |
| A4 | 375 to 380 mm | Complete. This crosses Brother's 37.5 cm threshold |
| A5 | 420 mm | Complete, also at 600 dpi |
| A6 | 900 mm | Complete |
| A7 | 1828 mm | Complete at 300 dpi. Record the time and memory used |
| A8 | Over 1829 mm | Reported as a failure, with nothing saved as complete |
| A9 | Feed blocked by hand | Jam message with Top Cover steps, and a rescan works |
| A10 | No paper | Clear prompt, no error dialog or crash |
| A11 | Unplug during a scan | Clear error, nothing saved, reconnects after replug |
| A12 | Faded thermal receipt | All text readable |
| A13 | Crumpled or very thin receipt | Scans or fails clearly, without a silent partial result |
| A14 | Idle for 15 minutes, then scan | Reconnects and scans |

**Pass criteria for A1 to A7:**

- Image length is within ±2% of the measured length.
- The first and last printed lines are both fully visible.
- No stretched, overlapped, or missing bands.
- Ten consecutive runs of A5 all succeed.

---

## 9. Local files and whether they can be reused

These paths are on the **Arch Linux PC**.

**None of these can run on Windows.** They are Linux ELF binaries, and `simple-scan` is built on SANE, which has no working path to this scanner on Windows. Use them only as references. Windows work should start from section 5.1.

### 9.1 Document Scanner (`simple-scan` 50.0, GPL-3.0-or-later)

| Path | What it is |
|---|---|
| `/usr/bin/simple-scan` | The application binary |
| `/usr/share/applications/org.gnome.SimpleScan.desktop` | App launcher entry |
| `/usr/share/glib-2.0/schemas/org.gnome.SimpleScan.gschema.xml` | Settings schema |
| `~/.config/simple-scan/state` | Saved window and app state |
| `~/.cache/simple-scan/simple-scan.log` | **The evidence log.** It may be overwritten on later runs, and the key results are in section 2.2 |
| `~/.cache/simple-scan/autosaves/` | Raw images from the last session. These contain real receipts, so do not commit them |

Its source is at `https://gitlab.gnome.org/GNOME/simple-scan`. It is not a useful base: it's Vala and GTK, built around SANE, and copying from it would make the app GPL-3.

### 9.2 Brother Linux driver (`brscan5` 1.7.0, Brother's proprietary license)

| Path | What it is |
|---|---|
| `/usr/lib/sane/libsane-brother5.so.1.0.7` | SANE backend, including the option list and jam reporting |
| `/usr/lib/libLxBsScanCoreApi.so.3.2.6` | Core scan logic, including the long-paper symbols listed in 2.3 |
| `/usr/lib/libLxBsUsbDevAccs.so.1.0.0` | USB access |
| `/usr/lib/libLxBsDeviceAccs.so.1.0.0` | Device access |
| `/usr/lib/libLxBsNetDevAccs.so.1.0.0` | Network access, not used by this scanner |
| `/opt/brother/scanner/brscan5/` | Config, model lists, tools, and docs |
| `/opt/brother/scanner/brscan5/doc/LICENSE_ENG.txt` | License terms. Read before reusing anything |
| `/etc/sane.d/dll.d/brother5.conf` | Registers the driver with SANE |
| `/usr/lib/udev/rules.d/40-brscan5.rules` | Brother's udev rule, which does not match this device |
| `/etc/udev/rules.d/60-brother-ds640.rules` | The custom rule added on this PC to allow access |

Do not ship, wrap, or copy these. Use the Brother **Windows** driver instead.

### 9.3 External references worth using

| Resource | Use | License |
|---|---|---|
| [NAPS2 source](https://github.com/cyanfish/naps2) | Base library, and a working example of TWAIN on Windows | App GPL-2.0-or-later, SDK LGPL-2.1-or-later, samples MIT |
| [`excellentplans/brscan`](https://github.com/excellentplans/brscan) | Protocol reference, for Option D only | GPL-2.0-or-later |
| [`docs/brscan5-protocol.md`](https://github.com/excellentplans/brscan/blob/master/docs/brscan5-protocol.md) | USB commands, responses, and timing | GPL-2.0-or-later |
| [`libsane-brother/brscan5_proto.c`](https://github.com/excellentplans/brscan/blob/master/libsane-brother/brscan5_proto.c) | Command encoding, including `LONG=OFF` | GPL-2.0-or-later |

---

## 10. Out of scope for version 1

- Separate handling for receipts over 72 in, beyond reporting failure
- Stitching multiple scans into one image
- OCR and expense data extraction
- Network scanning, other scanner models, Mac, or Linux
- Modifying or redistributing Brother's drivers or firmware

---

## 11. Sources

**Brother**

- [DS-640 Online User's Guide (PDF)](https://support.brother.com/g/s/id/htmldoc/ads/cv_ds640/uke/PDF/PDF.pdf)
- [FAQ: Scan a document longer than 37.5 cm (14.76 inches)](https://support.brother.com/g/b/faqend.aspx?c=us&lang=en&prod=ds640_us_eu_as&faqid=faq00100159_001)
- [FAQ: DS-640 Acceptable Documents](https://support.brother.com/g/b/faqend.aspx?c=us&lang=en&prod=ds640_us_eu_as&faqid=faq00002914_005)
- [FAQ: Can not scan a long document (ADS-2400N)](https://support.brother.com/g/b/faqend.aspx?c=us&lang=en&prod=ads2400n_all&faqid=faq00003064_001)
- [DS-640 Specifications](https://support.brother.com/g/b/spec.aspx?c=us&lang=en&prod=ds640_us_eu_as)
- [DS-640 Downloads](https://support.brother.com/g/b/downloadtop.aspx?c=us&lang=en&prod=ds640_us_eu_as)

**Open source**

- [excellentplans/brscan PR #2: raise the DS-640 scan area to 72 in](https://github.com/excellentplans/brscan/pull/2)
- [NAPS2.Sdk README](https://github.com/cyanfish/naps2/blob/master/NAPS2.Sdk/README.md)
- [NAPS2 `TwainScanRunner.cs`](https://github.com/cyanfish/naps2/blob/master/NAPS2.Sdk/Scan/Internal/Twain/TwainScanRunner.cs)

**TWAIN**

- [ICAP_AUTOMATICLENGTHDETECTION reference](https://asprise.com/twain/ICAP_AUTOMATICLENGTHDETECTION)
- [TWAIN forum: Kodak document size capability](https://twain.org/forums/topic/kodak-document-size-cap/)
