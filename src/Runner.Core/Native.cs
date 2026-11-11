using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ToolRunner;

// The Win32 calls the runner needs. None of them brings a window to the front or moves the real mouse: buttons are pressed
// through UI Automation or posted messages, text read with WM_GETTEXT, a list photographed with PrintWindow.
public static partial class Native
{
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	static extern bool CreateHardLinkW(string a_new, string a_existing, nint a_security);

	// one file under a second name; false when the volume or the file's link count refuses it
	public static bool HardLink(string a_new, string a_existing) => CreateHardLinkW(a_new, a_existing, 0);

	public const uint WM_CLOSE = 0x0010, WM_GETTEXT = 0x000D, WM_GETTEXTLENGTH = 0x000E, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101,
		WM_CHAR = 0x0102, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, BM_CLICK = 0x00F5;
	public const uint LB_SETCURSEL = 0x0186, LB_SETTOPINDEX = 0x0197, LB_GETCOUNT = 0x018B, LB_GETITEMRECT = 0x0198;
	public const int VK_RETURN = 0x0D, VK_SPACE = 0x20;

	public delegate bool EnumWindowsProc(nint a_hwnd, nint a_param);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool EnumWindows(EnumWindowsProc a_cb, nint a_param);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool EnumChildWindows(nint a_parent, EnumWindowsProc a_cb, nint a_param);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool IsWindowVisible(nint a_hwnd);

	[LibraryImport("user32.dll")]
	private static partial nint GetWindow(nint a_hwnd, uint a_cmd);

	// a window another window owns (GW_OWNER): a dialog or box of any toolkit - Delphi, WPF, WinForms - never a tool's main window
	public static bool IsOwned(nint a_hwnd) => GetWindow(a_hwnd, 4) != 0;

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool IsWindowEnabled(nint a_hwnd);

