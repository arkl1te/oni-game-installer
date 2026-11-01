//ONI-CS-BEGIN
// Installs Oni for this user from your disc, without running the disc's setup, then optionally the
// Anniversary Edition (official setup, mods.oni2.net) and Arklite (latest GitHub release). It's a
// console menu: arrow keys move, Enter acts.
// C# 5 and .NET 4 only: this is compiled by Windows PowerShell 5.1, which every Windows 10 has.
//
// The disc's ONISETUP.EXE is a MindVision Installer VISE stub, and the game is in ONISETUP.002:
//  - The last 8 bytes of ONISETUP.EXE are "ESIV" + the offset of the appended directory.
//  - Records there are separated by 32 0xFF bytes and a 0x00. A record is a u16 name length, the
//    name, then a folder or a file:
//      folder: u32 0, u32 attributes (0x1x), 10 zero bytes, ...
//      file:   u32 0, u32 size, u32 packed size, u32 part, u32 offset in part, u32 attributes,
//              3 FILETIMEs, u16 DOS date, u16 DOS time, 21 bytes, u32 CRC-32 of the unpacked file
//  - File data is raw deflate with every two bytes swapped, counted from the start of the file.
//  - Part 1 is ONISETUP.EXE itself, part n is ONISETUP.00n.
// The records' folder links don't give a usable tree, so destinations follow Oni's fixed layout.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

public class OniError : Exception {
	public OniError(string message) : base(message) { }
}

class OniCancelled : Exception { }

class OniAborted : Exception { }

class OniFile {
	public string Path;
	public uint Usize, Csize, Part, Offset, Crc;
	public int DosDate, DosTime;
}

class Span {
	public string File;
	public long Base, Size;
}

// The packed bytes of one file, with each pair put back in order.
class SwapStream : Stream {
	readonly Stream f;
	readonly byte[] buf = new byte[1 << 20]; // even, so pairs never straddle two reads
	long left;
	int pos, len;

	public SwapStream(Stream file, long size) {
		f = file;
		left = size;
	}

	public override int Read(byte[] b, int off, int count) {
		if (pos == len) {
			if (left == 0) return 0;
			int want = (int)Math.Min(left, buf.Length), got = 0;
			while (got < want) {
				int n = f.Read(buf, got, want - got);
				if (n == 0) throw new OniError("The disc image couldn't be read.");
				got += n;
			}
			left -= got;
			for (int i = 0; i + 1 < got; i += 2) {
				byte t = buf[i];
				buf[i] = buf[i + 1];
				buf[i + 1] = t;
			}
			pos = 0;
			len = got;
		}
		int take = Math.Min(count, len - pos);
		Buffer.BlockCopy(buf, pos, b, off, take);
		pos += take;
		return take;
	}

	public override bool CanRead { get { return true; } }
	public override bool CanSeek { get { return false; } }
	public override bool CanWrite { get { return false; } }
	public override long Length { get { throw new NotSupportedException(); } }
	public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
	public override void Flush() { }
	public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); }
	public override void SetLength(long v) { throw new NotSupportedException(); }
	public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
}

class Disc {
	const int Sector = 2048;
	const long MaxSetupExe = 32L << 20;

	readonly string iso, folder;
	byte[] root;
	public readonly List<OniFile> Files = new List<OniFile>();
	public long TotalBytes;

	static uint Rd32(byte[] d, int p) { return (uint)(d[p] | d[p + 1] << 8 | d[p + 2] << 16 | d[p + 3] << 24); }
	static int Rd16(byte[] d, int p) { return d[p] | d[p + 1] << 8; }

