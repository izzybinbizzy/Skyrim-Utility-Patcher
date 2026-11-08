using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToolRunner;

public enum StepState
{
	Pending,
	Running,
	Done,
	Failed,
	Skipped,
	Stopped,
}

public enum RunState
{
	Running,
	Finished,
	Failed,
	Stopped,
	Restored,
}

public sealed class StepRecord
{
	public string Key { get; set; } = "";
	public string Name { get; set; } = "";
	public StepState State { get; set; }
	public DateTime? Started { get; set; }
	public DateTime? Ended { get; set; }
	public string Note { get; set; } = "";
	public string Activity { get; set; } = "";
	public double? Progress { get; set; }
	// the mod this run's step builds into (null: a step with no output mod)
	public string? Output { get; set; }
}

// an output moved into the run's backup before a tool rewrote it; put back when its step did not finish
public sealed class ParkRecord
{
	public string Step { get; set; } = "";
	public string Mod { get; set; } = "";
	public string From { get; set; } = "";
	public string To { get; set; } = "";
	public bool Replaced { get; set; }
	public bool PutBack { get; set; }
}

// a file outside the mods that a step changes for its run (a tool's settings, DynDOLOD's preset, NGIO's flag): its old bytes
// kept in the run's backup (Backup null = it did not exist), put back by the step - or by Restore after a crash
public sealed class KeptFile
{
	public string Path { get; set; } = "";
	public string? Backup { get; set; }
	public bool Done { get; set; }
}

// The run record on disk: everything needed to put the profile back after a crash, a Stop now or a power cut, and what
// the window shows. Written atomically after every change. The order files as they were before the run sit beside it in
// "profile before\", byte for byte.
public sealed class Journal
{
	public string Id { get; set; } = "";
	public string SetupFile { get; set; } = "";
	public string SetupName { get; set; } = "";
	public string Profile { get; set; } = "";
	public string Mo2Exe { get; set; } = "";
	public string? Mo2Instance { get; set; }
	public string BackupDir { get; set; } = "";
	public int Pid { get; set; }
	// the owner's start time: a PID reused by another program after a crash never passes for the run
	public DateTime? PidStarted { get; set; }
	public DateTime Started { get; set; }
	public DateTime? Ended { get; set; }
	public RunState State { get; set; }
	public string Verdict { get; set; } = "";
	public string? ResumeFrom { get; set; }
	public bool StopAfterStep { get; set; }
	public List<StepRecord> Steps { get; set; } = [];
	public List<string> Built { get; set; } = [];
	public List<string> Retire { get; set; } = [];
	public List<string> Added { get; set; } = [];
	public List<ParkRecord> Parked { get; set; } = [];
	public List<KeptFile> Kept { get; set; } = [];
	// the marker left where the player stood before the clean save's teleport (the save to play takes them back to it)
	public string? ReturnMarker { get; set; }
	public List<string> Notes { get; set; } = [];

	[JsonIgnore]
	public string Folder { get; set; } = "";
	[JsonIgnore]
	public string File => Path.Combine(Folder, "journal.json");
	[JsonIgnore]
	public string SnapshotDir => Path.Combine(Folder, "profile before");

