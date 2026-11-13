using System.Text.RegularExpressions;

namespace ToolRunner;

// Clean removal (his checklist 484): everything the program keeps of its own goes to the Recycle Bin - its data folder (setups,
// run records, learned tool settings, themes, the change history) and the run backups it made in each setup's backup folder.
// The output mods the tools made stay in Mod Organizer: they are part of the player's load order, and Mod Organizer removes them.
// Refused while a run or a queue goes, and while a run still needs its profile put back.
public static class DataRemoval
{
	public sealed record Plan(List<string> Remove, List<string> Kept, string? Refused, string DataDir = "");

	// why removal may not go ahead now (null = it may): a run going or not put back
	static string? Busy(string a_dataDir)
	{
		var all = Journal.All(Runner.RunsDir(a_dataDir)).ToList();
		return all.Any(j => j.Live && j.OwnerAlive) || QueueRun.Read(a_dataDir) != null ? "a run is going - wait for it to end" :
			all.Any(j => j.Unfinished) ? "a run did not finish - put your profile back first, or its switched-off outputs stay off" : null;
	}

	public static Plan Make(string a_dataDir, bool a_keepSetups)
	{
		var remove = new List<string>();
		var kept = new List<string>();
		var refused = Busy(a_dataDir);
		var setups = Path.Combine(a_dataDir, "Setups");
		// each setup's own run backups, by the run-folder name the runner gives them; nothing else in that folder is touched. A setup
		// that cannot be read, or a folder that cannot be listed, refuses the removal: its backups would be left behind unseen
		try {
			if (Directory.Exists(setups)) {
				foreach (var f in Directory.GetFiles(setups, "*.json")) {
					Setup s;
					try {
						s = Setup.Load(f);
					} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) {
						refused ??= $"the setup \"{Path.GetFileName(f)}\" could not be read ({e.Message}) - its backups could not be found";
						continue;
					}
					remove.AddRange(BackupsOf(s.BackupDir, s.Name, ref refused));
				}
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			refused ??= "the Setups folder could not be listed: " + e.Message;
		}
		// a setup deleted since still has its run records, and each names its own backup folder - taken only when that folder is
		// named the way the runner names a run backup, so a record that points anywhere else removes nothing
		try {
			remove.AddRange(Journal.All(Runner.RunsDir(a_dataDir)).Select(RunBackup).OfType<string>());
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			refused ??= "the run records could not be listed: " + e.Message;
		}
		// its work folder in ProgramData (FasterNGIO's wrapper), under the new and the old name
		foreach (var work in WorkRoots.Where(Directory.Exists)) {
			remove.Add(work);
		}
		if (Directory.Exists(a_dataDir)) {
			if (a_keepSetups && Directory.Exists(setups)) {
				kept.Add(setups);
				remove.AddRange(Directory.EnumerateFileSystemEntries(a_dataDir).Where(e => !Files.SamePath(e, setups)));
			} else {
				remove.Add(a_dataDir);
			}
		}
		// one spelling per folder (a setup's own listing and a run record may write the same one differently)
		return new Plan(remove.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
			kept, refused, a_dataDir);
	}

	// the offline tests point this elsewhere (the real ProgramData folders are never touched by a test)
	public static IEnumerable<string> WorkRoots { get; set; } = [Brand.WorkRoot, .. Brand.OldWorkRoots];

	public static List<string> BackupsOf(string a_backupDir, string a_setupName)
	{
		string? ignored = null;
		return BackupsOf(a_backupDir, a_setupName, ref ignored);
	}

