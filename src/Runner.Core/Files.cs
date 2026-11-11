using System.Runtime.InteropServices;
using System.Text;

namespace ToolRunner;

// Every write the runner makes to a file it does not own goes through here: written beside the target, flushed to disk,
// then swapped in, so a crash or a power cut leaves either the old file or the new one - never half of one.
//
// And every change - a write, a move, a copy, a delete - is tried again for a couple of seconds before it counts as failed:
// an antivirus reading what was just written, the search indexer, a backup agent or an editor's preview holds a file for a
// moment and it is free again a blink later. One such moment on the run record must never fail a two-hour run.
public static partial class Files
{
	// the waits between tries, in ms: seven tries over about two seconds
	static readonly int[] Again = [30, 60, 120, 250, 500, 1000];

	// held by another program right now (a sharing or lock violation), or refused the way Windows refuses a file that is being
	// read or deleted by someone else. A file that is not there, a full disk and a bad path are never tried again
	static bool Held(Exception a_e, bool a_folder) => a_e is UnauthorizedAccessException ||
		(a_e is IOException io && ((io.HResult & 0xFFFF) is 32 or 33 || (a_folder && (io.HResult & 0xFFFF) == 5)));

	// a_folder: a folder's rename, which Windows refuses with "access denied" (as an IOException) while a file in it is open
	public static void Retry(Action a_change, bool a_folder = false)
	{
		for (var i = 0; ; ++i) {
			try {
				a_change();
				return;
			} catch (Exception e) when (i < Again.Length && Held(e, a_folder)) {
				Thread.Sleep(Again[i]);
			}
		}
	}

