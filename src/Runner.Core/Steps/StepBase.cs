using System.Diagnostics;
using System.Windows.Automation;

namespace ToolRunner.Steps;

public sealed record Issue(string Step, bool Blocking, string Text);

public interface IStep
{
	string Key { get; }
	void Run(RunContext a_ctx, StepSettings a_s);
	// what would stop this step, found before the run starts (the Check button and every Start)
	IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s);
}

public sealed class CheckContext
{
	public required Setup Setup { get; init; }
	public required Mo2Instance Mo2 { get; init; }
	public required Profile Profile { get; init; }
	public Dictionary<string, char> States { get; init; } = [];
}

public static class StepRegistry
{
	static readonly Dictionary<string, IStep> Steps = new IStep[] {
		new ShaderStep(), new CleanSaveStep(), new WryeBashStep(), new SynthesisStep(), new LandscapeFixerStep(), new PgPatcherStep(),
		new XLodGenStep(), new AcmosStep(), new GrassStep(), new TreeLodGenStep(), new TexGenStep(), new DynDolodStep(),
		new AnvilLodStep(), new PandoraStep(), new PlaySaveStep(),
	}.Concat(GuidedToolStep.All).ToDictionary(s => s.Key);

	public static IReadOnlyDictionary<string, IStep> All => Steps;

	public static IStep? For(string a_key) => a_key.StartsWith(AddOns.KeyPrefix, StringComparison.Ordinal) ? new AddOnStep(a_key) : Steps.GetValueOrDefault(a_key);

	// the offline tests put their own steps in (a step that writes files, fails or waits) to drive the whole run
	public static void Replace(IStep a_step) => Steps[a_step.Key] = a_step;
}

// what the steps share
public static class StepKit
{
	// a tool whose program lives inside a mod switched off in this profile: Mod Organizer cannot start it from there (it hands the
	// start to a copy of itself, which ends with -1, and nothing runs - measured on Landscape Fixer 2026-11-08), so that mod is
	// switched on for the tool's run and put back after. One that holds a plugin is never switched on by the run (it would change
	// the load order): the step stops and names it for the player. The list goes into the step's own switch, which is undone only
	// after the tool is closed (undone before, Mod Organizer's close waited on the tool still open - the trial of 2026-11-08 04:51)
	public static List<string> ToolMods(RunContext a_ctx, StepSettings a_s)
	{
		var title = a_s.Req("executable");
		if (ToolMod(a_ctx.Mo2, a_ctx.Profile, title) is not { } mod) {
			return [];
		}
		// a mod the setup itself names for this step is its own choice, plugin or not (some steps do not read that list, so the mod
		// is still returned here)
		if (!a_s.List("enable_during").Contains(mod, StringComparer.OrdinalIgnoreCase)) {
			NoPluginIn(a_ctx.Mo2, mod, title);
		}
		a_ctx.Log.Detail($"{title}'s program is inside the switched-off mod \"{mod}\" - switched on for its run, off again after");
		return [mod];
	}

	// the same as a switch of its own, for a step with no switch around its tool (held until the tool is closed)
	public static Toggle? ToolModOn(RunContext a_ctx, StepSettings a_s) =>
		ToolMods(a_ctx, a_s) is { Count: > 0 } mods ? new Toggle(a_ctx, mods, [], a_s.Req("executable") + "'s own mod", a_ctx.Cancel) : null;

	// a tool mod that holds a plugin, or cannot be read, is never switched on by the program (a folder that cannot be listed is no
	// proof that it holds none)
	public static void NoPluginIn(Mo2Instance a_mo2, string a_mod, string a_tool)
	{
		bool holds;
		try {
			holds = Directory.EnumerateFiles(a_mo2.ModDir(a_mod)).Any(f => Plugins.IsPlugin(Path.GetFileName(f)));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			throw new StepFailedException($"{a_tool}'s program is inside the mod \"{a_mod}\", which is switched off in this profile and could not be read ({e.Message}) - tick that mod");
		}
		if (holds) {
			throw new StepFailedException($"{a_tool}'s program is inside the mod \"{a_mod}\", which is switched off in this profile - Mod Organizer cannot " +
				"start a program from a switched-off mod; tick that mod (it holds a plugin, so the program does not switch it on for you)");
		}
	}

