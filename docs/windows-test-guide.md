# Windows test guide

Run this the first time you have Windows 11 and the DS-640 together. It answers what couldn't be tested without them, mainly whether the app can turn on Long Paper. Allow about an hour.

**You'll need:** a receipt or taped paper strip longer than 37.5 cm, ideally about 42 cm, plus a ruler and a short receipt.

Stop at the first step that fails, and bring back the files listed under "What to send back".

## 1. Prove the scanner works (spec Phase 0)

This uses Brother's software only, so a failure here can't be caused by this app.

1. Install Brother's **full** DS-640 driver package from support.brother.com.
2. In Brother iPrint&Scan, scan the long receipt with the default document size. Note what happens.
3. Scan it again with **Machine Scan Settings > Document Size > Long Paper**.

**If step 3 doesn't produce the whole receipt, stop.** No app can fix that. Check the cable, USB port, driver and paper, then contact Brother.

## 2. Start the app

1. Download `ReceiptScanner-Setup-<version>.exe` from the [Releases page](https://github.com/Malverma/Doc-Scanner/releases), double-click it, and open the app when it finishes. If SmartScreen warns about it, click **More info**, then **Run anyway**.
2. The bottom-left corner should say **DS-640 ready** within a few seconds.

If it says not found, the driver may not include a 32-bit TWAIN source. The log lists every source it found.

## 3. Run the driver check

1. Open **Settings > Run driver check**.
2. When Brother's dialog opens, set **Document Size** to **Long Paper**, then click **OK**.
3. Wait for the message that the report was saved.

The report's "What changed" section shows which setting controls Long Paper.

## 4. Try the automatic method

1. Leave Settings on **Automatic**, then scan the long receipt.
2. Measure the saved image against the receipt with a ruler.

If it's complete, the capability method works. Skip to step 7.

## 5. Try the saved-settings method

Do this if step 4 jammed or cut the receipt off.

1. Open **Settings > Set up Long Paper**, set **Document Size** to **Long Paper**, and click **OK**.
2. Scan the long receipt again.

If it's complete, skip to step 7.

## 6. Use Brother's dialog

Do this if step 5 also failed.

1. Set **Settings > Long Paper > Method** to **Brother's dialog on every scan**.
2. Scan, choose **Long Paper** in the dialog, and start the scan there.

This should work if step 1 did, but it costs extra clicks on every scan. Send back the files so a better route can be found.

## 7. Run the acceptance tests

Run tests A1 to A14 from section 8 of `spec.md`. Start with these, since they're the riskiest:

| Test | Do this | Expect |
|---|---|---|
| A1 | Scan a 150 mm receipt | Complete, with no long blank tail |
| A4 | Scan a 375 to 380 mm strip | Complete |
| A5 | Scan a 420 mm strip 10 times, once at 600 dpi | All complete |
| A7 | Scan an 1828 mm strip | Complete. Note the time taken |
| A9 | Hold the paper back mid-feed | Jam message with Top Cover steps, and the next scan works |
| A11 | Unplug during a scan | Clear error, nothing saved, works after replugging |
| A12 | Scan a faded receipt | All text is readable, and nothing is trimmed off |
| A14 | Leave idle for 15 minutes, then scan | Reconnects and scans |

Also check these, which the test setup couldn't confirm:

- A real paper jam shows this app's message. Brother's driver may show its own dialog first.
- **Delete** sends a receipt to the Recycle Bin.
- Trimming suits real scans. The scanner's background may differ from the simulator's, and every scan being held for review would point to this.

## What to send back

From `%LOCALAPPDATA%\ReceiptScanner\logs`, open it through **Settings > Open logs**:

- The newest `receipt-scanner-*.log`
- The `twain-capabilities-*.md` report. Save a copy as `docs/twain-capabilities.md`, as the spec asks

Logs record settings, status codes and image sizes, not receipt contents.
