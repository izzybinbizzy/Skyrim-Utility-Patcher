using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ToolRunner;

// Clean removal (his checklist 484): everything the program keeps of its own goes to the Recycle Bin - its data folder (setups,
// run records, learned tool settings, themes, the change history) and the run backups it made in each setup's backup folder.
// The output mods the tools made stay in Mod Organizer: they are part of the player's load order, and Mod Organizer removes them.
// Refused while a run or a queue goes, and while a run still needs its profile put back.
public static partial class DataRemoval
{
	public sealed record Plan(List<string> Remove, List<string> Kept, string? Refused);

	public static Plan Make(string a_dataDir, bool a_keepSetups)
	{
		var remove = new List<string>();
		var kept = new List<string>();
		var runs = Runner.RunsDir(a_dataDir);
		var all = Journal.All(runs).ToList();
		var refused = all.Any(j => j.Unfinished && j.OwnerAlive) || QueueRun.Read(a_dataDir) != null ? "a run is going - wait for it to end" :
			all.Any(j => j.Unfinished) ? "a run did not finish - put your profile back first, or its switched-off outputs stay off" : null;
		var setups = Path.Combine(a_dataDir, "Setups");
		// each setup's own run backups, by the run-folder name the runner gives them; nothing else in that folder is touched
		if (Directory.Exists(setups)) {
			foreach (var f in Directory.GetFiles(setups, "*.json")) {
				Setup s;
				try {
					s = Setup.Load(f);
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) {
					continue;
				}
				remove.AddRange(BackupsOf(s.BackupDir, s.Name));
			}
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
		return new Plan(remove.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), kept, refused);
	}

	// the offline tests point this elsewhere (the real ProgramData folders are never touched by a test)
	public static IEnumerable<string> WorkRoots { get; set; } = [Brand.WorkRoot, Brand.OldWorkRoot];

	public static List<string> BackupsOf(string a_backupDir, string a_setupName)
	{
		if (a_backupDir == "" || !Directory.Exists(a_backupDir)) {
			return [];
		}
		var own = new Regex(@"^\d{4}-\d\d-\d\d \d\d-\d\d-\d\d " + Regex.Escape(a_setupName) + "$");
		try {
			return Directory.GetDirectories(a_backupDir).Where(d => own.IsMatch(Path.GetFileName(d))).ToList();
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return [];
		}
	}

	// every entry of the plan to the Recycle Bin; returns what could not go, each with why (a_recycle is for the offline tests)
	public static List<string> Carry(Plan a_plan, Func<string, string?>? a_recycle = null)
	{
		if (a_plan.Refused != null) {
			throw new InvalidOperationException(a_plan.Refused);
		}
		var recycle = a_recycle ?? Recycle;
		var failed = new List<string>();
		foreach (var p in a_plan.Remove.Where(p => File.Exists(p) || Directory.Exists(p))) {
			if (recycle(p) is { } why) {
				failed.Add($"{p}: {why}");
			}
		}
		return failed;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	struct SHFILEOPSTRUCT
	{
		public nint Hwnd;
		public uint Func;
		public string From;
		public string? To;
		public ushort Flags;
		[MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
		public nint NameMappings;
		public string? ProgressTitle;
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
	static extern int SHFileOperation(ref SHFILEOPSTRUCT a_op);

	const uint FO_DELETE = 3;
	const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

	// to the Recycle Bin (never erased): null = gone, else why not
	static string? Recycle(string a_path)
	{
		var op = new SHFILEOPSTRUCT {
			Func = FO_DELETE, From = Path.GetFullPath(a_path) + "\0\0", Flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
		};
		var rc = SHFileOperation(ref op);
		return rc != 0 ? $"Windows answered {rc}" : op.AnyOperationsAborted ? "it was stopped" : File.Exists(a_path) || Directory.Exists(a_path) ? "it is still there" : null;
	}
}