	public static void WriteAtomic(string a_path, ReadOnlySpan<byte> a_bytes)
	{
		var dir = Path.GetDirectoryName(Path.GetFullPath(a_path)) ?? throw new ArgumentException("no folder: " + a_path);
		Directory.CreateDirectory(dir);
		var tmp = Path.Combine(dir, "." + Path.GetFileName(a_path) + "." + Environment.ProcessId + ".tmp");
		// a failed write (a full disk) leaves no temp file behind in the player's folder either
		try {
			using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
				fs.Write(a_bytes);
				fs.Flush(true);
			}
			Swap(tmp, a_path);
		} catch {
			TryDelete(tmp);
			throw;
		}
	}

	// A read-only file is still the player's file to keep read-only (a SkyrimPrefs.ini locked against the launcher): Windows
	// refuses to replace it, so the mark comes off for the swap and goes back on the new file
	static void Swap(string a_tmp, string a_path)
	{
		var readOnly = File.Exists(a_path) && File.GetAttributes(a_path).HasFlag(FileAttributes.ReadOnly);
		if (readOnly) {
			File.SetAttributes(a_path, File.GetAttributes(a_path) & ~FileAttributes.ReadOnly);
		}
		try {
			Retry(() => File.Move(a_tmp, a_path, true));
		} finally {
			if (readOnly && File.Exists(a_path)) {
				File.SetAttributes(a_path, File.GetAttributes(a_path) | FileAttributes.ReadOnly);
			}
		}
	}

	public static void WriteAtomic(string a_path, string a_text, Encoding? a_encoding = null) =>
		WriteAtomic(a_path, (a_encoding ?? new UTF8Encoding(false)).GetBytes(a_text));

	// read a file another program may hold open for writing (logs, MO2's files)
	public static byte[] ReadShared(string a_path)
	{
		using var fs = new FileStream(a_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		// its length now; a file that grows meanwhile is read to where it stood
		var buf = new byte[fs.Length];
		var got = 0;
		while (got < buf.Length) {
			var n = fs.Read(buf, got, buf.Length - got);
			if (n == 0) {
				break;
			}
			got += n;
		}
		return got == buf.Length ? buf : buf[..got];
	}

	public static string ReadSharedText(string a_path) => DecodeText(ReadShared(a_path));

	// The end of a text file another program is writing: its last a_most bytes, from the first whole line in them. For a look
	// that needs a log's last lines every few seconds - a shader log grows to megabytes, and reading all of it at each look, beside
	// a game that uses every core, was most of what the look cost. A file in UTF-16 (its mark is at its start) is read whole.
	public static string ReadSharedTailText(string a_path, int a_most)
	{
		using var fs = new FileStream(a_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		var mark = new byte[2];
		var utf16 = fs.Length >= 2 && fs.ReadAtLeast(mark, 2, false) == 2 && ((mark[0] == 0xFF && mark[1] == 0xFE) || (mark[0] == 0xFE && mark[1] == 0xFF));
		if (fs.Length <= a_most || utf16) {
			fs.Position = 0;
			var all = new byte[fs.Length];
			return DecodeText(all[..fs.ReadAtLeast(all, all.Length, false)]);
		}
		fs.Seek(-a_most, SeekOrigin.End);
		var tail = new byte[a_most];
		var got = fs.ReadAtLeast(tail, a_most, false);
		var line = Array.IndexOf(tail, (byte)'\n', 0, got);
		return DecodeText(tail[(line < 0 ? 0 : line + 1)..got]);
	}

	static readonly UTF8Encoding StrictUtf8 = new(false, true);

	// the PC's own code page (what the game and most tools write a file in when they do not say): 1252 in the west, 1251 for
	// Cyrillic, 932 for Japanese ...
	static readonly Lazy<Encoding> Ansi = new(() => {
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		return Encoding.GetEncoding(0);
	});

	public static Encoding AnsiEncoding => Ansi.Value;

	// the bytes begin with a mark that says how they are written (UTF-8 or UTF-16)
	public static bool HasMark(byte[] a_bytes) =>
		(a_bytes.Length >= 3 && a_bytes[0] == 0xEF && a_bytes[1] == 0xBB && a_bytes[2] == 0xBF) ||
		(a_bytes.Length >= 2 && ((a_bytes[0] == 0xFF && a_bytes[1] == 0xFE) || (a_bytes[0] == 0xFE && a_bytes[1] == 0xFF)));

	public static string DecodeText(byte[] a_bytes) => DecodeText(a_bytes, out _);

	// UTF-16 or UTF-8 by its mark; without one UTF-8 when the bytes are valid UTF-8, else the PC's own code page - the game's INIs
	// and most tools' files are written in it, and read as UTF-8 a path with an accent in one became a path that does not exist.
	// a_encoding is what to write the text back with, mark included, so a file changed and put back keeps its bytes' meaning
	public static string DecodeText(byte[] a_bytes, out Encoding a_encoding)
	{
		if (a_bytes.Length >= 2 && a_bytes[0] == 0xFF && a_bytes[1] == 0xFE) {
			a_encoding = Encoding.Unicode;
			return Encoding.Unicode.GetString(a_bytes, 2, a_bytes.Length - 2);
		}
		if (a_bytes.Length >= 2 && a_bytes[0] == 0xFE && a_bytes[1] == 0xFF) {
			a_encoding = Encoding.BigEndianUnicode;
			return Encoding.BigEndianUnicode.GetString(a_bytes, 2, a_bytes.Length - 2);
		}
		if (a_bytes.Length >= 3 && a_bytes[0] == 0xEF && a_bytes[1] == 0xBB && a_bytes[2] == 0xBF) {
			a_encoding = new UTF8Encoding(true);
			return Encoding.UTF8.GetString(a_bytes, 3, a_bytes.Length - 3);
		}
		try {
			a_encoding = new UTF8Encoding(false);
			return StrictUtf8.GetString(a_bytes);
		} catch (DecoderFallbackException) {
			a_encoding = Ansi.Value;
			return a_encoding.GetString(a_bytes);
		}
	}

	// a text's bytes for a file, with the mark its encoding writes (UTF-16 and UTF-8-with-mark files keep theirs)
	public static byte[] EncodeText(string a_text, Encoding a_encoding) => [.. a_encoding.GetPreamble(), .. a_encoding.GetBytes(a_text)];

	public static void Move(string a_src, string a_dst, bool a_overwrite = false) => Retry(() => File.Move(a_src, a_dst, a_overwrite));

	public static void Copy(string a_src, string a_dst, bool a_overwrite = false) => Retry(() => File.Copy(a_src, a_dst, a_overwrite));

	// a file that is not there is deleted already; one marked read-only is ours to delete all the same
	public static void Delete(string a_path) => Retry(() => {
		try {
			File.Delete(a_path);
		} catch (UnauthorizedAccessException) when (File.Exists(a_path) && File.GetAttributes(a_path).HasFlag(FileAttributes.ReadOnly)) {
			File.SetAttributes(a_path, FileAttributes.Normal);
			File.Delete(a_path);
		}
	});

	public static void TryDelete(string a_path)
	{
		try {
			Delete(a_path);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	// a folder and everything in it; read-only files inside (a tool's output often has them) do not stop it
	public static void DeleteDir(string a_dir) => Retry(() => {
		if (!Directory.Exists(a_dir)) {
			return;
		}
		try {
			Directory.Delete(a_dir, true);
		} catch (UnauthorizedAccessException) {
			foreach (var f in new DirectoryInfo(a_dir).EnumerateFiles("*", SearchOption.AllDirectories).Where(f => f.IsReadOnly)) {
				f.IsReadOnly = false;
			}
			Directory.Delete(a_dir, true);
		}
	});

	public static int CountFiles(string a_dir) =>
		Directory.Exists(a_dir) ? Directory.EnumerateFiles(a_dir, "*", SearchOption.AllDirectories).Count() : 0;

	public static long SizeOf(string a_dir) =>
		Directory.Exists(a_dir) ? new DirectoryInfo(a_dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

	public static DateTime NewestWrite(string a_dir)
	{
		if (!Directory.Exists(a_dir)) {
			return DateTime.MinValue;
		}
		var newest = DateTime.MinValue;
		foreach (var f in new DirectoryInfo(a_dir).EnumerateFiles("*", SearchOption.AllDirectories)) {
			if (f.LastWriteTimeUtc > newest) {
				newest = f.LastWriteTimeUtc;
			}
		}
		return newest;
	}

	// everything inside a_src moved into a_dst (a rename where Windows can; across drives a copy, then a delete). No count is
	// returned: counting meant walking the whole target after every move, three walks around one terrain LOD output
	public static void MoveContents(string a_src, string a_dst, Func<string, bool>? a_skip = null)
	{
		Directory.CreateDirectory(a_dst);
		foreach (var entry in Directory.EnumerateFileSystemEntries(a_src)) {
			var name = Path.GetFileName(entry);
			if (a_skip?.Invoke(name) == true) {
				continue;
			}
			var target = Path.Combine(a_dst, name);
			if (Directory.Exists(entry)) {
				if (Directory.Exists(target)) {
					MoveContents(entry, target);
					DeleteDir(entry);
				} else {
					MoveDirectory(entry, target);
				}
			} else {
				Move(entry, target, true);
			}
		}
	}

	// A folder moved whole: a rename when both ends are on one volume, else a copy and a delete. Which volume a path is on
	// cannot be read off its text - a mod folder kept on another disk through a junction, a mounted volume - so Windows is asked;
	// where it will not say, the rename is tried and what it refuses as "another device" is copied
	public static void MoveDirectory(string a_src, string a_dst)
	{
		var parent = Path.GetDirectoryName(Path.GetFullPath(a_dst)) ?? throw new ArgumentException("no folder: " + a_dst);
		Directory.CreateDirectory(parent);
		if (SameVolume(a_src, parent) != false) {
			try {
				// Windows refuses to rename a folder with "access denied" while a file in it is open somewhere: tried again too
				Retry(() => Directory.Move(a_src, a_dst), true);
				return;
			} catch (IOException e) when ((e.HResult & 0xFFFF) == NotSameDevice && Directory.Exists(a_src) && !Directory.Exists(a_dst)) {
			}
		}
		MoveContents(a_src, a_dst);
		DeleteDir(a_src);
	}

	const int NotSameDevice = 17;

	// Windows' own layout: its three times are pairs of 32-bit numbers, so nothing here is padded to 8 bytes
	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	struct ByHandleFileInformation
	{
		public uint Attributes;
		public long Created, Accessed, Written;
		public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
	}

	[LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
	private static partial nint CreateFile(string a_path, uint a_access, uint a_share, nint a_security, uint a_how, uint a_flags, nint a_template);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetFileInformationByHandle(nint a_handle, out ByHandleFileInformation a_info);

	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CloseHandle(nint a_handle);

	// the volume a folder is really on (a junction is followed to where it leads); null when Windows will not say
	static uint? VolumeOf(string a_dir)
	{
		const uint ReadAttributes = 0x80, ShareAll = 7, OpenExisting = 3, BackupSemantics = 0x02000000;
		var h = CreateFile(Path.GetFullPath(a_dir), ReadAttributes, ShareAll, 0, OpenExisting, BackupSemantics, 0);
		if (h == -1) {
			return null;
		}
		try {
			return GetFileInformationByHandle(h, out var info) ? info.VolumeSerial : null;
		} finally {
			CloseHandle(h);
		}
	}

	// null = not known
	public static bool? SameVolume(string a_dirA, string a_dirB) => VolumeOf(a_dirA) is { } a && VolumeOf(a_dirB) is { } b ? a == b : null;

	public static bool IsUnder(string a_path, string a_root)
	{
		var p = Path.GetFullPath(a_path).TrimEnd('\\') + "\\";
		var r = Path.GetFullPath(a_root).TrimEnd('\\') + "\\";
		return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
	}

	public static bool SamePath(string a_a, string a_b) =>
		string.Equals(Path.GetFullPath(a_a).TrimEnd('\\'), Path.GetFullPath(a_b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