	[LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
	private static partial nint SendMessageTimeout(nint a_hwnd, uint a_msg, nint a_w, nint a_l, uint a_flags, uint a_ms, out nint a_result);

	const uint WM_NULL = 0x0000, SMTO_ABORTIFHUNG = 0x0002;

	// the window's thread answers a message within a_ms (a window busy loading or hung does not)
	public static bool Responds(nint a_hwnd, uint a_ms = 1000) => SendMessageTimeout(a_hwnd, WM_NULL, 0, 0, SMTO_ABORTIFHUNG, a_ms, out _) != 0;

	[LibraryImport("user32.dll")]
	public static partial uint GetWindowThreadProcessId(nint a_hwnd, out uint a_pid);

	[LibraryImport("user32.dll")]
	public static partial nint GetForegroundWindow();

	[StructLayout(LayoutKind.Sequential)]
	struct LastInput
	{
		public uint Size;
		public uint Time;
	}

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetLastInputInfo(ref LastInput a_info);

	// how long since the player last touched the keyboard or mouse (any program) - TimeSpan.MaxValue when Windows cannot say
	public static TimeSpan SinceLastInput()
	{
		var li = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
		return GetLastInputInfo(ref li) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - li.Time)) : TimeSpan.MaxValue;
	}

	// the process that owns the window in front (0 when none)
	public static int ForegroundPid() => GetWindowThreadProcessId(GetForegroundWindow(), out var pid) == 0 ? 0 : (int)pid;

	[DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
	static extern int GetWindowTextW(nint a_hwnd, [Out] char[] a_text, int a_max);

	[DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
	static extern int GetClassNameW(nint a_hwnd, [Out] char[] a_text, int a_max);

	[LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
	public static partial nint SendMessage(nint a_hwnd, uint a_msg, nint a_w, nint a_l);

	[DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
	static extern nint SendMessageBuf(nint a_hwnd, uint a_msg, nint a_w, [Out] char[] a_buf);

	[LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool PostMessage(nint a_hwnd, uint a_msg, nint a_w, nint a_l);

	[StructLayout(LayoutKind.Sequential)]
	public struct RECT
	{
		public int Left, Top, Right, Bottom;
		public readonly int Width => Right - Left;
		public readonly int Height => Bottom - Top;
	}

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool GetWindowRect(nint a_hwnd, out RECT a_rect);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool GetClientRect(nint a_hwnd, out RECT a_rect);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool PrintWindow(nint a_hwnd, nint a_hdc, uint a_flags);

	[LibraryImport("kernel32.dll")]
	public static partial uint SetThreadExecutionState(uint a_flags);

	public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool FlashWindowEx(ref FLASHWINFO a_info);

	[StructLayout(LayoutKind.Sequential)]
	public struct FLASHWINFO
	{
		public uint Size;
		public nint Hwnd;
		public uint Flags;
		public uint Count;
		public uint Timeout;
	}

	public static string WindowText(nint a_hwnd)
	{
		var buf = new char[512];
		var n = GetWindowTextW(a_hwnd, buf, buf.Length);
		return new string(buf, 0, Math.Max(n, 0));
	}

	public static string ClassName(nint a_hwnd)
	{
		var buf = new char[256];
		var n = GetClassNameW(a_hwnd, buf, buf.Length);
		return new string(buf, 0, Math.Max(n, 0));
	}

	// the FULL text of an edit / memo (UI Automation cuts a VCL memo at 2048 characters), unpacked when a tool answers with its
	// ANSI bytes packed two to a character (xLODGen 4.1.5o, 2026-10-12)
	public static string ControlText(nint a_hwnd)
	{
		var n = (int)SendMessage(a_hwnd, WM_GETTEXTLENGTH, 0, 0);
		if (n <= 0) {
			return "";
		}
		var buf = new char[n + 1];
		var got = (int)SendMessageBuf(a_hwnd, WM_GETTEXT, n + 1, buf);
		return FromBuffer(buf, got);
	}

	// A tool that packs its ANSI bytes two to a character counts BYTES in its answer, so half the buffer stays empty; the text
	// ends at the first empty character, or those empties outvote the packed half and it is never unpacked (the trial of
	// 2026-10-27 22:40: xLODGen's "LOD generation done." went unseen and the finished tool was called idle)
	public static string FromBuffer(char[] a_buf, int a_got)
	{
		var s = new string(a_buf, 0, Math.Clamp(a_got, 0, a_buf.Length));
		var nul = s.IndexOf('\0');
		return UnpackAnsi(nul >= 0 ? s[..nul] : s);
	}

	public static string UnpackAnsi(string a_s)
	{
		if (string.IsNullOrEmpty(a_s)) {
			return a_s;
		}
		var wide = a_s.Count(c => c > 0x2FFF);
		if (wide < a_s.Length * 0.6) {
			return a_s;
		}
		var raw = Encoding.Unicode.GetBytes(a_s);
		var printable = raw.Count(b => b is >= 32 and < 127 or 9 or 10 or 13);
		if (printable < raw.Length * 0.9) {
			return a_s;
		}
		return Encoding.Latin1.GetString(raw).Replace("\0", "");
	}

	public static List<nint> TopWindowsOf(IReadOnlyCollection<int> a_pids, bool a_visibleOnly = true)
	{
		var outList = new List<nint>();
		EnumWindows((h, _) => {
			if ((!a_visibleOnly || IsWindowVisible(h)) && GetWindowThreadProcessId(h, out var pid) != 0 && a_pids.Contains((int)pid)) {
				outList.Add(h);
			}
			return true;
		}, 0);
		return outList;
	}

	public static List<nint> ChildWindows(nint a_parent)
	{
		var outList = new List<nint>();
		EnumChildWindows(a_parent, (h, _) => {
			outList.Add(h);
			return true;
		}, 0);
		return outList;
	}

	// a button click by posted messages to the control's own window (no focus, no real mouse): move, down, up at once - Tk
	// re-reads the real cursor on a timer, so the three go together
	public static void PostClick(nint a_hwnd)
	{
		GetClientRect(a_hwnd, out var r);
		var lp = (nint)(((r.Height / 2) << 16) | (r.Width / 2 & 0xFFFF));
		PostMessage(a_hwnd, WM_MOUSEMOVE, 0, lp);
		PostMessage(a_hwnd, WM_LBUTTONDOWN, 1, lp);
		PostMessage(a_hwnd, WM_LBUTTONUP, 0, lp);
	}

	public static void PostKey(nint a_hwnd, int a_vk)
	{
		PostMessage(a_hwnd, WM_KEYDOWN, a_vk, 0);
		PostMessage(a_hwnd, WM_KEYUP, a_vk, 0);
	}

	// keep the PC from sleeping while a run goes (the screen may still turn off and lock - nothing here needs it on)
	public static void KeepAwake(bool a_on) =>
		_ = SetThreadExecutionState(a_on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS);

	public static void Flash(nint a_hwnd, bool a_on)
	{
		var fi = new FLASHWINFO { Size = (uint)Marshal.SizeOf<FLASHWINFO>(), Hwnd = a_hwnd, Flags = a_on ? 3u | 0x0Cu : 0u, Count = 0, Timeout = 0 };
		FlashWindowEx(ref fi);
	}
}

// process helpers: find by image name, CPU of a whole tree, kill a tree by PID (never by name or window title)
public static partial class Procs
{
	// Every look by image name is one walk of Windows' own process list (the toolhelp snapshot), the names compared in place.
	// Process.GetProcessesByName builds an object for every process and thread of the PC on each call - the window asks every
	// poll and a step every two seconds, and that was most of what an idle window cost.

	// an image's name without its ".exe" - what the name a caller gives is compared with
	static ReadOnlySpan<char> Bare(ReadOnlySpan<char> a_image) =>
		a_image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a_image[..^4] : a_image;

	static bool Is(ReadOnlySpan<char> a_listed, ReadOnlySpan<char> a_want) => Bare(a_listed).Equals(a_want, StringComparison.OrdinalIgnoreCase);

	// the ids of the processes of an image
	public static List<int> Pids(string a_image)
	{
		var want = Path.GetFileNameWithoutExtension(a_image);
		var outList = new List<int>();
		Walk((pid, _, image) => {
			if (Is(image, want)) {
				outList.Add(pid);
			}
			return true;
		});
		return outList;
	}

	public static bool Running(string a_image)
	{
		var want = Path.GetFileNameWithoutExtension(a_image);
		var found = false;
		Walk((_, _, image) => !(found = Is(image, want)));
		return found;
	}

	// which of several images run, from ONE look (the window's poll asks for Mod Organizer and the game together)
	public static bool[] Running(IReadOnlyList<string> a_images)
	{
		var want = a_images.Select(Path.GetFileNameWithoutExtension).ToArray();
		var found = new bool[want.Length];
		Walk((_, _, image) => {
			for (var i = 0; i < want.Length; ++i) {
				found[i] |= Is(image, want[i]);
			}
			return true;
		});
		return found;
	}

	// the caller owns (and disposes) every Process returned; one that ended since the look is left out
	public static List<Process> ByImage(string a_image)
	{
		var outList = new List<Process>();
		foreach (var pid in Pids(a_image)) {
			try {
				outList.Add(Process.GetProcessById(pid));
			} catch (ArgumentException) {
			}
		}
		return outList;
	}

	public static string? ExePath(Process a_p)
	{
		try {
			return a_p.MainModule?.FileName;
		} catch (Win32Exception) {
			return null;
		} catch (InvalidOperationException) {
			return null;
		}
	}

	// child processes through a snapshot of every process's parent (CIM is slow; the toolhelp snapshot is not). A child counts
	// only when it started after its parent: Windows keeps a dead parent's PID in its children, and a new process can reuse it
	public static List<int> Tree(int a_root)
	{
		var parents = Snapshot();
		var outList = new List<int> { a_root };
		var started = new Dictionary<int, DateTime?>();
		DateTime? Start(int a_pid) => started.TryGetValue(a_pid, out var t) ? t : started[a_pid] = StartTime(a_pid);
		for (int i = 0; i < outList.Count; ++i) {
			foreach (var (pid, ppid) in parents) {
				if (ppid == outList[i] && pid != a_root && !outList.Contains(pid) &&
				    !(Start(pid) is { } c && Start(ppid) is { } p && c < p)) {
					outList.Add(pid);
				}
			}
		}
		return outList;
	}

	public static TimeSpan Cpu(int a_pid)
	{
		try {
			using var p = Process.GetProcessById(a_pid);
			return p.TotalProcessorTime;
		} catch (ArgumentException) {
		} catch (InvalidOperationException) {
		} catch (Win32Exception) {
		}
		return TimeSpan.Zero;
	}

	// the same, with "not readable" told apart from "none used" (null: the process is gone or may not be read)
	public static TimeSpan? CpuOrNull(int a_pid)
	{
		try {
			using var p = Process.GetProcessById(a_pid);
			return p.TotalProcessorTime;
		} catch (ArgumentException) {
		} catch (InvalidOperationException) {
		} catch (Win32Exception) {
		}
		return null;
	}

	public static TimeSpan TreeCpu(int a_root)
	{
		var total = TimeSpan.Zero;
		foreach (var pid in Tree(a_root)) {
			try {
				using var p = Process.GetProcessById(pid);
				total += p.TotalProcessorTime;
			} catch (ArgumentException) {
			} catch (InvalidOperationException) {
			} catch (Win32Exception) {
			}
		}
		return total;
	}

	// each CHILD process's CPU (the root left out: a tool's window drawing itself is not work) - read per process, so a child
	// that ends never makes the total fall
	public static Dictionary<int, TimeSpan> ChildCpu(int a_root) => Tree(a_root).Where(p => p != a_root).ToDictionary(p => p, Cpu);

	// how much CPU the children used since the last reading (a new child counts whole)
	public static TimeSpan CpuGain(IReadOnlyDictionary<int, TimeSpan> a_before, IReadOnlyDictionary<int, TimeSpan> a_now) =>
		a_now.Aggregate(TimeSpan.Zero, (sum, kv) => sum + (kv.Value - a_before.GetValueOrDefault(kv.Key) is { Ticks: > 0 } d ? d : TimeSpan.Zero));

	public static void KillTree(int a_root)
	{
		try {
			using var p = Process.GetProcessById(a_root);
			p.Kill(true);
			p.WaitForExit(10000);
		} catch (ArgumentException) {
		} catch (InvalidOperationException) {
		} catch (Win32Exception) {
		}
	}

	// when a process started (null = gone or not readable) - a PID alone can be reused by a new process after a crash
	public static DateTime? StartTime(int a_pid)
	{
		try {
			using var p = Process.GetProcessById(a_pid);
			return p.HasExited ? null : p.StartTime.ToUniversalTime();
		} catch (ArgumentException) {
		} catch (InvalidOperationException) {
		} catch (Win32Exception) {
		}
		return null;
	}

	// the process's image name as Windows lists it, "ModOrganizer.exe" ("" when gone): asked of the process itself through a
	// handle that reads nothing else, and off the process list for one this user may not open
	public static string Image(int a_pid)
	{
		if (PathOf(a_pid) is { } path) {
			return Path.GetFileName(path);
		}
		var found = "";
		Walk((pid, _, image) => {
			if (pid != a_pid) {
				return true;
			}
			found = image.ToString();
			return false;
		});
		return found != "" && Alive(a_pid) ? found : "";
	}

	// the same without its ".exe" (null when gone) - the name a program is known by
	public static string? Name(int a_pid) => Image(a_pid) is { Length: > 0 } image ? Bare(image).ToString() : null;

	const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, SYNCHRONIZE = 0x00100000;

	[LibraryImport("kernel32.dll", SetLastError = true)]
	private static partial nint OpenProcess(uint a_access, [MarshalAs(UnmanagedType.Bool)] bool a_inherit, uint a_pid);

	[LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static unsafe partial bool QueryFullProcessImageName(nint a_process, uint a_flags, char* a_path, ref uint a_size);

	[LibraryImport("kernel32.dll")]
	private static partial uint WaitForSingleObject(nint a_handle, uint a_ms);

	// a running process's program file (null: gone, ended, or closed to this user)
	static unsafe string? PathOf(int a_pid)
	{
		var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, (uint)a_pid);
		if (h == 0) {
			return null;
		}
		try {
			// a process that ended stays openable while anyone holds a handle to it: its handle is signalled
			if (WaitForSingleObject(h, 0) == 0) {
				return null;
			}
			const int Max = 1024;
			var buf = stackalloc char[Max];
			uint n = Max;
			return QueryFullProcessImageName(h, 0, buf, ref n) ? new string(buf, 0, (int)n) : null;
		} finally {
			CloseHandle(h);
		}
	}

	public static bool Alive(int a_pid)
	{
		try {
			using var p = Process.GetProcessById(a_pid);
			return !p.HasExited;
		} catch (ArgumentException) {
			return false;
		} catch (InvalidOperationException) {
			return false;
		} catch (Win32Exception) {
			// a process this user may not open is never one of ours (a PID reused by a protected one): not ours alive - a leak
			// guard that kept following it would read that stranger's children as the tool's
			return false;
		}
	}

	const int MaxImage = 260;

	// the name is read where Windows wrote it: no string is made for the hundreds of processes a look passes over
	[StructLayout(LayoutKind.Sequential)]
	unsafe struct PROCESSENTRY32W
	{
		public uint dwSize, cntUsage, th32ProcessID;
		public nint th32DefaultHeapID;
		public uint th32ModuleID, cntThreads, th32ParentProcessID;
		public int pcPriClassBase;
		public uint dwFlags;
		public fixed char szExeFile[MaxImage];
	}

	[LibraryImport("kernel32.dll", SetLastError = true)]
	private static partial nint CreateToolhelp32Snapshot(uint a_flags, uint a_pid);

	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool Process32FirstW(nint a_snap, ref PROCESSENTRY32W a_entry);

	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool Process32NextW(nint a_snap, ref PROCESSENTRY32W a_entry);

	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CloseHandle(nint a_h);

	// one process of a look: its id, its parent's, its image name ("ModOrganizer.exe"); false ends the look
	delegate bool Visit(int a_pid, int a_parent, ReadOnlySpan<char> a_image);

	static unsafe void Walk(Visit a_visit)
	{
		const uint TH32CS_SNAPPROCESS = 2;
		var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
		if (snap == -1) {
			return;
		}
		try {
			var e = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };
			if (Process32FirstW(snap, ref e)) {
				do {
					var image = new ReadOnlySpan<char>(e.szExeFile, MaxImage);
					var end = image.IndexOf('\0');
					if (!a_visit((int)e.th32ProcessID, (int)e.th32ParentProcessID, end < 0 ? image : image[..end])) {
						break;
					}
				} while (Process32NextW(snap, ref e));
			}
		} finally {
			CloseHandle(snap);
		}
	}

	public static List<(int Pid, int Parent)> Snapshot()
	{
		var outList = new List<(int, int)>();
		Walk((pid, parent, _) => {
			outList.Add((pid, parent));
			return true;
		});
		return outList;
	}
}