	// the switched-off mod a tool's program lives in (null: its program is outside the mods folder, or its mod is on)
	public static string? ToolMod(Mo2Instance a_mo2, Profile a_profile, string a_title)
	{
		if (a_mo2.Executable(a_title)?.Binary is not { Length: > 0 } bin) {
			return null;
		}
		var mods = Path.GetFullPath(a_mo2.ModsDir);
		var full = Path.GetFullPath(bin);
		if (!Files.IsUnder(full, mods)) {
			return null;
		}
		var mod = Path.GetRelativePath(mods, full).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
		return a_profile.States().TryGetValue(mod, out var state) && state == '-' ? mod : null;
	}

	// a name no other park in this backup has
	static string FreeFolder(string a_dir)
	{
		var dst = a_dir;
		for (var i = 2; Directory.Exists(dst) || File.Exists(dst); ++i) {
			dst = $"{a_dir} {i}";
		}
		return dst;
	}

	// a generated-output mod's files moved into the run's backup (the folder and its meta.ini stay), recorded so a step that
	// does not finish gets them back
	public static int ParkMod(RunContext a_ctx, string a_step, string a_mod)
	{
		var d = a_ctx.Mo2.ModDir(a_mod);
		if (!Directory.Exists(d)) {
			Directory.CreateDirectory(d);
			return 0;
		}
		var dst = FreeFolder(Path.Combine(a_ctx.BackupFolder, "outputs", a_mod));
		Directory.CreateDirectory(dst);
		// recorded BEFORE the first move: a move that fails half way still has its record, so the put-back finds what moved
		a_ctx.Journal.Parked.Add(new ParkRecord { Step = a_step, Mod = a_mod, From = d, To = dst });
		a_ctx.Journal.Save();
		var n = 0;
		foreach (var e in Directory.EnumerateFileSystemEntries(d).ToList()) {
			var name = Path.GetFileName(e);
			if (name.Equals("meta.ini", StringComparison.OrdinalIgnoreCase)) {
				continue;
			}
			if (Directory.Exists(e)) {
				Files.MoveDirectory(e, Path.Combine(dst, name));
			} else {
				Files.Move(e, Path.Combine(dst, name));
			}
			++n;
		}
		a_ctx.Log.Detail($"old {a_mod} moved to the backup ({n} item(s))");
		return n;
	}

	// a file a step is about to change (its settings, a preset, a flag): its old bytes into the run's backup and the record saved
	// FIRST, so a crash after the change is put back by Restore
	public static void KeepFile(RunContext a_ctx, string a_path)
	{
		if (a_ctx.Journal.Kept.Any(k => !k.Done && string.Equals(k.Path, a_path, StringComparison.OrdinalIgnoreCase))) {
			return;
		}
		string? backup = null;
		if (File.Exists(a_path)) {
			var dir = Path.Combine(a_ctx.BackupFolder, "files");
			Directory.CreateDirectory(dir);
			backup = Path.Combine(dir, $"{a_ctx.Journal.Kept.Count + 1} {Path.GetFileName(a_path)}");
			Files.Copy(a_path, backup, true);
		}
		a_ctx.Journal.Kept.Add(new KeptFile { Path = a_path, Backup = backup });
		a_ctx.Journal.Save();
	}

	// the kept files back as they were (one, or every one still out when a_path is null); returns those it could not put back
	public static List<string> PutFilesBack(RunContext a_ctx, string? a_path = null)
	{
		var failed = new List<string>();
		foreach (var k in a_ctx.Journal.Kept.Where(k => !k.Done && (a_path == null || string.Equals(k.Path, a_path, StringComparison.OrdinalIgnoreCase)))) {
			try {
				if (k.Backup == null) {
					if (File.Exists(k.Path)) {
						Files.Delete(k.Path);
					}
				} else if (!File.Exists(k.Backup)) {
					// its old copy is gone from the backup (a backup folder cleared by hand, a drive that is not there): nothing can
					// bring it back, and counting it as "could not be put back" would keep every later put-back from ever finishing
					k.Done = true;
					a_ctx.Log.Warn($"{k.Path} is left as it is: its old copy is no longer in the backup ({k.Backup})");
					continue;
				} else {
					Files.WriteAtomic(k.Path, File.ReadAllBytes(k.Backup));
					// as it was, its time too: a file in overwrite with a new time reads as the step's own
					File.SetLastWriteTimeUtc(k.Path, File.GetLastWriteTimeUtc(k.Backup));
				}
				k.Done = true;
				a_ctx.Log.Detail($"{Path.GetFileName(k.Path)} put back as it was");
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				failed.Add(k.Path);
				a_ctx.Log.Error($"{k.Path} could NOT be put back ({e.Message}) - its old copy: {k.Backup ?? "(it did not exist)"}");
			}
		}
		a_ctx.Journal.Save();
		return failed;
	}

