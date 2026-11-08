using System.Diagnostics;
using System.Management;

namespace ToolRunner;

public sealed class Mo2BusyException(string a_message) : Exception(a_message);

public sealed class StepFailedException(string a_message) : Exception(a_message);

// Mod Organizer itself: started detached (WMI - a child of a job-limited process makes MO2 fail to start SKSE), closed with
// its own window message and its own questions answered, never while it downloads or the game runs, and its executables run
// through the open instance so every tool sees the virtual Data folder.
public sealed class Mo2Control(Mo2Instance a_mo2, string a_profile, RunLog a_log)
{
	public Mo2Instance Mo2 { get; } = a_mo2;
	public string ProfileName { get; } = a_profile;
	readonly RunLog _log = a_log;

	// (a WMI start cannot reach a hidden desktop: its WinstationDesktop setting was ignored and the window opened on the player's
	// screen - measured 2026-10-27; a hidden start goes through HiddenDesktop.Start, which watches for exactly that)
	public static int StartDetached(string a_commandLine, string a_cwd)
	{
		using var cls = new ManagementClass(@"root\cimv2", "Win32_Process", null);
		// both WMI objects released at once (each holds a COM reference into the WMI service until it is disposed)
		using var startupClass = new ManagementClass(@"root\cimv2", "Win32_ProcessStartup", null);
		using var startup = startupClass.CreateInstance();
		// SW_SHOWNOACTIVATE: the window shows, but never takes the front from what the player is doing
		startup["ShowWindow"] = (ushort)4;
		using var inParams = cls.GetMethodParameters("Create");
		inParams["CommandLine"] = a_commandLine;
		inParams["CurrentDirectory"] = a_cwd;
		inParams["ProcessStartupInformation"] = startup;
		using var outParams = cls.InvokeMethod("Create", inParams, null);
		var rc = Convert.ToUInt32(outParams["ReturnValue"], InvariantCulture);
		if (rc != 0) {
			throw new InvalidOperationException($"Windows could not start {a_commandLine} (code {rc})");
		}
		return Convert.ToInt32(outParams["ProcessId"], InvariantCulture);
	}

	// a global instance named on every command line (else MO2 opens the instance it used last)
	string Instance => Mo2.GlobalName is { } n ? " -i " + Quote(n) : "";

	// one argument for a Windows command line (the CommandLineToArgvW rules): backslashes before a quote and at the end doubled,
	// so "D:\LOD\" does not swallow the next argument
	public static string Quote(string a)
	{
		if (a.Length > 0 && a.IndexOfAny([' ', '\t', '"']) < 0) {
			return a;
		}
		var sb = new System.Text.StringBuilder("\"");
		var slashes = 0;
		foreach (var c in a) {
			if (c == '\\') {
				++slashes;
				continue;
			}
			sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
			slashes = 0;
			sb.Append(c);
		}
		sb.Append('\\', slashes * 2);
		return sb.Append('"').ToString();
	}

	// every tool process this run started (a tool is only ever ended by these PIDs and their children - never by its name)
	public StartedSet Started { get; } = new();

	// this setup's Mod Organizer (its exe); one whose path cannot be read (started as administrator) is counted apart
	public List<int> Pids() => Mo2Processes(false);

	public List<int> Unreadable() => Mo2Processes(true);

	List<int> Mo2Processes(bool a_unreadable)
	{
		var want = Path.GetFullPath(Mo2.Exe);
		var outList = new List<int>();
		foreach (var p in Procs.ByImage("ModOrganizer.exe")) {
			var exe = Procs.ExePath(p);
			if (a_unreadable ? exe == null : exe != null && string.Equals(Path.GetFullPath(exe), want, StringComparison.OrdinalIgnoreCase)) {
				outList.Add(p.Id);
			}
			p.Dispose();
		}
		return outList;
	}

	// what runs under Mod Organizer that this run did not start (the player's own xEdit, BodySlide, a second tool): MO2 is never
	// closed under it - MO2 answers "programs are still running" by ending them. a_windowed: only those with a window open
	public List<(int Pid, string Image)> ForeignChildren(bool a_windowed)
	{
		var ours = new HashSet<int>(Started.SelectMany(Procs.Tree));
		var mo2 = Pids().Concat(Unreadable()).ToHashSet();
		var outList = new List<(int, string)>();
		foreach (var pid in mo2.SelectMany(Procs.Tree).Distinct()) {
			if (mo2.Contains(pid) || ours.Contains(pid)) {
				continue;
			}
			var image = Procs.Image(pid);
			if (image == "" || image.Equals("ModOrganizer.exe", StringComparison.OrdinalIgnoreCase)) {
				continue;
			}
			if (a_windowed && Native.TopWindowsOf([pid]).Count == 0) {
				continue;
			}
			outList.Add((pid, image));
		}
		return outList;
	}

