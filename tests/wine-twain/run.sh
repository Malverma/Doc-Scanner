#!/bin/bash
# Runs the published worker against a fake TWAIN source under Wine, in a throwaway prefix.
# Proves the worker's TWAIN code paths work. Says nothing about Brother's real driver.
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
check "second page ignored: one receipt per file" 0 "one('complete')['Height']==H and data==pat(1)"

scan clamp
check "clamped frame reported, not hidden" 0 "one('longPaper')['FrameHeightInches']==14 and one('complete')['Height']==H"

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
