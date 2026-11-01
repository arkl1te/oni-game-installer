#!/bin/bash
# Installs Oni on Linux under Wine, optionally with Anniversary Edition and Arklite, set up like the
# CachyOS reference machine:
#
#   <prefix>                         64-bit Wine prefix (winetricks: corefonts msacm32)
#   <prefix>/drive_c/Program Files (x86)/Oni      Oni, installed by the setup on your disc
#   <prefix>/drive_c/Program Files (x86)/Oni/AE   Anniversary Edition (official setup, mods.oni2.net)
#                                                 and Arklite (latest GitHub release)
#   ~/.local/bin/oni-ae-game.sh      runs Oni.exe windowed (-noswitch -debugfiles) under Wine
#   ~/.local/bin/oni-ae-launcher.sh  runs the AE Installer on native Java 8 (OniSplit on native mono)
#   ~/.local/share/applications/oni-ae-{game,launcher}.desktop   "Oni (AE)", "Oni Anniversary Edition"
#   ~/.local/share/icons/hicolor/*/apps/oni-ae-{game,launcher}.png   taken from Oni.exe and AElogo.ico
#   ~/.local/state/oni-installer/install-<date>.log   what each run did; it ends with the AE Installer's log
#
#   ./install-oni.sh [--source PATH] [--prefix DIR] [--no-ae] [--no-arklite] [--yes]
#   ./install-oni.sh --uninstall [--prefix DIR] [--purge] [--yes]
#
# Both open a menu: arrow keys move, Enter acts. The options fill it in, and --yes skips it.
#
# --source is your own Oni disc: an .iso, or the mount point of the CD or of a mounted image. It must
# hold Oni's setup program (ONISETUP.EXE or SETUP.EXE) and the game data. On the retail disc the game
# data isn't a plain folder: GameDataFolder is packed in ONISETUP.EXE + ONISETUP.002, so the file list
# in the setup program is what's checked. This script contains no game files; Oni is never downloaded.
set -euo pipefail

AE_URL=http://mods.oni2.net/system/files/Anniversary-Edition-Setup-v1.4.exe
AE_SHA256=374bed1ea78c419328d413b4e4c5c1cfd3f8e1dd730c52bb0c22595f4f9e1743
ONI_SHA256=363cf09684ca6b729b62f22f90701d7ebf9a33778e55b191f5040bccdcdd013d   # the Oni.exe AE uses
ARKLITE_API=https://api.github.com/repos/arkl1te/arklite-oni-patch/releases/latest
ARCH_PKGS=(wine winetricks jre8-openjdk mono icoutils 7zip curl)
NO_SOURCE=/path/to/oni   # what the Source field shows when nothing was found

PREFIX="$HOME/Games/oni"
SOURCE="" YES=0 UNINSTALL=0 PURGE=0 WANT_AE=1 WANT_ARKLITE=1 WANT_SHORTCUTS=1
TMP="" IN_MENU=0
BIN="$HOME/.local/bin"
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
APPS="$DATA/applications"
ICONS="$DATA/icons/hicolor"
LOG_DIR="${XDG_STATE_HOME:-$HOME/.local/state}/oni-installer"
LOG="" LOG_PID="" ISSUES=0

die() { echo "error: $*" >&2; exit 1; }
say() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }
warn() { echo "warning: $*" >&2; ISSUES=$((ISSUES + 1)); }
ask() { [ "$YES" = 1 ] && return 0; local a; read -rp "$1 [Y/n] " a; [[ -z "$a" || "$a" =~ ^[Yy] ]]; }
usage() { sed -n '2,/^set -e/{/^set -e/d;s/^# \{0,1\}//;p}' "$0"; exit "${1:-0}"; }