	static byte[] ReadAt(string file, long off, int size) {
		using (FileStream f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)) {
			if (off + size > f.Length) return null;
			f.Position = off;
			byte[] b = new byte[size];
			int got = 0;
			while (got < size) {
				int n = f.Read(b, got, size - got);
				if (n == 0) return null;
				got += n;
			}
			return b;
		}
	}

	public Disc(string path) {
		if (Directory.Exists(path)) { // the disc in a CD drive, or an image Windows mounted
			folder = path;
		} else if (!File.Exists(path)) {
			throw new OniError("This file can't be opened.");
		} else {
			iso = path;
			LoadRoot();
		}
		Span exe = OpenPart(1);
		if (exe == null) throw new OniError("This isn't the Oni disc for Windows: it has no ONISETUP.EXE.");
		if (OpenPart(2) == null) throw new OniError("This disc doesn't contain Oni: it has no ONISETUP.002.");
		const string bad = "The disc's setup program isn't in the expected format.";
		if (exe.Size <= 16 || exe.Size >= MaxSetupExe) throw new OniError(bad);
		byte[] tail = ReadAt(exe.File, exe.Base + exe.Size - 8, 8);
		if (tail == null || Encoding.ASCII.GetString(tail, 0, 4) != "ESIV") throw new OniError(bad);
		uint off = Rd32(tail, 4);
		if (off >= exe.Size - 16) throw new OniError(bad);
		byte[] data = ReadAt(exe.File, exe.Base + off, (int)(exe.Size - off));
		if (data == null || Encoding.ASCII.GetString(data, 0, 4) != "ESIV") throw new OniError(bad);
		Scan(data);
		if (!Has("Oni.exe") || !Has("GameDataFolder\\level0_Final.dat")) throw new OniError("This disc doesn't contain Oni.");
	}

	void LoadRoot() {
		const string notIso = "This isn't a CD image in .iso format.";
		byte[] pvd;
		try {
			pvd = ReadAt(iso, 16 * Sector, Sector);
		} catch (IOException) {
			throw new OniError("This file can't be opened.");
		}
		if (pvd == null || pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001") throw new OniError(notIso);
		uint extent = Rd32(pvd, 158), bytes = Rd32(pvd, 166);
		if (bytes == 0 || bytes > (1u << 20)) throw new OniError(notIso);
		root = ReadAt(iso, extent * (long)Sector, (int)bytes);
		if (root == null) throw new OniError(notIso);
	}

	// Finds a file in the ISO's root directory; names there end in ";1" and are upper case.
	Span IsoFind(string name) {
		int p = 0, n = name.Length;
		while (p + 34 <= root.Length) {
			if (root[p] == 0) { // records don't cross sectors; the rest of this one is padding
				p = (p / Sector + 1) * Sector;
				continue;
			}
			int nl = root[p + 32];
			if (p + 33 + nl > root.Length) return null;
			bool match = (root[p + 25] & 2) == 0 && nl >= n && (nl == n || root[p + 33 + n] == ';');
			if (match && string.Compare(Encoding.ASCII.GetString(root, p + 33, n), name, StringComparison.OrdinalIgnoreCase) == 0) {
				Span s = new Span();
				s.File = iso;
				s.Base = Rd32(root, p + 2) * (long)Sector;
				s.Size = Rd32(root, p + 10);
				return s;
			}
			p += root[p];
		}
		return null;
	}

	Span OpenPart(uint part) {
		if (part > 99) return null;
		string name = part == 1 ? "ONISETUP.EXE" : "ONISETUP.0" + part.ToString("00");
		if (iso != null) return IsoFind(name);
		string p = Path.Combine(folder, name);
		if (!File.Exists(p)) return null;
		Span s = new Span();
		s.File = p;
		s.Size = new FileInfo(p).Length;
		return s;
	}

	// Names become part of a path, so a damaged or hostile image must not be able to climb out of
	// the install folder with them.
	static bool SafeName(byte[] d, int at, int n) {
		if (n == 0 || n > 64 || d[at] == '.') return false;
		for (int i = 0; i < n; i++) {
			int c = d[at + i];
			if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == ' ' || c == '.' || c == '\'')) return false;
		}
		return true;
	}

	static string Place(string name, string igmd) {
		string low = name.ToLowerInvariant();
		if (low.EndsWith(".bsl")) return "GameDataFolder\\IGMD\\" + igmd + "\\" + name;
		if (low.EndsWith(".bik") || low.StartsWith("level")) return "GameDataFolder\\" + name;
		return name;
	}

	void Scan(byte[] d) {
		string igmd = "";
		int run = 0;
		for (int p = 0; p + 8 < d.Length; p++) {
			if (d[p] == 0xff) {
				run++;
				continue;
			}
			bool sep = run >= 32 && d[p] == 0;
			run = 0;
			if (!sep) continue;
			int at = p + 1, n = Rd16(d, at), q = at + 2 + n;
			if (q + 0x60 > d.Length || !SafeName(d, at + 2, n) || Rd32(d, q) != 0) continue;
			string name = Encoding.ASCII.GetString(d, at + 2, n);
			uint attr = Rd32(d, q + 4);
			if ((attr & ~0x37u) == 0 && (attr & 0x10) != 0) {
				bool zero = true;
				for (int i = 0; i < 10; i++) zero &= d[q + 8 + i] == 0;
				if (zero) {
					igmd = name;
					continue;
				}
			}
			uint usize = attr, csize = Rd32(d, q + 8), part = Rd32(d, q + 12);
			uint hi = Rd32(d, q + 36); // high half of a FILETIME; 2000-ish for a real file
			if (part < 1 || part > 99 || hi < 0x01b00000 || hi > 0x01d00000) continue;
			if (usize > (1u << 30) || csize > (1u << 30) || Files.Count == 4096) continue;
			OniFile f = new OniFile();
			f.Usize = usize;
			f.Csize = csize;
			f.Part = part;
			f.Offset = Rd32(d, q + 16);
			f.DosDate = Rd16(d, q + 48);
			f.DosTime = Rd16(d, q + 50);
			f.Crc = Rd32(d, q + 73);
			f.Path = Place(name, igmd);
			Files.Add(f);
			TotalBytes += usize;
		}
	}

	bool Has(string path) {
		foreach (OniFile f in Files)
			if (string.Compare(f.Path, path, StringComparison.OrdinalIgnoreCase) == 0) return true;
		return false;
	}

	static readonly uint[] CrcTable = MakeCrcTable();

	static uint[] MakeCrcTable() {
		uint[] t = new uint[256];
		for (uint i = 0; i < 256; i++) {
			uint c = i;
			for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
			t[i] = c;
		}
		return t;
	}

	public void Extract(OniFile f, string partPath) {
		Span s = OpenPart(f.Part);
		if (s == null) throw new OniError("The disc is missing part of the setup data.");
		const string damaged = "A file failed its checksum: the disc image is damaged.";
		byte[] buf = new byte[1 << 20];
		uint crc = 0xffffffffu;
		long written = 0;
		using (FileStream input = new FileStream(s.File, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
		using (FileStream output = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan)) {
			input.Position = s.Base + f.Offset;
			DeflateStream z = new DeflateStream(new SwapStream(input, f.Csize), CompressionMode.Decompress);
			for (;;) {
				int n;
				try {
					n = z.Read(buf, 0, buf.Length);
				} catch (InvalidDataException) {
					throw new OniError(damaged);
				}
				if (n == 0) break;
				for (int i = 0; i < n; i++) crc = CrcTable[(crc ^ buf[i]) & 0xff] ^ (crc >> 8);
				written += n;
				try {
					output.Write(buf, 0, n);
				} catch (IOException) {
					throw new OniError("The file couldn't be written. Is the drive full?");
				}
				Installer.Tick(n);
			}
		}
		if (written != f.Usize || ~crc != f.Crc) throw new OniError(damaged);
	}
}

public class Options {
	public string Source, Dir;
	public bool StartMenu = true, Desktop, Ae, Arklite;
	public bool Resume; // keep an Oni that's already in Dir, so a stopped install can carry on
}

[ComImport, Guid("00021401-0000-0000-C000-000000000046")]
class ShellLink { }

// Only the setters are called, but every slot has to be declared to keep them in place.
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
interface IShellLinkW {
	void GetPath();
	void GetIDList();
	void SetIDList();
	void GetDescription();
	void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
	void GetWorkingDirectory();
	void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
	void GetArguments();
	void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
	void GetHotkey();
	void SetHotkey();
	void GetShowCmd();
	void SetShowCmd();
	void GetIconLocation();
	void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
	void SetRelativePath();
	void Resolve();
	void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
}

static class Installer {
	const string UninstallKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Oni";
	const string BungieKey = "Software\\Bungie\\Oni";
	public const string Uninstaller = "Uninstall Oni.bat";
	const string FileList = "uninstall_files.txt";

	public static volatile bool CancelRequested;
	public static volatile bool ShortcutFailed;
	public static bool ShowProgress;
	public static long DoneBytes, TotalBytes;
	static int shownPercent = -1;

	public static void Tick(int bytes) {
		DoneBytes += bytes;
		if (CancelRequested) throw new OniCancelled();
		if (!ShowProgress || TotalBytes == 0) return;
		int percent = (int)(DoneBytes * 100 / TotalBytes);
		if (percent == shownPercent) return;
		shownPercent = percent;
		Console.Write("\r  " + percent + "%  ");
	}

