#!/bin/sh
# Sets up Epson's Linux driver (Epson Scan 2) for the RR-70W on Arch, so Receipt Scanner can scan
# through SANE. Run as your own user; it asks for sudo where it has to.
#
# Two fixes on top of the AUR package, both needed on Arch and both kept inside Epson's own folder:
# - Epson's code loads its modules from the Debian path /usr/lib/x86_64-linux-gnu/epsonscan2, which
#   epsonscan2-bin moves to /usr/lib/epsonscan2. A link puts them back where the code looks.
# - Its image decoder wants libjbig.so.0, and Arch's jbigkit names it libjbig.so.2.2. Without a link,
#   long receipts scan but then fail with a misleading "Out of memory".
set -eu

lib=/usr/lib/epsonscan2
if [ ! -d "$lib" ]; then
  echo "Installing epsonscan2-bin from the AUR..."
  yay -S --needed epsonscan2-bin jbigkit
fi

[ -d "$lib" ] || { echo "Epson Scan 2 isn't in $lib. Install epsonscan2-bin, then run this again."; exit 1; }
[ -e /usr/lib/libjbig.so.2.2 ] || { echo "libjbig.so.2.2 is missing. Install jbigkit, then run this again."; exit 1; }

if [ ! -e /usr/lib/x86_64-linux-gnu/epsonscan2 ]; then
  sudo mkdir -p /usr/lib/x86_64-linux-gnu
  sudo ln -s "$lib" /usr/lib/x86_64-linux-gnu/epsonscan2
fi

[ -e "$lib/libjbig.so.0" ] || sudo ln -s /usr/lib/libjbig.so.2.2 "$lib/libjbig.so.0"

echo "Looking for scanners (this takes a few seconds)..."
if scanimage -L 2>/dev/null | grep -q "epsonscan2:.*RR-70W"; then
  echo "Ready: Receipt Scanner will find the RR-70W."
else
  echo "Epson's driver is installed but didn't find an RR-70W. Check it's on and connected by USB."
  exit 1
fi