ARGS="$*"
while [ $# -gt 0 ]; do
	case "$1" in
	--prefix)     PREFIX="${2:?}"; shift ;;
	--source)     SOURCE="${2:?}"; shift ;;
	--no-ae)      WANT_AE=0; WANT_ARKLITE=0 ;;
	--no-arklite) WANT_ARKLITE=0 ;;
	--yes|-y)     YES=1 ;;
	--uninstall)  UNINSTALL=1 ;;
	--purge)      PURGE=1 ;;
	-h|--help)    usage ;;
	*)            echo "unknown option: $1" >&2; usage 1 ;;
	esac
	shift
done

# --- Paths ----------------------------------------------------------------------------------------
expand_path() {   # ~ and relative paths to an absolute one
	local p="$1"
	case "$p" in "~") p="$HOME" ;; "~/"*) p="$HOME/${p#\~/}" ;; esac
	realpath -m -- "$p"
}
short_path() { case "$1" in "$HOME") echo "~" ;; "$HOME/"*) echo "~/${1#"$HOME"/}" ;; *) echo "$1" ;; esac; }
oni_dir() { echo "$1/drive_c/Program Files (x86)/Oni"; }
is_oni() { [ -f "$1/Oni.exe" ] && [ -f "$1/GameDataFolder/level0_Final.dat" ]; }
# AE/Oni.exe doesn't count: the AE Installer puts it there as soon as it opens. Only Install makes levels.
ae_done() { [ -f "$AE/GameDataFolder/level0_Final.dat" ]; }
# The last Install left out XML patches: a level0 with no XmlTools run in it plays differently, and
# multiplayer refuses it against every PC whose patches went in.
ae_patch_failed() { grep -qs 'Execution of external tool failed' "$AE/AEInstaller/Installation.log"; }
ae_ready() { ae_done && ! ae_patch_failed; }