	static void MakeWritable(string p) {
		if (File.Exists(p)) File.SetAttributes(p, FileAttributes.Normal);
	}

	static void TryDelete(string p) {
		try {
			MakeWritable(p);
			File.Delete(p);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	static void TryRemoveDir(string p) {
		try {
			Directory.Delete(p, false);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	static void RemoveEmptyParents(string file, string dir) {
		for (string p = Path.GetDirectoryName(file); p != null && p.Length > dir.Length; p = Path.GetDirectoryName(p)) {
			if (!Directory.Exists(p) || Directory.GetFileSystemEntries(p).Length != 0) return;
			TryRemoveDir(p);
		}
	}

	static void SetTime(string path, OniFile f) {
		int d = f.DosDate, t = f.DosTime;
		try {
			DateTime local = new DateTime((d >> 9) + 1980, (d >> 5) & 15, d & 31, t >> 11, (t >> 5) & 63, (t & 31) * 2, DateTimeKind.Local);
			File.SetLastWriteTimeUtc(path, local.ToUniversalTime());
		} catch (ArgumentException) { // the disc's date isn't a real date; keep today's
		}
	}

	static void ExtractOne(Disc d, OniFile f, string dir) {
		string path = Path.Combine(dir, f.Path), part = path + ".part";
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		try {
			d.Extract(f, part);
			SetTime(part, f);
			// The setup marks several files read-only. That would stop the Anniversary Edition and
			// other mods from replacing them, so files stay writable.
			if (File.Exists(path)) {
				File.SetAttributes(path, FileAttributes.Normal);
				File.Delete(path);
			}
			File.Move(part, path);
		} catch (UnauthorizedAccessException) {
			throw new OniError("A file couldn't be replaced. Is Oni running?");
		} catch (IOException) {
			throw new OniError("A file couldn't be written. Is Oni running, or is the drive full?");
		} finally {
			TryDelete(part);
		}
	}

	static void CheckSpace(string dir, long need) {
		try {
			DriveInfo drive = new DriveInfo(Path.GetPathRoot(dir));
			long free = drive.AvailableFreeSpace;
			if (free >= need + (16L << 20)) return;
			throw new OniError("This needs " + ((need >> 20) + 16) + " MB of free space, and the drive has " + (free >> 20) + " MB.");
		} catch (ArgumentException) { // a network path
		} catch (IOException) {
		}
	}

	static void MakeShortcut(string lnk, string target, string args, string workdir, string description, string icon) {
		IShellLinkW link = (IShellLinkW)new ShellLink();
		link.SetPath(target);
		if (args != null) link.SetArguments(args);
		link.SetWorkingDirectory(workdir);
		link.SetDescription(description);
		if (icon != null) link.SetIconLocation(icon, 0);
		((IPersistFile)link).Save(lnk, true);
	}

	static void TryShortcut(List<string> list, string lnk, string target, string args, string workdir, string description, string icon) {
		try {
			MakeShortcut(lnk, target, args, workdir, description, icon);
			list.Add(lnk);
		} catch (Exception) { // the game works without it, so this only changes the closing message
			ShortcutFailed = true;
		}
	}

	static void WriteRegistry(string dir, long bytes) {
		using (RegistryKey k = Registry.CurrentUser.CreateSubKey(BungieKey)) k.SetValue("Path", dir);
		using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey)) {
			string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
			k.SetValue("DisplayName", "Oni");
			k.SetValue("DisplayVersion", "1.0");
			k.SetValue("Publisher", "Bungie West");
			k.SetValue("InstallLocation", dir);
			k.SetValue("DisplayIcon", Path.Combine(dir, "Oni.exe"));
			k.SetValue("UninstallString", "\"" + cmd + "\" /c \"\"" + Path.Combine(dir, Uninstaller) + "\" /uninstall\"");
			k.SetValue("NoModify", 1, RegistryValueKind.DWord);
			k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
			k.SetValue("EstimatedSize", (int)(bytes >> 10), RegistryValueKind.DWord);
		}
	}

	public static void Install(Options o, string self) {
		if (!Sources.SourceOk(o.Source)) throw new OniError(o.Source + " isn't an Oni .iso or a mounted Oni CD.");
		if (o.Resume && File.Exists(Path.Combine(o.Dir, "Oni.exe")) && File.Exists(Path.Combine(o.Dir, FileList))) {
			Console.WriteLine("Oni is already installed in " + o.Dir);
			return;
		}
		Disc d;
		try {
			d = new Disc(Sources.Full(o.Source));
		} catch (UnauthorizedAccessException) {
			throw new OniError("This file can't be opened.");
		} catch (IOException) {
			throw new OniError("This file can't be opened.");
		}
		TotalBytes = d.TotalBytes;
		CheckSpace(o.Dir, d.TotalBytes);
		bool fresh = !File.Exists(Path.Combine(o.Dir, "Oni.exe"));
		try {
			Directory.CreateDirectory(o.Dir);
		} catch (UnauthorizedAccessException) {
			throw new OniError("Windows doesn't allow creating this folder without admin rights:\n" + o.Dir + "\n\nChoose a folder outside Program Files.");
		} catch (IOException) {
			throw new OniError("This folder can't be created:\n" + o.Dir);
		}
		int done = 0;
		try {
			for (; done < d.Files.Count; done++) {
				ExtractOne(d, d.Files[done], o.Dir);
			}
		} catch (Exception) {
			if (fresh) {
				for (int i = 0; i < done; i++) {
					string p = Path.Combine(o.Dir, d.Files[i].Path);
					TryDelete(p);
					RemoveEmptyParents(p, o.Dir);
				}
				foreach (OniFile f in d.Files) RemoveEmptyParents(Path.Combine(o.Dir, f.Path), o.Dir);
				TryRemoveDir(o.Dir);
			}
			throw;
		}
		if (ShowProgress) Console.WriteLine();

		List<string> list = new List<string>();
		foreach (OniFile f in d.Files) list.Add(f.Path);
		string uninstaller = Path.Combine(o.Dir, Uninstaller);
		string exe = Path.Combine(o.Dir, "Oni.exe"), readme = Path.Combine(o.Dir, "readme.txt");
		try {
			MakeWritable(uninstaller);
			File.Copy(self, uninstaller, true);
		} catch (Exception) {
			throw new OniError("The uninstaller couldn't be written to the install folder.");
		}
		if (o.StartMenu) {
			string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Oni");
			Directory.CreateDirectory(folder);
			TryShortcut(list, Path.Combine(folder, "Oni.lnk"), exe, null, o.Dir, "Play Oni", exe);
			TryShortcut(list, Path.Combine(folder, "Read Me.lnk"), readme, null, o.Dir, "Oni read me", null);
			TryShortcut(list, Path.Combine(folder, "Uninstall Oni.lnk"), uninstaller, "/uninstall", o.Dir, "Remove Oni", null);
		}
		if (o.Desktop) {
			string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
			TryShortcut(list, Path.Combine(desk, "Oni.lnk"), exe, null, o.Dir, "Play Oni", exe);
		}
		WriteRegistry(o.Dir, d.TotalBytes);
		string listPath = Path.Combine(o.Dir, FileList);
		MakeWritable(listPath);
		File.WriteAllLines(listPath, list.ToArray(), Encoding.Unicode);
	}

	static bool IsLink(string line) {
		return line.Length > 2 && line[1] == ':' && line.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
	}

	public static bool IsInstalled(string dir) {
		return File.Exists(Path.Combine(dir, FileList));
	}

	// Later steps add their shortcuts here, so the uninstaller finds them too.
	static void AddToList(string dir, List<string> added) {
		string listPath = Path.Combine(dir, FileList);
		List<string> list = new List<string>(File.ReadAllLines(listPath));
		foreach (string line in added) if (!list.Contains(line)) list.Add(line);
		File.WriteAllLines(listPath, list.ToArray(), Encoding.Unicode);
	}

	public static void AddAeShortcuts(Options o, string ae) {
		string aei = Path.Combine(ae, "AEInstaller");
		string game = Path.Combine(ae, "Oni.exe"), javaw = Path.Combine(aei, "JRE\\bin\\javaw.exe");
		string logo = Path.Combine(aei, "AElogo.ico");
		List<string> list = new List<string>();
		if (o.StartMenu) {
			string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Oni");
			Directory.CreateDirectory(folder);
			TryShortcut(list, Path.Combine(folder, "Oni (AE).lnk"), game, null, ae, "Play Oni with the Anniversary Edition", Path.Combine(o.Dir, "Oni.exe"));
			TryShortcut(list, Path.Combine(folder, "Oni Anniversary Edition.lnk"), javaw, Extras.AeiArguments, aei, "Oni AE mod manager and launcher", logo);
		}
		if (o.Desktop) {
			string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
			TryShortcut(list, Path.Combine(desk, "Oni.lnk"), game, null, ae, "Play Oni with the Anniversary Edition", Path.Combine(o.Dir, "Oni.exe"));
		}
		AddToList(o.Dir, list);
	}

	// Removes only what the list names, so saves and mods stay. Returns a note if the folder stays.
	public static string Uninstall(string dir) {
		string listPath = Path.Combine(dir, FileList);
		if (!File.Exists(listPath)) throw new OniError("The list of installed files is missing, so nothing was removed.");
		foreach (string line in File.ReadAllLines(listPath)) {
			if (line.Length == 0) continue;
			if (IsLink(line)) {
				TryDelete(line);
				string folder = Path.GetDirectoryName(line);
				if (folder.EndsWith("\\Oni") && Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length == 0) TryRemoveDir(folder);
			} else if (!Path.IsPathRooted(line) && line.IndexOf("..") < 0) {
				string p = Path.Combine(dir, line);
				TryDelete(p);
				RemoveEmptyParents(p, dir);
			}
		}
		TryDelete(listPath);
		Registry.CurrentUser.DeleteSubKey(UninstallKey, false);
		Registry.CurrentUser.DeleteSubKey(BungieKey, false);
		try {
			Registry.CurrentUser.DeleteSubKey("Software\\Bungie", false);
		} catch (InvalidOperationException) { // it holds something else, so it stays
		}
		string[] rest = Directory.GetFileSystemEntries(dir);
		bool onlyMe = rest.Length == 1 && string.Compare(Path.GetFileName(rest[0]), Uninstaller, StringComparison.OrdinalIgnoreCase) == 0;
		return rest.Length == 0 || onlyMe ? "" : "The folder still holds your saves, settings and any mods, so it was kept:\n" + dir;
	}

	// Empties the folder. The running uninstaller stays: the .bat header removes it and the folder.
	public static void DeleteEverythingElse(string dir, string self) {
		foreach (string sub in Directory.GetDirectories(dir)) Directory.Delete(sub, true);
		foreach (string file in Directory.GetFiles(dir))
			if (string.Compare(file, self, StringComparison.OrdinalIgnoreCase) != 0) {
				MakeWritable(file);
				File.Delete(file);
			}
	}
}

// What Source and Target may be.
static class Sources {
	public static string Cwd = Environment.CurrentDirectory;

	public static string Clean(string text) {
		return Environment.ExpandEnvironmentVariables(text.Trim().Trim('"').Trim());
	}

	public static string Full(string text) {
		string p = Clean(text);
		if (p.Length == 2 && p[1] == ':') p += "\\"; // D: means the drive, not its current folder
		return Path.GetFullPath(Path.Combine(Cwd, p));
	}

	static bool IsCdRoot(string p) {
		string root = Path.GetPathRoot(p);
		if (string.Compare(p.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) != 0) return false;
		return new DriveInfo(root).DriveType == DriveType.CDRom;
	}

	// Only an .iso, or a CD drive (Windows mounts images as one). Disc checks for the setup program
	// and for the game data: on the retail disc those are packed in ONISETUP.EXE + ONISETUP.002.
	public static bool SourceOk(string text) {
		try {
			string p = Full(text);
			if (File.Exists(p)) {
				if (!p.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) return false;
			} else if (!Directory.Exists(p) || !IsCdRoot(p)) {
				return false;
			}
			new Disc(p);
			return true;
		} catch (Exception) {
			return false;
		}
	}

	// The first usable .iso in the folder the installer was started from, or next to the installer.
	public static string Detect(string fallback, string self) {
		foreach (string dir in new string[] { Cwd, Path.GetDirectoryName(self) }) {
			try {
				string[] isos = Directory.GetFiles(dir, "*.iso");
				Array.Sort(isos, StringComparer.OrdinalIgnoreCase);
				foreach (string iso in isos)
					if (SourceOk(iso)) return iso;
			} catch (Exception) {
			}
		}
		return fallback;
	}

	// Can the game folder be made here? Asks the drive by trying, since permissions are hard to read.
	public static bool TargetOk(string text) {
		try {
			string d = Full(text);
			while (!Directory.Exists(d)) {
				if (File.Exists(d)) return false;
				d = Path.GetDirectoryName(d);
				if (d == null) return false;
			}
			string probe = Path.Combine(d, "oni-write-test-" + Guid.NewGuid().ToString("N"));
			File.WriteAllBytes(probe, new byte[0]);
			File.Delete(probe);
			return true;
		} catch (Exception) {
			return false;
		}
	}

	public static string Complete(string text) {
		try {
			int cut = text.LastIndexOfAny(new char[] { '\\', '/' }) + 1;
			string head = text.Substring(0, cut), start = text.Substring(cut);
			string dir = Path.Combine(Cwd, head.Length == 0 ? "." : head);
			string best = null;
			foreach (string entry in Directory.GetFileSystemEntries(dir)) {
				string name = Path.GetFileName(entry);
				if (!name.StartsWith(start, StringComparison.OrdinalIgnoreCase)) continue;
				if (Directory.Exists(entry)) name += "\\";
				if (best == null) {
					best = name;
					continue;
				}
				int same = 0;
				while (same < best.Length && same < name.Length && char.ToLowerInvariant(best[same]) == char.ToLowerInvariant(name[same])) same++;
				best = best.Substring(0, same);
			}
			return best == null ? text : head + best;
		} catch (Exception) {
			return text;
		}
	}
}

// Anniversary Edition and Arklite, both downloaded.
static class Extras {
	const string AeUrl = "http://mods.oni2.net/system/files/Anniversary-Edition-Setup-v1.4.exe";
	const string AeSha256 = "374bed1ea78c419328d413b4e4c5c1cfd3f8e1dd730c52bb0c22595f4f9e1743";
	const string ArkliteApi = "https://api.github.com/repos/arkl1te/arklite-oni-patch/releases/latest";
	const string UserAgent = "Oni-Installer";
	public const string AeiArguments = "-Dsun.java2d.d3d=false -jar bin\\AEInstaller2.jar";

	static void Say(string text) {
		Console.WriteLine();
		Console.WriteLine("==> " + text);
	}

	static HttpWebResponse Open(string url) {
		HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
		r.UserAgent = UserAgent;
		return (HttpWebResponse)r.GetResponse();
	}

	static void Download(string url, string path) {
		try {
			using (HttpWebResponse response = Open(url))
			using (Stream input = response.GetResponseStream())
			using (FileStream output = new FileStream(path, FileMode.Create, FileAccess.Write)) {
				Installer.DoneBytes = 0;
				Installer.TotalBytes = Math.Max(0, response.ContentLength);
				byte[] buf = new byte[1 << 16];
				int n;
				while ((n = input.Read(buf, 0, buf.Length)) > 0) {
					output.Write(buf, 0, n);
					Installer.Tick(n);
				}
			}
			if (Installer.ShowProgress) Console.WriteLine();
		} catch (WebException e) {
			throw new OniError("The download failed: " + url + "\n" + e.Message);
		} catch (IOException e) {
			throw new OniError("The download failed: " + url + "\n" + e.Message);
		}
	}

	static string Sha256Of(string path) {
		using (SHA256 sha = SHA256.Create())
		using (FileStream f = File.OpenRead(path))
			return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
	}

	static void Wait(Process p) {
		while (!p.WaitForExit(200)) {
			if (!Installer.CancelRequested) continue;
			try {
				p.Kill();
			} catch (Exception) {
			}
			throw new OniCancelled();
		}
	}

	static bool AeiRunning(string ae) {
		foreach (Process p in Process.GetProcessesByName("javaw")) {
			try {
				if (p.MainModule.FileName.StartsWith(ae, StringComparison.OrdinalIgnoreCase)) return true;
			} catch (Exception) { // it can't be read, so it could be ours
				return true;
			}
		}
		return false;
	}

	public static void InstallAe(Options o) {
		string ae = Path.Combine(o.Dir, "AE");
		string aei = Path.Combine(ae, "AEInstaller");
		if (File.Exists(Path.Combine(aei, "bin\\AEInstaller2.jar"))) {
			Console.WriteLine("Anniversary Edition is already in " + ae);
		} else {
			Say("Downloading Anniversary Edition");
			string setup = Path.Combine(Path.GetTempPath(), "oni-ae-setup-" + Guid.NewGuid().ToString("N") + ".exe");
			try {
				Download(AeUrl, setup);
				if (Sha256Of(setup) != AeSha256) throw new OniError("The AE setup doesn't match the expected checksum. Was it updated? See https://wiki.oni2.net/Anniversary_Edition");
				Say("Installing Anniversary Edition");
				Console.WriteLine("A small window, \"AEInstaller2 self updater\", opens when the files are in.");
				Console.WriteLine("When it says AEI is up to date, click \"Close and launch AEI\".");
				ProcessStartInfo info = new ProcessStartInfo(setup, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS /DIR=\"" + o.Dir.TrimEnd('\\') + "\"");
				info.UseShellExecute = true; // it may ask for admin rights
				using (Process p = Process.Start(info)) Wait(p);
			} catch (System.ComponentModel.Win32Exception) { // said no to the admin prompt
				throw new OniError("The AE setup didn't start.");
			} finally {
				try {
					File.Delete(setup);
				} catch (Exception) {
				}
			}
			if (!File.Exists(Path.Combine(aei, "bin\\AEInstaller2.jar"))) throw new OniError("The AE setup didn't install the AE Installer.");
		}
		Installer.AddAeShortcuts(o, ae);
		if (File.Exists(Path.Combine(ae, "Oni.exe"))) return;

		Say("Setting up Anniversary Edition");
		Console.WriteLine("The AE Installer prepares your levels the first time (a few minutes). Then pick the");
		Console.WriteLine("mods you want, click Install, wait for it to finish and close it.");
		for (int i = 0; i < 30 && !AeiRunning(ae); i++) { // the updater starts it a moment after closing
			Thread.Sleep(100);
			Installer.Tick(0);
		}
		if (!AeiRunning(ae)) {
			ProcessStartInfo info = new ProcessStartInfo(Path.Combine(aei, "JRE\\bin\\javaw.exe"), AeiArguments);
			info.WorkingDirectory = aei;
			info.UseShellExecute = false;
			Process.Start(info).Dispose();
			Thread.Sleep(1000);
		}
		while (AeiRunning(ae)) {
			Thread.Sleep(500);
			Installer.Tick(0);
		}
		if (!File.Exists(Path.Combine(ae, "Oni.exe")))
			Console.WriteLine("warning: AE isn't installed yet (no AE\\Oni.exe). Open \"Oni Anniversary Edition\" from the Start menu and click Install.");
	}

	static uint Rd32(byte[] d, int p) { return (uint)(d[p] | d[p + 1] << 8 | d[p + 2] << 16 | d[p + 3] << 24); }
	static int Rd16(byte[] d, int p) { return d[p] | d[p + 1] << 8; }

	// ZipArchive would need one more reference when PowerShell compiles this; a release zip is simple.
	static void Unzip(string zip, string dir) {
		const string bad = "The Arklite release isn't a zip this installer can read.";
		byte[] d = File.ReadAllBytes(zip);
		int end = d.Length - 22;
		while (end >= 0 && Rd32(d, end) != 0x06054b50) end--;
		if (end < 0) throw new OniError(bad);
		int count = Rd16(d, end + 10), p = (int)Rd32(d, end + 16);
		string root = Path.GetFullPath(dir) + "\\";
		for (int i = 0; i < count; i++) {
			if (p < 0 || p + 46 > d.Length || Rd32(d, p) != 0x02014b50) throw new OniError(bad);
			int method = Rd16(d, p + 10), packed = (int)Rd32(d, p + 20), head = (int)Rd32(d, p + 42);
			int nameLength = Rd16(d, p + 28);
			string name = Encoding.UTF8.GetString(d, p + 46, nameLength);
			p += 46 + nameLength + Rd16(d, p + 30) + Rd16(d, p + 32);
			if (name.EndsWith("/")) continue;
			string path = Path.GetFullPath(Path.Combine(root, name.Replace('/', '\\')));
			if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new OniError(bad);
			if (head < 0 || head + 30 > d.Length) throw new OniError(bad);
			int data = head + 30 + Rd16(d, head + 26) + Rd16(d, head + 28);
			if (data + packed > d.Length || data + packed < 0) throw new OniError(bad);
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			using (FileStream output = new FileStream(path, FileMode.Create, FileAccess.Write))
			using (MemoryStream input = new MemoryStream(d, data, packed)) {
				if (method == 0) {
					input.CopyTo(output);
				} else if (method == 8) {
					using (DeflateStream z = new DeflateStream(input, CompressionMode.Decompress)) z.CopyTo(output);
				} else {
					throw new OniError(bad);
				}
			}
		}
	}

	public static void InstallArklite(Options o) {
		string ae = Path.Combine(o.Dir, "AE");
		if (!File.Exists(Path.Combine(ae, "Oni.exe"))) {
			Console.WriteLine("warning: skipped Arklite, because AE isn't installed yet. Run this again once it is.");
			return;
		}
		Say("Installing Arklite (latest release)");
		string json;
		try {
			using (HttpWebResponse response = Open(ArkliteApi))
			using (StreamReader r = new StreamReader(response.GetResponseStream()))
				json = r.ReadToEnd();
		} catch (WebException e) {
			throw new OniError("GitHub couldn't be reached: " + e.Message);
		}
		Match url = Regex.Match(json, "https://[^\"]*/releases/download/[^\"]*\\.zip");
		if (!url.Success) throw new OniError("The latest Arklite release has no .zip.");
		string work = Path.Combine(Path.GetTempPath(), "oni-arklite-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(work);
		try {
			string zip = Path.Combine(work, "arklite.zip");
			Download(url.Value, zip);
			Unzip(zip, Path.Combine(work, "files"));
			string[] found = Directory.GetFiles(Path.Combine(work, "files"), "install.bat", SearchOption.AllDirectories);
			if (found.Length == 0) throw new OniError("The Arklite release has no install.bat.");
			ProcessStartInfo info = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec"), "/c \"\"" + found[0] + "\" \"" + ae + "\"\"");
			info.UseShellExecute = false;
			using (Process p = Process.Start(info)) Wait(p);
			FileInfo wanted = new FileInfo(Path.Combine(Path.GetDirectoryName(found[0]), "Arklite.dll"));
			FileInfo got = new FileInfo(Path.Combine(ae, "Arklite.dll"));
			if (!got.Exists || got.Length != wanted.Length) throw new OniError("Arklite wasn't installed. Is Oni running?");
		} finally {
			try {
				Directory.Delete(work, true);
			} catch (Exception) {
			}
		}
	}
}

class MenuRow {
	public char Kind; // p: a path, Enter edits it. c: a checkbox, Enter flips it. s: Enter leaves the menu.
	public string Label, Text = "";
	public bool On;

	public MenuRow(char kind, string label, string text, bool on) {
		Kind = kind;
		Label = label;
		Text = text;
		On = on;
	}
}

class Menu {
	const string Dot = "\u00b7";
	public readonly List<MenuRow> Rows = new List<MenuRow>();
	public string Footer = "", Message;
	public int Pos;
	public Action<MenuRow> Changed = delegate { };
	public Func<MenuRow, int> Status = delegate { return 0; }; // 1 (OK), -1 (Oni not found), 0 nothing
	public Func<bool> Ready = delegate { return true; };       // may set Message and Pos

	string RowText(MenuRow r, string edit) {
		if (r.Kind == 'p') return PathHead(r) + (edit ?? r.Text);
		if (r.Kind == 'c') return ":" + Dot + ": [" + (r.On ? "X" : " ") + "] " + r.Label;
		return Dot + ": " + r.Label + " :" + Dot;
	}

	static string PathHead(MenuRow r) {
		return ":" + Dot + ": " + r.Label + "    ";
	}

	void Draw(MenuRow editing, string edit, int caret) {
		Console.CursorVisible = false;
		Console.ResetColor();
		Console.Clear();
		Console.WriteLine();
		int top = 0;
		for (int i = 0; i < Rows.Count; i++) {
			MenuRow r = Rows[i];
			Console.Write("  ");
			if (r == editing) top = Console.CursorTop;
			if (i == Pos && editing == null) {
				ConsoleColor fore = Console.ForegroundColor;
				Console.ForegroundColor = Console.BackgroundColor;
				Console.BackgroundColor = fore;
			}
			Console.Write(RowText(r, r == editing ? edit : null));
			Console.ResetColor();
			int status = r == editing ? 0 : Status(r);
			if (status != 0) {
				Console.Write(" ");
				Console.ForegroundColor = status > 0 ? ConsoleColor.Green : ConsoleColor.Red;
				Console.Write(status > 0 ? "(OK)" : "(Oni not found)");
				Console.ResetColor();
			}
			Console.WriteLine();
		}
		Console.WriteLine();
		Console.WriteLine(Footer);
		if (Message != null) {
			Console.WriteLine();
			Console.ForegroundColor = ConsoleColor.Red;
			Console.WriteLine(Message);
			Console.ResetColor();
		}
		if (editing == null) return;
		int at = 2 + PathHead(editing).Length + caret, width = Console.BufferWidth;
		Console.SetCursorPosition(at % width, top + at / width);
		Console.CursorVisible = true;
	}

	static bool IsAbort(ConsoleKeyInfo k) {
		return k.KeyChar == '\u0003' || (k.Key == ConsoleKey.C && (k.Modifiers & ConsoleModifiers.Control) != 0);
	}

	public string Placeholder = ""; // a hint, not a path: editing starts without it

	void Edit(MenuRow r) {
		string v = r.Text == Placeholder ? "" : r.Text;
		int i = v.Length;
		for (;;) {
			Draw(r, v, i);
			do { // a paste or a dropped file arrives as many keys: draw once, after all of them
				ConsoleKeyInfo k = Console.ReadKey(true);
				if (IsAbort(k)) throw new OniAborted();
				switch (k.Key) {
				case ConsoleKey.Enter:
					string done = v.Trim().Trim('"').Trim();
					if (done.Length != 0) r.Text = done;
					return;
				case ConsoleKey.Escape: return;
				case ConsoleKey.LeftArrow: if (i > 0) i--; break;
				case ConsoleKey.RightArrow: if (i < v.Length) i++; break;
				case ConsoleKey.Home: i = 0; break;
				case ConsoleKey.End: i = v.Length; break;
				case ConsoleKey.Backspace:
					if (i > 0) {
						v = v.Remove(i - 1, 1);
						i--;
					}
					break;
				case ConsoleKey.Delete: if (i < v.Length) v = v.Remove(i, 1); break;
				case ConsoleKey.Tab:
					string done2 = Sources.Complete(v.Substring(0, i));
					v = done2 + v.Substring(i);
					i = done2.Length;
					break;
				default:
					if (!char.IsControl(k.KeyChar)) {
						v = v.Insert(i, k.KeyChar.ToString());
						i++;
					}
					break;
				}
			} while (Console.KeyAvailable);
		}
	}

	public void Run() {
		Console.TreatControlCAsInput = true;
		try {
			for (;;) {
				Draw(null, null, 0);
				ConsoleKeyInfo k = Console.ReadKey(true);
				if (IsAbort(k)) throw new OniAborted();
				Message = null;
				if (k.Key == ConsoleKey.UpArrow) Pos = (Pos + Rows.Count - 1) % Rows.Count;
				else if (k.Key == ConsoleKey.DownArrow) Pos = (Pos + 1) % Rows.Count;
				else if (k.Key == ConsoleKey.Enter) {
					MenuRow r = Rows[Pos];
					if (r.Kind == 'p') {
						Edit(r);
						Changed(r);
					} else if (r.Kind == 'c') {
						r.On = !r.On;
						Changed(r);
					} else if (Ready()) {
						return;
					}
				}
			}
		} finally {
			Console.TreatControlCAsInput = false;
			Console.ResetColor();
			Console.CursorVisible = true;
			Console.Clear();
		}
	}
}

public static class OniInstaller {
	const int Uninstalled = 77;
	const string NoSource = "C:\\path\\to\\oni";
	const string SilentUsage = "Usage: Install-Oni.bat /S /iso PATH /dir PATH [/desktop] [/ae] [/arklite]\n       Install-Oni.bat /S /uninstall [/purge]";

	static List<string> Args() {
		List<string> args = new List<string>();
		for (int i = 1; i <= 8; i++) {
			string a = Environment.GetEnvironmentVariable("ONI_A" + i);
			if (!string.IsNullOrEmpty(a)) args.Add(a.Trim('"'));
		}
		return args;
	}

	static void InstallAll(Options o, string self) {
		Installer.Install(o, self);
		if (o.Ae) Extras.InstallAe(o);
		if (o.Arklite) Extras.InstallArklite(o);
	}

	static int RunSilent(List<string> args, string self) {
		Options o = new Options();
		for (int i = 1; i < args.Count; i++) {
			string a = args[i].ToLowerInvariant();
			if (a == "/desktop") o.Desktop = true;
			else if (a == "/ae") o.Ae = true;
			else if (a == "/arklite") o.Ae = o.Arklite = true;
			else if (a == "/iso" && i + 1 < args.Count) o.Source = args[++i];
			else if (a == "/dir" && i + 1 < args.Count) o.Dir = args[++i];
			else {
				Console.Error.WriteLine(SilentUsage);
				return 2;
			}
		}
		if (o.Source == null || o.Dir == null) {
			Console.Error.WriteLine(SilentUsage);
			return 2;
		}
		try {
			o.Dir = Sources.Full(o.Dir);
			InstallAll(o, self);
			if (Installer.ShortcutFailed) Console.Error.WriteLine("Oni is installed, but its shortcuts couldn't be made.");
			return 0;
		} catch (Exception e) {
			Console.Error.WriteLine(e.Message);
			return 1;
		}
	}

	static void Fail(string message) {
		Console.ResetColor();
		Console.ForegroundColor = ConsoleColor.Red;
		Console.WriteLine();
		Console.WriteLine("error: " + message);
		Console.ResetColor();
	}

	static void PressAnyKey() {
		Console.WriteLine();
		Console.WriteLine("Press any key to close this window.");
		Console.ReadKey(true);
	}

	static int RunMenu(string self, string iso) {
		MenuRow source = new MenuRow('p', "Source:", iso.Length != 0 ? iso : Sources.Detect(NoSource, self), false);
		MenuRow target = new MenuRow('p', "Target:", Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "Games\\Oni"), false);
		MenuRow ae = new MenuRow('c', "Anniversary Edition", null, true);
		MenuRow arklite = new MenuRow('c', "Arklite patch", null, true);
		bool sourceOk = Sources.SourceOk(source.Text);
		Menu m = new Menu();
		m.Rows.AddRange(new MenuRow[] { source, target, ae, arklite, new MenuRow('s', "Start", null, false) });
		m.Footer = "Press CTRL+C to abort installation.";
		m.Placeholder = NoSource;
		m.Status = delegate(MenuRow r) { return r == source ? (sourceOk ? 1 : -1) : 0; };
		m.Changed = delegate(MenuRow r) {
			if (r == source) sourceOk = Sources.SourceOk(source.Text);
			if (r == ae && !ae.On) arklite.On = false; // Arklite goes into AE's folder
			if (r == arklite && arklite.On) ae.On = true;
		};
		m.Ready = delegate {
			if (!sourceOk) {
				m.Message = "Source must be an Oni .iso or a mounted Oni CD.";
				m.Pos = 0;
				return false;
			}
			if (!Sources.TargetOk(target.Text)) {
				m.Message = "Target can't be created or written to. Avoid Program Files.";
				m.Pos = 1;
				return false;
			}
			return true;
		};
		m.Run();

		Options o = new Options();
		o.Source = source.Text;
		o.Dir = Sources.Full(target.Text);
		o.Ae = ae.On;
		o.Arklite = arklite.On;
		o.Desktop = true;
		o.Resume = true;
		Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e) {
			e.Cancel = true;
			Installer.CancelRequested = true;
		};
		try {
			Console.WriteLine("==> Installing Oni");
			Installer.ShowProgress = true;
			InstallAll(o, self);
		} catch (OniCancelled) {
			Console.WriteLine();
			Console.WriteLine("Aborted.");
			return 0;
		} catch (Exception e) {
			Fail(e.Message);
			return 1;
		}
		Console.WriteLine();
		Console.WriteLine("==> Done");
		if (Installer.ShortcutFailed) Console.WriteLine("Oni is installed, but its shortcuts couldn't be made. Run Oni.exe in " + o.Dir);
		else Console.WriteLine(o.Ae ? "Start menu: \"Oni (AE)\" plays, \"Oni Anniversary Edition\" manages mods." : "Start menu: \"Oni\" plays.");
		Console.WriteLine("Game: " + o.Dir);
		PressAnyKey();
		return 0;
	}