	public bool Running => IsRunning();

	public List<string> DownloadsInProgress() => Downloads(Mo2.DownloadsDir).Active;

	// a download MO2 finished but whose "<file>.unfinished" stayed beside it, byte for byte the same: MO2 cannot finish it and
	// resumes it at every start, so it is no download in progress (one held a whole evening's game line, 2026-11-02) - named,
	// never deleted (the player removes it, or its row in MO2's Downloads tab)
	public static (List<string> Active, List<string> Stale) Downloads(string a_dir)
	{
		var active = new List<string>();
		var stale = new List<string>();
		try {
			if (!Directory.Exists(a_dir)) {
				return (active, stale);
			}
			foreach (var stub in Directory.GetFiles(a_dir, "*.unfinished")) {
				var done = stub[..^".unfinished".Length];
				(File.Exists(done) && SameBytes(stub, done) ? stale : active).Add(Path.GetFileName(stub));
			}
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
		return (active, stale);
	}

	// a pair compared once per (size, write time) of both files - Close() polls the downloads while it waits. Held to a few hundred
	// answers (every new size or time of a download is a new key: a program open for weeks must not keep them all)
	static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, long, DateTime, DateTime), bool> s_same = new();
	const int SameKept = 256;

	static bool SameBytes(string a_a, string a_b)
	{
		try {
			FileInfo fa = new(a_a), fb = new(a_b);
			if (fa.Length != fb.Length) {
				return false;
			}
			if (s_same.Count > SameKept) {
				s_same.Clear();
			}
			return s_same.GetOrAdd((a_a.ToLowerInvariant(), fa.Length, fa.LastWriteTimeUtc, fb.LastWriteTimeUtc), _ => {
				using var x = File.OpenRead(a_a);
				using var y = File.OpenRead(a_b);
				return System.Security.Cryptography.SHA256.HashData(x).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(y));
			});
		} catch (IOException) {
			return false;
		} catch (UnauthorizedAccessException) {
			return false;
		}
	}

	// a program still running from Mod Organizer's own folders while MO2 is closed (a crash dumper of a game that died, kept
	// alive inside the virtual Data folder): MO2 started beside it can crash at once (measured 2026-10-06, three crashes) - so
	// MO2 waits for it to end, then names it and refuses the start; it is never ended here (its PID may not be ours)
	public Func<List<(int Pid, string Image)>>? LeftoverProbe { get; set; }
	public static TimeSpan LeftoverWait { get; set; } = TimeSpan.FromMinutes(2);

	// only a process still hooked into MO2's virtual folder (usvfs loaded) - a program that merely lives in a mod folder (a
	// DevBench bridge, a tool kept there) and runs on its own is no leftover
	List<(int Pid, string Image)> Leftovers()
	{
		var roots = new[] { Mo2.BaseDir, Path.GetDirectoryName(Mo2.Exe)!, Mo2.GameDir }.Select(Path.GetFullPath).ToList();
		var outList = new List<(int, string)>();
		foreach (var p in Process.GetProcesses()) {
			using (p) {
				if (p.Id == Environment.ProcessId || p.ProcessName.Equals("ModOrganizer", StringComparison.OrdinalIgnoreCase)) {
					continue;
				}
				if (Procs.ExePath(p) is { } exe && roots.Any(r => Files.IsUnder(exe, r)) && InVfs(p)) {
					outList.Add((p.Id, Path.GetFileName(exe)));
				}
			}
		}
		return outList;
	}

	static bool InVfs(Process a_p)
	{
		try {
			return a_p.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.StartsWith("usvfs", StringComparison.OrdinalIgnoreCase));
		} catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) {
			return false;
		}
	}

	public void WaitForLeftovers(CancellationToken a_ct)
	{
		var probe = LeftoverProbe ?? Leftovers;
		var left = probe();
		if (left.Count == 0) {
			return;
		}
		_log.Detail($"still running from Mod Organizer's folders: {string.Join(", ", left.Select(l => $"{l.Image} ({l.Pid})"))} - waiting for it to end before Mod Organizer starts");
		var sw = Stopwatch.StartNew();
		while (sw.Elapsed < LeftoverWait) {
			a_ct.ThrowIfCancellationRequested();
			Thread.Sleep(Poll);
			if ((left = probe()).Count == 0) {
				return;
			}
		}
		if ((left = probe()).Count == 0) {
			return;
		}
		throw new StepFailedException($"{string.Join(", ", left.Select(l => $"{l.Image} (process {l.Pid})"))} still runs from Mod Organizer's folders - " +
			"left over from a game that closed? Let it finish or close it, then start again (Mod Organizer can crash when started beside it)");
	}

	public static readonly string[] GameImages = ["SkyrimSE.exe", "skse64_loader.exe"];

	public static Func<List<string>> GameProbe { get; set; } = () => GameImages.Where(Procs.Running).ToList();

	public static List<string> GameRunning() => GameProbe();

	nint MainWindow()
	{
		var pids = Pids();
		return Native.TopWindowsOf(pids).FirstOrDefault(h => Native.WindowText(h).Contains("Mod Organizer v2", StringComparison.Ordinal));
	}

	public void Start(CancellationToken a_ct)
	{
		if (!Running) {
			WaitForLeftovers(a_ct);
			_log.Detail($"starting Mod Organizer (profile {ProfileName})");
			_mo2LaunchedAt = DateTime.UtcNow;
			_ownMo2.Add(Launch($"{Quote(Mo2.Exe)}{Instance} -p {Quote(ProfileName)}", Path.GetDirectoryName(Mo2.Exe)!));
		}
		WaitReady(a_ct);
	}

	// the Mod Organizer processes this run started itself - any other one is the player's (his 2026-11-06 find: "the tool keeps
	// trying to fight you when you're doing something in mo2 even though it's running"). The player's is never closed mid-run:
	// the run says so and waits for the player to close it (UserWait), then goes on; only the run's very first close, which the
	// player asked for by pressing Start, may close it (AllowCloseTheirs)
	// with their start times: a PID Windows gives the player's Mod Organizer later is never taken for the run's
	readonly StartedSet _ownMo2 = new();

	public bool AllowCloseTheirs { get; set; }

	public static TimeSpan UserWait { get; set; } = TimeSpan.FromMinutes(30);

	public Func<List<int>>? TheirsProbe { get; set; }

	public List<int> TheirMo2()
	{
		if (TheirsProbe != null) {
			return TheirsProbe();
		}
		var others = Pids().Where(p => !_ownMo2.SameProcess(p)).ToList();
		if (others.Count == 0) {
			return others;
		}
		// a Mod Organizer started by one of this run's own (it restarts itself, or a request it was handed starts another) is the
		// run's too - only one with no such parent is the player's
		var parent = new Dictionary<int, int>();
		foreach (var (pid, par) in Procs.Snapshot()) {
			parent[pid] = par;
		}
		var theirs = new List<int>();
		foreach (var p in others) {
			var ours = false;
			for (int at = p, hops = 0; hops < 8 && parent.TryGetValue(at, out var up) && up != 0 && up != at; at = up, ++hops) {
				// a real parent of ours: the very process we started, and started before this one
				if (_ownMo2.SameProcess(up) && Procs.StartTime(up) is { } ut && Procs.StartTime(p) is { } pt && ut <= pt) {
					ours = true;
					break;
				}
			}
			if (ours) {
				_ownMo2.Add(p);
			} else {
				theirs.Add(p);
			}
		}
		return theirs;
	}

	// true while the run waits for the player's own Mod Organizer to close (the window shows it)
	public bool WaitingForPlayer { get; private set; }

	void WaitForTheirs(CancellationToken a_ct)
	{
		var theirs = TheirMo2();
		if (theirs.Count == 0) {
			return;
		}
		_log.Warn($"Mod Organizer was opened outside this run (process {string.Join(", ", theirs)}) - it is never closed under you. " +
			$"Close it when you are done and the run goes on by itself (it waits up to {UserWait.TotalMinutes:0} minutes; Stop ends the run safely)");
		// the simple log (the window's default view) says it too - the wait can be long
		_log.Simple($"Waiting for you: close the Mod Organizer you opened and the run goes on (up to {UserWait.TotalMinutes:0} minutes).");
		WaitingForPlayer = true;
		try {
			var w = Stopwatch.StartNew();
			while (TheirMo2().Count > 0) {
				a_ct.ThrowIfCancellationRequested();
				if (w.Elapsed > UserWait) {
					throw new Mo2BusyException($"the Mod Organizer you opened stayed open for {UserWait.TotalMinutes:0} minutes - the run stopped here without touching it; close it, then run again from this step");
				}
				a_ct.WaitHandle.WaitOne(Poll);
			}
		} finally {
			WaitingForPlayer = false;
		}
		_log.Detail("the Mod Organizer you opened is closed - the run goes on");
	}

	// A second ModOrganizer.exe ("run -e <tool>", an nxm link) hands its request to the open one, which can take it only once it
	// has finished loading its plugins; one started sooner gets "failed to receive data from secondary process" (2026-10-27, 19 s
	// after a start). Ready = its window open and enabled, answering messages, no box of its own open, and its own process
	// quiet (under a sixth of one core) for ReadyQuiet.
	public sealed record ReadySignals(bool Window, bool Responsive, TimeSpan Cpu, string? Box);

	public Func<ReadySignals>? ReadyProbe { get; set; }
	public Action<string>? PressBoxProbe { get; set; }
	public Func<string, string, int> Launch { get; set; } = StartDetached;
	public static TimeSpan ReadyQuiet { get; set; } = TimeSpan.FromSeconds(3);
	public static TimeSpan ReadyLimit { get; set; } = TimeSpan.FromMinutes(5);
	public static TimeSpan Poll { get; set; } = TimeSpan.FromMilliseconds(500);
	const double QuietShare = 0.16;
	const string SecondaryBox = "secondary process";
	// a second ModOrganizer.exe started in the same second as another (an nxm link, the player's click): its own box, OK ends it.
	// Answered only within a minute of a start this run sent - a box of a copy the player started is left to the player
	const string AlreadyBox = "already running";
	DateTime? _mo2LaunchedAt;

	bool WeJustLaunched() => _mo2LaunchedAt is { } t && DateTime.UtcNow - t < TimeSpan.FromMinutes(1);

	ReadySignals ReadNow()
	{
		var pids = Pids();
		var h = MainWindow();
		var cpu = pids.Aggregate(TimeSpan.Zero, (t, pid) => t + Procs.Cpu(pid));
		var box = Uia.Boxes(pids).Select(b => b.Text).FirstOrDefault();
		return new(h != 0 && Native.IsWindowEnabled(h), h != 0 && Native.Responds(h), cpu, box);
	}

	void PressBox(string a_text)
	{
		if (PressBoxProbe != null) {
			PressBoxProbe(a_text);
			return;
		}
		foreach (var b in Uia.Boxes(Pids()).Where(b => b.Text == a_text)) {
			if (Uia.Button(b.Element, "OK", "Close") is { } ok) {
				Uia.Press(ok);
			}
		}
	}

	// MO2's "secondary process" box is ours to answer (it only says the request was lost - the caller asks again);
	// any other box of MO2's keeps it from being ready and is named when the wait gives up
	public void WaitReady(CancellationToken a_ct)
	{
		var sw = Stopwatch.StartNew();
		TimeSpan? quietSince = null, lastAt = null;
		var lastCpu = TimeSpan.Zero;
		while (true) {
			a_ct.ThrowIfCancellationRequested();
			var s = (ReadyProbe ?? ReadNow)();
			var now = sw.Elapsed;
			var answer = s.Box != null && (s.Box.Contains(SecondaryBox, StringComparison.OrdinalIgnoreCase) ||
				s.Box.Contains(AlreadyBox, StringComparison.OrdinalIgnoreCase) && WeJustLaunched());
			if (answer && now > ReadyLimit) {
				throw new StepFailedException($"Mod Organizer was not ready after {ReadyLimit.TotalMinutes:0.#} minutes - its box stays open after OK: " + Trim(s.Box!));
			}
			if (answer && s.Box!.Contains(AlreadyBox, StringComparison.OrdinalIgnoreCase)) {
				_log.Detail("a second copy of Mod Organizer, started at the same moment, said one is already running - answered OK (it ends)");
				PressBox(s.Box);
				quietSince = null;
				Thread.Sleep(Poll);
				continue;
			}
			if (answer) {
				_log.Detail("Mod Organizer said it lost a request from a second copy of itself - answered OK");
				PressBox(s.Box!);
				quietSince = null;
				Thread.Sleep(Poll);
				continue;
			}
			var busy = lastAt is not { } t || (s.Cpu - lastCpu).TotalMilliseconds > QuietShare * Math.Max(1, (now - t).TotalMilliseconds);
			lastCpu = s.Cpu;
			lastAt = now;
			var why = !s.Window ? "its window is not open" : !s.Responsive ? "it does not answer" : s.Box != null ? "it shows a box: " + Trim(s.Box) :
				busy ? "it is still loading" : null;
			if (why == null) {
				quietSince ??= now;
				if (now - quietSince >= ReadyQuiet) {
					return;
				}
			} else {
				quietSince = null;
			}
			if (now > ReadyLimit) {
				throw new StepFailedException($"Mod Organizer was not ready after {ReadyLimit.TotalMinutes:0.#} minutes - {why}");
			}
			Thread.Sleep(Poll);
		}
	}

	// closes MO2 by its own window message (re-sent every 20 s), answering its questions; refused while it downloads or the
	// game runs (MO2 answers "programs are still running" by ending them - 2026-10-22 his loading game ended that way)
	// a download or a game started while the run goes: MO2 is never closed under it - the run waits for it to end (up to
	// a_busyWait), then gives up with the reason
	public static TimeSpan BusyWait { get; set; } = TimeSpan.FromMinutes(30);

	public Func<bool>? RunningProbe { get; set; }

	// a Mod Organizer whose path cannot be read counts as running (no profile file is written under it)
	bool IsRunning() => RunningProbe?.Invoke() ?? (Pids().Count > 0 || Unreadable().Count > 0);

	public Func<List<(int Pid, string Image)>>? ForeignProbe { get; set; }

	public void Close(TimeSpan? a_timeout = null, CancellationToken a_ct = default)
	{
		if (!IsRunning()) {
			return;
		}
		if (RunningProbe == null && Unreadable().Count > 0) {
			throw new Mo2BusyException("a Mod Organizer runs that this program cannot read (started as administrator?) - close it yourself, then start again");
		}
		// (a test that fakes MO2 with RunningProbe fakes the player's copy with TheirsProbe too, or it has none)
		if (!AllowCloseTheirs && (RunningProbe == null || TheirsProbe != null)) {
			WaitForTheirs(a_ct);
			if (!IsRunning()) {
				return;
			}
		}
		var waited = Stopwatch.StartNew();
		var told = false;
		while (true) {
			a_ct.ThrowIfCancellationRequested();
			var dl = DownloadsInProgress();
			var game = GameRunning();
			var theirs = (ForeignProbe ?? (() => ForeignChildren(true)))();
			if (dl.Count == 0 && game.Count == 0 && theirs.Count == 0) {
				break;
			}
			var why = dl.Count > 0 ? "Mod Organizer is downloading (" + string.Join(", ", dl.Take(3)) + ")" :
				game.Count > 0 ? "the game is running (" + string.Join(", ", game) + ")" :
				"a program you started runs under Mod Organizer (" + string.Join(", ", theirs.Select(t => t.Image).Distinct()) + ")";
			if (waited.Elapsed > BusyWait) {
				throw new Mo2BusyException($"{why} for {BusyWait.TotalMinutes:0} minutes - Mod Organizer is never closed under it");
			}
			if (!told) {
				_log.Warn($"{why} - waiting for it to end before closing Mod Organizer (up to {BusyWait.TotalMinutes:0} minutes)");
				told = true;
			}
			a_ct.ThrowIfCancellationRequested();
			Thread.Sleep(5000);
		}
		// the player may be working in the run's own Mod Organizer (one per instance - a second start lands in this one): while it
		// is in front and touched in the last PlayerQuiet, the close waits for the player to stop (never shuts a window under them)
		if (RunningProbe == null) {
			WaitWhilePlayerUses(a_ct);
		}
		_log.Detail("closing Mod Organizer");
		var sw = Stopwatch.StartNew();
		var limit = a_timeout ?? TimeSpan.FromMinutes(5);
		var lastPost = TimeSpan.FromSeconds(-30);
		while (IsRunning() && sw.Elapsed < limit) {
			if (sw.Elapsed - lastPost > TimeSpan.FromSeconds(20)) {
				var h = MainWindow();
				if (h != 0) {
					Native.PostMessage(h, Native.WM_CLOSE, 0, 0);
				}
				lastPost = sw.Elapsed;
			}
			AnswerCloseBoxes();
			Thread.Sleep(700);
		}
		if (IsRunning()) {
			throw new StepFailedException($"Mod Organizer did not close within {limit.TotalSeconds:0} s");
		}
		_log.Detail($"Mod Organizer closed after {sw.Elapsed.TotalSeconds:0} s");
		Thread.Sleep(800);
	}

	readonly HashSet<string> _leftAlone = [];

	public static TimeSpan PlayerQuiet { get; set; } = TimeSpan.FromSeconds(45);

	public static bool PlayerUsing(IReadOnlyCollection<int> a_mo2Pids, int a_foregroundPid, TimeSpan a_sinceInput) =>
		a_mo2Pids.Contains(a_foregroundPid) && a_sinceInput < PlayerQuiet;

	void WaitWhilePlayerUses(CancellationToken a_ct)
	{
		var told = false;
		var w = Stopwatch.StartNew();
		while (PlayerUsing(Pids(), Native.ForegroundPid(), Native.SinceLastInput())) {
			if (!told) {
				_log.Warn($"you are using Mod Organizer - the run closes it once you have left it alone for {PlayerQuiet.TotalSeconds:0} seconds (or switched to another window)");
				told = true;
			}
			if (w.Elapsed > UserWait) {
				throw new Mo2BusyException($"Mod Organizer was in use for {UserWait.TotalMinutes:0} minutes - the run stopped here without closing it; run again from this step when you are done");
			}
			a_ct.ThrowIfCancellationRequested();
			a_ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
		}
	}

	// the questions and notices Mod Organizer itself shows when it closes - every Windows language says them in its own words, so
	// the known shapes are read by their key words in English and by MO2's own one-button notice (an OK alone)
	public static bool CloseBox(string a_text, int a_buttons = 0) =>
		a_buttons == 1 ||
		System.Text.RegularExpressions.Regex.IsMatch(a_text, @"\b(quit\w*|exit\w*|clos\w*|error\w*|fail\w*|warning\w*|notification\w*)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

	// a box MO2 shows while closing: the downloads question is never answered Yes (Cancel, and closing stops); "programs are
	// still running" gets Yes only when every one of them is this run's (else No - MO2 would end the player's own program); its
	// Notifications window and its own Qt error boxes block the close - answered OK / Close
	void AnswerCloseBoxes()
	{
		foreach (var box in Uia.Boxes(Pids())) {
			if (box.Text.Contains("download", StringComparison.OrdinalIgnoreCase)) {
				var cancel = Uia.Button(box.Element, "Cancel", "No");
				if (cancel != null) {
					Uia.Press(cancel);
				}
				throw new Mo2BusyException("Mod Organizer asked about downloads in progress - answered Cancel, left open: " + Trim(box.Text));
			}
			if (box.Text.Contains("running", StringComparison.OrdinalIgnoreCase) && ForeignChildren(false) is { Count: > 0 } theirs) {
				if (Uia.Button(box.Element, "No", "Cancel") is { } no) {
					Uia.Press(no);
				}
				throw new Mo2BusyException($"Mod Organizer asked to end programs that are still running, among them yours ({string.Join(", ", theirs.Select(t => t.Image).Distinct())}) - answered No, left open: {Trim(box.Text)}");
			}
			// only the boxes MO2 itself shows on closing are answered (a quit question, its own error / notice); any other box
			// may be the player's own work in MO2 and is left alone - the close then waits, and names it at its limit
			if (!CloseBox(box.Text, box.Buttons.Count)) {
				if (_leftAlone.Add(box.Text)) {
					_log.Warn($"Mod Organizer shows a box this run does not answer - left for you: {Trim(box.Text)}");
				}
				continue;
			}
			// a one-button notice is pressed by its only button whatever its words (a Mod Organizer in another language)
			var ok = box.Buttons.Count == 1 ? box.Buttons[0] : Uia.Button(box.Element, "Yes", "OK", "Close", "Exit");
			if (ok != null) {
				_log.Detail($"Mod Organizer asked on close: {Trim(box.Text)} - {Uia.Name(ok)}");
				Uia.Press(ok);
			}
		}
		var main = MainWindow();
		if (main != 0 && Uia.FromHandle(main) is { } e) {
			foreach (var w in Uia.All(e, System.Windows.Automation.ControlType.Window).Where(w => Uia.Name(w) == "Notifications")) {
				if (Uia.Button(w, "Close") is { } c) {
					_log.Detail("Mod Organizer's notifications box closed");
					Uia.Press(c);
				}
			}
		}
	}

	static string Trim(string a_s) => a_s.Length > 200 ? a_s[..200] + " ..." : a_s;

	// run one of MO2's executables through the running instance; returns the tool's process once it exists
	public Process RunExecutable(string a_title, string a_image, string? a_args, CancellationToken a_ct, TimeSpan? a_startTimeout = null)
	{
		Start(a_ct);
		var old = Procs.Pids(a_image).ToHashSet();
		if (old.Count > 0) {
			throw new StepFailedException($"{a_image} is already running (process {string.Join(", ", old)}) - close it first");
		}
		_log.Detail($"running {a_title} through Mod Organizer{(string.IsNullOrEmpty(a_args) ? "" : " with " + a_args)}");
		var cmd = $"{Quote(Mo2.Exe)}{Instance} -p {Quote(ProfileName)} run -e {Quote(a_title)}" + (string.IsNullOrEmpty(a_args) ? "" : $" -a {Quote(a_args)}");
		var logAt = LogLength();
		var pid = LaunchTool(cmd, () => FreshPid(a_image, old), a_startTimeout ?? TimeSpan.FromMinutes(3), a_ct) ?? throw new StepFailedException(
			$"{a_image} did not start within {(a_startTimeout ?? TimeSpan.FromMinutes(3)).TotalMinutes:0} minutes - is MO2's executable \"{a_title}\" right?" +
			WhatMo2Said(a_title, a_image, logAt));
		_log.Detail($"{a_image} started (process {pid})");
		Ours(pid);
		return Process.GetProcessById(pid);
	}

	// his checklist 479: the tools at a lower priority (the setup's "lowerPriority") - the PC stays smooth while they work; the
	// processes a tool starts after this take its priority with them (Windows passes below-normal on to its children)
	public bool LowerPriority { get; set; }

	public void Ours(int a_pid)
	{
		Started.Add(a_pid);
		if (!LowerPriority) {
			return;
		}
		try {
			using var p = Process.GetProcessById(a_pid);
			p.PriorityClass = ProcessPriorityClass.BelowNormal;
		} catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) {
			// it ended already, or Windows refused: it runs at its own priority
		}
	}

	// Mod Organizer's own log of this session (beside its ModOrganizer.ini, in "logs")
	string Mo2Log => Path.Combine(Path.GetDirectoryName(Mo2.IniPath)!, "logs", "mo_interface.log");

	long LogLength()
	{
		try {
			return File.Exists(Mo2Log) ? new FileInfo(Mo2Log).Length : 0;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return 0;
		}
	}

	// a tool Mod Organizer was asked to start never appeared: what Mod Organizer shows (any window beside its main one) and what it
	// wrote in its log since the request, so the failure says why instead of only that (2026-11-07: Landscape Fixer's start went
	// unanswered, and by the time anyone looked Mod Organizer had rotated its logs away)
	string WhatMo2Said(string a_title, string a_image, long a_logFrom)
	{
		var parts = new List<string>();
		try {
			var main = MainWindow();
			foreach (var h in Native.TopWindowsOf(Pids()).Where(h => h != main)) {
				var text = Uia.FromHandle(h) is { } e ? Uia.TextOf(e, 300) : "";
				var title = Native.WindowText(h);
				if (title != "" || text != "") {
					parts.Add($"a window \"{title}\"{(text == "" || text == title ? "" : ": " + text)}");
				}
			}
		} catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) {
		}
		var lines = LogLinesSince(Mo2Log, a_logFrom, a_title, a_image);
		return (parts.Count > 0 ? " Mod Organizer shows " + string.Join("; ", parts) + "." : " Mod Organizer shows no box.") +
			(lines.Count > 0 ? " Its log since the request: " + string.Join(" | ", lines) : "");
	}

	// the lines a log gained after a_from (the whole log when it was started again meanwhile) that name the tool or are a warning /
	// error of Mod Organizer's - at most a_max, newest last
	public static List<string> LogLinesSince(string a_file, long a_from, string a_title, string a_image, int a_max = 6)
	{
		try {
			if (!File.Exists(a_file)) {
				return [];
			}
			// only the part written since the request is read (a long session's log can be large), and at most its last 1 MB
			using var fs = new FileStream(a_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			var from = a_from <= fs.Length ? a_from : 0;
			from = Math.Max(from, fs.Length - 1024 * 1024);
			fs.Seek(from, SeekOrigin.Begin);
			var bytes = new byte[fs.Length - from];
			fs.ReadExactly(bytes);
			var text = System.Text.Encoding.UTF8.GetString(bytes);
			var stem = Path.GetFileNameWithoutExtension(a_image);
			return text.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l != "" &&
				(l.Contains(a_title, StringComparison.OrdinalIgnoreCase) || l.Contains(stem, StringComparison.OrdinalIgnoreCase) ||
				 System.Text.RegularExpressions.Regex.IsMatch(l, @"\[(E|W)\]|\b(error|warning|failed)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
				.TakeLast(a_max).Select(l => l.Length > 240 ? l[..240] + " ..." : l).ToList();
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return [];
		}
	}

	// hand MO2 one request and wait for the tool's process; a request MO2 lost (its "secondary process" box) is asked again
	// once MO2 is ready - at most twice more
	static int FreshPid(string a_image, HashSet<int> a_old)
	{
		var procs = Procs.ByImage(a_image);
		var pid = procs.Select(p => p.Id).FirstOrDefault(id => !a_old.Contains(id));
		procs.ForEach(p => p.Dispose());
		return pid;
	}

	public int? LaunchTool(string a_cmd, Func<int> a_freshPid, TimeSpan a_limit, CancellationToken a_ct)
	{
		for (var attempt = 0; attempt < 3; ++attempt) {
			WaitReady(a_ct);
			_mo2LaunchedAt = DateTime.UtcNow;
			// the second Mod Organizer that hands the request on is the run's own, never taken for the player's while it lives
			_ownMo2.Add(Launch(a_cmd, Path.GetDirectoryName(Mo2.Exe)!));
			var until = DateTime.UtcNow + a_limit;
			var lost = false;
			while (DateTime.UtcNow < until && !lost) {
				a_ct.ThrowIfCancellationRequested();
				if (a_freshPid() is var pid && pid != 0) {
					return pid;
				}
				lost = (ReadyProbe ?? ReadNow)().Box?.Contains(SecondaryBox, StringComparison.OrdinalIgnoreCase) == true;
				Thread.Sleep(Poll);
			}
			if (!lost) {
				return null;
			}
			_log.Warn("Mod Organizer lost the request (a second copy of it started while it was busy) - asking again once it is ready");
		}
		return null;
	}

	// the game through MO2's executable (SKSE); returns the game's process id once it exists
	public int LaunchGame(string a_title, Func<int> a_gamePid, CancellationToken a_ct)
	{
		EnsureSteam(a_ct);
		Start(a_ct);
		_log.Detail($"starting the game through Mod Organizer ({a_title})");
		var pid = LaunchTool($"{Quote(Mo2.Exe)}{Instance} -p {Quote(ProfileName)} run -e {Quote(a_title)}", a_gamePid, TimeSpan.FromMinutes(5), a_ct) ??
			throw new StepFailedException($"the game did not start within 5 minutes - is MO2's executable \"{a_title}\" right?");
		// the run's own game: the only one it may close (a game the player starts is never touched)
		Started.Add(pid);
		return pid;
	}

	// a Steam copy of the game needs Steam running: without it Mod Organizer's game start stops on its own "start Steam?" question
	// and waits there - so Steam is started first (silently, from the path it registered) and given time to sign in
	public Func<bool>? SteamRunningProbe { get; set; }
	public Func<string?>? SteamExeProbe { get; set; }
	public static TimeSpan SteamWait { get; set; } = TimeSpan.FromMinutes(2);
	public static TimeSpan SteamSettle { get; set; } = TimeSpan.FromSeconds(20);

	// a Steam build ships steam_api64.dll wherever its folder was copied to (a modlist's "Stock Game"); a GOG build does not
	public static bool IsSteamGame(string a_gameDir) =>
		File.Exists(Path.Combine(a_gameDir, "steam_api64.dll")) || a_gameDir.Replace('/', '\\').Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase);

	static string? SteamExe()
	{
		var p = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe", null) as string;
		return string.IsNullOrEmpty(p) ? null : Path.GetFullPath(p.Replace('/', '\\'));
	}

	// a Steam of another Windows account on this PC does not serve this user's game
	static bool SteamInMySession()
	{
		int session;
		using (var self = Process.GetCurrentProcess()) {
			session = self.SessionId;
		}
		var found = false;
		foreach (var p in Procs.ByImage("steam.exe")) {
			using (p) {
				try {
					found |= p.SessionId == session;
				} catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) {
				}
			}
		}
		return found;
	}

	public void EnsureSteam(CancellationToken a_ct) => EnsureSteam(Mo2.GameDir, a_ct);

	public void EnsureSteam(string a_gameDir, CancellationToken a_ct)
	{
		if (!IsSteamGame(a_gameDir)) {
			return;
		}
		bool Up() => SteamRunningProbe?.Invoke() ?? SteamInMySession();
		if (Up()) {
			return;
		}
		var exe = (SteamExeProbe ?? SteamExe)();
		if (exe == null || (SteamExeProbe == null && !File.Exists(exe))) {
			throw new StepFailedException("Steam is not running and its program could not be found - start Steam, then start the run again");
		}
		_log.Detail("Steam is not running - starting it (Mod Organizer waits for it before the game starts)");
		Launch($"{Quote(exe)} -silent", Path.GetDirectoryName(exe)!);
		var sw = Stopwatch.StartNew();
		while (!Up()) {
			a_ct.ThrowIfCancellationRequested();
			if (sw.Elapsed > SteamWait) {
				throw new StepFailedException($"Steam did not start within {SteamWait.TotalMinutes:0} minutes - start it, then start the run again");
			}
			Thread.Sleep(Poll);
		}
		a_ct.WaitHandle.WaitOne(SteamSettle);
		a_ct.ThrowIfCancellationRequested();
	}

	// run an arbitrary program inside MO2's virtual folder (MO2 cannot start some tools straight - FasterNGIO ends at once); its
	// process (by its image, once it appears) is the run's own, so a stop ends it by PID
	public int? RunInside(string a_program, string a_args, string a_image, CancellationToken a_ct)
	{
		Start(a_ct);
		var old = Procs.Pids(a_image).ToHashSet();
		var cmd = $"{Quote(Mo2.Exe)}{Instance} -p {Quote(ProfileName)} run {Quote(a_program)} -a {Quote(a_args)}";
		var pid = LaunchTool(cmd, () => FreshPid(a_image, old), TimeSpan.FromMinutes(1), a_ct);
		if (pid is { } p) {
			Ours(p);
		}
		return pid;
	}
}
