#!/bin/bash
# Runs the published worker against a fake TWAIN source under Wine, in a throwaway prefix.
# Proves the worker's TWAIN code paths work. Says nothing about Brother's or Epson's real drivers.
# Needs: wine (with display), clang, lld. Run scripts/publish.sh first.
set -u
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
work="${RS_WINE_WORK:-$(mktemp -d)}"
mkdir -p "$work"
# Disabling Wine's own sources keeps the test from probing real scanners on the network.
export WINEPREFIX="$work/prefix" WINEDEBUG=-all WINEDLLOVERRIDES="mshtml=;winemenubuilder.exe=d;sane.ds=;gphoto2.ds=;twain_32=n"
worker="${RS_WORKER:-$root/dist/ReceiptScanner/worker/ReceiptScanner.TwainWorker.exe}"

[ -f "$worker" ] || { echo "Run scripts/publish.sh first"; exit 2; }
[ -d "$WINEPREFIX" ] || timeout 200 wineboot -i >/dev/null 2>&1

clang --target=i686-w64-windows-gnu -O1 -ffreestanding -fno-builtin -nostdlib -shared -fuse-ld=lld \
  -Wl,-e,_DllMainCRTStartup@12 -Wl,--kill-at -Wall -Wno-unused-function "$here/fake_ds640.c" \
  -L/usr/lib/wine/i386-windows -lkernel32 -o "$work/fake.ds" || { echo "Compile failed"; exit 2; }
clang --target=i686-w64-windows-gnu -O1 -ffreestanding -fno-builtin -nostdlib -shared -fuse-ld=lld \
  -Wl,-e,_DllMainCRTStartup@12 -Wl,--kill-at -Wall "$here/twain32_shim.c" \
  -L/usr/lib/wine/i386-windows -lkernel32 -o "$work/twain_32.dll" || { echo "Compile failed"; exit 2; }
cp "$work/twain_32.dll" "$WINEPREFIX/drive_c/windows/twain_32.dll"
mkdir -p "$WINEPREFIX/drive_c/windows/twain_32/fake"
cp "$work/fake.ds" "$WINEPREFIX/drive_c/windows/twain_32/fake/fake.ds"

raw="$work/s.raw"; winraw="Z:$(echo "$raw" | tr / '\\')"
prof="$work/p.json"; winprof="Z:$(echo "$prof" | tr / '\\')"
fails=0

run() { # mode, args...
  local mode="$1"; shift
  rm -f "$raw"
  FAKE_DS_MODE="$mode" timeout 120 wine "$worker" "$@" 2>/dev/null | grep -a '^{"Type"' > "$work/out.jsonl"
  code=${PIPESTATUS[0]}
}
scan() { local mode="$1"; shift; run "$mode" scan --dpi 300 --color "${COLOR:-Gray}" --out "$winraw" --strategy "${STRATEGY:-Auto}" --profile "$winprof" "$@"; }
check() { # name, expected exit code, python assertion over events e and file raw
  local name="$1" want="$2" assertion="$3"
  if RAW="$raw" OUT="$work/out.jsonl" python3 -I - "$assertion" <<'PY' && [ "$code" = "$want" ]
import json,os,sys
ev=[json.loads(l) for l in open(os.environ['OUT'],errors='replace')]
by=lambda t:[e for e in ev if e['Type']==t]
one=lambda t:(by(t) or [{}])[-1]
raw=os.environ['RAW']
data=open(raw,'rb').read() if os.path.exists(raw) else b''
W,H=301,2000
def pat(ch):
    b=bytearray()
    for y in range(H):
        for x in range(W):
            for c in range(ch):
                b.append(20+((x*3+y*7+c*11)%200))
    return bytes(b)
sys.exit(0 if eval(sys.argv[1]) else 1)
PY
  then echo "PASS $name"
  else echo "FAIL $name (exit $code)"; cut -c1-260 "$work/out.jsonl" | sed 's/^/     /'; fails=$((fails+1)); fi
}

run normal probe
check "probe finds the source" 0 "'DS-640' in one('source')['Message'] and 'fake-1.0' in one('source')['Message']"
check "probe recognizes the DS-640" 0 "one('source')['ScannerId']=='brother-ds640'"

FAKE_DS_NAME="EPSON RR-70W" run normal probe
check "probe recognizes the Epson RR-70W" 0 "one('source')['ScannerId']=='epson-rr70w' and 'RR-70W' in one('source')['Message']"

FAKE_DS_NAME="EPSON RR-70W" run normal probe --scanner brother-ds640
check "a preferred model that isn't installed is not found" 1 "one('error')['Code']=='ScannerNotFound' and not by('source')"

FAKE_DS_NAME="EPSON RR-70W" scan normal --scanner epson-rr70w
check "Epson-named source scans" 0 "one('complete').get('Height')==H and data==pat(1)"

rm -f "$prof"
scan normal
check "gray scan: pixels exact, padding removed" 0 "one('complete').get('Width')==W and one('complete')['Height']==H and one('complete')['Dpi']==300 and data==pat(1)"
check "Long Paper set via capabilities, frame 72 in" 0 "one('longPaper')['Confirmed'] and one('longPaper')['FrameHeightInches']==72 and one('longPaper')['Method']=='TWAIN capabilities'"

COLOR=Color scan normal
check "color scan: pixels exact" 0 "one('complete')['BytesPerPixel']==3 and data==pat(3)"

