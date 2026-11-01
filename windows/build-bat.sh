#!/bin/bash
# Joins script/header.bat and script/OniInstaller.cs into build/Install-Oni.bat.
# Needs mono's mcs, only to check that the C# compiles in the language version Windows 10 has.
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build

if LC_ALL=C grep -nP '[^\x00-\x7F]' script/header.bat script/OniInstaller.cs; then
	echo "These lines aren't ASCII: cmd and PowerShell 5.1 would misread them" >&2
	exit 1
fi

mcs -langversion:5 -warnaserror -target:library -out:build/check.dll script/OniInstaller.cs
rm -f build/check.dll

# cmd needs CRLF line endings to find its way through a batch file reliably.
cat script/header.bat script/OniInstaller.cs | sed 's/\r$//; s/$/\r/' > build/Install-Oni.bat
ls -l build/Install-Oni.bat
