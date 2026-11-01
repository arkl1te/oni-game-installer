# Oni installer

Installs Oni from your own disc, then adds the Anniversary Edition (optional) and the Arklite patch (optional, requires AE). 

Linux: the game runs under Wine.


| System | Run |
| --- | --- |
| Windows 10 | `windows/build/Install-Oni.bat` |
| Linux | `install-oni.sh` |

## What you need

This installer doesn't include or download the game. 

| | Windows | Linux |
| --- | --- | --- |
| Game | Your own copy of Oni for Windows: an `.iso` of the disc, the CD, or an `.iso` mounted as a CD drive | Same as Windows |
| Internet connection | Only for the Anniversary Edition (mods.oni2.net) and Arklite (GitHub) | Always on a first install, for the Wine components and any missing packages. Also for the Anniversary Edition and Arklite |
| Disk space | About 630 MB for Oni. About 900 MB more with the Anniversary Edition, plus any mods | Same as Windows, plus about 700 MB for Wine and about 450 MB while installing |

### Windows

The installer is a single text file and needs nothing else. You don't need admin rights for Oni itself, though the Anniversary Edition's setup may ask for them.

### Linux

On Arch, CachyOS, Manjaro and similar systems, the installer offers to install the packages it needs. On other systems, install these yourself first:

* Wine (with 32-bit support)
* winetricks
* OpenJDK 8 (the full JRE, not headless only)
* mono
* icoutils
* 7-Zip
* curl

## Install

1. Usage:

   * **Windows**: put `Install-Oni.bat` next to your `.iso` and double-click it. You can also drop your `.iso` onto the bat file.
   
   * **Linux**: put `install-oni.sh` next to your `.iso` and run it in a terminal:

     ```bash
     chmod +x install-oni.sh
     ./install-oni.sh
     ```

2. The menu:

   ```
     :·: Source:    ~/Games/Oni.iso (OK)
     :·: Target:    ~/Games/oni
     :·: [X] Anniversary Edition
     :·: [X] Arklite patch
     ·: Start :·

   Press CTRL+C to abort installation.
   ```

   Move with the arrow keys and press ENTER to act on the highlighted line:

   * **Source**: points to the game's installing files. If the path to the `.iso` file isn't seen here, you can press ENTER and paste its path, or change it to your CD: a drive letter (such as `D:` on Windows) or a mount point (`/run/media/you/ONI` on Linux). Press TAB to autocomplete. 
     The line shows **(OK)** in green, or **(Oni not found)** in red.
   * **Target** is where the game will install to play.
   * **Anniversary Edition** and **Arklite patch** can be turned off. Arklite needs the Anniversary Edition, so turning one on or off can change the other.
   * **Start** installs. It won't start until Source shows **(OK)**.

   On Windows, you can also drop your `.iso` on the window while editing Source, and ESC cancels the edit.

3. Installing:

   * **Windows**: the files are copied. Every file is checked against the disc, so a damaged image stops with an error. If this fails or you press CTRL+C, the files it wrote are removed.
   * **Linux**: Oni's own setup opens. Keep its folder, click **Next** to the end, and answer **No** to GLSetup, registering and the desktop shortcut. When it opens a folder window at the end, close that window so the installer can continue.

4. If you kept the Anniversary Edition:

   1. A small "AEInstaller2 self updater" window opens. When it says AEI is up to date, click **Close and launch AEI**.
   2. The first time, the AE Installer spends a few minutes preparing your levels. Pick the mods you want, click **Install**, wait for it to finish and close it.

   The latest Arklite release is then downloaded and installed.

If the installer stops partway through, run it again. It skips the steps that are already done.

### Windows will "protect you"

Windows warns about any file downloaded from the internet that isn't suscribed to Microsoft's codesign. 

If "Windows protected your PC" appears, choose **More info**, then **Run anyway**. If you're still in doubt, you can check the script itself (the whole installer is readable: open the `.bat` file in Notepad to see what it does) or ask an AI to do it for you.

To avoid the warning, right-click `Install-Oni.bat` before running it, choose **Properties**, tick **Unblock**, then choose **OK**.

### Which folder to choose

The Target defaults to:
* Windows: `C:\Games\Oni` 
* Linux: `~/Games/oni`

On Windows, avoid `Program Files`: Oni keeps its saves and settings in its own folder, and Windows *might* block those.

## Uninstall

* **Windows**: remove Oni from **Settings > Apps**, or use **Uninstall Oni** in the Start menu.
* **Linux**: run this:

  ```bash
  ./install-oni.sh --uninstall
  ```

The same kind of menu opens. What it removes by default differs:

| | Ticked by default | Tick this to delete everything |
| --- | --- | --- |
| Windows | **Oni and its shortcuts**. Your saves, settings, mods and the Anniversary Edition stay in the Oni folder. | **Mods, saves and settings** |
| Linux | **Menu entries and launch scripts**. The whole game stays. | **Game, mods and saves** |

The installer asks once more before deleting everything.

## Optional - command line

To skip the menu:

### Windows
```
Install-Oni.bat /S /iso PATH /dir PATH [/desktop] [/ae] [/arklite]
Install-Oni.bat /S /uninstall [/purge]
```

Without `/ae` or `/arklite` it installs Oni only, and `/arklite` includes `/ae`.

### Linux

```bash
./install-oni.sh --yes --source PATH [--prefix DIR] [--no-ae] [--no-arklite]
./install-oni.sh --uninstall --yes [--prefix DIR] [--purge]
```
The Anniversary Edition and Arklite are included unless you turn them off.

The Anniversary Edition still opens its own windows, so someone needs to be at the computer. On Linux, Oni's setup does too.

## Where things are

| What | Windows | Linux |
| --- | --- | --- |
| Game | `C:\Games\Oni` | `~/Games/oni/drive_c/Program Files (x86)/Oni` |
| Saves and settings | `…\Oni\AE\persist.dat`, `…\Oni\AE\daodan.ini` | `…/Oni/AE/persist.dat`, `…/Oni/AE/daodan.ini` |
| Launch scripts | | `~/.local/bin/oni-ae-game.sh`, `~/.local/bin/oni-ae-launcher.sh` |
| Menu entries | Start menu, in the **Oni** folder | `~/.local/share/applications/oni-ae-game.desktop`, `oni-ae-launcher.desktop` |

## Building

The Linux installer doesn't need building.

### Windows

You need mono's `mcs`, which only checks that the code compiles.

```bash
windows/build-bat.sh
```

This joins `windows/script/header.bat` and `windows/script/OniInstaller.cs` into `windows/build/Install-Oni.bat`. The disc's setup format is described at the top of `windows/script/OniInstaller.cs`.
