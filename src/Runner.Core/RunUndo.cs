using System.IO.Compression;

namespace ToolRunner;

// Undo the last run, and its backups kept small (his checklist 454). Once a run has ended with nothing left to put back, the old
// outputs in its backup are packed into one zip - the folder goes only after the zip is read back. Runner.Undo unpacks them when
// the player asks for the run to be undone.
public static class RunUndo
{
	public const string OutputsFolder = "outputs";
	public const string OutputsZip = "outputs.zip";

	// what is in a run's backup as files (name relative to the folder -> size), the empty folders left out
	static Dictionary<string, long> Sizes(string a_dir) =>
		new DirectoryInfo(a_dir).EnumerateFiles("*", SearchOption.AllDirectories)
			.ToDictionary(f => Path.GetRelativePath(a_dir, f.FullName).Replace('\\', '/'), f => f.Length, StringComparer.OrdinalIgnoreCase);

	// the old outputs into one zip. A zip that does not read back whole, or any failure, keeps the folder and says so - packing only
	// saves room, it never loses a backup or fails a run
	public static void Pack(string a_backup, RunLog a_log)
	{
		var dir = Path.Combine(a_backup, OutputsFolder);
		var zip = Path.Combine(a_backup, OutputsZip);
		var part = zip + ".part";
		Dictionary<string, long> want;
		try {
			if (!Directory.Exists(dir) || !Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any()) {
				return;
			}
			File.Delete(part);
			want = Sizes(dir);
			ZipFile.CreateFromDirectory(dir, part, CompressionLevel.Fastest, false);
			using (var z = ZipFile.OpenRead(part)) {
				var files = z.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
				if (files.Count != want.Count || files.Any(e => !want.TryGetValue(e.FullName, out var n) || n != e.Length)) {
					throw new InvalidDataException($"the zip holds {files.Count} of {want.Count} files");
				}
			}
			File.Move(part, zip, true);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException) {
			a_log.Warn("the old outputs stay unpacked in this run's backup: " + e.Message);
			try {
				File.Delete(part);
			} catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) {
				// a part left behind goes with the run's backup
			}
			return;
		}
		// the zip is the whole copy from here on: a folder that will not go only takes room, and Unpack writes over what is left of it
		try {
			foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Where(f => f.IsReadOnly)) {
				f.IsReadOnly = false;
			}
			Directory.Delete(dir, true);
			a_log.Detail($"the old outputs in this run's backup packed: {want.Count} file(s), {want.Values.Sum() / 1048576} MB into {new FileInfo(zip).Length / 1048576} MB");
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			a_log.Warn("the old outputs are packed in this run's backup, but part of their folder could not be removed: " + e.Message);
		}
	}

	// the zip back into its folder, for an undo; nothing outside that folder is ever written
	public static void Unpack(string a_backup)
	{
		var zip = Path.Combine(a_backup, OutputsZip);
		if (!File.Exists(zip)) {
			return;
		}
		ZipFile.ExtractToDirectory(zip, Path.Combine(a_backup, OutputsFolder), true);
		File.Delete(zip);
	}

	// what stands at a_from (its meta.ini stays) moved into a folder of its own under a_aside: what is set aside is never written over,
	// not by a second output of the same mod, not by a second press
	public static void SetAside(string a_from, string a_aside, string a_name)
	{
		static bool Meta(string a_n) => a_n.Equals("meta.ini", StringComparison.OrdinalIgnoreCase);
		if (!Directory.Exists(a_from) || !Directory.EnumerateFileSystemEntries(a_from).Any(e => !Meta(Path.GetFileName(e)))) {
			return;
		}
		var dst = Path.Combine(a_aside, a_name);
		for (var i = 2; Directory.Exists(dst); ++i) {
			dst = Path.Combine(a_aside, $"{a_name} {i}");
		}
		Files.MoveContents(a_from, dst, Meta);
	}

	// did this run leave anything an undo would take back? (one that built nothing and put everything back did not)
	public static bool LeftSomething(Journal a_j) => a_j.Built.Count > 0 || a_j.Retire.Count > 0 || a_j.Parked.Any(p => p.Replaced && !p.PutBack);

	// a later run still standing that changed what undoing a_j would put back: the same setup's, one that moved the same output or tool
	// folder, or another's on the same profile or with an output of a_j's - undoing a_j would lay its old lists and outputs over that run
	static bool InTheWay(Journal a_later, Journal a_j, HashSet<string> a_folders)
	{
		if (a_later.Undone || a_later.Started <= a_j.Started || (!a_later.Unfinished && !LeftSomething(a_later))) {
			return false;
		}
		static bool Same(string? a_a, string? a_b) => string.Equals(a_a, a_b, StringComparison.OrdinalIgnoreCase);
		static bool SamePlace(string? a_a, string? a_b) => string.IsNullOrEmpty(a_a) || string.IsNullOrEmpty(a_b) ? string.IsNullOrEmpty(a_a) && string.IsNullOrEmpty(a_b) : Files.SamePath(a_a, a_b);
		// a portable instance may be left unset in one setup and named in another: it is the folder beside its Mod Organizer
		static string? Instance(Journal a_r) => string.IsNullOrEmpty(a_r.Mo2Instance) ? Path.GetDirectoryName(a_r.Mo2Exe) : a_r.Mo2Instance;
		return Same(a_later.SetupFile, a_j.SetupFile) || a_folders.Overlaps(FoldersMoved(a_later)) ||
			(SamePlace(Instance(a_later), Instance(a_j)) &&
			 (Same(a_later.Profile, a_j.Profile) || a_later.Built.Intersect(a_j.Built, StringComparer.OrdinalIgnoreCase).Any()));
	}

	// the mods and tool folders a run emptied and still holds the old content of
	static HashSet<string> FoldersMoved(Journal a_r) =>
		a_r.Parked.Where(p => p.Replaced && !p.PutBack && (!p.IsFolder || p.Moved) && p.From != "")
			.Select(p => Path.GetFullPath(p.From).TrimEnd('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);

	// why this run cannot be undone now (null = it can): only the last run on its profile and outputs still standing, ended, with its backup there
	public static string? WhyNot(Journal a_j, IEnumerable<Journal> a_runs)
	{
		if (a_j.Undone) {
			return "this run was undone already";
		}
		// one begun is ended whatever else has changed: only that frees the profile again
		if (a_j.Undoing) {
			return null;
		}
		if (a_j.Unfinished || (a_j.InGameAfter && a_j.OwnerAlive)) {
			return "this run did not finish - put your profile back first";
		}
		if (!LeftSomething(a_j)) {
			return "this run built nothing to undo";
		}
		var folders = FoldersMoved(a_j);
		if (a_runs.FirstOrDefault(j => InTheWay(j, a_j, folders)) is { } later) {
			return $"only the last run can be undone - a later run of \"{later.SetupName}\" changed the same profile or outputs";
		}
		if (!Directory.Exists(a_j.SnapshotDir) || !Directory.EnumerateFiles(a_j.SnapshotDir).Any()) {
			return "this run's copy of your mod and plugin lists is gone";
		}
		if (!File.Exists(Path.Combine(a_j.BackupDir, OutputsZip)) && a_j.Parked.Any(p => p.Replaced && !p.PutBack && (!p.IsFolder || p.Moved) && !Directory.Exists(p.To))) {
			return "this run's backup is gone";
		}
		return null;
	}
}