	static string DefaultTarget(string self) {
		string here = Path.GetDirectoryName(self);
		if (Installer.IsInstalled(here)) return here;
		using (RegistryKey k = Registry.CurrentUser.OpenSubKey("Software\\Bungie\\Oni")) {
			string path = k == null ? null : k.GetValue("Path") as string;
			if (path != null) return path;
		}
		return Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "Games\\Oni");
	}

	static int RunUninstallMenu(string self) {
		MenuRow target = new MenuRow('p', "Target:", DefaultTarget(self), false);
		MenuRow game = new MenuRow('c', "Oni and its shortcuts", null, true);
		MenuRow mods = new MenuRow('c', "Mods, saves and settings", null, false);
		Func<bool> found = delegate {
			try {
				return Installer.IsInstalled(Sources.Full(target.Text));
			} catch (Exception) {
				return false;
			}
		};
		Menu m = new Menu();
		m.Rows.AddRange(new MenuRow[] { target, game, mods, new MenuRow('s', "Uninstall", null, false) });
		m.Footer = "Press CTRL+C to abort uninstallation.";
		m.Status = delegate(MenuRow r) { return r == target ? (found() ? 1 : -1) : 0; };
		m.Changed = delegate(MenuRow r) {
			if (r == game && !game.On) mods.On = false; // the list of Oni's files goes with the game
			if (r == mods && mods.On) game.On = true;
		};
		m.Ready = delegate {
			if (found()) return true;
			m.Message = "Target isn't a folder this installer put Oni in, so nothing would be removed.";
			m.Pos = 0;
			return false;
		};
		m.Run();
		if (!game.On) {
			Console.WriteLine("Nothing was removed.");
			return 0;
		}
		string dir = Sources.Full(target.Text);
		try {
			if (mods.On) {
				Console.WriteLine("This deletes " + dir + ": Oni, AE, mods and your saves.");
				Console.Write("Delete it? [y/N] ");
				if (!Console.ReadLine().Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase)) {
					Console.WriteLine("Nothing was removed.");
					return 0;
				}
			}
			string note = Installer.Uninstall(dir);
			if (mods.On) {
				Installer.DeleteEverythingElse(dir, self);
				note = "";
			}
			Console.WriteLine("Oni was removed.");
			if (note.Length != 0) Console.WriteLine(note);
			PressAnyKey();
			if (string.Compare(Path.GetDirectoryName(self), dir, StringComparison.OrdinalIgnoreCase) == 0) return Uninstalled;
			if (mods.On) Directory.Delete(dir);
			return 0;
		} catch (Exception e) {
			Fail(e.Message);
			return 1;
		}
	}

	static int RunUninstallSilent(string self, bool purge) {
		string dir = Path.GetDirectoryName(self);
		try {
			string note = Installer.Uninstall(dir);
			if (purge) Installer.DeleteEverythingElse(dir, self);
			else if (note.Length != 0) Console.Out.WriteLine(note);
			return Uninstalled;
		} catch (Exception e) {
			Console.Error.WriteLine(e.Message);
			return 1;
		}
	}

	public static int Run() {
		string self = Environment.GetEnvironmentVariable("ONI_BAT");
		string cwd = Environment.GetEnvironmentVariable("ONI_CWD");
		if (!string.IsNullOrEmpty(cwd)) Sources.Cwd = cwd;
		try {
			ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2, for GitHub
		} catch (NotSupportedException) {
		}
		List<string> args = Args();
		bool silent = args.Count > 0 && string.Compare(args[0], "/S", StringComparison.OrdinalIgnoreCase) == 0;
		if (silent && args.Count >= 2 && args[1].ToLowerInvariant() == "/uninstall") return RunUninstallSilent(self, args.Count > 2 && args[2].ToLowerInvariant() == "/purge");
		if (silent) return RunSilent(args, self);
		if (Console.IsInputRedirected || Console.IsOutputRedirected) {
			Console.Error.WriteLine("The menu needs a console window. Without one:\n" + SilentUsage);
			return 2;
		}
		try {
			Console.Title = "Install Oni";
		} catch (Exception) {
		}
		try {
			if (args.Count > 0 && args[0].ToLowerInvariant() == "/uninstall") return RunUninstallMenu(self);
			return RunMenu(self, args.Count > 0 ? args[0] : "");
		} catch (OniAborted) {
			Console.WriteLine("Aborted.");
			return 0;
		}
	}
}
