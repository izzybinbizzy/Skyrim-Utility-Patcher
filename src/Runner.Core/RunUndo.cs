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
		try {
			if (!Directory.Exists(dir) || !Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any()) {
				return;
			}
			File.Delete(part);
			var want = Sizes(dir);
			ZipFile.CreateFromDirectory(dir, part, CompressionLevel.Fastest, false);
			using (var z = ZipFile.OpenRead(part)) {
				var files = z.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
				if (files.Count != want.Count || files.Any(e => !want.TryGetValue(e.FullName, out var n) || n != e.Length)) {
					throw new InvalidDataException($"the zip holds {files.Count} of {want.Count} files");
				}
			}
			File.Move(part, zip, true);
			foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Where(f => f.IsReadOnly)) {
				f.IsReadOnly = false;
			}
			Directory.Delete(dir, true);
			a_log.Detail($"the old outputs in this run's backup packed: {want.Count} file(s), {want.Values.Sum() / 1048576} MB into {new FileInfo(zip).Length / 1048576} MB");
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException) {
			a_log.Warn("the old outputs stay unpacked in this run's backup: " + e.Message);
			try {
				File.Delete(part);
			} catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) {
				// a part left behind goes with the run's backup
			}
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

	// why this run cannot be undone now (null = it can): only a setup's last run still standing, ended, with its backup there
	public static string? WhyNot(Journal a_j, IEnumerable<Journal> a_runs)
	{
		if (a_j.Undone) {
			return "this run was undone already";
		}
		if (a_j.Unfinished || (a_j.InGameAfter && a_j.OwnerAlive)) {
			return "this run did not finish - put your profile back first";
		}
		if (a_j.Built.Count == 0 && !a_j.Parked.Any(p => p.Replaced && !p.PutBack)) {
			return "this run built nothing to undo";
		}
		if (a_runs.Any(j => j.Folder != a_j.Folder && !j.Undone && j.Started > a_j.Started && string.Equals(j.SetupFile, a_j.SetupFile, StringComparison.OrdinalIgnoreCase))) {
			return "only the last run of a setup can be undone";
		}
		if (!Directory.Exists(a_j.SnapshotDir) || !Directory.EnumerateFiles(a_j.SnapshotDir).Any()) {
			return "this run's copy of your mod and plugin lists is gone";
		}
		if (a_j.Parked.Any(p => p.Replaced && !p.PutBack) && !File.Exists(Path.Combine(a_j.BackupDir, OutputsZip)) && a_j.Parked.Any(p => p.Replaced && !p.PutBack && !Directory.Exists(p.To))) {
			return "this run's backup is gone";
		}
		return null;
	}
}