	static readonly JsonSerializerOptions Options = new() {
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter() },
	};

	readonly Lock _gate = new();

	public void Save()
	{
		lock (_gate) {
			Files.WriteAtomic(File, JsonSerializer.SerializeToUtf8Bytes(this, Options));
		}
	}

	public static Journal Load(string a_folder)
	{
		var j = JsonSerializer.Deserialize<Journal>(Files.ReadShared(Path.Combine(a_folder, "journal.json")), Options) ??
		        throw new InvalidDataException("empty run record in " + a_folder);
		j.Folder = a_folder;
		return j;
	}

	public StepRecord? Step(string a_key) => Steps.FirstOrDefault(s => s.Key == a_key);

	public void SaveSnapshot(IReadOnlyDictionary<string, byte[]> a_snap)
	{
		Directory.CreateDirectory(SnapshotDir);
		foreach (var (f, b) in a_snap) {
			Files.WriteAtomic(Path.Combine(SnapshotDir, f), b);
		}
	}

	public Dictionary<string, byte[]> LoadSnapshot()
	{
		var d = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
		if (Directory.Exists(SnapshotDir)) {
			foreach (var f in Directory.GetFiles(SnapshotDir)) {
				d[Path.GetFileName(f)] = System.IO.File.ReadAllBytes(f);
			}
		}
		return d;
	}

	// set when putting the profile back failed (e.g. a file locked): the window keeps offering Restore until it worked
	public bool NeedsRestore { get; set; }
	// the player undid this run (his checklist 454): its outputs are no longer in the profile
	public bool Undone { get; set; }

	public bool Unfinished => State == RunState.Running || NeedsRestore;

	// the save to play is being made: the run's profile is already put back (it is not Unfinished - nothing to restore), but its
	// game still runs, so the window keeps it as the running run (Stop and the close guard reach it)
	public bool InGameAfter { get; set; }

	// is the program that wrote it still alive? (a run that is still going is never "unfinished")
	public bool OwnerAlive => Pid != 0 && Procs.Alive(Pid) && IsRunner(Pid) &&
		(PidStarted == null || Procs.StartTime(Pid) is { } s && Math.Abs((s - PidStarted.Value).TotalSeconds) < 2);

	public static bool IsRunner(int a_pid)
	{
		try {
			using var p = System.Diagnostics.Process.GetProcessById(a_pid);
			// a copy the player renamed is known by the product name inside its file (the start time check above keeps a reused
			// PID out)
			return IsRunnerName(p.ProcessName) || IsRunnerName(ProductOf(p));
		} catch (ArgumentException) {
			return false;
		} catch (InvalidOperationException) {
			return false;
		}
	}

	// the program under its name or any it had ("Skyrim Utility Patcher", "MO2 Tool Runner", ...), its project names when built
	// without one, and the offline tests' own process. The rename of 2026-11-03 had left this on the old names only: every real
	// run then read as dead to the window - Stop now sent nothing, closing the window neither asked nor stopped the run, and a
	// running run showed as interrupted (his finds of 2026-11-04); the tests did not see it, their own process name matched
	static string ProductOf(System.Diagnostics.Process a_p)
	{
		try {
			return a_p.MainModule?.FileVersionInfo.ProductName ?? "";
		} catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) {
			return "";
		}
	}

	public static bool IsRunnerName(string a_processName) =>
		Brand.AllNames.Any(n => string.Equals(a_processName, n, StringComparison.OrdinalIgnoreCase)) ||
		a_processName is "Runner.App" or "Runner.Tests";

	public static IEnumerable<Journal> All(string a_runsDir)
	{
		if (!Directory.Exists(a_runsDir)) {
			yield break;
		}
		foreach (var d in Directory.GetDirectories(a_runsDir).OrderDescending()) {
			Journal? j = null;
			try {
				if (System.IO.File.Exists(Path.Combine(d, "journal.json"))) {
					j = Load(d);
				}
			} catch (JsonException) {
			} catch (IOException) {
			}
			if (j != null) {
				yield return j;
			}
		}
	}

	public static Journal? Interrupted(string a_runsDir) => All(a_runsDir).FirstOrDefault(j => j.Unfinished && !j.OwnerAlive);
}

// The run records for a reader that looks often (the window, once a second): a record is read again only when its file
// changed, so a hundred past runs cost a hundred file looks, not a hundred reads. The records it hands out are shared - a
// caller that changes one loads its own copy (Journal.Load).
public sealed class JournalCache(string a_runsDir)
{
	readonly Dictionary<string, (DateTime Stamp, long Size, Journal Run)> _seen = new(StringComparer.OrdinalIgnoreCase);
	readonly Lock _lock = new();

	// the window reads it from its poll thread and from its own thread
	public List<Journal> Read()
	{
		lock (_lock) {
			return ReadLocked();
		}
	}

	List<Journal> ReadLocked()
	{
		var list = new List<Journal>();
		if (!Directory.Exists(a_runsDir)) {
			_seen.Clear();
			return list;
		}
		var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var d in Directory.GetDirectories(a_runsDir).OrderDescending()) {
			var f = new FileInfo(Path.Combine(d, "journal.json"));
			if (!f.Exists) {
				continue;
			}
			live.Add(d);
			if (_seen.TryGetValue(d, out var c) && c.Stamp == f.LastWriteTimeUtc && c.Size == f.Length) {
				list.Add(c.Run);
				continue;
			}
			try {
				var j = Journal.Load(d);
				_seen[d] = (f.LastWriteTimeUtc, f.Length, j);
				list.Add(j);
			} catch (JsonException) {
			} catch (IOException) {
			}
		}
		foreach (var gone in _seen.Keys.Where(k => !live.Contains(k)).ToList()) {
			_seen.Remove(gone);
		}
		return list;
	}
}