	// files kept only while a step changed them in place: the change is complete, so they are never put back
	public static void Settled(RunContext a_ctx, IReadOnlyCollection<string> a_paths)
	{
		if (a_paths.Count == 0) {
			return;
		}
		foreach (var k in a_ctx.Journal.Kept.Where(k => !k.Done && a_paths.Contains(k.Path, StringComparer.OrdinalIgnoreCase))) {
			k.Done = true;
			k.Settled = true;
		}
		a_ctx.Journal.Save();
	}

	// an output mod made mid-run (the shader caches' mods): its line is recorded at once, so a Restore after a crash keeps it
	public static void EnsureOutput(RunContext a_ctx, string a_mod)
	{
		a_ctx.Profile.EnsureMod(a_mod, a_ctx.Log);
		if (!a_ctx.Journal.Added.Contains(a_mod, StringComparer.OrdinalIgnoreCase)) {
			a_ctx.Journal.Added.Add(a_mod);
			a_ctx.Journal.Save();
		}
	}

	// a tool's output folder may not be one of MO2's own folders or the game's (a mod there would be moved into the backup)
	public static IEnumerable<Issue> CheckOutputDir(CheckContext a_c, string a_step, string? a_dir, string a_label = "its output folder")
	{
		if (string.IsNullOrWhiteSpace(a_dir)) {
			yield break;
		}
		foreach (var (root, what) in new[] { (a_c.Mo2.ModsDir, "Mod Organizer's mods folder"), (a_c.Mo2.OverwriteDir, "Mod Organizer's overwrite folder"),
			         (a_c.Mo2.ProfilesDir, "Mod Organizer's profiles folder"), (a_c.Mo2.GameDir, "the game's folder") }) {
			if (!string.IsNullOrEmpty(root) && (Files.IsUnder(a_dir, root) || Files.IsUnder(root, a_dir))) {
				yield return new Issue(a_step, true, $"{a_label} {a_dir} is in (or holds) {what} - use an empty folder of its own");
			}
		}
	}

	// a mod's new output is complete: its parked old output stays in the backup and is never put back
	public static void MarkReplaced(RunContext a_ctx, string a_mod)
	{
		foreach (var p in a_ctx.Journal.Parked.Where(p => p.Mod == a_mod && !p.Replaced && !p.PutBack)) {
			p.Replaced = true;
		}
		a_ctx.Journal.Save();
	}