	static string? RunBackup(Journal a_j)
	{
		if (string.IsNullOrWhiteSpace(a_j.BackupDir) || string.IsNullOrWhiteSpace(a_j.SetupName)) {
			return null;
		}
		// named like a run backup AND holding the copy of the run's profile every run backup starts with
		var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a_j.BackupDir));
		return RunBackupName(a_j.SetupName).IsMatch(Path.GetFileName(dir)) && !string.IsNullOrWhiteSpace(a_j.Profile) &&
		       Directory.Exists(Path.Combine(dir, "profile " + a_j.Profile)) ? dir : null;
	}

	static Regex RunBackupName(string a_setupName) => new(@"^\d{4}-\d\d-\d\d \d\d-\d\d-\d\d " + Regex.Escape(a_setupName) + "$");

	// a backup folder that cannot be listed sets a_problem (the removal is then refused, never silently incomplete)
	static List<string> BackupsOf(string a_backupDir, string a_setupName, ref string? a_problem)
	{
		if (a_backupDir == "" || !Directory.Exists(a_backupDir)) {
			return [];
		}
		var own = RunBackupName(a_setupName);
		try {
			return Directory.GetDirectories(a_backupDir).Where(d => own.IsMatch(Path.GetFileName(d))).ToList();
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			a_problem ??= $"the backup folder \"{a_backupDir}\" could not be listed ({e.Message})";
			return [];
		}
	}

	// a path on a network or cloud-mapped drive: Windows deletes it for good there instead of putting it in the Recycle Bin
	public static Func<string, bool> OnNetwork { get; set; } = a_path => {
		var full = Path.GetFullPath(a_path);
		if (full.StartsWith(@"\\", StringComparison.Ordinal)) {
			return true;
		}
		try {
			return new DriveInfo(Path.GetPathRoot(full)!).DriveType is DriveType.Network or DriveType.NoRootDirectory or DriveType.Unknown;
		} catch (Exception e) when (e is ArgumentException or IOException) {
			return true;
		}
	};

	[System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
	[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
	static extern bool GetVolumeNameForVolumeMountPoint(string a_root, [System.Runtime.InteropServices.Out] char[] a_name, uint a_length);

	// How much the Recycle Bin of a path's drive takes at most, in bytes; 0 = that drive keeps no bin (a stick, a card, a bin
	// switched off). Windows keeps the limit per drive (measured on a PC with two 900 GB drives: about 46 and 50 GB), and what is
	// bigger than it is not recycled: asked without its own questions, as here, Windows deletes it for good. A drive Windows
	// holds no limit for yet is judged by Windows' own starting rule (a tenth of the first 40 GB and a twentieth of the rest).
	// The offline tests answer for it.
	public static Func<string, long> BinRoom { get; set; } = a_path => {
		var root = Path.GetPathRoot(Path.GetFullPath(a_path)) ?? "";
		DriveInfo drive;
		try {
			drive = new DriveInfo(root);
			if (drive.DriveType == DriveType.Removable) {
				return 0;
			}
		} catch (Exception e) when (e is ArgumentException or IOException) {
			return 0;
		}
		var name = new char[64];
		if (GetVolumeNameForVolumeMountPoint(root, name, (uint)name.Length) && new string(name).TrimEnd('\0') is var volume &&
		    Regex.Match(volume, @"\{[0-9a-fA-F-]+\}") is { Success: true } id) {
			using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\" + id.Value);
			if (key?.GetValue("NukeOnDelete") is int nuke && nuke != 0) {
				return 0;
			}
			if (key?.GetValue("MaxCapacity") is int mb && mb > 0) {
				return mb * 1048576L;
			}
		}
		const long FortyGb = 40L << 30;
		try {
			var size = drive.TotalSize;
			return Math.Min(size, FortyGb) / 10 + Math.Max(0, size - FortyGb) / 20;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return 0;
		}
	};

	static long SizeOf(string a_path) => Directory.Exists(a_path) ? Files.SizeOf(a_path) : File.Exists(a_path) ? new FileInfo(a_path).Length : 0;

	// every entry of the plan to the Recycle Bin; returns what could not go, each with why (a_recycle is for the offline tests)
	public static List<string> Carry(Plan a_plan, Func<string, string?>? a_recycle = null)
	{
		if (a_plan.Refused != null) {
			throw new InvalidOperationException(a_plan.Refused);
		}
		// the run lock through the whole removal, and the check made again under it: a run started from the command line while the
		// list was on screen is never left without its record and backups
		using var hold = a_plan.DataDir == "" ? null : Runner.HoldIfFree() ?? throw new InvalidOperationException("a run started - nothing was removed");
		if (a_plan.DataDir != "" && Busy(a_plan.DataDir) is { } now) {
			throw new InvalidOperationException(now + " - nothing was removed");
		}
		var recycle = a_recycle ?? Recycle;
		var failed = new List<string>();
		foreach (var p in a_plan.Remove.Where(p => File.Exists(p) || Directory.Exists(p))) {
			if (OnNetwork(p)) {
				failed.Add($"{p}: on a network drive, where Windows would delete it for good instead of recycling it - left in place");
				continue;
			}
			// "to the Recycle Bin, never erased" holds only for what fits the bin: what does not is left where it is, and named
			try {
				var (size, room) = (SizeOf(p), BinRoom(p));
				if (size > room) {
					failed.Add(room == 0 ? $"{p}: its drive keeps no Recycle Bin, so Windows would delete it for good - left in place; delete it yourself when you are sure"
						: $"{p}: {size / 1048576:#,0} MB is more than its drive's Recycle Bin takes ({room / 1048576:#,0} MB), so Windows would delete it for good - left in place; delete it yourself when you are sure");
					continue;
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				failed.Add($"{p}: it could not be measured ({e.Message}) - left in place");
				continue;
			}
			if (recycle(p) is { } why) {
				failed.Add($"{p}: {why}");
			}
		}
		return failed;
	}

	// to the Recycle Bin (never erased) through Windows' own file operation, as Microsoft.VisualBasic's FileSystem calls it - never a
	// hand-rolled SHFileOperationW (one once renamed a file instead of recycling it); null = gone, else why not
	static string? Recycle(string a_path)
	{
		try {
			var full = Path.GetFullPath(a_path);
			if (Directory.Exists(full)) {
				Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(full, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
			} else if (File.Exists(full)) {
				Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(full, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or System.Security.SecurityException) {
			return e.Message;
		}
		return File.Exists(a_path) || Directory.Exists(a_path) ? "it is still there" : null;
	}
}