# --- Is this an Oni disc? -------------------------------------------------------------------------
# $1 is an .iso or a mounted disc. Only ever call this as a condition.
source_ok() {
	local path list setup kind names
	path="$(expand_path "$1")"
	if [ -f "$path" ]; then
		[[ "${path,,}" == *.iso ]] || return 1
		kind=iso
		list="$(7z l -slt -ba "$path" 2>/dev/null | sed -n 's/^Path = //p')"
	elif [ -d "$path" ]; then
		case "$(findmnt -no FSTYPE -M "$path" 2>/dev/null)" in
		iso9660|udf|cd9660|fuse.*iso*) ;;
		*) return 1 ;;
		esac
		kind=disc
		list="$(find "$path" -mindepth 1 -printf '%P\n' 2>/dev/null)"
	else
		return 1
	fi
	setup="$(grep -iE '^(oni)?setup\.exe$' <<<"$list" | head -n 1)"
	[ -n "$setup" ] || return 1
	grep -qiE '(^|/)GameDataFolder/level0_Final\.dat$' <<<"$list" && return 0

	# The retail disc: the data is in <setup>.002, and the setup program lists what's inside.
	grep -qiFx "${setup%.*}.002" <<<"$list" || return 1
	if [ "$kind" = iso ]; then
		names="$(7z x -so "$path" "$setup" 2>/dev/null | LC_ALL=C grep -aoE 'GameDataFolder|level0_Final\.dat')"
	else
		names="$(LC_ALL=C grep -aoE 'GameDataFolder|level0_Final\.dat' "$path/$setup")"
	fi
	grep -qx GameDataFolder <<<"$names" && grep -qxF level0_Final.dat <<<"$names"
}
# The first usable .iso in the folder this script was started from, or next to the script.
detect_source() (
	shopt -s nullglob nocaseglob
	for iso in "$PWD"/*.iso "$(cd "$(dirname "$0")" && pwd)"/*.iso; do
		if source_ok "$iso"; then short_path "$iso"; exit 0; fi
	done
	echo "$NO_SOURCE"
)
# Can a game folder be made here?
target_ok() {
	local d
	d="$(expand_path "$1")"
	while [ ! -e "$d" ]; do d="$(dirname "$d")"; done
	[ -d "$d" ] && [ -w "$d" ]
}

# --- Menu -----------------------------------------------------------------------------------------
# Rows are "kind|label|variable". Kinds: path (Enter edits it), check (Enter flips it), start (Enter
# leaves the menu). It draws on the terminal's alternate screen, so the install log starts clean.
OK='\033[32m' BAD='\033[1;91m' SEL='\033[7m' END='\033[0m'
MENU_ROWS=() MENU_POS=0 MENU_MSG="" MENU_FOOTER="" SOURCE_STATUS=0 TARGET_STATUS=0

menu_close() {
	[ "$IN_MENU" = 1 ] || return 0
	IN_MENU=0
	stty echo 2>/dev/null || true
	printf '\033[?25h\033[?1049l'
}
status_text() { if [ "$1" = 1 ]; then printf "$OK(OK)$END"; else printf "$BAD(Oni not found)$END"; fi; }
menu_status() {   # variable
	case "$1" in
	SOURCE) status_text "$SOURCE_STATUS" ;;
	PREFIX) [ "$UNINSTALL" = 0 ] || status_text "$TARGET_STATUS" ;;
	esac
}
menu_changed() {   # a value changed; variable
	case "$1" in
	SOURCE) SOURCE_STATUS=0; if source_ok "$SOURCE"; then SOURCE_STATUS=1; fi ;;
	PREFIX) TARGET_STATUS=0; if is_oni "$(oni_dir "$(expand_path "$PREFIX")")"; then TARGET_STATUS=1; fi ;;
	WANT_AE) [ "$WANT_AE" = 1 ] || WANT_ARKLITE=0 ;;       # Arklite goes into AE's folder
	WANT_ARKLITE) [ "$WANT_ARKLITE" = 0 ] || WANT_AE=1 ;;
	esac
}
menu_ready() {   # may Start go ahead? if not: sets the message and the row to look at
	if [ "$UNINSTALL" = 0 ]; then
		if [ "$SOURCE_STATUS" != 1 ]; then
			MENU_MSG="Source must be an Oni .iso or a mounted Oni CD."; MENU_POS=0; return 1
		fi
		if ! target_ok "$PREFIX"; then
			MENU_MSG="Target can't be created or written to."; MENU_POS=1; return 1
		fi
	elif [ "$PURGE" = 1 ] && [ ! -f "$(expand_path "$PREFIX")/system.reg" ]; then
		MENU_MSG="Target isn't a Wine prefix, so it won't be deleted."; MENU_POS=0; return 1
	fi
}
menu_draw() {
	local i kind label var text status mark
	printf '\033[H\033[J\n'
	for i in "${!MENU_ROWS[@]}"; do
		IFS='|' read -r kind label var <<<"${MENU_ROWS[$i]}"
		status=""
		case "$kind" in
		path)  text=":·: $label    ${!var}"; status="$(menu_status "$var")" ;;
		check) mark=" "; if [ "${!var}" = 1 ]; then mark=X; fi; text=":·: [$mark] $label" ;;
		start) text="·: $label :·" ;;
		esac
		printf '  '
		if [ "$i" = "$MENU_POS" ]; then printf "$SEL"; fi
		printf '%s' "$text"
		printf "$END"
		[ -z "$status" ] || printf ' %s' "$status"
		printf '\n'
	done
	printf '\n%s\n' "$MENU_FOOTER"
	[ -z "$MENU_MSG" ] || printf "\n$BAD%s$END\n" "$MENU_MSG"
}
menu_edit() {   # row label variable; Tab completes paths
	local value start="${!3}"
	[ "$start" != "$NO_SOURCE" ] || start=""
	printf '\033[%d;1H\033[K\033[?25h' "$(($1 + 2))"
	stty echo 2>/dev/null || true
	# No -r: filename completion escapes spaces with backslashes, and read takes them out again.
	IFS= read -e -i "$start" -p "  :·: $2    " value || value="${!3}"
	stty -echo 2>/dev/null || true
	printf '\033[?25l'
	value="${value%"${value##*[![:space:]]}"}"
	case "$value" in \'*\'|\"*\") value="${value:1:${#value}-2}" ;; esac   # dropped from a file manager
	[ -z "$value" ] || printf -v "$3" '%s' "$value"
}
menu_run() {
	local key rest kind label var count=${#MENU_ROWS[@]}
	IN_MENU=1
	printf '\033[?1049h\033[?25l'
	stty -echo 2>/dev/null || true
	while :; do
		menu_draw
		IFS= read -rsn1 key || { menu_close; die "no input"; }
		if [ "$key" = $'\033' ]; then
			rest=""
			IFS= read -rsn2 -t 0.05 rest || true
			key+="$rest"
		fi
		MENU_MSG=""
		case "$key" in
		$'\033[A'|$'\033OA') MENU_POS=$(((MENU_POS + count - 1) % count)) ;;
		$'\033[B'|$'\033OB') MENU_POS=$(((MENU_POS + 1) % count)) ;;
		"")
			IFS='|' read -r kind label var <<<"${MENU_ROWS[$MENU_POS]}"
			case "$kind" in
			path)  menu_edit "$MENU_POS" "$label" "$var"; menu_changed "$var" ;;
			check) printf -v "$var" '%s' "$((1 - ${!var}))"; menu_changed "$var" ;;
			start) if menu_ready; then break; fi ;;
			esac ;;
		esac
	done
	menu_close
}
need_terminal() { [ -t 0 ] && [ -t 1 ] || die "the menu needs a terminal; use --yes to run without it"; }

# --- Log ------------------------------------------------------------------------------------------
# Starts after the menu, which needs the real terminal. Everything printed from here on goes to both.
start_log() {
	mkdir -p "$LOG_DIR"
	LOG="$LOG_DIR/install-$(date +%Y%m%d-%H%M%S).log"
	{
		echo "Oni installer log, $(date)"
		echo "command: $0 $ARGS"
		echo "system:  $(uname -sr), session ${XDG_SESSION_TYPE:-unknown}"
		echo "wine:    $("$WINE" --version)"
		echo "java:    $JAVA"
		echo "mono:    $(mono --version | head -n 1)"
		echo "source:  $SOURCE"
		echo "prefix:  $PREFIX"
		echo "options: ae=$WANT_AE arklite=$WANT_ARKLITE yes=$YES"
		echo "packages missing at the start: ${missing[*]:-none}"
	} >"$LOG"
	exec 3>&1 4>&2
	# -i: CTRL+C must reach this script's trap and leave tee alive to log it. sed drops colors and the
	# \r redraws of curl's progress bar.
	exec > >(tee -i >(sed -u 's/\x1b\[[0-9;?]*[A-Za-z]//g; s/\r$//; s/^.*\r//' >>"$LOG")) 2>&1
	LOG_PID=$!
	trap 'echo "failed at line $LINENO: $BASH_COMMAND" >&2' ERR
	echo "Log: $LOG"
}
stop_log() {
	[ -n "$LOG_PID" ] || return 0
	local i
	exec 1>&3 2>&4 3>&- 4>&-
	# Not a plain wait: a wineserver still shutting down can hold the pipe open for a few seconds.
	for i in {1..30}; do kill -0 "$LOG_PID" 2>/dev/null || break; sleep 0.1; done
	LOG_PID=""
	echo "Log: $LOG"
}

cleanup() { menu_close; [ -z "$TMP" ] || rm -rf -- "${TMP:?}"; stop_log; }
trap cleanup EXIT
trap 'menu_close; echo "Aborted."; exit 130' INT TERM

refresh_menu() {
	command -v update-desktop-database >/dev/null && update-desktop-database -q "$APPS" || true
	command -v kbuildsycoca6 >/dev/null && kbuildsycoca6 >/dev/null 2>&1 || true
	command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -qtf "$ICONS" 2>/dev/null || true
}

if [ "$UNINSTALL" = 1 ]; then
	PREFIX="$(short_path "$(expand_path "$PREFIX")")"
	if [ "$YES" = 0 ]; then
		need_terminal
		MENU_ROWS=("path|Target:|PREFIX" "check|Menu entries and launch scripts|WANT_SHORTCUTS"
		           "check|Game, mods and saves|PURGE" "start|Uninstall")
		MENU_FOOTER="Press CTRL+C to abort uninstallation."
		menu_changed PREFIX
		menu_run
	fi
	PREFIX="$(expand_path "$PREFIX")"
	export WINEPREFIX="$PREFIX" WINEDEBUG=-all
	if [ "$WANT_SHORTCUTS" = 1 ]; then
		say "Removing shortcuts and launch scripts"
		rm -fv "${BIN:?}/oni-ae-game.sh" "${BIN:?}/oni-ae-launcher.sh" \
		       "${APPS:?}/oni-ae-game.desktop" "${APPS:?}/oni-ae-launcher.desktop" \
		       "${ICONS:?}"/*/apps/oni-ae-game.png "${ICONS:?}"/*/apps/oni-ae-launcher.png
		refresh_menu
	fi
	if [ "$PURGE" = 1 ]; then
		[ -f "$PREFIX/system.reg" ] || die "$PREFIX isn't a Wine prefix; nothing deleted"
		echo "This deletes $PREFIX: Oni, AE, mods and your saves (AE/persist.dat)."
		if ask "Delete it?"; then
			wineserver -k 2>/dev/null || true
			rm -rf -- "${PREFIX:?}" "${LOG_DIR:?}"
			echo "deleted"
		else
			echo "Kept the game in $PREFIX."
		fi
	else
		echo "Kept the game in $PREFIX."
	fi
	exit 0
fi

# --- 1. Packages ----------------------------------------------------------------------------------
find_java8() {
	local j
	for j in /usr/lib/jvm/java-8-openjdk/jre/bin/java /usr/lib/jvm/java-8-*/jre/bin/java \
	         /usr/lib/jvm/java-1.8.0*/jre/bin/java /usr/lib/jvm/*-8-*/bin/java; do
		[ -x "$j" ] && { echo "$j"; return; }
	done
}