	// a tool's own output folder emptied (so the tool writes into an empty one); its old content to the backup
	public static void ParkDir(RunContext a_ctx, string a_path, string a_label)
	{
		if (Directory.Exists(a_path) && Directory.EnumerateFileSystemEntries(a_path).Any()) {
			var dst = FreeFolder(Path.Combine(a_ctx.BackupFolder, "outputs", a_label));
			Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
			// on record BEFORE the move, as ParkMod, so what a move that fails half way took comes back; until Moved, an undo takes what
			// stands here for what that move left, never for the run's output. Replaced, because a step that fails has always kept
			// this folder's own way back
			var park = new ParkRecord { Mod = a_label, From = a_path, To = dst, Replaced = true, IsFolder = true };
			a_ctx.Journal.Parked.Add(park);
			a_ctx.Journal.Save();
			try {
				Files.MoveDirectory(a_path, dst);
			} catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && !Directory.Exists(dst)) {
				// nothing moved (a move on one drive is all or nothing): nothing to come back
				a_ctx.Journal.Parked.Remove(park);
				a_ctx.Journal.Save();
				throw;
			}
			park.Moved = true;
			a_ctx.Journal.Save();
			a_ctx.Log.Detail($"old {a_label} moved to the backup");
		}
		Directory.CreateDirectory(a_path);
	}

	// the tool's fresh output becomes the mod's content
	public static int IntoMod(RunContext a_ctx, string a_step, string a_src, string a_mod, int a_minFiles = 1)
	{
		var n = Files.CountFiles(a_src);
		if (n < a_minFiles) {
			throw new StepFailedException($"{StepCatalog.Name(a_step)} made {n} file(s) - too few for {a_mod}");
		}
		ParkMod(a_ctx, a_step, a_mod);
		Files.MoveContents(a_src, a_ctx.Mo2.ModDir(a_mod));
		a_ctx.Log.Detail($"{a_mod} now holds the {n} file(s) it made");
		return n;
	}

	// A tool whose work is done (its own "done" line was read, its output is whole) is waited for to close by itself. One that is
	// slow to close, or sits idle while it lets go, is then closed by the step's own CloseTool: it never turns a finished step
	// into a failed one (a 24-minute DynDOLOD once read FAILED "passed its 3 minute limit"). A Stop still ends the wait.
	public static void LetClose(RunContext a_ctx, string a_step, Process a_proc, TimeSpan a_wait, string a_tool)
	{
		try {
			Waits.Exit(a_ctx, a_step, a_proc, a_wait);
		} catch (StepFailedException e) {
			a_ctx.Log.Warn($"{a_tool} had finished but did not close by itself ({e.Message}) - it is closed now");
		}
	}

	// a tool left open keeps MO2 locked: its windows asked to close, then the process tree ended (by PID)
	public static void CloseTool(RunContext a_ctx, string? a_image) => CloseTool(a_ctx.Control, a_image, a_ctx.Log);

	// the processes of this image that this run started, and the children of them (a packed program runs its window in a child)
	public static List<int> StartedRunning(Mo2Control a_control, string a_image)
	{
		// only what is still the very process the run started: hours into a run the list holds many ended ones, and Windows hands
		// their numbers out again - to a program of the player's, which must never be closed as "the run's"
		var live = a_control.Started.Live();
		var mine = new HashSet<int>(live);
		foreach (var root in live) {
			foreach (var c in Procs.Tree(root)) {
				mine.Add(c);
			}
		}
		var procs = Procs.ByImage(a_image);
		var ours = procs.Select(p => p.Id).Where(mine.Contains).ToList();
		procs.ForEach(p => p.Dispose());
		return ours;
	}

	public static void CloseTool(Mo2Control a_control, string? a_image, RunLog a_log)
	{
		if (string.IsNullOrEmpty(a_image)) {
			return;
		}
		List<int> Ours() => StartedRunning(a_control, a_image);
		var left = Ours();
		if (left.Count == 0) {
			return;
		}
		foreach (var h in Native.TopWindowsOf(left)) {
			Native.PostMessage(h, Native.WM_CLOSE, 0, 0);
		}
		var sw = Stopwatch.StartNew();
		while (Ours().Count > 0 && sw.Elapsed < TimeSpan.FromSeconds(8)) {
			Thread.Sleep(500);
		}
		foreach (var pid in Ours()) {
			Procs.KillTree(pid);
		}
		a_log.Detail($"{a_image} closed");
	}

	public static Func<IReadOnlyCollection<int>> PidsOf(string a_image) => () => Procs.Pids(a_image);

	// the Executables entry exists and points at a file that exists
	public static IEnumerable<Issue> CheckExecutable(CheckContext a_c, string a_step, string? a_title)
	{
		if (string.IsNullOrEmpty(a_title)) {
			yield return new Issue(a_step, true, "no Mod Organizer executable is set for this step");
			yield break;
		}
		var e = a_c.Mo2.Executable(a_title);
		if (e == null) {
			yield return new Issue(a_step, true, $"Mod Organizer has no executable called \"{a_title}\" (Executables list)");
		} else if (!File.Exists(e.Binary)) {
			yield return new Issue(a_step, true, $"Mod Organizer's executable \"{a_title}\" points at {e.Binary}, which does not exist");
		}
	}

	public static IEnumerable<Issue> CheckModsExist(CheckContext a_c, string a_step, IEnumerable<string> a_mods, bool a_blocking = false)
	{
		foreach (var m in a_mods.Where(m => !a_c.States.ContainsKey(m))) {
			yield return new Issue(a_step, a_blocking, $"\"{m}\" is not in this profile's mod list{(a_blocking ? "" : " (skipped)")}");
		}
	}

	public static AutomationElement WaitWindow(RunContext a_ctx, string a_image, Func<string, bool> a_title, TimeSpan a_timeout, string a_what) =>
		Uia.WaitWindow(PidsOf(a_image), a_title, a_timeout, a_ctx.Cancel) ?? throw new StepFailedException($"{a_what} did not appear within {a_timeout.TotalMinutes:0} minutes");
}