scan undefined
check "undefined length (-1) from driver" 0 "one('complete')['Height']==H and data==pat(1)"

scan vanilla
check "inverted (vanilla) gray is corrected" 0 "one('complete')['Height']==H and data==pat(1)"

scan twopages
check "more pages pending: first page kept and flagged" 0 "one('complete')['Height']==H and data==pat(1) and one('complete')['PaperRemaining'] is True"

scan clamp
check "clamped frame reported, not hidden" 0 "one('longPaper')['FrameHeightInches']==14 and one('complete')['Height']==H"

scan normal
check "emptied feeder is not flagged" 0 "'PaperRemaining' not in one('complete')"
scan paperleft
check "paper left in the feeder is flagged" 0 "one('complete')['PaperRemaining'] is True and data==pat(1)"
scan resetframe
check "frame reset by scan settings is set again" 0 "one('longPaper')['Confirmed'] and one('longPaper')['FrameHeightInches']==72"
scan normal --length-mm 200
check "short expected length still asks for the full 72 in" 0 "one('longPaper')['FrameHeightInches']==72 and one('complete')['Height']==H"
scan clamp --length-mm 200
check "expected length never shortens a longer frame" 0 "one('longPaper')['FrameHeightInches']==14"
scan reject72
check "frame refused: unconfirmed without an expected length" 1 "one('error')['Code']=='LongPaperUnconfirmed'"
scan reject72 --length-mm 762
check "frame refused: expected length is accepted instead" 0 "one('longPaper')['Confirmed'] and abs(one('longPaper')['FrameHeightInches']-30)<0.01 and one('complete')['Height']==H"
scan normal --width-mm 80
check "width setting sets the frame" 0 "'3.15x72 in at left 0' in one('longPaper')['Evidence'] and one('longPaper')['FrameHeightInches']==72"
scan centered --width-mm 80
check "width is centered on a center-feeding scanner" 0 "'3.15x72 in at left 2.68' in one('longPaper')['Evidence']"

scan jam
check "jam reported" 1 "one('error')['Code']=='Jam' and not by('complete')"
scan cover
check "cover open reported" 1 "one('error')['Code']=='CoverOpen' and not by('complete')"
scan offline
check "disconnect mid-scan reported" 1 "one('error')['Code']=='Disconnected' and not by('complete')"
scan cancel
check "driver cancel is not complete" 1 "one('error')['Code']=='Canceled' and not by('complete')"
scan short
check "fewer rows than announced is not complete" 1 "one('error')['Code']=='IncompleteData' and not by('complete')"
scan nopaper
check "no paper reported" 1 "one('error')['Code']=='NoPaper'"
scan noopen
check "offline at open reported" 1 "one('error')['Code']=='ScannerNotFound'"

scan unconfirmed
check "unconfirmed Long Paper refuses to scan" 1 "one('error')['Code']=='LongPaperUnconfirmed' and one('longPaper')['Confirmed'] is False and not data"
scan unconfirmed --allow-unconfirmed
check "explicit override scans, still marked unconfirmed" 0 "one('longPaper')['Confirmed'] is False and one('complete')['Height']==H"

rm -f "$prof"
run dialogcancel setup --profile "$winprof"
check "canceled setup saves nothing" 1 "one('result')['Success'] is False and not os.path.exists(os.environ['RAW'].replace('s.raw','p.json'))"
run normal setup --profile "$winprof"
check "setup saves driver state" 0 "one('result')['Success'] is True and os.path.exists(os.environ['RAW'].replace('s.raw','p.json'))"
scan normal
check "saved profile restored and confirmed" 0 "one('longPaper')['Method']=='Saved driver profile' and one('longPaper')['Confirmed'] and data==pat(1)"
scan resetframe
check "saved profile's frame survives a driver reset" 0 "one('longPaper')['Method']=='Saved driver profile' and one('longPaper')['Confirmed'] and one('longPaper')['FrameHeightInches']==72"
scan normal --width-mm 80
check "width applied on top of a saved profile" 0 "one('longPaper')['Method']=='Saved driver profile' and 'frame reasserted, 3.15 in wide' in one('longPaper')['Evidence'] and one('longPaper')['FrameHeightInches']==72"
scan rejectprofile
check "rejected profile falls back to capabilities" 0 "one('longPaper')['Method']=='TWAIN capabilities' and one('longPaper')['Confirmed']"
python3 -I -c "import json,sys;p=sys.argv[1];d=json.load(open(p));d['DriverVersion']='old';json.dump(d,open(p,'w'))" "$prof"
scan normal
check "profile from another driver version not used" 0 "one('longPaper')['Method']=='TWAIN capabilities' and any('old' in e.get('Message','') for e in by('log'))"
rm -f "$prof"

report="$work/report.md"
run normal discover --out "Z:$(echo "$report" | tr / '\\')"
check "driver check writes a report" 0 "one('result')['Success'] is True"
if grep -q 'ICapUndefinedImageSize' "$report" && grep -q '0x8042' "$report" && grep -q 'What changed' "$report" && grep -Eq 'ICapUndefinedImageSize.*\| *(0|False).*\| *(1|True)' "$report"; then
  echo "PASS report shows the Long Paper difference and vendor capability"
else
  echo "FAIL report contents"; fails=$((fails+1))
fi
cp "$report" "$work/last-report.md"

echo; [ "$fails" = 0 ] && echo "All passed" || echo "$fails failed"
exit "$fails"
