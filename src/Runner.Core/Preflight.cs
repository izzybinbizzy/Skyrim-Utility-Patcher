using System.Diagnostics;
using ToolRunner.Steps;

namespace ToolRunner;

// Everything that would stop a run, found before anything is touched (every Start, and the window's Check): a blocking issue
// refuses the run; the others are told and the run goes on.
public static class Preflight
{
	// which game processes run now (the offline tests answer "none" - a game of another session may run on this PC)
	public static Func<List<string>> GameCheck { get; set; } = () => Mo2Control.GameRunning();

	public static List<Issue> Run(Setup a_setup, Mo2Instance a_mo2, IReadOnlyList<string> a_steps)
	{
		var outList = new List<Issue>();
		var control = new Mo2Control(a_mo2, a_setup.Profile, RunLog.Null);
		var profile = new Profile(a_mo2, a_setup.Profile, () => control.Running);
		if (!profile.Exists) {
			outList.Add(new Issue("", true, $"Mod Organizer has no profile \"{a_setup.Profile}\""));
			return outList;
		}
		if (InsideMo2()) {
			outList.Add(new Issue("", true, "the runner was started from inside Mod Organizer - start it on its own (it closes and opens Mod Organizer itself)"));
		}
		if (GameCheck() is { Count: > 0 } game) {
			outList.Add(new Issue("", true, "the game is running (" + string.Join(", ", game) + ") - close it first"));
		}
		var downloads = Mo2Control.Downloads(a_mo2.DownloadsDir);
		if (downloads.Active is { Count: > 0 } dl) {
			outList.Add(new Issue("", true, "Mod Organizer is downloading (" + string.Join(", ", dl.Take(3)) + ") - wait for it"));
		}
		if (downloads.Stale is { Count: > 0 } stale) {
			outList.Add(new Issue("", false, $"Mod Organizer keeps {stale.Count} download(s) that already finished as \"unfinished\" (" + string.Join(", ", stale.Take(3)) +
				") - each is the same as the finished file beside it; remove its row in Mod Organizer's Downloads tab, or MO2 resumes it at every start"));
		}
		var pc = SystemChecks.Run(a_mo2, profile, new[] { ("the game", a_mo2.GameDir), ("Mod Organizer's instance", a_mo2.BaseDir), ("Mod Organizer's mods", a_mo2.ModsDir),
			("Mod Organizer's overwrite", a_mo2.OverwriteDir) }.Concat(a_setup.Folders(true)));
		if (control.Running && !pc.Any(i => i.Blocking && i.Text.StartsWith(SystemChecks.UnreadableText, StringComparison.Ordinal))) {
			outList.Add(new Issue("", false, "Mod Organizer is open - the run will close it"));
		}
		outList.AddRange(pc);
		foreach (var d in new[] { a_setup.BackupDir, a_mo2.ModsDir }.Where(d => d != "")) {
			var root = Path.GetPathRoot(Path.GetFullPath(d));
			if (root == null) {
				continue;
			}
			var free = new DriveInfo(root).AvailableFreeSpace / (1024.0 * 1024 * 1024);
			if (free < a_setup.MinFreeGb) {
				outList.Add(new Issue("", true, $"only {free:0.0} GB free on {root} - the run needs at least {a_setup.MinFreeGb} GB for its outputs and backups"));
			}
		}
		var ow = Files.CountFiles(a_mo2.OverwriteDir);
		if (ow > 0) {
			outList.Add(new Issue("", false, $"Mod Organizer's overwrite folder holds {ow} file(s) - the tools will read them as part of your load order"));
		}
		var order = a_setup.EnabledSteps.Select(s => s.Key).ToList();
		foreach (var w in StepCatalog.OrderWarnings(order)) {
			outList.Add(new Issue("", false, "out of the recommended order: " + w));
		}
		var c = new CheckContext { Setup = a_setup, Mo2 = a_mo2, Profile = profile, States = profile.States() };
		HashSet<string>? rivalsOff = null;
		foreach (var key in a_steps) {
			var step = StepRegistry.For(key);
			if (step == null) {
				outList.Add(new Issue(key, true, "this step is not available in this version"));
				continue;
			}
			var s = new StepSettings(a_setup.StepSettings(key) ?? []);
			try {
				outList.AddRange(step.Check(c, s));
				// his checklist 445: a note, never a stop
				if (ToolProof.Newer(key, ToolUpdates.Yours(a_mo2, s).Version) is { } newer) {
					outList.Add(newer);
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
				outList.Add(new Issue(key, true, "its check could not read a file: " + e.Message));
			} catch (Exception e) when (e is InvalidOperationException or FormatException) {
				// a settings file that reads but holds a value of the wrong kind ("pglitemode": "yes") is the step's problem too
				outList.Add(new Issue(key, true, "its check found a setting of the wrong kind: " + e.Message));
			}
			outList.AddRange(ToolVersions.Check(a_setup, a_mo2, key, s));
			rivalsOff ??= Duplicates.Off(a_setup, c.States);
			outList.AddRange(Duplicates.Check(a_setup, a_mo2, c.States, key, rivalsOff));
			// the settings learned from the tool's own window: a tool updated since may have changed what they mean
			if (ToolSettings.CanLearn(s) && ToolSettings.Load(a_setup, key) is { } l && ToolUpdates.Yours(a_mo2, s).Exe is { } exe &&
			    File.GetLastWriteTimeUtc(exe) > l.At) {
				outList.Add(new Issue(key, false, "the tool was updated after its settings were learned - open its window once (Learn its settings) to check them"));
			}
		}
		return outList;
	}

	// started as one of Mod Organizer's executables: it hooks its virtual folder into those. A window its Tools menu opened is only its child, and runs as any other
	static bool InsideMo2()
	{
		using var me = Process.GetCurrentProcess();
		return Mo2Control.InVfs(me);
	}
}

// his checklist 455: a second, older output of the same tool still ticked (an earlier manual run, another tool's output under
// another name). A new output goes to the bottom of Mod Organizer's left pane, so an old copy higher up WINS and hides the fresh
// build without a word - each is named before the run. Only files nothing but that tool writes count (measured on a 1,200-mod
// testing setup 2026-11-07: Pandora's and Nemesis's engine folders are also in 50 ordinary animation mods, so they are not used)
public static class Duplicates
{
	public static readonly IReadOnlyDictionary<string, string[]> Signatures = new Dictionary<string, string[]>(StringComparer.Ordinal) {
		["dyndolod"] = ["DynDOLOD.esm", "DynDOLOD.esp", "Occlusion.esp"],
		["texgen"] = [@"textures\DynDOLOD\LOD\TexGen_SSE.ini"],
		["treelodgen"] = ["TreeLODGen_report.csv"],
		["pgpatcher"] = ["PGPatcher.esp", "PG_1.esp"],
		["synthesis"] = ["Synthesis.esp"],
		["landscapefixer"] = ["LandscapeFixer.esp"],
		["wryebash"] = ["Bashed Patch, 0.esp"],
		["upf"] = ["zPatch.esp"],
		["smash"] = ["Smashed Patch.esp"],
		["reqtificator"] = ["Requiem for the Indifferent.esp"],
	};