say "Checking packages"
missing=()
for c in wine winetricks mono wrestool icotool 7z curl; do command -v "$c" >/dev/null || missing+=("$c"); done
[ -n "$(find_java8)" ] || missing+=("java 8")
if [ ${#missing[@]} -gt 0 ]; then
	echo "missing: ${missing[*]}"
	if command -v pacman >/dev/null; then
		ask "Install them with: sudo pacman -S --needed ${ARCH_PKGS[*]} ?" || die "needed: ${missing[*]}"
		sudo pacman -S --needed "${ARCH_PKGS[@]}"
	else
		die "install them with your package manager (Wine with 32-bit support, winetricks, OpenJDK 8 JRE with GUI, mono, icoutils, 7-Zip, curl), then run this again"
	fi
fi
JAVA="$(find_java8)"
[ -n "$JAVA" ] || die "no Java 8 found under /usr/lib/jvm"
WINE="$(command -v wine)"
echo "wine: $("$WINE" --version)   java: $JAVA"

# --- 2. Menu --------------------------------------------------------------------------------------
[ -n "$SOURCE" ] || SOURCE="$(detect_source)"
PREFIX="$(short_path "$(expand_path "$PREFIX")")"
menu_changed SOURCE
if [ "$YES" = 0 ]; then
	need_terminal
	MENU_ROWS=("path|Source:|SOURCE" "path|Target:|PREFIX" "check|Anniversary Edition|WANT_AE"
	           "check|Arklite patch|WANT_ARKLITE" "start|Start")
	MENU_FOOTER="Press CTRL+C to abort installation."
	menu_run
else
	[ "$SOURCE_STATUS" = 1 ] || die "$SOURCE isn't an Oni .iso or a mounted Oni CD"
	target_ok "$PREFIX" || die "$PREFIX can't be created or written to"
fi
PREFIX="$(expand_path "$PREFIX")"
SOURCE="$(expand_path "$SOURCE")"
ONI="$(oni_dir "$PREFIX")"
AE="$ONI/AE"
export WINEPREFIX="$PREFIX" WINEDEBUG=-all
start_log

# --- 3. Wine prefix -------------------------------------------------------------------------------
if [ ! -f "$PREFIX/system.reg" ]; then
	say "Creating the Wine prefix $PREFIX"
	mkdir -p "$PREFIX"
	# No wine-mono/gecko prompts: nothing here needs .NET (OniSplit runs on native mono) or a browser.
	WINEARCH=win64 WINEDLLOVERRIDES="mscoree,mshtml=" "$WINE" wineboot -i >/dev/null 2>&1
	wineserver -w
else
	echo "Using the existing prefix $PREFIX"
fi
if ! grep -qx msacm32 "$PREFIX/winetricks.log" 2>/dev/null; then
	say "winetricks corefonts msacm32 (a few minutes)"
	winetricks -q corefonts msacm32
fi

# --- 4. Oni ---------------------------------------------------------------------------------------
TMP="$(mktemp -d "$PREFIX/.installer.XXXXXX")"   # on disk: the image doesn't fit a small /tmp

if is_oni "$ONI"; then
	echo "Oni is already installed in $ONI"
else
	disc="$SOURCE"
	if [ -f "$SOURCE" ]; then
		say "Extracting $SOURCE"
		7z x -y -o"$TMP/cd" "$SOURCE" >/dev/null || die "7-Zip couldn't read $SOURCE"
		disc="$TMP/cd"
	fi
	setup="$(find "$disc" -maxdepth 1 -type f \( -iname onisetup.exe -o -iname setup.exe \) -print -quit)"
	[ -n "$setup" ] || die "$SOURCE has no setup program"
	say "Running Oni's setup"
	echo "Oni's setup opens now. Keep the folder C:\\Program Files (x86)\\Oni and click Next to the end."
	echo "Answer No to GLSetup, registering and the desktop shortcut. When it opens a folder window"
	echo "at the end, close that window to continue."
	(cd "$disc" && "$WINE" "$(basename "$setup")") || true
	wineserver -w
	is_oni "$ONI" || die "Oni isn't in $ONI; install it there (C:\\Program Files (x86)\\Oni) and run this again"
fi
[ "$(sha256sum "$ONI/Oni.exe" | cut -d' ' -f1)" = "$ONI_SHA256" ] \
	|| echo "note: Oni.exe isn't the usual retail build; AE installs its own AE/Oni.exe, so this is fine"

# --- 5. Anniversary Edition -----------------------------------------------------------------------
JAR="$AE/AEInstaller/bin/AEInstaller2.jar"
if [ "$WANT_AE" = 0 ]; then
	echo "Skipping Anniversary Edition"
elif [ -f "$JAR" ]; then
	echo "Anniversary Edition is already in $AE"
else
	say "Downloading Anniversary Edition"
	curl -fL --progress-bar -o "$TMP/ae-setup.exe" "$AE_URL" || die "download failed: $AE_URL"
	[ "$(sha256sum "$TMP/ae-setup.exe" | cut -d' ' -f1)" = "$AE_SHA256" ] \
		|| die "the AE setup doesn't match the expected checksum (was it updated? see https://wiki.oni2.net/Anniversary_Edition)"
	say "Installing Anniversary Edition"
	echo "A small window, \"AEInstaller2 self updater\", opens when the files are in."
	echo "When it says AEI is up to date, click \"Close and launch AEI\"."
	# No Start Menu icons: they'd run AE's bundled Windows Java under Wine; we run it natively instead.
	"$WINE" "$TMP/ae-setup.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS \
		/DIR="$("$WINE" winepath -w "$ONI" 2>/dev/null)" || true
	wineserver -k 2>/dev/null || true   # in case it started the AE Installer under Wine
	if [ ! -f "$JAR" ]; then   # set up into Oni/AE/AE? move it up
		found="$(find "$PREFIX/drive_c" -path '*/AEInstaller/bin/AEInstaller2.jar' -print -quit)"
		[ -n "$found" ] || die "the AE setup didn't install the AE Installer; run it by hand: wine \"$TMP/ae-setup.exe\""
		src="$(dirname "$(dirname "$(dirname "$found")")")"
		mkdir -p "$AE"; cp -a "$src/." "$AE/"; rm -rf -- "$src"
	fi
fi

# --- 6. Launch scripts, icons, menu entries -------------------------------------------------------
say "Creating the launch scripts and menu entries"
mkdir -p "$BIN" "$APPS"
if [ "$WANT_AE" = 1 ]; then GAME_DIR="$AE" GAME_NAME="Oni (AE)"; else GAME_DIR="$ONI" GAME_NAME="Oni"; fi
cat > "$BIN/oni-ae-game.sh" <<EOF
#!/bin/bash
cd $(printf %q "$GAME_DIR") || exit 1
export WINEPREFIX=$(printf %q "$PREFIX")
exec $(printf %q "$WINE") Oni.exe -noswitch -debugfiles
EOF
chmod +x "$BIN/oni-ae-game.sh"
if [ "$WANT_AE" = 1 ]; then
	cat > "$BIN/oni-ae-launcher.sh" <<EOF
#!/bin/bash
cd $(printf %q "$ONI")
export WINEPREFIX=$(printf %q "$PREFIX")
exec $(printf %q "$JAVA") \\
    -jar "AE/AEInstaller/bin/AEInstaller2.jar"
EOF
	chmod +x "$BIN/oni-ae-launcher.sh"
	# On Linux the AE Installer runs Tools/XmlTools/XmlTools, but XmlTools only comes for Windows and
	# Mac, and without it Install leaves out every XML patch and says nothing. From /, so Wine is on Z:
	# and the Unix paths the AE Installer passes resolve.
	mkdir -p "$AE/Tools/XmlTools"
	cat > "$AE/Tools/XmlTools/XmlTools" <<EOF
#!/bin/bash
cd /
export WINEPREFIX=$(printf %q "$PREFIX") WINEDEBUG=-all
exec $(printf %q "$WINE") $(printf %q "$AE/Tools/XmlTools/XmlTools.exe") "\$@"
EOF
	chmod +x "$AE/Tools/XmlTools/XmlTools"
fi

# Every size in an .ico/.exe goes to hicolor/<size>x<size>/apps/<name>.png
install_icons() {   # name ico
	local png size
	rm -rf "$TMP/ico"; mkdir -p "$TMP/ico"
	icotool -x -o "$TMP/ico" "$2" 2>/dev/null || return 0
	for png in "$TMP/ico"/*.png; do
		[ -f "$png" ] || continue
		size="$(file -b "$png" | grep -o '[0-9]* x' | head -1 | tr -dc 0-9)"
		[ -n "$size" ] && install -Dm644 "$png" "$ICONS/${size}x${size}/apps/$1.png"
	done
}
wrestool -x -t 14 "$ONI/Oni.exe" -o "$TMP/oni.ico" 2>/dev/null && install_icons oni-ae-game "$TMP/oni.ico"
[ "$WANT_AE" = 0 ] || install_icons oni-ae-launcher "$AE/AEInstaller/AElogo.ico"

cat > "$APPS/oni-ae-game.desktop" <<EOF
[Desktop Entry]
Version=1.0
Name=$GAME_NAME
Comment=Launch Oni in windowed mode
Exec="$BIN/oni-ae-game.sh"
Icon=oni-ae-game
Terminal=false
Type=Application
Categories=Game;
StartupWMClass=oni.exe
EOF
if [ "$WANT_AE" = 1 ]; then
	cat > "$APPS/oni-ae-launcher.desktop" <<EOF
[Desktop Entry]
Version=1.0
Name=Oni Anniversary Edition
Comment=Oni AE mod manager and launcher
Exec="$BIN/oni-ae-launcher.sh"
Icon=oni-ae-launcher
Terminal=false
Type=Application
Categories=Game;
EOF
fi
refresh_menu

# --- 7. First run of the AE Installer -------------------------------------------------------------
if [ "$WANT_AE" = 1 ]; then
	while ! ae_ready; do
		say "Setting up Anniversary Edition"
		if ae_done; then
			echo "The last Install in this AE left out its XML patches, so level0 is wrong and multiplayer"
			echo "refuses it. The AE Installer opens now: click Install, wait for it to finish, then close it."
		else
			echo "The AE Installer opens now. The first time it prepares your levels (a few minutes)."
			echo "Then pick the mods you want and click Install, even with no mods picked: that's what builds"
			echo "the game's levels. Wait for it to finish, then close it."
		fi
		"$BIN/oni-ae-launcher.sh" || true
		ae_ready && break
		if ae_patch_failed; then
			echo "The AE Installer's Install left out XML patches again; see AE/AEInstaller/Installation.log."
		else
			echo "The AE Installer closed without installing, so AE has no levels and Oni can't start yet."
		fi
		[ "$YES" = 0 ] && ask "Open it again?" || break
	done
	ae_done || warn "AE isn't installed yet; open \"Oni Anniversary Edition\" from the menu and click Install"
	if ae_done && ae_patch_failed; then
		warn "the AE Installer's last Install had errors, so multiplayer won't work; see AE/AEInstaller/Installation.log"
	fi
fi

# --- 8. Arklite -----------------------------------------------------------------------------------
if [ "$WANT_ARKLITE" = 1 ]; then
	if ! ae_done; then
		warn "skipped Arklite, because AE isn't installed yet; run this again once it is"
	else
		say "Installing Arklite (latest release)"
		release="$(curl -fsSL "$ARKLITE_API")" || die "couldn't reach GitHub: $ARKLITE_API"
		url="$(grep -o 'https://[^"]*/releases/download/[^"]*\.zip' <<<"$release" | head -n 1)" || true
		[ -n "$url" ] || die "the latest Arklite release has no .zip"
		curl -fL --progress-bar -o "$TMP/arklite.zip" "$url" || die "download failed: $url"
		7z x -y -o"$TMP/arklite" "$TMP/arklite.zip" >/dev/null || die "7-Zip couldn't read $url"
		script="$(find "$TMP/arklite" -name install.sh -print -quit)"
		[ -n "$script" ] || die "the Arklite release has no install.sh"
		bash "$script" "$AE"
	fi
fi

if [ "$WANT_AE" = 1 ] && [ -f "$AE/AEInstaller/Installation.log" ]; then
	{ printf '\n--- AE Installer: Installation.log ---\n'; cat "$AE/AEInstaller/Installation.log"; } >>"$LOG"
fi

if [ "$ISSUES" != 0 ]; then
	say "Finished, but the game isn't ready: see the warnings above"
	echo "Game: $ONI"
	exit 1
fi
say "Done"
if [ "$WANT_AE" = 1 ]; then
	echo "Menu: \"Oni (AE)\" plays, \"Oni Anniversary Edition\" manages mods."
else
	echo "Menu: \"Oni\" plays."
fi
echo "Game: $ONI"
