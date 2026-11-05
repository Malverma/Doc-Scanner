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

If the driver refuses the full 72 in page, the app says Long Paper isn't confirmed. Enter the receipt's length in **Settings > Receipt size > Expected length** and try again, since the app then also accepts a page of that length.

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
- **Show in File Explorer** opens the folder with the new receipt selected, and the path on screen matches it.
- **Rotate** on a long receipt: the bar keeps moving and the buttons grey out, then return. If the window stops responding, wait a minute, close it, and send the log. Its `Rotate` lines show how far it got.
- Dragging a receipt from the list into File Explorer copies it and leaves the original. Dragging onto a folder tile moves it.
- A receipt pulled out or still in the scanner when the scan ends is held for review, not saved. If every scan is held with a "paper still in the scanner" warning, the driver's paper sensor can't be trusted, so send the log.
- Trimming suits real scans. The scanner's background may differ from the simulator's, and every scan being held for review would point to this.

## Epson RapidReceipt RR-70W

Run this if you're using the Epson instead of, or as well as, the DS-640. It hasn't been tested against Epson's real driver.

1. Install **Epson Scan 2** and the RR-70W scanner driver from Epson's support site. In Epson Scan 2, scan the long receipt with **Document Size > Auto Detect (Long Paper)** and check you get the whole receipt. If not, stop, as with step 1.
2. In the app, set **Settings > Scanner** to **Epson RR-70W**. The bottom-left corner should say **RR-70W ready**.
3. Run steps 3 to 6 above. Where they mention Brother's dialog, use Epson's, and choose **Auto Detect (Long Paper)** as the document size.
4. If it says not found, hover over the status in the bottom-left corner. It lists the scanner drivers the app found. If no Epson entry appears, Windows is using its built-in driver, which this app can't use, so install Epson Scan 2. Either way, send the newest log. Its `TWAIN source details` line shows the name Epson's driver uses.

## What to send back

From `%LOCALAPPDATA%\ReceiptScanner\logs`, open it through **Settings > Open logs**:

- The newest `receipt-scanner-*.log`
- The `twain-capabilities-*.md` report. Save a copy as `docs/twain-capabilities.md`, as the spec asks

Logs record settings, status codes and image sizes, not receipt contents.