	// what in a mod folder shows it holds a step's output: one of its own files, or (grass cache, terrain LOD) a file of its kind
	public static string? Holds(string a_modDir, string a_step)
	{
		if (Signatures.TryGetValue(a_step, out var files)) {
			return files.FirstOrDefault(f => File.Exists(Path.Combine(a_modDir, f)));
		}
		try {
			if (a_step == "grass" && Directory.Exists(Path.Combine(a_modDir, "grass")) &&
			    Directory.EnumerateFiles(Path.Combine(a_modDir, "grass"), "*.cgid").FirstOrDefault() is { } cgid) {
				return Path.GetRelativePath(a_modDir, cgid);
			}
			if (a_step == "xlodgen" && Directory.Exists(Path.Combine(a_modDir, "meshes", "terrain")) &&
			    Directory.EnumerateFiles(Path.Combine(a_modDir, "meshes", "terrain"), "*.btr", SearchOption.AllDirectories).FirstOrDefault() is { } btr) {
				return Path.GetRelativePath(a_modDir, btr);
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
		}
		return null;
	}

	// the mods the run itself switches off are no rivals: every output of the setup, every mod a step replaces, its LOD outputs -
	// the same for every step, so the pre-check works it out once
	public static HashSet<string> Off(Setup a_setup, IReadOnlyDictionary<string, char> a_states) =>
		Runner.OutputsToTurnOff(a_setup, a_states).Select(o => o.Mod).ToHashSet(StringComparer.OrdinalIgnoreCase);

	public static IEnumerable<Issue> Check(Setup a_setup, Mo2Instance a_mo2, IReadOnlyDictionary<string, char> a_states, string a_step, IReadOnlySet<string>? a_off = null)
	{
		var s = new StepSettings(a_setup.StepSettings(a_step) ?? []);
		if (s.OutputMod is not { } own) {
			yield break;
		}
		var off = a_off ?? Off(a_setup, a_states);
		foreach (var (mod, flag) in a_states) {
			if (flag != '+' || off.Contains(mod) || string.Equals(mod, own, StringComparison.OrdinalIgnoreCase)) {
				continue;
			}
			if (Holds(a_mo2.ModDir(mod), a_step) is { } file) {
				yield return new Issue(a_step, false, $"\"{mod}\" also holds {file}, which {StepCatalog.Name(a_step)} makes - with both ticked, the one LOWER in Mod Organizer's " +
					$"left pane (higher priority) wins wherever their files overlap, so the two can mix or hide the new output. Untick it, or add it to the step's \"Mods it replaces\" so every run keeps it off");
			}
		}
	}
}

// each tool's program version against the last run that finished with it: a changed tool is named before the run (a tool
// update broke three of the four restarts of 2026-10-26)
public static class ToolVersions
{
	public static string? ProgramOf(Mo2Instance a_mo2, StepSettings a_s) =>
		a_s.Str("exe") ?? a_s.Str("fasterngio_exe") ?? (a_s.Str("executable") is { } t ? a_mo2.Executable(t)?.Binary : null);

	public static string Version(string a_exe)
	{
		try {
			var v = FileVersionInfo.GetVersionInfo(a_exe);
			var ver = v.ProductVersion ?? v.FileVersion;
			return string.IsNullOrWhiteSpace(ver) ? File.GetLastWriteTimeUtc(a_exe).ToString("yyyy-MM-dd HH:mm", InvariantCulture) : ver.Trim();
		} catch (FileNotFoundException) {
			return "";
		}
	}

	static string StoreFile(Setup a_setup) => Path.Combine(Path.GetDirectoryName(a_setup.File) ?? "", "tool versions.json");

	public static IEnumerable<Issue> Check(Setup a_setup, Mo2Instance a_mo2, string a_key, StepSettings a_s)
	{
		var exe = ProgramOf(a_mo2, a_s);
		if (exe == null || !File.Exists(exe)) {
			yield break;
		}
		var now = Version(exe);
		var known = Load(a_setup);
		if (known.TryGetValue(a_key, out var was) && was != now) {
			yield return new Issue(a_key, false, $"{StepCatalog.Name(a_key)} changed since the last run that finished ({was} -> {now}) - read its update notes if it fails");
		}
	}

	public static Dictionary<string, string> Load(Setup a_setup)
	{
		var f = StoreFile(a_setup);
		try {
			return File.Exists(f) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(Files.ReadShared(f)) ?? [] : [];
		} catch (System.Text.Json.JsonException) {
			return [];
		}
	}

	public static void Remember(Setup a_setup, Mo2Instance a_mo2, IEnumerable<string> a_doneSteps)
	{
		var known = Load(a_setup);
		foreach (var k in a_doneSteps) {
			var s = new StepSettings(a_setup.StepSettings(k) ?? []);
			if (ProgramOf(a_mo2, s) is { } exe && File.Exists(exe)) {
				known[k] = Version(exe);
			}
		}
		Files.WriteAtomic(StoreFile(a_setup), System.Text.Json.JsonSerializer.Serialize(known, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
	}
}
