using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using ToolRunner;
using ToolRunner.Steps;

namespace ToolRunner.Tests;

// The offline tests: every edge case of the audit proven against a fake Mod Organizer setup in a scratch folder (no real MO2,
// no game, nothing of his touched). Exit 0 = every test passed.
//   Runner.Tests.exe [<filter>]
public static class Program
{
	static int s_pass, s_fail;
	static readonly List<string> Failures = [];

	public static int Main(string[] a_args)
	{
		if (a_args.Length > 0 && a_args[0] == "-autorun") {
			return FakeAcmos(a_args);
		}
		var filter = a_args.Length > 0 ? a_args[0] : "";
		// a game another session runs on this PC is not this test's business
		Preflight.GameCheck = () => [];
		// a real run of the program on this PC holds the real run lock
		Runner.LockScope = " tests " + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
		Mo2Control.GameProbe = () => [];
		// nor this PC's own state: its Mod Organizer, an update waiting, its battery, how it was started
		SystemChecks.SelfAdmin = () => false;
		SystemChecks.Mo2Unreadable = _ => false;
		SystemChecks.AdminFlagged = _ => false;
		SystemChecks.Mo2Paths = () => [];
		SystemChecks.RestartPending = () => false;
		SystemChecks.Battery = () => null;
		SystemChecks.MemoryProbe = () => (32, 16);
		SystemChecks.ControlledFolderAccess = () => false;
		SystemChecks.CloudFolders = () => [];
		SystemChecks.ProgramFolders = () => [];
		SystemChecks.IsNetwork = _ => false;
		InGame.FreeBytes = _ => 1L << 50;
		SystemChecks.SteamRunning = () => false;
		var tests = typeof(Program).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
			.Where(m => m.Name.StartsWith("T_", StringComparison.Ordinal) && m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).OrderBy(m => m.MetadataToken);
		foreach (var t in tests) {
			var sw = Stopwatch.StartNew();
			try {
				t.Invoke(null, null);
				++s_pass;
				Console.WriteLine($"PASS  {t.Name[2..]}  ({sw.ElapsedMilliseconds} ms)");
			} catch (System.Reflection.TargetInvocationException e) {
				++s_fail;
				var msg = e.InnerException?.Message ?? e.Message;
				Failures.Add($"{t.Name[2..]}: {msg}");
				Console.WriteLine($"FAIL  {t.Name[2..]}: {msg}");
				Console.WriteLine(e.InnerException?.StackTrace?.Split('\n').FirstOrDefault()?.Trim());
			}
		}
		Console.WriteLine();
		Console.WriteLine(s_fail == 0 ? $"=== ALL {s_pass} TESTS PASS ===" : $"=== {s_fail} FAILED, {s_pass} passed ===");
		return s_fail == 0 ? 0 : 1;
	}

	static void Check(bool a_ok, string a_what)
	{
		if (!a_ok) {
			throw new InvalidOperationException(a_what);
		}
	}

	static void Equal<T>(T a_expected, T a_actual, string a_what)
	{
		if (!EqualityComparer<T>.Default.Equals(a_expected, a_actual)) {
			throw new InvalidOperationException($"{a_what}: expected <{a_expected}>, got <{a_actual}>");
		}
	}

	static void Throws<TE>(Action a_act, string a_what) where TE : Exception
	{
		try {
			a_act();
		} catch (TE) {
			return;
		}
		throw new InvalidOperationException(a_what + ": did not throw " + typeof(TE).Name);
	}

	// ------------------------------------------------------------------ a fake Mod Organizer on disk
	sealed class FakeMo2 : IDisposable
	{
		public string Root { get; }
		// the folder Dispose removes: Root, or the folder a deeper Root was made in
		public string Top { get; }
		public string Exe => Path.Combine(Root, "MO2", "ModOrganizer.exe");
		public string Instance => Path.Combine(Root, "MO2");
		public string Game => Path.Combine(Root, "Game");
		public string ProfileDir => Path.Combine(Instance, "profiles", "Test");
		public string Mods => Path.Combine(Instance, "mods");
		public string Overwrite => Path.Combine(Instance, "overwrite");
		public string Backups => Path.Combine(Root, "Backups");
		public string Data => Path.Combine(Root, "RunnerData");
		public string SetupFile => Path.Combine(Data, "Setups", "Test.json");

		// every test runs under a folder with a space, an apostrophe, accents and a non-Latin letter in it - a player's paths can hold any
		public const string OddFolder = "Pläyer's Mödding – ü ж";

		public FakeMo2(string a_nl = "\r\n", string a_deeper = "")
		{
			Top = Path.Combine(Path.GetTempPath(), "mo2 tool runner tests", OddFolder + " " + Guid.NewGuid().ToString("N")[..8]);
			Root = Path.Combine(Top, a_deeper);
			Directory.CreateDirectory(Path.Combine(Game, "Data"));
			Directory.CreateDirectory(ProfileDir);
			Directory.CreateDirectory(Mods);
			Directory.CreateDirectory(Overwrite);
			File.WriteAllText(Exe, "");
			File.WriteAllText(Path.Combine(Instance, "ModOrganizer.ini"),
				$"[General]\r\ngameName=Skyrim Special Edition\r\ngamePath=@ByteArray({Game.Replace("\\", "\\\\")})\r\nselected_profile=@ByteArray(Test)\r\n" +
				"[customExecutables]\r\nsize=1\r\n1\\title=SKSE\r\n1\\binary=" + Path.Combine(Game, "skse64_loader.exe").Replace('\\', '/') + "\r\n1\\arguments=\r\n" +
				"[Settings]\r\nbase_directory=" + Instance.Replace('\\', '/') + "\r\n");
			Plugin(Path.Combine(Game, "Data", "Skyrim.esm"), true);
			Mod("Base Mod", ("BaseMod.esp", false, []));
			Mod("Master Mod", ("MasterMod.esm", true, []));
			Mod("Output A", ("OutA.esp", false, []));
			Mod("Output B", ("OutB.esp", false, ["OutA.esp"]));
			Mod("Needs B", ("NeedsB.esp", false, ["OutB.esp"]));
			Mod("Replaced", ("Old.esp", false, []));
			// modlist: first line = highest priority; the last line is the top of MO2's pane
			File.WriteAllText(Path.Combine(ProfileDir, "modlist.txt"),
				string.Join(a_nl, "# This file was automatically generated by Mod Organizer.", "+Needs B", "+Output B", "+Output A", "+Replaced", "+Master Mod", "+Base Mod", "*DLC: Dawnguard", "-Other_separator") + a_nl);
			File.WriteAllText(Path.Combine(ProfileDir, "plugins.txt"), string.Join(a_nl, "# comment", "*MasterMod.esm", "*BaseMod.esp", "*Old.esp", "*OutA.esp", "*OutB.esp", "*NeedsB.esp") + a_nl);
			File.WriteAllText(Path.Combine(ProfileDir, "loadorder.txt"), string.Join(a_nl, "Skyrim.esm", "MasterMod.esm", "BaseMod.esp", "Old.esp", "OutA.esp", "OutB.esp", "NeedsB.esp") + a_nl);
			File.WriteAllText(Path.Combine(ProfileDir, "archives.txt"), "");
			Directory.CreateDirectory(Path.GetDirectoryName(SetupFile)!);
		}

		public void Mod(string a_name, params (string Plugin, bool Master, string[] Masters)[] a_plugins)
		{
			var d = Path.Combine(Mods, a_name);
			Directory.CreateDirectory(d);
			File.WriteAllText(Path.Combine(d, "meta.ini"), "[General]\r\n");
			foreach (var (p, m, ms) in a_plugins) {
				Plugin(Path.Combine(d, p), m, ms);
			}
		}

		public static void Plugin(string a_path, bool a_master, params string[] a_masters)
		{
			var body = new MemoryStream();
			foreach (var m in a_masters) {
				var b = Encoding.ASCII.GetBytes(m + "\0");
				body.Write(Encoding.ASCII.GetBytes("MAST"));
				body.Write(BitConverter.GetBytes((ushort)b.Length));
				body.Write(b);
				body.Write(Encoding.ASCII.GetBytes("DATA"));
				body.Write(BitConverter.GetBytes((ushort)8));
				body.Write(new byte[8]);
			}
			var head = new byte[24];
			Encoding.ASCII.GetBytes("TES4").CopyTo(head, 0);
			BitConverter.GetBytes((uint)body.Length).CopyTo(head, 4);
			head[8] = (byte)(a_master ? 1 : 0);
			using var f = File.Create(a_path);
			f.Write(head);
			f.Write(body.ToArray());
		}

		public string Text(string a_file) => File.ReadAllText(Path.Combine(ProfileDir, a_file));

		public Setup WriteSetup(params (string Step, JsonObject Settings)[] a_steps)
		{
			var steps = new JsonArray();
			foreach (var (s, o) in a_steps) {
				steps.Add(new JsonObject { ["step"] = s, ["on"] = true, ["settings"] = o });
			}
			var root = new JsonObject {
				["schema"] = 2, ["name"] = "Test", ["mo2"] = new JsonObject { ["exe"] = Exe, ["instance"] = Instance, ["profile"] = "Test" },
				["backups"] = Backups, ["minFreeGb"] = 0, ["idleMinutes"] = 2, ["steps"] = steps,
			};
			var setup = Setup.FromJson(root, SetupFile);
			setup.Save();
			return setup;
		}

		public void Dispose()
		{
			try {
				// a test's read-only files would stop the delete
				foreach (var f in new DirectoryInfo(Top).EnumerateFiles("*", SearchOption.AllDirectories).Where(f => f.IsReadOnly)) {
					f.IsReadOnly = false;
				}
				Directory.Delete(Top, true);
			} catch (IOException) {
			} catch (UnauthorizedAccessException) {
			}
		}
	}

	static Profile ProfileOf(FakeMo2 a_f) => new(Mo2Instance.Load(a_f.Exe, a_f.Instance), "Test", () => false);

	// ------------------------------------------------------------------ files
	static void T_AtomicWriteReplacesAndLeavesNoTemp()
	{
		using var f = new FakeMo2();
		var p = Path.Combine(f.Root, "x.txt");
		File.WriteAllText(p, "old");
		Files.WriteAtomic(p, "new");
		Equal("new", File.ReadAllText(p), "content");
		Equal(1, Directory.GetFiles(f.Root, "*x.txt*").Length, "no temp file beside it");
	}

	static void T_DecodeKeepsUtf8Utf16AndBom()
	{
		Equal("é", Files.DecodeText([0xEF, 0xBB, 0xBF, 0xC3, 0xA9]), "utf-8 with bom");
		Equal("a", Files.DecodeText([0xFF, 0xFE, 0x61, 0x00]), "utf-16");
		Equal("abc", Files.DecodeText(Encoding.UTF8.GetBytes("abc")), "plain");
	}

	static void T_UnpackAnsiTwoBytesPerChar()
	{
		var packed = Encoding.Unicode.GetString(Encoding.ASCII.GetBytes("LOD generation done."));
		Check(Native.UnpackAnsi(packed).Contains("LOD generation done", StringComparison.Ordinal), "packed ANSI read back");
		Equal("normal text", Native.UnpackAnsi("normal text"), "plain text untouched");
		// the buffer as xLODGen fills it: the packed half, then as many empty characters (its answer counts bytes)
		var bytes = Encoding.ASCII.GetBytes("[17/17] Tamriel\r\nLOD generation done.");
		var buf = new char[bytes.Length + 1];
		var half = Encoding.Unicode.GetString(bytes);
		half.CopyTo(0, buf, 0, half.Length);
		Check(Native.FromBuffer(buf, bytes.Length).Contains("LOD generation done", StringComparison.Ordinal), "packed text with an empty tail read back");
		var plain = "plain log line".ToCharArray();
		Equal("plain log line", Native.FromBuffer(plain, plain.Length), "a plain buffer untouched");
	}

	// ------------------------------------------------------------------ MO2's ini
	static void T_QtIniReadsByteArraysAndExecutables()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		Equal(f.Game, mo2.GameDir, "game folder");
		Equal(Path.Combine(f.Instance, "mods"), mo2.ModsDir, "mods folder");
		Equal("Test", mo2.SelectedProfile, "selected profile");
		Check(mo2.Executable("skse") != null, "executables found case-insensitively");
		// an Executables entry's arguments as Qt escapes them: undone once (a share's \\ stays two backslashes)
		var ini = Mo2Instance.ReadQtIni("""
			[customExecutables]
			1\arguments=-o:\"D:\\LOD\\\" -x \\\\srv\\share
			""");
		Equal(@"-o:""D:\LOD\"" -x \\srv\share", ini["customExecutables"]["1\\arguments"], "quotes and backslashes unescaped once");
	}

	// a real Mod Organizer, read only: SUP_REAL_MO2 = its ModOrganizer.exe, SUP_REAL_BASE = its instance folder (skipped
	// when unset - a build machine has none)
	static void T_RealIniReads()
	{
		var exe = Environment.GetEnvironmentVariable("SUP_REAL_MO2");
		if (exe == null || !File.Exists(exe)) {
			Console.WriteLine("      (skipped: SUP_REAL_MO2 not set)");
			return;
		}
		var mo2 = Mo2Instance.Load(exe);
		if (Environment.GetEnvironmentVariable("SUP_REAL_BASE") is { } b) {
			Equal(b, mo2.BaseDir, "the instance folder");
		}
		Check(mo2.Executables.Any(e => e.Title == "SKSE"), "an SKSE entry");
		Check(Directory.Exists(mo2.GameDir), "the game folder exists");
	}

	// ------------------------------------------------------------------ the profile's files
	static void T_SetModsKeepsLineEndingsAndOtherLines()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		var before = f.Text("modlist.txt");
		var changed = p.SetMods(["Replaced"], ["Output A"]);
		Equal(1, changed.Count, "only the one that changed is reported");
		var after = f.Text("modlist.txt");
		Check(after.Contains("-Output A\r\n", StringComparison.Ordinal) && after.Contains("+Replaced\r\n", StringComparison.Ordinal), "flags written, CRLF kept");
		Equal(before.Replace("+Output A", "-Output A"), after, "every other line byte for byte");
	}

	static void T_SetModsLfFileStaysLf()
	{
		using var f = new FakeMo2("\n");
		ProfileOf(f).SetMods([], ["Base Mod"]);
		Check(!f.Text("modlist.txt").Contains('\r'), "an LF file stays LF");
	}

	static void T_SetModsRefusesUnknownAndBoth()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		Throws<KeyNotFoundException>(() => p.SetMods(["Nope"], []), "unknown mod");
		Throws<ArgumentException>(() => p.SetMods(["Base Mod"], ["Base Mod"]), "on and off at once");
	}

	static void T_ProfileRefusesWritesWhileMo2Open()
	{
		using var f = new FakeMo2();
		var p = new Profile(Mo2Instance.Load(f.Exe, f.Instance), "Test", () => true);
		Throws<InvalidOperationException>(() => p.SetMods([], ["Base Mod"]), "a write while MO2 is open");
	}

	static void T_EnsureModAddsFolderAndFirstLine()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		p.EnsureMod("New Output");
		Check(File.Exists(Path.Combine(f.Mods, "New Output", "meta.ini")), "folder with meta.ini");
		var lines = f.Text("modlist.txt").Split("\r\n");
		Equal("-New Output", lines[1], "first mod line, unticked (the bottom of MO2's pane)");
	}

	static void T_SnapshotRestoreIsByteExactAndKeepsAdded()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		var snap = p.Snapshot();
		p.EnsureMod("Added Output");
		p.SetMods([], ["Base Mod", "Output A"]);
		File.WriteAllText(Path.Combine(f.ProfileDir, "plugins.txt"), "garbage");
		p.Restore(snap);
		Equal(Encoding.UTF8.GetString(snap["plugins.txt"]), f.Text("plugins.txt"), "plugins.txt byte for byte");
		var ml = f.Text("modlist.txt");
		Check(ml.Contains("+Base Mod", StringComparison.Ordinal) && ml.Contains("+Output A", StringComparison.Ordinal), "ticks back");
		Check(ml.Contains("-Added Output", StringComparison.Ordinal), "the output mod this run added keeps its line");
	}

	static void T_PlacePluginsRules()
	{
		using var f = new FakeMo2();
		f.Mod("PG", ("PGPatcher.esp", false, []));
		f.Mod("Dyn", ("DynDOLOD.esm", true, []), ("DynDOLOD.esp", false, []), ("Occlusion.esp", false, []));
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "+Dyn\r\n+PG\r\n+Base Mod\r\n+Master Mod\r\n");
		File.WriteAllText(Path.Combine(f.ProfileDir, "plugins.txt"), "*Occlusion.esp\r\n*DynDOLOD.esp\r\n*DynDOLOD.esm\r\nPGPatcher.esp\r\n*MasterMod.esm\r\n*BaseMod.esp\r\nPG_1.esp\r\n");
		var p = ProfileOf(f);
		p.PlacePlugins([new("PGPatcher.esp", "last_master", true), new("DynDOLOD.esm", "after:PGPatcher.esp"), new("DynDOLOD.esp", "end"), new("Occlusion.esp", "end"), new("PG_1.esp", "end", true)]);
		var lines = f.Text("plugins.txt").Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
		Equal("*MasterMod.esm,*PGPatcher.esp,*DynDOLOD.esm,*BaseMod.esp,*DynDOLOD.esp,*Occlusion.esp,PG_1.esp", string.Join(",", lines), "placed and ticked, a plugin nothing provides left unticked");
	}

	static void T_ActivateNewMasterGoesToEndOfMasterBlock()
	{
		using var f = new FakeMo2();
		f.Mod("New", ("NewMaster.esm", true, []), ("NewPlugin.esp", false, []));
		var p = ProfileOf(f);
		p.SetMods(["Base Mod"], []);
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "+New\r\n" + f.Text("modlist.txt"));
		p.ActivatePlugins(["New"]);
		var lo = f.Text("loadorder.txt").Split("\r\n", StringSplitOptions.RemoveEmptyEntries).ToList();
		Equal(lo.IndexOf("MasterMod.esm") + 1, lo.IndexOf("NewMaster.esm"), "new master right after the last master");
		Equal("NewPlugin.esp", lo[^1], "new plugin at the end");
		Check(f.Text("plugins.txt").Contains("*NewMaster.esm", StringComparison.Ordinal), "ticked in plugins.txt");
	}

	static void T_DependentsFollowMasterChains()
	{
		using var f = new FakeMo2();
		var dep = ProfileOf(f).Dependents(["Output A"]);
		Check(dep.ContainsKey("Output B") && dep.ContainsKey("Needs B"), "a mod needing OutA, and one needing that, go off too: " + string.Join(", ", dep.Keys));
		// a setup that spells the mod otherwise than the list finds the same dependents
		var spelt = ProfileOf(f).Dependents(["output a"]);
		Check(spelt.ContainsKey("Output B") && spelt.ContainsKey("Needs B"), "the same dependents for another spelling: " + string.Join(", ", spelt.Keys));
		// a second ticked mod that ships the same master keeps it there: nothing needs to go off with the first
		f.Mod("Copy A", ("OutA.esp", false, []));
		var list = Path.Combine(f.ProfileDir, "modlist.txt");
		File.WriteAllText(list, File.ReadAllText(list).Replace("+Base Mod", "+Copy A\r\n+Base Mod", StringComparison.Ordinal));
		Equal(0, ProfileOf(f).Dependents(["Output A"]).Count, "another copy of its master stays on");
		Check(ProfileOf(f).Dependents(["Output A", "Copy A"]).ContainsKey("Output B"), "every copy going off takes it along");
		// a master the game's own Data folder ships too is never missing when the only mod that also ships it goes off
		f.Mod("Output C", ("Skyrim.esm", true, []));
		f.Mod("Needs Sky", ("NeedsSky.esp", false, ["Skyrim.esm"]));
		File.WriteAllText(list, File.ReadAllText(list).Replace("+Base Mod", "+Output C\r\n+Needs Sky\r\n+Base Mod", StringComparison.Ordinal));
		Equal(0, ProfileOf(f).Dependents(["Output C"]).Count, "the game's own copy of the master keeps its dependent on");
	}

	static void T_DeactivatePluginsUnticksOnly()
	{
		using var f = new FakeMo2();
		ProfileOf(f).DeactivatePlugins(["Replaced"]);
		var t = f.Text("plugins.txt");
		Check(t.Contains("\r\nOld.esp\r\n", StringComparison.Ordinal) && !t.Contains("*Old.esp", StringComparison.Ordinal), "unticked, not removed");
	}

	static void T_PluginMastersRead()
	{
		using var f = new FakeMo2();
		Equal("OutA.esp", Plugins.Masters(Path.Combine(f.Mods, "Output B", "OutB.esp")).Single(), "MAST read");
		Check(Plugins.IsMaster(Path.Combine(f.Mods, "Master Mod", "MasterMod.esm")), "ESM flag");
		Check(!Plugins.IsMaster(Path.Combine(f.Mods, "Base Mod", "BaseMod.esp")), "not a master");
	}

	// ------------------------------------------------------------------ setups and order
	static void T_OrderWarningsCatchTheOldOrder()
	{
		var old = new[] { "cleansave", "xlodgen", "acmos", "grass", "landscapefixer", "pgpatcher", "treelodgen", "texgen", "dyndolod", "synthesis", "pandora", "shaders" };
		var w = StepCatalog.OrderWarnings(old);
		Check(w.Any(x => x.StartsWith("Synthesis runs after DynDOLOD", StringComparison.Ordinal)), "Synthesis after DynDOLOD is warned");
		Check(w.Any(x => x.StartsWith("Landscape Fixer runs after xLODGen", StringComparison.Ordinal)), "Landscape Fixer after xLODGen is warned");
		Equal(2, w.Count, "those two warnings and no other: " + string.Join(" | ", w));
		Equal(0, StepCatalog.OrderWarnings(StepCatalog.DefaultOrder.ToList()).Count, "the default order is safe");
	}

	static void T_PlanOnlyFromUntil()
	{
		using var f = new FakeMo2();
		var s = f.WriteSetup(("landscapefixer", []), ("synthesis", []), ("pgpatcher", []), ("texgen", []));
		Equal("synthesis", string.Join(",", Runner.Plan(s, "synthesis", null, null).Select(x => x.Key)), "only");
		Equal("pgpatcher,texgen", string.Join(",", Runner.Plan(s, null, "pgpatcher", null).Select(x => x.Key)), "from");
		Equal("landscapefixer,synthesis", string.Join(",", Runner.Plan(s, null, null, "synthesis").Select(x => x.Key)), "until");
		Throws<ArgumentException>(() => Runner.Plan(s, null, "texgen", "landscapefixer"), "a range the wrong way round");
		Throws<ArgumentException>(() => Runner.Plan(s, "dyndolod", null, null), "a step the setup does not have");
	}

	// his ask 2026-11-03: a new game has no save to keep clean - the clean save and the save to play are left out while it is on
	static void T_NewGameLeavesOutTheSaveSteps()
	{
		using var f = new FakeMo2();
		var s = f.WriteSetup(("cleansave", []), ("landscapefixer", []), ("synthesis", []), ("playsave", []));
		Equal("cleansave,landscapefixer,synthesis,playsave", string.Join(",", Runner.Plan(s, null, null, null).Select(x => x.Key)), "an existing save: every step");
		s.Root["newGame"] = true;
		s.Save();
		var ng = Setup.Load(f.SetupFile);
		Check(ng.NewGame, "the tick is kept in the setup");
		Equal("landscapefixer,synthesis", string.Join(",", Runner.Plan(ng, null, null, null).Select(x => x.Key)), "a new game: the save steps left out");
		Equal("landscapefixer,synthesis", string.Join(",", ng.EnabledSteps.Select(x => x.Key)), "the check and the run see the same steps");
		ArgumentException? refusal = null;
		try {
			Runner.Plan(ng, "cleansave", null, null);
		} catch (ArgumentException e) {
			refusal = e;
		}
		Check(refusal?.Message.Contains("New game", StringComparison.Ordinal) == true, "only the clean save on a new game is refused, naming the tick: " + refusal?.Message);
		Equal(0, Preflight.Run(ng, Mo2Instance.Load(f.Exe, f.Instance), ng.EnabledSteps.Select(x => x.Key).ToList()).Count(i => i.Step is "cleansave" or "playsave"),
			"no save step asks for a save");
		var lines = RunPreview.Build(ng, Mo2Instance.Load(f.Exe, f.Instance), Runner.Plan(ng, null, null, null)).Lines(_ => null);
		Check(lines.Any(l => l.Contains("new game", StringComparison.Ordinal) && l.Contains("Clean save", StringComparison.Ordinal) && l.Contains("Save to play", StringComparison.Ordinal)),
			"the preview says what is left out: " + string.Join(" | ", lines));
		Check(!lines.Any(l => l.StartsWith("The game starts", StringComparison.Ordinal)), "and that the game does not start");
		ng.Root["steps"]!.AsArray().OfType<JsonObject>().Where(o => Setup.Str(o, "step") is "landscapefixer" or "synthesis").ToList().ForEach(o => o["on"] = false);
		Check(ng.Problems().Any(p => p.Contains("new game", StringComparison.Ordinal)), "only save steps on, on a new game: said plainly");
	}

	static void T_SetupProblems()
	{
		var s = Setup.FromJson(new JsonObject { ["steps"] = new JsonArray(new JsonObject { ["step"] = "nonsense" }) });
		var p = s.Problems();
		Check(p.Any(x => x.Contains("Mod Organizer", StringComparison.Ordinal)) && p.Any(x => x.Contains("unknown step", StringComparison.Ordinal)), string.Join("; ", p));
	}

	static void T_SetupSaveKeepsUnknownKeys()
	{
		using var f = new FakeMo2();
		var s = f.WriteSetup(("landscapefixer", new JsonObject { ["_note"] = "his note 🔁", ["custom"] = 3 }));
		var back = Setup.Load(f.SetupFile);
		Equal("his note 🔁", Setup.Str(back.StepSettings("landscapefixer")!, "_note"), "a note key kept");
		Equal(3, Setup.Int(back.StepSettings("landscapefixer")!, "custom"), "an unknown key kept");
		_ = s;
	}

	// ------------------------------------------------------------------ in-game signals
	static void T_LoadBoxDecisions()
	{
		Equal(("answered NO", 1), DevBench.DecideBox("Would you like to search the Creations menu for missing content?", ["Yes", "No"], 2), "search -> No");
		Equal(("answered YES", 0), DevBench.DecideBox("Some content is missing. Continue loading?", ["Yes", "No"], 2), "continue -> Yes");
		Equal(("acknowledged", 0), DevBench.DecideBox("Perks reset", ["Ok"], 1), "one button");
		Equal<string?>(null, DevBench.DecideBox("Delete your save?", ["Yes", "No"], 2).Answer, "an unknown question is not answered");
	}

	static void T_FollowersAreLivingTeammates()
	{
		var reply = JsonNode.Parse("""
			{ "refs": [
				{ "formId": "0x00000014", "actor": { "playerTeammate": true, "alive": true } },
				{ "formId": "0x000A2C94", "actor": { "playerTeammate": true, "alive": true } },
				{ "formId": 2228229, "actor": { "playerTeammate": true, "alive": true } },
				{ "formId": "0x00033333", "actor": { "playerTeammate": true } },
				{ "formId": "0x000A2C94", "actor": { "playerTeammate": true, "alive": true } },
				{ "formId": "0x0001A694", "actor": { "playerTeammate": false, "alive": true } },
				{ "formId": "0x0001B07F", "actor": { "playerTeammate": true, "alive": false } },
				{ "formId": "0x00012345" },
				{ "formId": "0x00054321", "actor": { "playerTeammate": "yes" } }
			] }
			""")!.AsObject();
		Equal("0x000A2C94,0x00220005", string.Join(",", InGame.Teammates(reply)), "living teammates only, never the player, once each");
		Equal(0, InGame.Teammates([]).Count, "no list = no followers");
	}

	// a teleport reads who is with the player first, then moves, waits for the new place and moves each living follower over
	static void T_FollowersFollowATeleport()
	{
		var (order, moved) = Teleport(false);
		Equal("read,move,wait,follow", order, "followers read before the move, moved after the new place");
		Equal("0x000A2C94 MoveTo 0x00000014", moved, "the living follower moved to the player, nobody else");
	}

	// a Stop that lands while the followers are read: no teleport, nobody moved
	static void T_StopDuringTheFollowerReadMovesNobody()
	{
		var (order, moved) = Teleport(true);
		Equal("read", order, "no move after a stop during the follower read");
		Equal("", moved, "no follower moved");
	}

	// the teleport against a local stand-in for DevBench (its actor list and its Papyrus calls) -> the call order and the moves
	static (string Order, string Moved) Teleport(bool a_stopAfterRead)
	{
		using var f = new FakeMo2();
		using var cts = new CancellationTokenSource();
		var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
		probe.Start();
		var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		using var http = new System.Net.HttpListener();
		http.Prefixes.Add($"http://localhost:{port}/");
		http.Start();
		var order = new List<string>();
		var moved = new List<string>();
		var serve = Task.Run(() => {
			while (http.IsListening) {
				System.Net.HttpListenerContext c;
				try {
					c = http.GetContext();
				} catch (Exception e) when (e is System.Net.HttpListenerException or ObjectDisposedException or InvalidOperationException) {
					return;
				}
				using var reader = new StreamReader(c.Request.InputStream);
				var body = JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? [];
				string reply;
				if (c.Request.Url!.AbsolutePath.EndsWith("/inspect", StringComparison.Ordinal)) {
					lock (order) {
						order.Add("read");
					}
					if (a_stopAfterRead) {
						cts.Cancel();
					}
					reply = """{"result":{"refs":[{"formId":"0x00000014","actor":{"playerTeammate":true,"alive":true}},{"formId":"0x000A2C94","actor":{"playerTeammate":true,"alive":true}},{"formId":"0x0001B07F","actor":{"playerTeammate":true,"alive":false}}]}}""";
				} else {
					lock (order) {
						order.Add("follow");
					}
					lock (moved) {
						moved.Add($"{body["self"]?["form"]} {body["function"]} {(body["args"] as JsonArray)?[0]?["form"]}");
					}
					reply = """{"result":{"called":true,"returned":null}}""";
				}
				var bytes = Encoding.UTF8.GetBytes(reply);
				c.Response.ContentType = "application/json";
				c.Response.OutputStream.Write(bytes);
				c.Response.Close();
			}
		});
		f.WriteSetup(("cleansave", new JsonObject()));
		var ctx = StepContext(f, "cleansave", new DevBench($"http://localhost:{port}/api/tool/"), cts.Token);
		Action teleport = () => InGame.MoveWithFollowers(ctx, () => { lock (order) { order.Add("move"); } }, () => { lock (order) { order.Add("wait"); } });
		if (a_stopAfterRead) {
			Throws<OperationCanceledException>(teleport, "a stop during the follower read ends the teleport");
		} else {
			teleport();
		}
		ctx.Log.Dispose();
		http.Stop();
		serve.Wait(TimeSpan.FromSeconds(5));
		lock (order) {
			return (string.Join(",", order), string.Join(";", moved));
		}
	}

	static void T_CompilingSignals()
	{
		using var f = new FakeMo2();
		var logs = Path.Combine(f.Root, "Logs");
		Directory.CreateDirectory(logs);
		var log = Path.Combine(logs, "BottledShaders.log");
		File.WriteAllText(log, "[2026-10-26 17:50:34.017] [info] [ShaderTiming] x remaining=2707\n");
		Check(InGame.Compiling(logs, f.Overwrite, DateTime.Now.AddMinutes(-1)), "dated stamp, remaining > 0");
		File.WriteAllText(log, "[17:50:34.017] [info] [ShaderTiming] x remaining=0\n");
		Check(!InGame.Compiling(logs, f.Overwrite, DateTime.Now.AddMinutes(-1)), "remaining 0 = done");
		var open = Path.Combine(logs, "OpenShaders.log");
		File.WriteAllText(open, "[info] Shader compilation started\n");
		// the newer log by its write time (a fast machine writes both in the same clock tick)
		File.SetLastWriteTimeUtc(open, File.GetLastWriteTimeUtc(log).AddSeconds(5));
		Check(InGame.Compiling(logs, f.Overwrite, DateTime.Now.AddMinutes(-1)), "Open Shaders: started, no completed (the newest log wins)");
		File.AppendAllText(open, "[info] Shader compilation completed: 3169/3169 tasks\n");
		File.SetLastWriteTimeUtc(open, File.GetLastWriteTimeUtc(log).AddSeconds(10));
		Check(!InGame.Compiling(logs, f.Overwrite, DateTime.Now.AddMinutes(-1)), "completed");
		var cache = Path.Combine(f.Overwrite, "ShaderCache");
		Directory.CreateDirectory(cache);
		File.WriteAllText(Path.Combine(cache, "a.bin"), "x");
		Check(InGame.Compiling(logs, f.Overwrite, DateTime.Now.AddMinutes(-1)), "a fresh cache file = compiling, whatever the log says");
		Directory.Delete(cache, true);
		Check(!InGame.Compiling(logs, f.Overwrite, DateTime.Now.AddMinutes(10)), "a log older than the start does not count");
	}

	static void T_ShaderFingerprintChangesWithTheMod()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		f.Mod("Shader Mod");
		var dll = Path.Combine(f.Mods, "Shader Mod", "SKSE", "Plugins", "Shaders.dll");
		Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
		File.WriteAllText(dll, "v1");
		var c = new ShaderStep.Cache("Shader Mod", "Cache Mod", null);
		var a = ShaderStep.Fingerprint(mo2, c);
		File.WriteAllText(dll, "v2");
		Check(a != ShaderStep.Fingerprint(mo2, c), "a new DLL = a new fingerprint");
		Check(!ShaderStep.UpToDate(mo2, c), "no cache = not up to date");
		// with the profile: a feature mod's shader file changes it; what a generator writes (a plugin, a mesh) never does
		var p = ProfileOf(f);
		var feature = Path.Combine(f.Mods, "Base Mod", "Shaders", "Features", "Grass.ini");
		Directory.CreateDirectory(Path.GetDirectoryName(feature)!);
		File.WriteAllText(feature, "[Info]\r\nVersion=1-0-0\r\n");
		var withProfile = ShaderStep.Fingerprint(mo2, c, p);
		File.WriteAllText(feature, "[Info]\r\nVersion=1-1-0\r\n");
		var changed = ShaderStep.Fingerprint(mo2, c, p);
		Check(withProfile != changed, "a ticked mod's shader file is part of it");
		FakeMo2.Plugin(Path.Combine(f.Mods, "Output A", "OutA.esp"), true);
		var mesh = Path.Combine(f.Mods, "Output A", "Meshes", "lod.nif");
		Directory.CreateDirectory(Path.GetDirectoryName(mesh)!);
		File.WriteAllText(mesh, "a rebuilt LOD mesh");
		Equal(changed, ShaderStep.Fingerprint(mo2, c, p), "a rebuilt plugin or mesh leaves it as it was");
	}

	// the fourth review, M1: his Bottled profile keeps a cache for each of two shader mods - switching the tick between them, or
	// updating one, must leave the other's cache current
	static void T_TwoShaderCachesStayApart()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		foreach (var m in new[] { "Shader A", "Shader B" }) {
			var hlsl = Path.Combine(f.Mods, m, "Shaders", "Common.hlsl");
			Directory.CreateDirectory(Path.GetDirectoryName(hlsl)!);
			File.WriteAllText(hlsl, m + " v1");
		}
		var ml = Path.Combine(f.ProfileDir, "modlist.txt");
		var baseList = f.Text("modlist.txt");
		var a = new ShaderStep.Cache("Shader A", "Cache A", null);
		var b = new ShaderStep.Cache("Shader B", "Cache B", null);
		var notA = ShaderStep.OtherCacheMods([a, b], a);
		var p = ProfileOf(f);
		File.WriteAllText(ml, "+Shader A\r\n-Shader B\r\n" + baseList);
		var aTicked = ShaderStep.Fingerprint(mo2, a, p, notA);
		File.WriteAllText(ml, "-Shader A\r\n+Shader B\r\n" + baseList);
		Equal(aTicked, ShaderStep.Fingerprint(mo2, a, p, notA), "the tick moved to the other shader mod: A's cache is still current");
		File.WriteAllText(Path.Combine(f.Mods, "Shader B", "Shaders", "Common.hlsl"), "Shader B v2");
		Equal(aTicked, ShaderStep.Fingerprint(mo2, a, p, notA), "the other shader mod updated: A's cache is still current");
		File.WriteAllText(Path.Combine(f.Mods, "Shader A", "Shaders", "Common.hlsl"), "Shader A v2");
		Check(aTicked != ShaderStep.Fingerprint(mo2, a, p, notA), "its own shader files changed (ticked or not): A's cache is stale");
	}

	// the fourth review, H1: the save to play never starts from an old clean save when the player played after it
	static void T_SaveToPlayStartsFromTheRightSave()
	{
		var mon = new DateTime(2026, 10, 26, 3, 0, 0, DateTimeKind.Utc);
		var thu = mon.AddDays(3);
		Equal("Skyrim Utility Patcher - clean save", InGame.PlaySaveFrom("Skyrim Utility Patcher - clean save", null, mon, "Save9", thu).Save, "this run's clean save first");
		Equal("Named", InGame.PlaySaveFrom(null, "Named", mon, "Save9", thu).Save, "then the one the setup names");
		Equal("Save9", InGame.PlaySaveFrom(null, null, mon, "Save9", thu).Save, "an old clean save loses to newer progress of the player's");
		Equal("Skyrim Utility Patcher - clean save", InGame.PlaySaveFrom(null, null, thu, "Save9", mon).Save, "a clean save newer than every save of theirs is used");
		Equal("Save9", InGame.PlaySaveFrom(null, null, null, "Save9", mon).Save, "no clean save: the player's newest");
		Equal<string?>(null, InGame.PlaySaveFrom(null, null, null, null, null).Save, "nothing at all: no save");
		// only the runner's GAME steps count as its own time - a save the player made while a tool step ran is theirs
		var steps = new[] {
			new StepRecord { Key = "cleansave", Started = mon, Ended = mon.AddMinutes(5) },
			new StepRecord { Key = "dyndolod", Started = mon.AddMinutes(5), Ended = mon.AddMinutes(30) },
			new StepRecord { Key = "playsave", Started = mon.AddMinutes(30), Ended = null },
			new StepRecord { Key = "pandora", Started = null },
		};
		var times = InGame.GameTimes(steps, mon.AddMinutes(40)).ToList();
		Equal(2, times.Count, "the clean save and the save to play");
		Check(!times.Any(t => mon.AddMinutes(15) >= t.From && mon.AddMinutes(15) <= t.To), "a save made during DynDOLOD is the player's");
		// the fifth review, N1: a run that died in a game step - its open step ends where its record stopped, so a save made a
		// day later is still the player's
		var died = InGame.GameTimes([new StepRecord { Key = "cleansave", Started = mon, Ended = null }], mon.AddMinutes(3)).Single();
		Check(!(thu >= died.From && thu <= died.To), "a save three days after a crashed run is the player's");
		Equal(mon.AddMinutes(3), died.To, "the open step ends where the dead run's record stopped");
	}

	// the fourth review, M2: a patcher's CPU counts per child process - one that ends never makes it fall, a new one counts whole
	static void T_ChildCpuGain()
	{
		var before = new Dictionary<int, TimeSpan> { [1] = TimeSpan.FromSeconds(5), [2] = TimeSpan.FromSeconds(9) };
		var now = new Dictionary<int, TimeSpan> { [1] = TimeSpan.FromSeconds(5.1), [3] = TimeSpan.FromSeconds(2) };
		Equal(TimeSpan.FromSeconds(2.1), Procs.CpuGain(before, now), "child 2 ended (not a loss), child 3 is new (whole), child 1 +0.1 s");
		Equal(TimeSpan.Zero, Procs.CpuGain(now, now), "nothing used: no gain");
	}

	// the fourth review, L5 / M4 / L6: a form id read either way, a setup read from bytes, a task's own setup file
	static void T_SmallReadsFromTheReview()
	{
		Equal("0x0000003B", DevBench.FormKey(JsonValue.Create("0x3B")), "0x is hex");
		Equal("0xFF000801", DevBench.FormKey(JsonValue.Create("FF000801")), "an 8-digit id is hex");
		Equal("0x0000007B", DevBench.FormKey(JsonValue.Create("123")), "a plain number is decimal");
		Equal("0x0000003B", DevBench.FormKey(JsonValue.Create("0000003B")), "a padded 8-digit id is hex");
		Equal("0x00BC614E", DevBench.FormKey(JsonValue.Create("12345678")), "an 8-digit decimal is decimal");
		Equal("0x0000003B", DevBench.FormKey(JsonValue.Create(59)), "a JSON number");
		var s = Setup.Parse(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes("{\"schema\":1,\"name\":\"From bytes\"}")).ToArray(), @"C:\x.json");
		Equal("From bytes", s.Name, "a setup read from its bytes (a BOM in front)");
	}

	static void T_CheckBoxPixels()
	{
		var px = new int[20 * 3];
		for (int x = 3; x < 12; ++x) {
			px[20 + x] = unchecked((int)0xFF2060E0);
		}
		Check(CheckList.IsTicked(new Picture(20, 3, px), 3, 15, 1), "a filled box is ticked");
		Check(!CheckList.IsTicked(new Picture(20, 3, new int[60]), 3, 15, 1), "a grey box is not");
	}

	// ------------------------------------------------------------------ the whole run, with test steps
	sealed class FakeStep(string a_key, Action<RunContext, StepSettings> a_run) : IStep
	{
		public string Key => a_key;
		public void Run(RunContext a_ctx, StepSettings a_s) => a_run(a_ctx, a_s);
		public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s) => [];
	}

	static void WriteOutput(RunContext a_ctx, string a_step, string a_mod, string a_plugin)
	{
		StepKit.ParkMod(a_ctx, a_step, a_mod);
		FakeMo2.Plugin(Path.Combine(a_ctx.Mo2.ModDir(a_mod), a_plugin), false);
		File.WriteAllText(Path.Combine(a_ctx.Mo2.ModDir(a_mod), "new.txt"), "fresh");
		a_ctx.SetAfter(a_mod);
	}

	static void T_RunSwitchesOutputsOffAndBuiltOnesBackOn()
	{
		using var f = new FakeMo2();
		var seenOff = false;
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			seenOff = !ctx.Profile.IsOn("Output A") && !ctx.Profile.IsOn("Output B");
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
		}));
		StepRegistry.Replace(new FakeStep("synthesis", (ctx, s) => WriteOutput(ctx, "synthesis", "Output B", "OutB.esp")));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", new JsonObject { ["output_mod"] = "Output B" }));
		var before = f.Text("loadorder.txt");
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "finished: " + r.Verdict);
		Check(seenOff, "every output was OFF while the first tool ran");
		var p = ProfileOf(f);
		Check(p.IsOn("Output A") && p.IsOn("Output B") && p.IsOn("Needs B"), "built outputs on, everything else as before");
		Equal(before, f.Text("loadorder.txt"), "the load order byte for byte");
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "new.txt")), "the new output is in its mod");
		var zips = Directory.GetFiles(f.Backups, RunUndo.OutputsZip, SearchOption.AllDirectories);
		Check(zips.Length == 1, "the old outputs are packed in the backup");
		using var packed = System.IO.Compression.ZipFile.OpenRead(zips[0]);
		Check(packed.Entries.Any(e => e.Name == "OutA.esp"), "the old output is in it");
	}

	// his checklist 454: a finished run's old outputs are packed, its folder gone; undoing the run puts each output and the lists back,
	// sets the new outputs aside (never erased), takes out an output the run made new, keeps what the player changed; it can be done once,
	// only to the last run on that profile and those outputs; a switched-off pack keeps the folder
	static void T_TheLastRunCanBeUndone()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		StepRegistry.Replace(new FakeStep("synthesis", (ctx, s) => {
			ctx.Profile.EnsureMod("Brand New", ctx.Log);
			File.WriteAllText(Path.Combine(f.Mods, "Brand New", "fresh.txt"), "made by the run");
			ctx.Journal.Built.Add("Brand New");
			ctx.Journal.Added.Add("Brand New");
			ctx.Journal.Save();
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", new JsonObject()));
		var before = f.Text("loadorder.txt");
		var oldBytes = File.ReadAllBytes(Path.Combine(f.Mods, "Output A", "OutA.esp"));
		Equal(RunState.Finished, Runner.Run(new RunRequest(f.SetupFile, f.Data)).State, "the run finished");
		var j = Journal.All(Runner.RunsDir(f.Data)).First();
		Check(File.Exists(Path.Combine(j.BackupDir, RunUndo.OutputsZip)) && !Directory.Exists(Path.Combine(j.BackupDir, RunUndo.OutputsFolder)), "packed: the zip there, the folder gone");
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "new.txt")), "(the new output in its mod)");
		// the player changes something after the run
		var list = Path.Combine(f.ProfileDir, "modlist.txt");
		File.WriteAllText(list, File.ReadAllText(list).Replace("+Base Mod", "-Base Mod", StringComparison.Ordinal));
		var r = Runner.Undo(Journal.Load(j.Folder));
		Equal(RunState.Restored, r.State, "undone: " + r.Verdict);
		Check(!File.Exists(Path.Combine(f.Mods, "Output A", "new.txt")), "the new output left its mod");
		Check(File.ReadAllBytes(Path.Combine(f.Mods, "Output A", "OutA.esp")).SequenceEqual(oldBytes), "the old output is back, byte for byte");
		Check(File.Exists(Path.Combine(j.BackupDir, "undone", "Output A", "new.txt")), "the new output is set aside, never erased");
		Check(File.Exists(Path.Combine(j.BackupDir, "undone", "Brand New", "fresh.txt")), "so is one the run made new");
		var p = ProfileOf(f);
		Check(!p.States().ContainsKey("Brand New") && !Directory.Exists(Path.Combine(f.Mods, "Brand New")), "an output the run made new is out of the mod list and the mods folder");
		Check(!p.IsOn("Base Mod"), "what the player changed since is kept");
		Check(p.IsOn("Output A") && p.IsOn("Output B"), "the rest as before the run");
		Equal(before, f.Text("loadorder.txt"), "the load order as before");
		var again = Runner.Undo(Journal.Load(j.Folder));
		Check(again.Verdict.Contains("undone already", StringComparison.Ordinal), "only once: " + again.Verdict);
		Check(LastRuns.Of(Journal.All(Runner.RunsDir(f.Data)), f.SetupFile).Count == 0, "an undone run no longer counts as a step's last run");
		// an older run cannot be undone past a newer one that changed the same profile or outputs; a later run that changed nothing is in nobody's way
		Journal J(string a_id, int a_days, string? a_setup = null, string a_profile = "P", string[]? a_built = null, RunState a_state = RunState.Finished) =>
			new() { Id = a_id, Folder = Path.Combine(Runner.RunsDir(f.Data), a_id), SetupFile = a_setup ?? f.SetupFile, Profile = a_profile, Started = DateTime.Now.AddDays(a_days), State = a_state, Built = [.. a_built ?? ["Output A"]] };
		var older = J("older", -1);
		bool Blocked(Journal a_later) => RunUndo.WhyNot(older, [older, a_later])?.Contains("last run", StringComparison.Ordinal) == true;
		Check(Blocked(J("newer", 0)), "only the last run");
		Check(!Blocked(J("empty", 0, a_built: [], a_state: RunState.Failed)), "a later run that built nothing is in nobody's way");
		Check(Blocked(J("cut off", 0, a_built: [], a_state: RunState.Running)), "a later run that did not finish is");
		Check(Blocked(J("other setup", 0, "other.json")), "so is another setup's run on the same profile");
		Check(Blocked(J("other profile", 0, "other.json", "Q")), "or on another profile with one of the same outputs");
		Check(!Blocked(J("elsewhere", 0, "other.json", "Q", ["Output Z"])), "another setup's run elsewhere is not");
		// a pack that is switched off keeps the folder
		var dir = Path.Combine(f.Root, "pack test");
		Directory.CreateDirectory(Path.Combine(dir, RunUndo.OutputsFolder, "Mod"));
		File.WriteAllText(Path.Combine(dir, RunUndo.OutputsFolder, "Mod", "a.txt"), "a");
		RunUndo.Pack(dir, RunLog.Null);
		Check(File.Exists(Path.Combine(dir, RunUndo.OutputsZip)), "packed");
		RunUndo.Unpack(dir);
		Equal("a", File.ReadAllText(Path.Combine(dir, RunUndo.OutputsFolder, "Mod", "a.txt")), "unpacked whole");
		Check(!File.Exists(Path.Combine(dir, RunUndo.OutputsZip)), "the zip gone after it");
		// a folder that will not go once the zip is whole: said as packed, and the zip brings everything back
		var held = Path.Combine(f.Root, "pack held");
		Directory.CreateDirectory(Path.Combine(held, RunUndo.OutputsFolder, "Mod"));
		File.WriteAllText(Path.Combine(held, RunUndo.OutputsFolder, "Mod", "a.txt"), "a");
		File.WriteAllText(Path.Combine(held, RunUndo.OutputsFolder, "Mod", "b.txt"), "b");
		using (var log = new RunLog(held)) {
			using (new FileStream(Path.Combine(held, RunUndo.OutputsFolder, "Mod", "a.txt"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
				RunUndo.Pack(held, log);
			}
		}
		Check(File.ReadAllText(Path.Combine(held, "log.txt")).Contains("are packed", StringComparison.Ordinal), "said as packed, not as left unpacked");
		RunUndo.Unpack(held);
		Check(File.ReadAllText(Path.Combine(held, RunUndo.OutputsFolder, "Mod", "b.txt")) == "b" && File.ReadAllText(Path.Combine(held, RunUndo.OutputsFolder, "Mod", "a.txt")) == "a", "and the zip brings both back");
	}

	// an undo cut off half way (a file held open) can be pressed again: what the first press put back stays, what it set aside stays
	static void T_AnUndoCutOffCanBePressedAgain()
	{
		using var f = new FakeMo2();
		File.WriteAllText(Path.Combine(f.Mods, "Output A", "old.txt"), "old A");
		File.WriteAllText(Path.Combine(f.Mods, "Output B", "old.txt"), "old B");
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		StepRegistry.Replace(new FakeStep("synthesis", (ctx, s) => WriteOutput(ctx, "synthesis", "Output B", "OutB.esp")));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", new JsonObject { ["output_mod"] = "Output B" }));
		Equal(RunState.Finished, Runner.Run(new RunRequest(f.SetupFile, f.Data)).State, "the run finished");
		var j = Journal.All(Runner.RunsDir(f.Data)).First();
		var undone = Path.Combine(j.BackupDir, "undone");
		// an undo walks from the last step back: B goes first, then A's new output is held open
		using (new FileStream(Path.Combine(f.Mods, "Output A", "new.txt"), FileMode.Open, FileAccess.Read, FileShare.None)) {
			Equal(RunState.Failed, Runner.Undo(Journal.Load(j.Folder)).State, "the first press stops at the held file");
		}
		Equal("old B", File.ReadAllText(Path.Combine(f.Mods, "Output B", "old.txt")), "B is back after the first press");
		var second = Runner.Undo(Journal.Load(j.Folder));
		Equal(RunState.Restored, second.State, "the second press: " + second.Verdict);
		Equal("old B", File.ReadAllText(Path.Combine(f.Mods, "Output B", "old.txt")), "B's old output is not taken for the run's");
		Check(!File.Exists(Path.Combine(f.Mods, "Output B", "new.txt")) && File.Exists(Path.Combine(undone, "Output B", "new.txt")), "B's new output stays set aside");
		Equal("old A", File.ReadAllText(Path.Combine(f.Mods, "Output A", "old.txt")), "A is back after the second");
		Check(!File.Exists(Path.Combine(f.Mods, "Output A", "new.txt")) && Directory.GetFiles(undone, "new.txt", SearchOption.AllDirectories).Length == 2, "and its new output set aside too");
	}

	// two steps writing one output each keep what they wrote; a tool folder's label never passes for a mod of that name; a tool folder whose
	// move failed is not on record
	static void T_AnUndoKeepsEveryOutputAside()
	{
		using var f = new FakeMo2();
		var tool = Path.Combine(f.Root, "tool out");
		var other = Path.Combine(f.Root, "other tool out");
		var heldDir = Path.Combine(f.Root, "held tool out");
		foreach (var d in new[] { tool, other, heldDir }) {
			Directory.CreateDirectory(d);
			File.WriteAllText(Path.Combine(d, "tool old.txt"), "old");
		}
		File.WriteAllText(Path.Combine(f.Mods, "Output A", "old.txt"), "old A");
		void Second(RunContext a_ctx, string a_text) => File.WriteAllText(Path.Combine(a_ctx.Mo2.ModDir("Output A"), "new.txt"), a_text);
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
			Second(ctx, "first");
		}));
		StepRegistry.Replace(new FakeStep("synthesis", (ctx, s) => {
			WriteOutput(ctx, "synthesis", "Output A", "OutA.esp");
			Second(ctx, "second");
			StepKit.ParkDir(ctx, tool, "brand new");
			File.WriteAllText(Path.Combine(tool, "tool new.txt"), "made by the run");
			StepKit.ParkDir(ctx, other, "base mod");
			ctx.Profile.EnsureMod("Brand New", ctx.Log);
			File.WriteAllText(Path.Combine(f.Mods, "Brand New", "fresh.txt"), "made by the run");
			ctx.Journal.Built.Add("Brand New");
			ctx.Journal.Added.Add("Brand New");
			using (new FileStream(Path.Combine(heldDir, "tool old.txt"), FileMode.Open, FileAccess.Read, FileShare.None)) {
				try {
					StepKit.ParkDir(ctx, heldDir, "held");
					Check(false, "a held folder does not move");
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					Check(!ctx.Journal.Parked.Any(p => p.Mod == "held"), "a move that failed is not on record");
				}
			}
			ctx.Journal.Save();
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", new JsonObject()));
		Equal(RunState.Finished, Runner.Run(new RunRequest(f.SetupFile, f.Data)).State, "the run finished");
		var j = Journal.All(Runner.RunsDir(f.Data)).First();
		var list = Path.Combine(f.ProfileDir, "modlist.txt");
		File.WriteAllText(list, File.ReadAllText(list).Replace("+Base Mod", "-Base Mod", StringComparison.Ordinal));
		var r = Runner.Undo(Journal.Load(j.Folder));
		Equal(RunState.Restored, r.State, "undone: " + r.Verdict);
		var undone = Path.Combine(j.BackupDir, "undone");
		var kept = Directory.GetFiles(undone, "new.txt", SearchOption.AllDirectories).Select(File.ReadAllText).Order(StringComparer.Ordinal).ToList();
		Equal("first|second", string.Join("|", kept), "both steps' files are kept");
		Equal("old A", File.ReadAllText(Path.Combine(f.Mods, "Output A", "old.txt")), "the old output is back");
		Equal(1, Directory.GetFiles(undone, "fresh.txt", SearchOption.AllDirectories).Length, "a mod with a tool folder's name is set aside all the same");
		Check(!ProfileOf(f).States().ContainsKey("Brand New"), "and taken out");
		Check(!ProfileOf(f).IsOn("Base Mod"), "the player's change to a mod named like a tool folder's label is kept");
		Check(File.Exists(Path.Combine(tool, "tool old.txt")) && !File.Exists(Path.Combine(tool, "tool new.txt")), "the tool's folder is as it was");
		Equal(1, Directory.GetFiles(undone, "tool new.txt", SearchOption.AllDirectories).Length, "what the run left in it is set aside");
	}

	// ------------------------------------------------------------------ what MO2 changed (his ask 2026-10-27)
	static void T_Mo2ChangesReadThePaneTopDown()
	{
		// the file lists the pane bottom-up: a separator sits AFTER its mods; "*" lines are MO2's own, not mods
		var pane = Mo2Changes.Pane(["# generated", "+Mod C", "-Mod B", "+Utilities_separator", "*DLC: Dawnguard", "+Mod A", "-Other_separator", "+Top Mod", ""]);
		Equal("Top Mod|-|Mod A|Other|Mod B|Utilities|Mod C|Utilities", string.Join("|", pane.SelectMany(m => new[] { m.Name, m.Section == "" ? "-" : m.Section })), "top-down, each under its separator");
		Check(pane.Single(m => m.Name == "Mod B").On == false && pane.Single(m => m.Name == "Mod C").On, "ticks read");
		Check(pane.All(m => !m.Name.StartsWith("DLC", StringComparison.Ordinal) && !m.Name.EndsWith("_separator", StringComparison.Ordinal)), "no base game, no separator");
	}

	static void T_Mo2ChangesNameEveryKindOfChange()
	{
		Mo2Changes.PaneMod M(string a_n, bool a_on, string a_s) => new(a_n, a_on, a_s);
		var before = new List<Mo2Changes.PaneMod> { M("A", true, "S1"), M("B", true, "S1"), M("C", true, "S1"), M("D", false, "S1"), M("G", true, "S2") };
		var now = new List<Mo2Changes.PaneMod> { M("B", true, "S1"), M("A", true, "S1"), M("C", false, "S1"), M("D", false, "Other"), M("F", true, "") };
		var got = Mo2Changes.Compare(before, now);
		Check(got.Contains(("F", "added (ticked, under no section)")), "added: " + string.Join("; ", got));
		Check(got.Contains(("G", "removed (was under S2)")), "removed");
		Check(got.Contains(("C", "unticked")), "unticked");
		Equal(1, got.Count(r => r.What.StartsWith("moved within", StringComparison.Ordinal)), "two mods swapped = ONE moved");
		Check(!got.Any(r => r.Mod == "D" && r.What.StartsWith("moved", StringComparison.Ordinal)), "D kept its place: a new section is not a move");
		// a separator moved past mods: their section changes, their place does not - no row
		Equal(0, Mo2Changes.Compare([M("A", true, "S1"), M("B", true, "S1")], [M("A", true, "S0"), M("B", true, "S0")]).Count, "a separator's move is no mod's move");
		// one mod taken from the top to the bottom: only it moved, into its new section
		var moved = Mo2Changes.Compare([M("A", false, "S1"), M("B", true, "S1"), M("C", true, "S1")], [M("B", true, "S1"), M("C", true, "S1"), M("A", false, "Other")]);
		Equal("A: moved from S1 to Other", string.Join("; ", moved.Select(r => $"{r.Mod}: {r.What}")), "one move, both sections named");
		// names as Windows sees folders: a new spelling is the same mod
		Equal(0, Mo2Changes.Compare([M("PG Patch", true, "S")], [M("PG patch", true, "S")]).Count, "same mod, other case");
	}

	static void T_Mo2ChangesRecordGroupAndHide()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var second = Path.Combine(f.Instance, "profiles", "Second");
		Directory.CreateDirectory(second);
		File.Copy(Path.Combine(f.ProfileDir, "modlist.txt"), Path.Combine(second, "modlist.txt"));
		Equal(0, Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside).Count, "the first look only starts watching");
		var dir = Mo2Changes.Dir(f.Data, mo2);
		Check(Mo2Changes.WatchedSince(dir) != null && !Path.GetFileName(dir).Contains('_', StringComparison.Ordinal), "watched, its folder named without underscores");
		// the same change in both profiles, a change in one, a mod added to MO2, a test mod
		foreach (var p in new[] { f.ProfileDir, second }) {
			var file = Path.Combine(p, "modlist.txt");
			File.WriteAllText(file, File.ReadAllText(file).Replace("+Base Mod", "-Base Mod", StringComparison.Ordinal));
		}
		File.WriteAllText(Path.Combine(second, "modlist.txt"), File.ReadAllText(Path.Combine(second, "modlist.txt")).Replace("+Replaced", "-Replaced", StringComparison.Ordinal));
		f.Mod("Fresh Mod");
		f.Mod("TEST Something");
		var rows = Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside);
		Check(rows.Any(r => r.Mod == "Base Mod" && r.What == "unticked" && r.Profiles.SequenceEqual(["Second", "Test"])), "one row for both profiles: " + string.Join("; ", rows.Select(r => $"{r.Mod} {r.What} [{string.Join(",", r.Profiles)}]")));
		Check(rows.Any(r => r.Mod == "Replaced" && r.What == "unticked" && r.Profiles.SequenceEqual(["Second"])), "a change in one profile names it alone");
		Check(rows.Any(r => r.Mod == "Fresh Mod" && r.What == "added to Mod Organizer" && r.Profiles.Count == 0), "a mod added to MO2");
		Equal(0, Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside).Count, "nothing new on the next look");
		var kept = Mo2Changes.Read(dir);
		Equal(rows.Count, kept.Count, "every row kept on disk");
		Check(kept.All(r => r.Who == Mo2Changes.Outside), "and whose");
		// the view: a profile left out comes off each row (a row with none left goes), a mod name left out goes
		var shown = Mo2Changes.Shown(kept, ["Second"], ["TEST "]);
		Check(shown.Any(r => r.Mod == "Base Mod" && r.Profiles.SequenceEqual(["Test"])), "Second taken off the shared row");
		Check(!shown.Any(r => r.Mod == "Replaced"), "a row only of Second dropped");
		Check(!shown.Any(r => r.Mod.StartsWith("TEST", StringComparison.Ordinal)) && shown.Any(r => r.Mod == "Fresh Mod"), "test mods left out, Mod Organizer's rows kept");
		// a profile's own row is never dropped by a mod-name filter (only by the profiles left out)
		var p3 = Path.Combine(f.Instance, "profiles", "TEST Profile");
		Directory.CreateDirectory(p3);
		File.Copy(Path.Combine(f.ProfileDir, "modlist.txt"), Path.Combine(p3, "modlist.txt"));
		var added = Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside);
		Check(Mo2Changes.Shown(added, [], ["TEST "]).Any(r => r.What == "profile added" && r.Mod == "TEST Profile"), "a profile named like a hidden mod still shows");
		// one mod spelt two ways in two profiles is ONE row
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), File.ReadAllText(Path.Combine(f.ProfileDir, "modlist.txt")).Replace("+Master Mod", "-Master Mod", StringComparison.Ordinal));
		File.WriteAllText(Path.Combine(second, "modlist.txt"), File.ReadAllText(Path.Combine(second, "modlist.txt")).Replace("+Master Mod", "-master mod", StringComparison.Ordinal));
		var cased = Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside);
		Equal(1, cased.Count(r => r.What == "unticked" && r.Mod.Equals("Master Mod", StringComparison.OrdinalIgnoreCase)), "one row for both spellings: " + string.Join("; ", cased.Select(r => $"{r.Mod} {r.What} [{string.Join(",", r.Profiles)}]")));
		// a mod list read half-written (empty here) or missing for a known profile: no look at all, nothing written
		var lookBytes = File.ReadAllBytes(Path.Combine(dir, "last look.json"));
		var rowsBytes = File.ReadAllBytes(Path.Combine(dir, "changes.json"));
		var secondList = Path.Combine(second, "modlist.txt");
		var keepList = File.ReadAllBytes(secondList);
		File.WriteAllBytes(secondList, []);
		Throws<IOException>(() => Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside), "an empty mod list is not a look");
		File.Delete(secondList);
		Throws<IOException>(() => Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside), "a known profile's missing mod list is not a removed profile");
		Check(File.ReadAllBytes(Path.Combine(dir, "last look.json")).AsSpan().SequenceEqual(lookBytes) && File.ReadAllBytes(Path.Combine(dir, "changes.json")).AsSpan().SequenceEqual(rowsBytes), "nothing written by a look that did not read");
		File.WriteAllBytes(secondList, keepList);
		// a look stopped between its rows and its own file: finished next time, its rows kept once
		File.WriteAllText(f.ProfileDir + "\\modlist.txt", File.ReadAllText(f.ProfileDir + "\\modlist.txt").Replace("+Output A", "-Output A", StringComparison.Ordinal));
		var lastLook = File.ReadAllBytes(Path.Combine(dir, "last look.json"));
		var once = Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside);
		Check(once.Any(r => r.Mod == "Output A"), "the change seen");
		File.Copy(Path.Combine(dir, "last look.json"), Path.Combine(dir, "next look.json"));
		File.WriteAllBytes(Path.Combine(dir, "last look.json"), lastLook);
		var total = Mo2Changes.Read(dir).Count;
		Equal(0, Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside).Count, "the stopped look finished, not seen again");
		Equal(total, Mo2Changes.Read(dir).Count, "no row kept twice");
		Check(!File.Exists(Path.Combine(dir, "next look.json")), "the next look became the last one");
		// stopped before its rows were kept (no row carries its time): dropped, and the change it saw is found again
		File.WriteAllText(f.ProfileDir + "\\modlist.txt", File.ReadAllText(f.ProfileDir + "\\modlist.txt").Replace("+Output B", "-Output B", StringComparison.Ordinal));
		var unkept = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "last look.json")))!.AsObject();
		unkept["taken"] = "2001-01-01T00:00:00.0000000";
		foreach (var m in unkept["profiles"]!["Test"]!.AsArray().OfType<JsonArray>().Where(m => m[0]!.ToString() == "Output B")) {
			m[1] = false;
		}
		File.WriteAllText(Path.Combine(dir, "next look.json"), unkept.ToJsonString());
		Check(Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside).Any(r => r.Mod == "Output B" && r.What == "unticked"), "a look whose rows were never kept is dropped: the change is found");
		// a damaged history is never written over: set aside whole, and a new one starts
		var history = Path.Combine(dir, "changes.json");
		File.WriteAllText(history, "[ { not json");
		File.WriteAllText(f.ProfileDir + "\\modlist.txt", File.ReadAllText(f.ProfileDir + "\\modlist.txt").Replace("-Base Mod", "+Base Mod", StringComparison.Ordinal));
		Check(Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside).Count > 0, "(a change to keep)");
		var aside = Directory.GetFiles(dir, "changes unreadable *.json");
		Check(aside.Length == 1 && File.ReadAllText(aside[0]) == "[ { not json", "the damaged history kept as it was, beside the new one");
		Check(Mo2Changes.Read(dir).Count > 0, "the new history reads");
		// a damaged last look starts the watch again - never a failure
		var keptNow = Mo2Changes.Read(dir).Count;
		File.WriteAllText(Path.Combine(dir, "last look.json"), "{ not json");
		Equal(0, Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside).Count, "a damaged look starts over");
		Equal(keptNow, Mo2Changes.Read(dir).Count, "the rows already kept stay");
	}

	// "Look now" and the command line's --look hold the run lock from their check to their end: never beside a run
	static void T_OutsideLookNeverBesideARun()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		using var taken = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		var t = new Thread(() => {
			using var hold = Runner.HoldIfFree();
			taken.Set();
			release.Wait();
		});
		t.Start();
		taken.Wait();
		try {
			Equal<int?>(null, Mo2Changes.RecordOutside(mo2, f.Data), "no look while a run holds the lock");
			Check(!Directory.Exists(Mo2Changes.Dir(f.Data, mo2)) || Directory.GetFiles(Mo2Changes.Dir(f.Data, mo2)).Length == 0, "nothing written");
		} finally {
			release.Set();
			t.Join();
		}
		Equal<int?>(0, Mo2Changes.RecordOutside(mo2, f.Data), "free again: the look is taken");
	}

	static void T_RunNotesWhatItChangedApartFromWhatWasChangedOutside()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside);
		// the player unticks a mod in MO2, then a run makes a new output mod
		var list = Path.Combine(f.ProfileDir, "modlist.txt");
		File.WriteAllText(list, File.ReadAllText(list).Replace("+Base Mod", "-Base Mod", StringComparison.Ordinal));
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "New Output", "New.esp")));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "New Output" }));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "finished: " + r.Verdict);
		var rows = Mo2Changes.Read(Mo2Changes.Dir(f.Data, mo2));
		var text = string.Join("; ", rows.Select(x => $"{x.Mod} {x.What} ({x.Who})"));
		Check(rows.Any(x => x.Mod == "Base Mod" && x.What == "unticked" && x.Who == Mo2Changes.Outside), "the player's change, as made outside the runner: " + text);
		Check(rows.Any(x => x.Mod == "New Output" && x.What == "added to Mod Organizer" && x.Who.StartsWith("the run of", StringComparison.Ordinal)), "the run's new mod, as the run's: " + text);
		Check(rows.Any(x => x.Mod == "New Output" && x.What.StartsWith("added (ticked", StringComparison.Ordinal) && x.Who.StartsWith("the run of", StringComparison.Ordinal)), "ticked in the profile by the run: " + text);
		Check(!rows.Any(x => x.Mod is "Output A" or "Output B"), "outputs switched off and back on during the run are no net change: " + text);

		// the setup's own before-run command changes MO2: the run's change, not one made outside it
		var changed = Path.Combine(f.Root, "modlist changed.txt");
		File.WriteAllText(changed, File.ReadAllText(list).Replace("+Master Mod", "-Master Mod", StringComparison.Ordinal));
		var s = Setup.Load(f.SetupFile);
		s.Root["beforeRun"] = new JsonArray($"copy /y \"{changed}\" \"{list}\" >nul");
		s.Save();
		r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "finished: " + r.Verdict);
		rows = Mo2Changes.Read(Mo2Changes.Dir(f.Data, mo2));
		text = string.Join("; ", rows.Select(x => $"{x.Mod} {x.What} ({x.Who})"));
		Check(rows.Any(x => x.Mod == "Master Mod" && x.What == "unticked" && x.Who.StartsWith("the run of", StringComparison.Ordinal)), "the before-run command's change is the run's: " + text);
		Check(!rows.Any(x => x.Mod == "Master Mod" && x.Who == Mo2Changes.Outside), "never as made outside the runner: " + text);
	}

	// a put-back after a crash: what changed before it (the cut-off run, the player since) is noted apart from the put-back's own
	static void T_PutBackNotesOnlyItsOwnChanges()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside);
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			ctx.Journal.Pid = 999999;
			ctx.Journal.Save();
			throw new CrashForTest();
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		try {
			RunUntilCrash(f);
		} catch (CrashForTest) {
		}
		Equal(RunState.Restored, Runner.Restore(Journal.Interrupted(Runner.RunsDir(f.Data))!).State, "restored");
		var rows = Mo2Changes.Read(Mo2Changes.Dir(f.Data, mo2));
		var text = string.Join("; ", rows.Select(x => $"{x.Mod} {x.What} ({x.Who})"));
		Check(rows.Any(x => x.Mod == "Output A" && x.What == "unticked" && x.Who.StartsWith("seen before putting back", StringComparison.Ordinal)), "the cut-off run's change noted before the put-back: " + text);
		Check(rows.Any(x => x.Mod == "Output A" && x.What == "ticked" && x.Who.StartsWith("putting back", StringComparison.Ordinal)), "the put-back's own change: " + text);
		Check(!rows.Any(x => x.What == "unticked" && x.Who.StartsWith("putting back", StringComparison.Ordinal)), "nothing from before it named as the put-back's: " + text);
	}

	static void T_FailedStepPutsItsOldOutputBack()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		StepRegistry.Replace(new FakeStep("synthesis", (ctx, s) => {
			StepKit.ParkMod(ctx, "synthesis", "Output B");
			File.WriteAllText(Path.Combine(ctx.Mo2.ModDir("Output B"), "half.txt"), "half");
			throw new StepFailedException("the tool crashed");
		}));
		StepRegistry.Replace(new FakeStep("pgpatcher", (ctx, s) => throw new InvalidOperationException("must not run")));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", new JsonObject { ["output_mod"] = "Output B" }), ("pgpatcher", []));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Failed, r.State, "failed");
		Check(r.Verdict.Contains("Synthesis", StringComparison.Ordinal) && r.Verdict.Contains("the tool crashed", StringComparison.Ordinal), "the verdict names the step and why: " + r.Verdict);
		Check(File.Exists(Path.Combine(f.Mods, "Output B", "OutB.esp")), "the old output is back in its mod");
		Check(!File.Exists(Path.Combine(f.Mods, "Output B", "half.txt")), "the half-written output is moved out");
		var p = ProfileOf(f);
		Check(p.IsOn("Output A") && p.IsOn("Output B") && p.IsOn("Needs B"), "the profile as before, the finished output on");
		var j = Journal.All(Runner.RunsDir(f.Data)).First();
		Equal(StepState.Skipped, j.Step("pgpatcher")!.State, "the step after it never ran");
	}

	static void T_StopAfterThisStep()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			Commands.Send(ctx.Journal.Folder, "stop after");
			Thread.Sleep(1200);
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
		}));
		var ran = false;
		StepRegistry.Replace(new FakeStep("synthesis", (ctx, s) => ran = true));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("synthesis", []));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Stopped, r.State, "stopped: " + r.Verdict);
		Check(!ran, "the next step did not run");
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "new.txt")), "the running step finished");
	}

	static void T_StopNowIsSafe()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			StepKit.ParkMod(ctx, "landscapefixer", "Output A");
			Commands.Send(ctx.Journal.Folder, "stop");
			for (int i = 0; i < 50; ++i) {
				ctx.Cancel.ThrowIfCancellationRequested();
				Thread.Sleep(100);
			}
			throw new StepFailedException("the stop was not seen");
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Stopped, r.State, "stopped: " + r.Verdict);
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "OutA.esp")), "the old output back");
		Check(ProfileOf(f).IsOn("Output A"), "the profile back");
	}

	static void T_CrashedRunIsRestored()
	{
		using var f = new FakeMo2();
		// a run that "crashed": the record says running, its owner is gone, outputs off, one output parked
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			StepKit.ParkMod(ctx, "landscapefixer", "Output A");
			ctx.Journal.Pid = 999999;
			ctx.Journal.Save();
			throw new CrashForTest();
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		try {
			RunUntilCrash(f);
		} catch (CrashForTest) {
		}
		var j = Journal.Interrupted(Runner.RunsDir(f.Data));
		Check(j != null, "the unfinished run is found");
		Check(!File.Exists(Path.Combine(f.Mods, "Output A", "OutA.esp")), "(the crash left the output parked)");
		var refused = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Check(refused.Verdict.Contains("did not finish", StringComparison.Ordinal), "a new run is refused until it is put back: " + refused.Verdict);
		var r = Runner.Restore(j!);
		Equal(RunState.Restored, r.State, "restored: " + r.Verdict);
		Check(Journal.Load(j!.Folder).Steps.All(s => s.State != StepState.Stopped || s.Ended != null), "the step the crash cut off is closed (it never reads as still running)");
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "OutA.esp")), "the parked output is back");
		var p = ProfileOf(f);
		Check(p.IsOn("Output A") && p.IsOn("Output B") && p.IsOn("Needs B"), "the profile as it was");
	}

	// the put-back after a crash keeps what the player changed in Mod Organizer while the run went on and after it was cut off -
	// the run's last expectation is read back from its folder (CodeRabbit on #24); the run's own switches are still undone
	static void T_RestoreKeepsThePlayersChanges()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			StepKit.ParkMod(ctx, "landscapefixer", "Output A");
			// the player unticks a mod of theirs in Mod Organizer while the step works
			var list = Path.Combine(f.ProfileDir, "modlist.txt");
			File.WriteAllText(list, File.ReadAllText(list).Replace("+Base Mod", "-Base Mod", StringComparison.Ordinal));
			ctx.Journal.Pid = 999999;
			ctx.Journal.Save();
			throw new CrashForTest();
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		try {
			RunUntilCrash(f);
		} catch (CrashForTest) {
		}
		var j = Journal.Interrupted(Runner.RunsDir(f.Data));
		Check(j != null && File.Exists(Path.Combine(j.Folder, Profile.WatchFileName)), "the run kept what it wrote in its folder");
		// and after the crash, before Restore, the player installs and ticks a new mod
		f.Mod("Players New");
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "+Players New\r\n" + f.Text("modlist.txt"));
		var r = Runner.Restore(j!);
		Equal(RunState.Restored, r.State, "restored: " + r.Verdict);
		var p = ProfileOf(f);
		Check(!p.IsOn("Base Mod"), "the mod the player unticked during the run stays off");
		Check(p.IsOn("Players New"), "the mod the player ticked after the crash stays on");
		Check(p.IsOn("Output A") && p.IsOn("Output B") && p.IsOn("Needs B"), "the run's own switches are undone - the profile as it was");
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "OutA.esp")), "the parked output is back");
	}

	// a file a step changed (a tool's settings): put back by the step's failure, and after a crash by Restore
	static void T_KeptFilesComeBackAfterAFailureAndACrash()
	{
		using var f = new FakeMo2();
		var settings = Path.Combine(f.Root, "tool settings.json");
		var flag = Path.Combine(f.Root, "flag.txt");
		File.WriteAllText(settings, "his own");
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			StepKit.KeepFile(ctx, settings);
			StepKit.KeepFile(ctx, flag);
			File.WriteAllText(settings, "pointed at the output");
			File.WriteAllText(flag, "");
			throw new StepFailedException("the tool crashed");
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Failed, r.State, "failed");
		Equal("his own", File.ReadAllText(settings), "the changed file is back");
		Check(!File.Exists(flag), "the file it made is gone again");

		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			StepKit.KeepFile(ctx, settings);
			File.WriteAllText(settings, "pointed at the output");
			ctx.Journal.Pid = 999999;
			ctx.Journal.Save();
			throw new CrashForTest();
		}));
		try {
			RunUntilCrash(f);
		} catch (CrashForTest) {
		}
		Equal("pointed at the output", File.ReadAllText(settings), "(the crash left it changed)");
		Equal(RunState.Restored, Runner.Restore(Journal.Interrupted(Runner.RunsDir(f.Data))!).State, "restored");
		Equal("his own", File.ReadAllText(settings), "Restore put the file back");
	}

	// what a failed step left in MO2's overwrite would win over the output put back: moved into the run's backup
	static void T_FailedStepsOverwriteFilesGoToTheBackup()
	{
		using var f = new FakeMo2();
		Directory.CreateDirectory(f.Overwrite);
		File.WriteAllText(Path.Combine(f.Overwrite, "was there.ini"), "old");
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			Directory.CreateDirectory(Path.Combine(f.Overwrite, "meshes"));
			File.WriteAllText(Path.Combine(f.Overwrite, "meshes", "half.nif"), "half");
			throw new StepFailedException("the tool crashed");
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Check(!File.Exists(Path.Combine(f.Overwrite, "meshes", "half.nif")), "the step's new file left overwrite");
		Check(File.Exists(Path.Combine(f.Overwrite, "was there.ini")), "what was there before stays");
		Check(Directory.EnumerateFiles(f.Backups, "half.nif", SearchOption.AllDirectories).Any(), "it is in the run's backup");
	}

	// a setup that spells a mod differently from the list ("output a" / "Output A") switches the same mod
	static void T_ModNamesIgnoreCase()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		var changed = p.SetMods([], ["output a"]);
		Check(changed.ContainsKey("output a") && !p.IsOn("Output A"), "unticked through the other spelling");
		Check(f.Text("modlist.txt").Contains("-Output A", StringComparison.Ordinal), "the list keeps its own spelling");
		p.EnsureMod("OUTPUT A");
		Equal(1, f.Text("modlist.txt").Split('\n').Count(l => l.TrimEnd().Equals("-Output A", StringComparison.OrdinalIgnoreCase)), "no second line for the same folder");
	}

	// MO2 is never closed under a program the player started from it (MO2 would end it)
	static void T_CloseWaitsForThePlayersOwnProgram()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var keep = Mo2Control.BusyWait;
		Mo2Control.BusyWait = TimeSpan.FromSeconds(1);
		try {
			var c = new Mo2Control(mo2, "Test", RunLog.Null) { RunningProbe = () => true, ForeignProbe = () => [(4242, "SSEEdit.exe")] };
			try {
				c.Close();
				Check(false, "MO2 was closed under the player's xEdit");
			} catch (Mo2BusyException e) {
				Check(e.Message.Contains("SSEEdit.exe", StringComparison.Ordinal), "the program is named: " + e.Message);
			}
		} finally {
			Mo2Control.BusyWait = keep;
		}
	}

	// a run record whose PID now belongs to another program (after a crash and a restart) is not "alive"
	static void T_ReusedPidIsNotTheRun()
	{
		var me = Environment.ProcessId;
		var j = new Journal { Pid = me, PidStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime() };
		Check(j.OwnerAlive, "the real owner is alive");
		j.PidStarted = j.PidStarted.GetValueOrDefault().AddMinutes(-5);
		Check(!j.OwnerAlive, "the same PID with another start time is not the run");
	}

	// one argument on a Windows command line, read back the way Windows splits it
	static void T_QuoteRoundTripsThroughWindows()
	{
		foreach (var a in new[] { @"D:\LOD\", @"C:\Program Files\x", "say \"hi\"", @"a\\""b", @"ends\\", "plain", "" }) {
			var line = "x.exe " + Mo2Control.Quote(a) + " next";
			var got = Argv(line);
			Equal(3, got.Length, "three arguments for " + a);
			Equal(a, got[1], "the argument comes back");
			Equal("next", got[2], "the next argument is not swallowed");
		}
	}

	[System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	static extern nint CommandLineToArgvW(string a_line, out int a_count);

	[System.Runtime.InteropServices.DllImport("kernel32.dll")]
	static extern nint LocalFree(nint a_mem);

	static string[] Argv(string a_line)
	{
		var p = CommandLineToArgvW(a_line, out var n);
		try {
			return Enumerable.Range(0, n).Select(i => System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(p, i * nint.Size))!).ToArray();
		} finally {
			LocalFree(p);
		}
	}

	// the shader cache is done when its log says remaining=0 - the last "remaining=" line is what counts
	static void T_ShaderLogRemainingIsRead()
	{
		using var f = new FakeMo2();
		var log = Path.Combine(f.Root, "BottledShaders.log");
		File.WriteAllText(log, "[2026-10-26 17:50:34.017] [info] [ShaderTiming] x remaining=2707\n[17:51:00.000] [info] [ShaderTiming] y remaining=0\n");
		Equal(0, InGame.LastRemaining(log), "the last line wins");
		File.WriteAllText(log, "[info] Shader compilation started\n");
		Equal(null, InGame.LastRemaining(log), "no ShaderTiming lines = unknown (only then may quiet end it)");
	}

	// an emptied setting is not set (its default applies); a relative folder is refused (it would follow the starting folder)
	static void T_EmptySettingsAndRelativePaths()
	{
		var o = new JsonObject { ["args"] = "", ["image"] = "  ", ["executable"] = "X" };
		Equal(null, Setup.Str(o, "args"), "an emptied setting counts as not set");
		Equal(null, new StepSettings(o).Str("image"), "spaces only, too");
		Equal("X", Setup.Str(o, "executable"), "a real value stays");
		using var f = new FakeMo2();
		f.WriteSetup(("pandora", new JsonObject { ["output_mod"] = "Output A", ["output_dir"] = @"scratch\Pandora" }));
		var s = Setup.Load(f.SetupFile);
		Check(s.Problems().Any(p => p.Contains("whole path", StringComparison.Ordinal)), "a relative output folder is a problem: " + string.Join("; ", s.Problems()));
	}

	// the profile's own save folder (sLocalSavePath in its Skyrim.ini when it keeps its own INIs)
	static void T_SavesFollowTheProfilesSavePath()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		File.WriteAllText(Path.Combine(p.Dir, "settings.ini"), "[General]\r\nLocalSettings=true\r\n");
		File.WriteAllText(Path.Combine(p.Dir, "Skyrim.ini"), "[General]\r\nsLocalSavePath=Saves\\MO2 Test\\\r\n");
		Check(InGame.SavesDir(p).EndsWith(@"Skyrim Special Edition\Saves\MO2 Test", StringComparison.OrdinalIgnoreCase), "the profile's own save path: " + InGame.SavesDir(p));
		File.WriteAllText(Path.Combine(p.Dir, "Skyrim.ini"), "[General]\r\n");
		Check(InGame.SavesDir(p).EndsWith(@"Skyrim Special Edition\Saves", StringComparison.OrdinalIgnoreCase), "none set: the game's own Saves");
	}

	// a global instance (%LOCALAPPDATA%\ModOrganizer\<name>) is named on MO2's command line; a portable one is not
	static void T_GlobalInstanceIsNamed()
	{
		using var f = new FakeMo2();
		Equal(null, Mo2Instance.Load(f.Exe, f.Instance).GlobalName, "portable: no -i");
	}

	// a tool's version on its page against the player's copy, written the many ways tools write them
	static void T_ToolVersionsCompare()
	{
		Check(!ToolUpdates.IsNewer("2.3.1.0", "2.3.1"), "2.3.1.0 = 2.3.1");
		Check(!ToolUpdates.IsNewer("5.0.0.0b-", "5.0.0-beta"), "5.0.0.0b- = 5.0.0-beta");
		Check(!ToolUpdates.IsNewer("3.0.0.217", "Alpha-217"), "DynDOLOD's file version 3.0.0.217 = its page's Alpha-217");
		Check(ToolUpdates.IsNewer("3.0.0.217", "Alpha-218"), "Alpha-218 is newer");
		Check(ToolUpdates.IsNewer("1.0.4.0", "1.0.6"), "1.0.6 is newer than 1.0.4");
		Check(!ToolUpdates.IsNewer("0.2.54.0", "0.2.11"), "an older number on the page is not newer");
		Check(!ToolUpdates.IsNewer("", "1.0"), "no version of ours: never called newer");
		Check(ToolUpdates.IsNewer("1.9.2", "2.0"), "2.0 (its zero trimmed) is newer than 1.9.2");
		Check(!ToolUpdates.IsNewer("2.1.0", "2.0"), "2.0 is not newer than 2.1.0");
		// the line under the rows counts every tool a row names (TexGen and DynDOLOD share one row)
		var both = new ToolUpdates.Result("texgen", "TexGen / DynDOLOD", "3.0", "", false, "page", "could not ask its page: x", 2);
		var one = new ToolUpdates.Result("pgpatcher", "PGPatcher", "1.0", "1.0", false, "page2", null);
		Check(ToolUpdates.Summary([both, one]).StartsWith("2 tool(s) could not be checked", StringComparison.Ordinal), "a shared row's two tools both counted: " + ToolUpdates.Summary([both, one]));
		Check(ToolUpdates.Summary([both with { Problem = null, Newer = true }, one]).StartsWith("2 tool(s) have a newer version", StringComparison.Ordinal), "both tools of a shared row newer");
		Equal("Every tool is up to date.", ToolUpdates.Summary([one]), "nothing newer, nothing unchecked");
	}

	// the settings a tool's own window wrote: found, kept, put in place for a run and put back after
	static void T_LearnedSettingsKeepAndApply()
	{
		using var f = new FakeMo2();
		var tool = Path.Combine(f.Root, "Tools", "SomeTool");
		Directory.CreateDirectory(Path.Combine(tool, "cfg"));
		Directory.CreateDirectory(Path.Combine(tool, "Logs"));
		var settings = Path.Combine(tool, "cfg", "settings.json");
		File.WriteAllText(settings, "{\"a\": 1}");
		File.WriteAllText(Path.Combine(tool, "untouched.ini"), "x=1");
		var before = ToolSettings.Scan([tool], []);
		Thread.Sleep(50);
		File.WriteAllText(settings, "{\"a\": 2, \"set\": \"by its window\"}");
		File.WriteAllText(Path.Combine(tool, "new.ini"), "y=2");
		File.WriteAllText(Path.Combine(tool, "Logs", "run.json"), "{}");
		File.WriteAllText(Path.Combine(tool, "notes.txt"), "not a settings file");
		var changed = ToolSettings.Changed(before, ToolSettings.Scan([tool], []));
		Equal(2, changed.Count, "the two settings files it wrote (not its log, not a text file, not one it left alone): " + string.Join(", ", changed));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A", ["executable"] = "SomeTool" }));
		var setup = Setup.Load(f.SetupFile);
		var l = ToolSettings.Keep(setup, "landscapefixer", "SomeTool", changed);
		Equal(2, ToolSettings.Load(setup, "landscapefixer")!.Files.Count, "kept with the setup");
		// the player changes the tool by hand since: a run still uses the learned settings, and puts the player's back after
		File.WriteAllText(settings, "{\"a\": 3}");
		var seen = "";
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			seen = File.ReadAllText(settings);
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
		}));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, r.Verdict);
		Check(seen.Contains("by its window", StringComparison.Ordinal), "the run used the learned settings: " + seen);
		Equal("{\"a\": 3}", File.ReadAllText(settings), "the player's own settings are back after the run");
		Equal(l.Files.Count, 2, "(learned set)");
	}

	sealed class CrashForTest : Exception;

	// a crash = the runner process dying mid-step: the exception escapes the step, and nothing after it runs
	static void RunUntilCrash(FakeMo2 a_f)
	{
		var s = Setup.Load(a_f.SetupFile);
		var mo2 = Mo2Instance.Load(s.Mo2Exe, s.Mo2Instance);
		var runs = Runner.RunsDir(a_f.Data);
		var j = new Journal { Id = "crash", Folder = Path.Combine(runs, "0000 crash"), SetupFile = Path.GetFullPath(a_f.SetupFile), SetupName = "Test", Profile = "Test",
			Mo2Exe = s.Mo2Exe, Mo2Instance = s.Mo2Instance, BackupDir = Path.Combine(a_f.Backups, "crash"), Pid = Environment.ProcessId, Started = DateTime.Now,
			State = RunState.Running, Steps = [new StepRecord { Key = "landscapefixer", State = StepState.Running }] };
		Directory.CreateDirectory(j.Folder);
		var profile = new Profile(mo2, "Test", () => false);
		j.SaveSnapshot(profile.Snapshot());
		// as Runner.Run: the player's changes watched from here, the expectation kept in the run folder
		profile.OursFile = Path.Combine(j.Folder, Profile.WatchFileName);
		profile.WatchForOthers();
		profile.SetMods([], ["Output A", "Output B", "Needs B"]);
		j.Save();
		using var log = new RunLog(j.Folder);
		var ctx = new RunContext { Setup = s, Mo2 = mo2, Profile = profile, Control = new Mo2Control(mo2, "Test", log), Log = log, Journal = j,
			Cancel = CancellationToken.None, BackupFolder = j.BackupDir };
		StepRegistry.For("landscapefixer")!.Run(ctx, new StepSettings(s.StepSettings("landscapefixer")!));
	}

	static void T_OneRunAtATime()
	{
		using var f = new FakeMo2();
		using var hold = new ManualResetEventSlim();
		using var started = new ManualResetEventSlim();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			started.Set();
			hold.Wait(10000);
		}));
		f.WriteSetup(("landscapefixer", []));
		var first = Task.Run(() => Runner.Run(new RunRequest(f.SetupFile, f.Data)));
		started.Wait(10000);
		var second = Task.Run(() => Runner.Run(new RunRequest(f.SetupFile, f.Data))).Result;
		hold.Set();
		first.Wait();
		Check(second.Verdict.Contains("another run is going", StringComparison.Ordinal), "the second run is refused: " + second.Verdict);
		Equal(RunState.Finished, first.Result.State, "the first finishes");
	}

	static void T_GuidedStepWaitsForContinue()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			var t = Task.Run(() => {
				Thread.Sleep(800);
				Commands.Send(ctx.Journal.Folder, "continue");
			});
			ctx.Guide.Ask("landscapefixer", "do the thing", ctx.Cancel);
			t.Wait();
		}));
		f.WriteSetup(("landscapefixer", []));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "the guided step went on after Done: " + r.Verdict);
	}

	// Wrye Bash's patch lands in overwrite (its output mod is off during the run): it and its log go into the mod, nothing stays
	static void T_WryeBashPatchLeavesOverwrite()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("wryebash", (ctx, s) => {
			var t0 = DateTime.UtcNow.AddSeconds(-1);
			FakeMo2.Plugin(Path.Combine(f.Overwrite, "Bashed Patch, 0.esp"), false);
			Directory.CreateDirectory(Path.Combine(f.Overwrite, "Docs"));
			File.WriteAllText(Path.Combine(f.Overwrite, "Docs", "Bashed Patch, 0.txt"), "log");
			File.WriteAllText(Path.Combine(f.Overwrite, "unrelated.ini"), "not Wrye Bash's");
			WryeBashStep.Collect(ctx, "wryebash", s.OutputMod!, "Bashed Patch, 0.esp", t0);
			ctx.SetAfter(s.OutputMod!);
		}));
		f.WriteSetup(("wryebash", new JsonObject { ["output_mod"] = "Output A" }));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "finished: " + r.Verdict);
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "Bashed Patch, 0.esp")), "the patch is in the output mod");
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "Docs", "Bashed Patch, 0.txt")), "its log went with it, at its own place");
		Check(!File.Exists(Path.Combine(f.Overwrite, "Bashed Patch, 0.esp")), "nothing of the patch left in overwrite");
		Check(File.Exists(Path.Combine(f.Overwrite, "unrelated.ini")), "another file in overwrite is not taken");
		using var packed = System.IO.Compression.ZipFile.OpenRead(Directory.GetFiles(f.Backups, RunUndo.OutputsZip, SearchOption.AllDirectories).Single());
		Check(packed.Entries.Any(e => e.Name == "OutA.esp"), "the old output is in the backup");
	}

	// the player pressed Done without building: the step fails and the old output comes back
	static void T_WryeBashWithoutAPatchFails()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("wryebash", (ctx, s) => WryeBashStep.Collect(ctx, "wryebash", s.OutputMod!, "Bashed Patch, 0.esp", DateTime.UtcNow)));
		f.WriteSetup(("wryebash", new JsonObject { ["output_mod"] = "Output A" }));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Check(r.State != RunState.Finished, "the run does not call it finished: " + r.State);
		Check(r.Verdict.Contains("no new Bashed Patch", StringComparison.Ordinal), "the reason is named: " + r.Verdict);
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "OutA.esp")), "the old output is still there");
		Check(ProfileOf(f).IsOn("Output A"), "and switched back on");
		Equal(1, StepCatalog.DefaultOrder.ToList().IndexOf("synthesis") - StepCatalog.DefaultOrder.ToList().IndexOf("wryebash"), "Wrye Bash sits right before Synthesis");
	}

	// a command whose program path is quoted (a path with spaces) runs as written - cmd's own quoting, not Windows' escaping
	// (2026-10-27: the trial's after-run command failed with '\"C:\...python.exe\"' is not recognized)
	static void T_CommandWithAQuotedPathRuns()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => { }));
		var s = f.WriteSetup(("landscapefixer", []));
		var flag = Path.Combine(f.Root, "after ran.txt");
		var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
		s.Root["beforeRun"] = new JsonArray($"\"{cmd}\" /c exit 0");
		s.Root["afterRun"] = new JsonArray($"\"{cmd}\" /c echo ok> \"{flag}\"");
		s.Save();
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, r.Verdict);
		Check(File.Exists(flag), "the after-run command with quoted paths ran");
	}

	// a tool driven through its window whose window never changes is idle, whatever CPU its window uses drawing itself - the
	// step fails naming what the window shows (2026-10-27: Synthesis sat on its main screen 4 min, its window using CPU)
	static void T_AWindowThatNeverChangesIsIdle()
	{
		using var f = new FakeMo2();
		Waits.IdleForTests = TimeSpan.FromSeconds(8);
		try {
			StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
				using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
				try {
					Waits.Tool(ctx, "landscapefixer", p, TimeSpan.FromMinutes(2), () => Waits.Poll.Wait, a_progress: () => "SYNTHESIS  Run");
				} finally {
					p.Kill();
				}
			}));
			f.WriteSetup(("landscapefixer", []));
			var sw = Stopwatch.StartNew();
			var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
			Equal(RunState.Failed, r.State, r.Verdict);
			Check(r.Verdict.Contains("sat idle", StringComparison.Ordinal) && r.Verdict.Contains("SYNTHESIS  Run", StringComparison.Ordinal), "named, with the window's text: " + r.Verdict);
			Check(sw.Elapsed < TimeSpan.FromSeconds(40), "within the idle limit, not the deadline: " + sw.Elapsed);
		} finally {
			Waits.IdleForTests = null;
		}
	}

	// a Synthesis patcher that failed is named off Synthesis's own log once its run ends (2026-10-27: a patcher
	// could not write a compressed record, no Synthesis.esp, and the step only said "sat idle")
	static void T_SynthesisPatcherFailureIsNamed()
	{
		const string head = """
			2026-10-27 16:04:36.640 -04:00 [INF] [T16] ================= Starting Patcher Example Patcher Run =================
			2026-10-27 16:04:44.387 -04:00 [INF] [T34] Writing to output: C:\Temp\Synthesis.esp
			2026-10-27 16:04:45.899 -04:00 [ERR] [T35] System.NotImplementedException: Writing with compression enabled is not currently supported.
			2026-10-27 16:04:45.907 -04:00 [ERR] [T35] Error while running:
			""";
		Equal<string?>(null, SynthesisStep.Failure(head), "still running: not judged yet");
		var why = SynthesisStep.Failure(head + "2026-10-27 16:04:45.911 -04:00 [INF] [T2] Finished patcher run\n");
		Check(why != null && why.Contains("\"Example Patcher\"", StringComparison.Ordinal) && why.Contains("compression", StringComparison.Ordinal), "patcher and reason named: " + why);
		Equal<string?>(null, SynthesisStep.Failure("[INF] [T2] Starting Patcher A Run\n[INF] [T2] Finished patcher run\n"), "a clean run is no failure");
	}

	// the trial of 2026-10-27 20:26: a window being destroyed answers UI Automation with a raw COM error - a "gone" element, never
	// a failed step
	static void T_AWindowGoingAwayIsNotAFailure()
	{
		Check(Uia.Gone(new System.Runtime.InteropServices.COMException("Catastrophic failure", unchecked((int)0x8000FFFF))), "E_UNEXPECTED = gone");
		Check(Uia.Gone(new System.Windows.Automation.ElementNotAvailableException()), "not available = gone");
		Check(!Uia.Gone(new System.Runtime.InteropServices.COMException("Unspecified error", unchecked((int)0x80004005))), "E_FAIL is a real fault");
		Check(!Uia.Gone(new InvalidOperationException()), "anything else is a real fault");
	}

	// the trial of 2026-10-27 21:36: xLODGen wrote where MO2's entry for it said (-o:), not into the step's output folder
	static void T_XLodGenWritesWhereTheStepSays()
	{
		var mo2Args = Mo2Instance.Unescape(@"-o:\""D:\\Modding\\Old Scratch\\Terrain LOD\"" -sse");
		Equal(@"-o:""D:\Modding\Old Scratch\Terrain LOD"" -sse", mo2Args, "MO2's ini escaping undone");
		Equal(@"-o:""C:\New\Terrain LOD"" -sse", XLodGenStep.WithOutput(mo2Args, @"C:\New\Terrain LOD\"), "an -o: there is replaced");
		Equal(@"-sse -o:""C:\New""", XLodGenStep.WithOutput("-sse -o:C:\\Old", @"C:\New"), "an unquoted one too");
		Equal(@"-o:""C:\New"" -sse", XLodGenStep.WithOutput("-sse", @"C:\New"), "none there: put first");
		Equal(@"-o:""C:\New""", XLodGenStep.WithOutput("", @"C:\New"), "no arguments at all");
		Equal(@"-sse -d:""C:\Game Copy\Data""", XLodGenStep.WithData("-sse", @"C:\Game Copy"), "the game Mod Organizer runs named");
		Equal(@"-sse -d:""E:\Mine""", XLodGenStep.WithData(@"-sse -d:""E:\Mine""", @"C:\Game Copy"), "a data folder the player set stays");
	}

	// the trial of 2026-10-31: xLODGen had moved its output into its own mod, so ACMOS read an empty scratch folder
	// ("Invalid Path to LOD") - ACMOS paints the terrain LOD wherever it is
	static void T_AcmosFindsTheTerrainLodWhereItIs()
	{
		var dir = Path.Combine(Path.GetTempPath(), "SUP acmos " + Guid.NewGuid().ToString("N"));
		var scratch = Path.Combine(dir, "Terrain LOD");
		var mod = Path.Combine(dir, "mods", "SSELODGen_Output - X");
		try {
			Equal(null, AcmosStep.LodFolder(scratch, mod), "neither holds terrain LOD");
			Directory.CreateDirectory(Path.Combine(mod, "textures", "terrain", "tamriel"));
			File.WriteAllText(Path.Combine(mod, "textures", "terrain", "tamriel", "a.dds"), "x");
			Directory.CreateDirectory(scratch);
			Equal(mod, AcmosStep.LodFolder(scratch + "\\", mod + "\\"), "an empty scratch: the mod xLODGen moved it into");
			Directory.CreateDirectory(Path.Combine(scratch, "textures", "terrain"));
			File.WriteAllText(Path.Combine(scratch, "textures", "terrain", "readme.txt"), "x");
			Equal(mod, AcmosStep.LodFolder(scratch, mod), "a stray file in the scratch is no terrain LOD");
			File.WriteAllText(Path.Combine(scratch, "textures", "terrain", "b.dds"), "x");
			Equal(scratch, AcmosStep.LodFolder(scratch, mod), "the scratch folder first when it holds the LOD");
		} finally {
			Directory.Delete(dir, true);
		}
	}

	// ACMOS itself, with this test program standing in for it (Main's -autorun): the LOD already in the mod is painted THERE
	// (never moved into itself), a move that fails half way puts the plain textures back, and a "reads" in MO2's folders is refused
	static void T_AcmosPaintsTheLodInTheModItIsIn()
	{
		using var f = new FakeMo2();
		var keepDesktop = HiddenDesktop.OnPlayersDesktop;
		HiddenDesktop.OnPlayersDesktop = _ => [];
		var logs = Path.Combine(AppContext.BaseDirectory, "logs");
		var hadLogs = Directory.Exists(logs);
		try {
			var scratch = Path.Combine(f.Root, "Terrain LOD");
			Directory.CreateDirectory(scratch);
			f.Mod("Terrain LOD Mod");
			var tex = Path.Combine(f.Mods, "Terrain LOD Mod", "textures", "terrain", "tamriel");
			Directory.CreateDirectory(tex);
			foreach (var n in new[] { "a.dds", "c.dds" }) {
				File.WriteAllText(Path.Combine(tex, n), "plain");
			}
			// this test program itself (its apphost beside it, however the tests are started)
			var standIn = Path.Combine(AppContext.BaseDirectory, Path.GetFileNameWithoutExtension(typeof(Program).Assembly.Location) + ".exe");
			var settings = new JsonObject { ["exe"] = standIn, ["output_mod"] = "Terrain LOD Mod", ["reads"] = scratch, ["min_files"] = 1, ["roads"] = "Paths Only" };
			f.WriteSetup(("acmos", settings));
			var ctx = StepContext(f, "acmos");
			new AcmosStep().Run(ctx, new StepSettings(settings));
			Equal("painted", File.ReadAllText(Path.Combine(tex, "a.dds")), "painted in the mod");
			Equal(0, Directory.GetFileSystemEntries(scratch).Length, "the empty scratch was never moved into the mod");
			var told = Directory.GetDirectories(logs).Order(StringComparer.Ordinal).Select(d => Path.Combine(d, "log.log")).Where(File.Exists).Select(File.ReadAllText).Last();
			Check(told.Contains(Path.Combine(f.Mods, "Terrain LOD Mod"), StringComparison.OrdinalIgnoreCase), "ACMOS was given the mod's LOD: " + told);
			Check(ctx.Journal.Kept.All(k => k.Done), "the plain textures are never put back over the painted ones");
			Check(ctx.PendingEnable.Contains("Terrain LOD Mod"), "the mod goes on after the step");
			ctx.Log.Dispose();

			// the move fails half way (c.dds held open): the plain textures come back where they could
			foreach (var n in new[] { "a.dds", "c.dds" }) {
				File.WriteAllText(Path.Combine(tex, n), "plain");
			}
			var ctx2 = StepContext(f, "acmos");
			using (new FileStream(Path.Combine(tex, "c.dds"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
				Throws<StepFailedException>(() => new AcmosStep().Run(ctx2, new StepSettings(settings)), "a failed move fails the step");
			}
			Equal("plain", File.ReadAllText(Path.Combine(tex, "a.dds")), "a texture already replaced is put back");
			Equal("plain", File.ReadAllText(Path.Combine(tex, "c.dds")), "the one that could not be replaced stays plain");
			ctx2.Log.Dispose();

			// "reads" inside MO2's mods folder: refused before a run
			var bad = new JsonObject { ["exe"] = standIn, ["output_mod"] = "Terrain LOD Mod", ["reads"] = Path.Combine(f.Mods, "Terrain LOD Mod") };
			var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
			var c = new CheckContext { Setup = Setup.Load(f.SetupFile), Mo2 = mo2, Profile = ProfileOf(f) };
			Check(new AcmosStep().Check(c, new StepSettings(bad)).Any(i => i.Blocking && i.Text.Contains("mods folder", StringComparison.Ordinal)), "the mod as its own source is refused");
		} finally {
			HiddenDesktop.OnPlayersDesktop = keepDesktop;
			if (!hadLogs && Directory.Exists(logs)) {
				Directory.Delete(logs, true);
			}
		}
	}

	// a step run on its own, as a run would (its record, log, backup folder and MO2 control)
	static RunContext StepContext(FakeMo2 a_f, string a_step, DevBench? a_devBench = null, CancellationToken a_cancel = default)
	{
		var s = Setup.Load(a_f.SetupFile);
		var mo2 = Mo2Instance.Load(s.Mo2Exe, s.Mo2Instance);
		var id = Guid.NewGuid().ToString("N")[..8];
		var j = new Journal { Id = id, Folder = Path.Combine(Runner.RunsDir(a_f.Data), id), SetupFile = Path.GetFullPath(a_f.SetupFile), SetupName = "Test", Profile = "Test",
			Mo2Exe = s.Mo2Exe, Mo2Instance = s.Mo2Instance, BackupDir = Path.Combine(a_f.Backups, id), Started = DateTime.Now, State = RunState.Running,
			Steps = [new StepRecord { Key = a_step, State = StepState.Running }] };
		Directory.CreateDirectory(j.Folder);
		j.Save();
		var log = new RunLog(j.Folder);
		return new RunContext { Setup = s, Mo2 = mo2, Profile = new Profile(mo2, "Test", () => false), Control = new Mo2Control(mo2, "Test", log), Log = log,
			Journal = j, Cancel = a_cancel, BackupFolder = j.BackupDir, DevBench = a_devBench ?? new() };
	}

	// the stand-in for ACMOS Road Generator: every terrain texture of -l: painted into -o:, and the log ACMOS writes beside itself
	// (naming the folder it was given)
	static int FakeAcmos(string[] a_args)
	{
		string Arg(string a_p) => a_args.First(a => a.StartsWith(a_p, StringComparison.Ordinal))[a_p.Length..];
		var lod = Arg("-l:");
		var outDir = Arg("-o:");
		foreach (var t in Directory.EnumerateFiles(Path.Combine(lod, "textures", "terrain"), "*.dds", SearchOption.AllDirectories)) {
			var dst = Path.Combine(outDir, Path.GetRelativePath(lod, t));
			Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
			File.WriteAllText(dst, "painted");
		}
		var log = Path.Combine(AppContext.BaseDirectory, "logs", DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss-fffffff", System.Globalization.CultureInfo.InvariantCulture));
		Directory.CreateDirectory(log);
		File.WriteAllText(Path.Combine(log, "log.log"), $"read {lod}\nAll Done!\n");
		return 0;
	}

	// both trials of 2026-10-27 ticked xLODGen's LOD levels, the first list in its options, instead of its worldspaces
	static void T_TheWorldspaceListIsTheOneWithNames()
	{
		IReadOnlyList<string> levels = ["", "4", "8", "16", "32"];
		IReadOnlyList<string> worlds = ["Tamriel [WRLD:0000003C]", "DLC2SolstheimWorld [WRLD:04000800]", "MarkarthWorld [WRLD:00016BB4]"];
		Equal<int?>(1, XLodGenStep.PickWorldspaces([levels, worlds]), "the named rows, not the LOD levels before them");
		Equal<int?>(0, XLodGenStep.PickWorldspaces([worlds, levels]), "whichever comes first");
		Equal<int?>(null, XLodGenStep.PickWorldspaces([levels]), "numbers only: no worldspace list yet");
		Equal<int?>(null, XLodGenStep.PickWorldspaces([]), "no list at all");
	}

	static void T_SynthesisLoadingErrorIsNotAFailure()
	{
		var t = new DateTime(2026, 10, 27, 23, 29, 20, DateTimeKind.Utc);
		DateTime? since = null;
		Check(!SynthesisStep.StillBlocked(ref since, true, t), "a blocking error just seen is not judged yet");
		Check(!SynthesisStep.StillBlocked(ref since, false, t.AddSeconds(5)), "gone 5 s later (its settings loaded)");
		Check(!SynthesisStep.StillBlocked(ref since, true, t.AddSeconds(40)), "seen again: its clock starts over");
		Check(!SynthesisStep.StillBlocked(ref since, true, t.AddSeconds(69)), "29 s later still not judged");
		Check(SynthesisStep.StillBlocked(ref since, true, t.AddSeconds(70)), "30 s without a break: the step fails");
	}

	// a run that finished stays finished when an after-run command fails: said in its notes, the profile put back once, the
	// command run once
	static void T_AfterRunCommandFailingKeepsTheRunFinished()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		var s = f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		var marker = Path.Combine(f.Root, "after ran.txt");
		s.Root["afterRun"] = new JsonArray($"echo ran>>\"{marker}\" & exit /b 5", $"echo next>>\"{marker}\"");
		s.Save();
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "finished: " + r.Verdict);
		var j = Journal.All(Runner.RunsDir(f.Data)).First();
		Check(j.Notes.Any(n => n.Contains("exit code 5", StringComparison.Ordinal)), "the failed command is in the run's notes: " + string.Join("; ", j.Notes));
		Equal("ran|next", string.Join("|", File.ReadAllLines(marker).Select(l => l.Trim())), "each command ran once, and the one after the failed one too");
		Check(ProfileOf(f).IsOn("Output A"), "the built output on");
	}

	static void T_BeforeRunCommandMustSucceed()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => throw new InvalidOperationException("must not run")));
		var s = f.WriteSetup(("landscapefixer", []));
		s.Root["beforeRun"] = new JsonArray("exit /b 3");
		s.Save();
		var before = f.Text("modlist.txt");
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Check(r.State == RunState.Failed && r.Verdict.Contains("exit code 3", StringComparison.Ordinal), "refused when the before-run command fails: " + r.Verdict);
		Equal(before, f.Text("modlist.txt"), "nothing changed");
	}

	static void T_LowDiskRefused()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => throw new InvalidOperationException("must not run")));
		var s = f.WriteSetup(("landscapefixer", []));
		s.Root["minFreeGb"] = 1_000_000;
		s.Save();
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Check(r.Verdict.StartsWith("not started", StringComparison.Ordinal) && r.Verdict.Contains("GB free", StringComparison.Ordinal), r.Verdict);
	}

	static void T_MissingProfileRefused()
	{
		using var f = new FakeMo2();
		var s = f.WriteSetup(("landscapefixer", []));
		((JsonObject)s.Root["mo2"]!)["profile"] = "Nope";
		s.Save();
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Check(r.Verdict.Contains("no profile", StringComparison.Ordinal), r.Verdict);
	}

	static void T_CloseWaitsForADownloadToEnd()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		Directory.CreateDirectory(mo2.DownloadsDir);
		var dl = Path.Combine(mo2.DownloadsDir, "Some Mod.7z.unfinished");
		File.WriteAllText(dl, "");
		var open = true;
		var c = new Mo2Control(mo2, "Test", RunLog.Null) { RunningProbe = () => open };
		var t = Task.Run(() => {
			Thread.Sleep(1500);
			File.Delete(dl);
			open = false;
		});
		var sw = Stopwatch.StartNew();
		c.Close();
		t.Wait();
		Check(sw.Elapsed >= TimeSpan.FromSeconds(1.4), "it waited for the download instead of closing MO2 under it");
	}

	static void T_CloseGivesUpOnALongDownload()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		Directory.CreateDirectory(mo2.DownloadsDir);
		File.WriteAllText(Path.Combine(mo2.DownloadsDir, "Big.7z.unfinished"), "");
		var keep = Mo2Control.BusyWait;
		Mo2Control.BusyWait = TimeSpan.FromSeconds(1);
		try {
			var c = new Mo2Control(mo2, "Test", RunLog.Null) { RunningProbe = () => true };
			Throws<Mo2BusyException>(() => c.Close(), "a download that never ends");
		} finally {
			Mo2Control.BusyWait = keep;
		}
	}

	// MO2's readiness, timed short; the statics go back after each test
	static void FastReady(Action a_body)
	{
		var (q, l, p) = (Mo2Control.ReadyQuiet, Mo2Control.ReadyLimit, Mo2Control.Poll);
		Mo2Control.ReadyQuiet = TimeSpan.FromSeconds(1);
		Mo2Control.ReadyLimit = TimeSpan.FromSeconds(8);
		Mo2Control.Poll = TimeSpan.FromMilliseconds(100);
		try {
			a_body();
		} finally {
			(Mo2Control.ReadyQuiet, Mo2Control.ReadyLimit, Mo2Control.Poll) = (q, l, p);
		}
	}

	static void T_ReadyWaitsWhileMo2StillLoads() => FastReady(() => {
		using var f = new FakeMo2();
		var sw = Stopwatch.StartNew();
		// a full core busy for 2 s (its plugins loading), then quiet
		var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
			RunningProbe = () => true,
			ReadyProbe = () => new(true, true, sw.Elapsed < TimeSpan.FromSeconds(2) ? sw.Elapsed : TimeSpan.FromSeconds(2), null),
		};
		c.WaitReady(default);
		Check(sw.Elapsed >= TimeSpan.FromSeconds(2.9), $"ready after {sw.Elapsed.TotalSeconds:0.0} s - before MO2 finished loading");
		Check(sw.Elapsed < TimeSpan.FromSeconds(6), $"ready only after {sw.Elapsed.TotalSeconds:0.0} s - a quiet MO2 was not taken as ready");
	});

	static void T_ReadyNowWhenMo2IsAlreadyQuiet() => FastReady(() => {
		using var f = new FakeMo2();
		var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) { ReadyProbe = () => new(true, true, TimeSpan.FromSeconds(9), null) };
		var sw = Stopwatch.StartNew();
		c.WaitReady(default);
		Check(sw.Elapsed < TimeSpan.FromSeconds(2), $"a quiet MO2 waited {sw.Elapsed.TotalSeconds:0.0} s");
	});

	static void T_ReadyGivesUpNamingWhy() => FastReady(() => {
		using var f = new FakeMo2();
		Mo2Control.ReadyLimit = TimeSpan.FromSeconds(1);
		var hung = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) { ReadyProbe = () => new(true, false, TimeSpan.Zero, null) };
		try {
			hung.WaitReady(default);
			Check(false, "a hung MO2 was taken as ready");
		} catch (StepFailedException e) {
			Check(e.Message.Contains("does not answer", StringComparison.Ordinal), e.Message);
		}
		var boxed = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) { ReadyProbe = () => new(true, true, TimeSpan.Zero, "Some plugins failed to load") };
		try {
			boxed.WaitReady(default);
			Check(false, "an MO2 with a box open was taken as ready");
		} catch (StepFailedException e) {
			Check(e.Message.Contains("plugins failed", StringComparison.Ordinal), e.Message);
		}
	});

	static void T_LostRequestIsAnsweredAndAskedAgain() => FastReady(() => {
		using var f = new FakeMo2();
		int launches = 0, pressed = 0;
		string? box = null;
		var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
			ReadyProbe = () => new(true, true, TimeSpan.Zero, box),
			PressBoxProbe = _ => { ++pressed; box = null; },
			// the first request reaches an MO2 still loading: it shows its box and no tool starts; the second one works
			Launch = (_, _) => { if (++launches == 1) { box = "failed to receive data from secondary process: Unknown error"; } return 1; },
		};
		var pid = c.LaunchTool("run -e Tool", () => launches >= 2 ? 4242 : 0, TimeSpan.FromSeconds(3), default);
		Equal<int?>(4242, pid, "the tool's process");
		Equal(2, launches, "requests sent");
		Equal(1, pressed, "boxes answered");
	});

	static void T_ToolThatNeverStartsIsReported() => FastReady(() => {
		using var f = new FakeMo2();
		var launches = 0;
		var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
			ReadyProbe = () => new(true, true, TimeSpan.Zero, null),
			Launch = (_, _) => ++launches,
		};
		var pid = c.LaunchTool("run -e Tool", () => 0, TimeSpan.FromSeconds(1), default);
		Equal<int?>(null, pid, "no process");
		Equal(1, launches, "a request with no box is not sent again");
	});

	// two MO2 starts in the same second: the loser's "already running" box is answered OK, and MO2 is then ready
	// only within a minute of a start this run sent; a box of a copy the player started is left alone (and named on time-out)
	static void T_AlreadyRunningBoxIsAnswered() => FastReady(() => {
		using var f = new FakeMo2();
		var pressed = 0;
		string? box = "An instance of Mod Organizer is already running";
		var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
			RunningProbe = () => false,
			LeftoverProbe = () => [],
			Launch = (_, _) => 1,
			ReadyProbe = () => new(true, true, TimeSpan.Zero, box),
			PressBoxProbe = _ => { ++pressed; box = null; },
		};
		c.Start(default);
		Equal(1, pressed, "boxes answered after our own start");
		Mo2Control.ReadyLimit = TimeSpan.FromSeconds(1);
		var notOurs = 0;
		var theirs = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
			ReadyProbe = () => new(true, true, TimeSpan.Zero, "An instance of Mod Organizer is already running"),
			PressBoxProbe = _ => ++notOurs,
		};
		Throws<StepFailedException>(() => theirs.WaitReady(default), "a box we did not cause keeps MO2 not ready");
		Equal(0, notOurs, "a box of a copy the player started was pressed");
	});

	// a box that stays open after OK cannot hold the ready wait past its limit
	static void T_ABoxThatStaysOpenEndsTheWait() => FastReady(() => {
		using var f = new FakeMo2();
		Mo2Control.ReadyLimit = TimeSpan.FromSeconds(1);
		var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
			ReadyProbe = () => new(true, true, TimeSpan.Zero, "failed to receive data from secondary process"),
			PressBoxProbe = _ => { },
		};
		var sw = Stopwatch.StartNew();
		Throws<StepFailedException>(() => c.WaitReady(default), "an endless box");
		Check(sw.Elapsed < TimeSpan.FromSeconds(4), $"the wait ran {sw.Elapsed.TotalSeconds:0.0} s past a 1 s limit");
	});

	// Start itself waits for the leftover before it launches MO2
	static void T_StartWaitsForTheLeftoverFirst() => FastReady(() => {
		using var f = new FakeMo2();
		var keep = Mo2Control.LeftoverWait;
		Mo2Control.LeftoverWait = TimeSpan.FromSeconds(3);
		try {
			int probes = 0, probesAtLaunch = -1;
			var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
				RunningProbe = () => false,
				LeftoverProbe = () => ++probes < 3 ? [(99003, "TheWatcherDump.exe")] : [],
				Launch = (_, _) => { probesAtLaunch = probes; return 1; },
				ReadyProbe = () => new(true, true, TimeSpan.Zero, null),
			};
			c.Start(default);
			Equal(3, probesAtLaunch, "probe calls when MO2 was launched (only after the leftover was gone)");
		} finally {
			Mo2Control.LeftoverWait = keep;
		}
	});

	// a finished download MO2 left as "<file>.unfinished" beside the same bytes is no download in progress; a real one still is
	static void T_StaleUnfinishedDownloadIsNotInProgress()
	{
		var d = Path.Combine(Path.GetTempPath(), "mo2 tool runner tests", "dl " + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(d);
		try {
			File.WriteAllText(Path.Combine(d, "Done.zip"), "same bytes");
			File.WriteAllText(Path.Combine(d, "Done.zip.unfinished"), "same bytes");
			File.WriteAllText(Path.Combine(d, "Going.zip.unfinished"), "part");
			File.WriteAllText(Path.Combine(d, "Diff.zip"), "the whole file");
			File.WriteAllText(Path.Combine(d, "Diff.zip.unfinished"), "the whole fil?");
			var (active, stale) = Mo2Control.Downloads(d);
			Equal("Diff.zip.unfinished, Going.zip.unfinished", string.Join(", ", active.Order()), "still downloading");
			Equal("Done.zip.unfinished", string.Join(", ", stale), "finished, left behind");
		} finally {
			Directory.Delete(d, true);
		}
	}

	// a program still running from MO2's folders (a dead game's crash dumper) holds MO2's start: waited for, then named
	static void T_LeftoverFromMo2FoldersHoldsTheStart()
	{
		using var f = new FakeMo2();
		var keep = Mo2Control.LeftoverWait;
		Mo2Control.LeftoverWait = TimeSpan.FromMilliseconds(400);
		try {
			var calls = 0;
			var ends = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
				LeftoverProbe = () => ++calls < 3 ? [(99001, "TheWatcherDump.exe")] : [],
			};
			ends.WaitForLeftovers(default);
			Check(calls >= 3, "the start went on while the leftover still ran");
			var stays = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
				LeftoverProbe = () => [(99002, "SkyrimDiagHelper.exe")],
			};
			try {
				stays.WaitForLeftovers(default);
				Check(false, "MO2 would start beside a leftover program");
			} catch (StepFailedException e) {
				Check(e.Message.Contains("SkyrimDiagHelper.exe (process 99002)", StringComparison.Ordinal), e.Message);
			}
		} finally {
			Mo2Control.LeftoverWait = keep;
		}
	}

	// a Steam copy of the game: Steam started first when it is not running; a GOG copy, or Steam already up, starts nothing
	static void T_SteamStartedBeforeTheGame() => FastReady(() => {
		using var f = new FakeMo2();
		var keep = Mo2Control.SteamSettle;
		Mo2Control.SteamSettle = TimeSpan.Zero;
		try {
			const string steamGame = @"D:\SteamLibrary\steamapps\common\Skyrim Special Edition";
			Check(Mo2Control.IsSteamGame(steamGame), "a Steam library path");
			Check(!Mo2Control.IsSteamGame(@"C:\GOG Games\Skyrim Anniversary Edition"), "a GOG path");
			Check(!Mo2Control.IsSteamGame(f.Game), "a copied game folder with no Steam library");
			File.WriteAllText(Path.Combine(f.Game, "steam_api64.dll"), "");
			Check(Mo2Control.IsSteamGame(f.Game), "a Steam copy moved out of steamapps (a modlist's Stock Game)");
			var up = false;
			var cmds = new List<string>();
			var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
				SteamRunningProbe = () => up,
				SteamExeProbe = () => @"C:\Program Files (x86)\Steam\steam.exe",
				Launch = (cmd, _) => { cmds.Add(cmd); up = true; return 1; },
			};
			c.EnsureSteam(@"C:\GOG Games\Skyrim Anniversary Edition", default);
			Equal(0, cmds.Count, "nothing started for a GOG game");
			c.EnsureSteam(steamGame, default);
			Equal(1, cmds.Count, "Steam started once");
			Check(cmds[0].Contains("steam.exe\" -silent", StringComparison.OrdinalIgnoreCase), cmds[0]);
			c.EnsureSteam(steamGame, default);
			Equal(1, cmds.Count, "Steam already up - not started again");
			var none = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) { SteamRunningProbe = () => false, SteamExeProbe = () => null };
			Throws<StepFailedException>(() => none.EnsureSteam(steamGame, default), "no Steam to start");
		} finally {
			Mo2Control.SteamSettle = keep;
		}
	});

	// LaunchGame starts Steam BEFORE it asks MO2 for the game
	static void T_LaunchGameStartsSteamFirst() => FastReady(() => {
		using var f = new FakeMo2();
		var keep = Mo2Control.SteamSettle;
		Mo2Control.SteamSettle = TimeSpan.Zero;
		try {
			File.WriteAllText(Path.Combine(f.Game, "steam_api64.dll"), "");
			bool steam = false, mo2 = false;
			var cmds = new List<string>();
			var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
				RunningProbe = () => mo2,
				LeftoverProbe = () => [],
				SteamRunningProbe = () => steam,
				SteamExeProbe = () => @"C:\Program Files (x86)\Steam\steam.exe",
				ReadyProbe = () => new(true, true, TimeSpan.Zero, null),
				Launch = (cmd, _) => {
					cmds.Add(cmd);
					steam |= cmd.Contains("steam.exe", StringComparison.OrdinalIgnoreCase);
					mo2 |= cmd.Contains("ModOrganizer.exe", StringComparison.OrdinalIgnoreCase);
					return 1;
				},
			};
			var pid = c.LaunchGame("SKSE", () => cmds.Any(x => x.Contains("run -e", StringComparison.Ordinal)) ? 4243 : 0, default);
			Equal(4243, pid, "the game's process");
			var s = cmds.FindIndex(x => x.Contains("steam.exe\" -silent", StringComparison.OrdinalIgnoreCase));
			var g = cmds.FindIndex(x => x.Contains("run -e", StringComparison.Ordinal));
			Check(s >= 0 && g > s, $"Steam at {s}, the game start at {g}: " + string.Join(" | ", cmds));
			Check(cmds.All(x => x.Contains("steam.exe", StringComparison.OrdinalIgnoreCase) || x.Contains(f.Exe, StringComparison.OrdinalIgnoreCase)),
				"a start outside the fake MO2: " + string.Join(" | ", cmds));
		} finally {
			Mo2Control.SteamSettle = keep;
		}
	});

	// the start wait answers each box through its own press: the plugin error's No, a notice's button; an unknown one stops it
	static void T_NativeBoxesArePressedByTheirVerdict()
	{
		var pressed = new List<string>();
		InGame.AnswerNativeBoxes([
			new InGame.NativeBox("Plugin error", "SKSE plugin X.dll failed to load. Continue running?", ["Yes", "No"], l => pressed.Add("1:" + l)),
			new InGame.NativeBox("Notice", "Settings reset.", ["OK"], l => pressed.Add("2:" + l)),
		], RunLog.Null);
		Equal("1:No, 2:OK", string.Join(", ", pressed), "buttons pressed");
		pressed.Clear();
		Throws<StepFailedException>(() => InGame.AnswerNativeBoxes([
			new InGame.NativeBox("Question", "Overwrite your settings?", ["Yes", "No"], l => pressed.Add("3:" + l))], RunLog.Null), "an unknown box");
		Equal(0, pressed.Count, "an unknown box was pressed");
	}

	// a Windows box of the game before DevBench answers: SKSE's plugin error -> No, a notice -> its button, anything else stops
	static void T_GameWindowsBoxesBeforeDevBench()
	{
		var (press, note) = InGame.NativeBoxVerdict("Plugin error",
			"SKSE plugin ENBWorldspaceWeatherlists.dll failed to load. Continue running the game?", ["&Yes", "&No"]);
		Equal("No", press, "SKSE's plugin error");
		Check(note.Contains("ENBWorldspaceWeatherlists.dll", StringComparison.Ordinal), note);
		Equal("OK", InGame.NativeBoxVerdict("Notice", "Your settings were reset.", ["OK"]).Press, "a one-button notice");
		Equal<string?>(null, InGame.NativeBoxVerdict("Question", "Overwrite your settings?", ["Yes", "No"]).Press, "an unknown question");
	}

	// a mod's box that promises a second one (perks being reset) keeps the settle going; an ordinary notice does not
	static void T_BoxThatPromisesAnotherIsWaitedFor()
	{
		Check(InGame.PromisesBox("Ordinator: perk reset in progress. A message box will appear when the process is complete."), "the promise");
		Check(!InGame.PromisesBox("Sacrosanct: scripts modified by another mod were found."), "a plain notice");
		Check(!InGame.PromisesBox("Your new settings will appear after you reload."), "'will appear' about something else");
		var t0 = new DateTime(2026, 11, 2, 20, 0, 0, DateTimeKind.Utc);
		var plain = t0.AddSeconds(60);
		Equal(t0.AddMinutes(10), InGame.SettleUntil(plain, t0, "A message box will appear when the process is complete."), "the promise holds 10 minutes");
		Equal(t0.AddSeconds(80), InGame.SettleUntil(plain, t0.AddSeconds(70), "Perks reset."), "the promised box ends the hold (10 s more)");
		Equal(plain, InGame.SettleUntil(plain, t0.AddSeconds(5), "A notice."), "an early notice keeps the plain minute");
	}

	// Bottled Shaders' "[DDC]" line ends the compile watch on a warm start - unless a compile ("remaining=N" > 0) follows it
	static void T_BootPassOverEndsTheCompileWatch()
	{
		Check(InGame.BootPassOver(["[2026-10-28 19:33:10.000] [info] [ShaderTiming] x remaining=0", "[2026-10-28 19:33:54.000] [info] [DDC] 6651 entries, 0 trimmed"]),
			"warm start: the trim line stands");
		Check(!InGame.BootPassOver(["[19:33:54.000] [info] [DDC] 6651 entries", "[19:34:10.000] [info] [ShaderTiming] Very slow y remaining=41"]),
			"a compile after the trim line");
		Check(!InGame.BootPassOver(["[19:33:10.000] [info] [ShaderTiming] x remaining=12"]), "no trim line yet");
		Check(!InGame.BootPassOver(["Open Shaders 2.19.0 loaded"]), "a shader mod that writes no trim line");
		var start = new DateTime(2026, 11, 2, 19, 30, 0);
		Check(!InGame.BootPassOver(["[2026-11-01 22:10:00.000] [info] [DDC] 6651 entries"], start), "an earlier session's trim line");
		Check(InGame.BootPassOver(["[2026-11-02 19:30:40.000] [info] [DDC] 6651 entries"], start), "this start's trim line");
	}

	// the leak guard, both ways, with a harmless program (no window of any kind - the "player's screen" is the fake below)
	static void T_LeakGuardEndsAToolThatShowsOnHisScreen()
	{
		var keep = HiddenDesktop.OnPlayersDesktop;
		HiddenDesktop.GuardPoll = TimeSpan.FromMilliseconds(100);
		try {
			// pythonw where it is installed (no window of any kind); elsewhere Windows' own ping, which opens none either:
			// HiddenDesktop.Start gives a console program no console at all (CREATE_NO_WINDOW)
			var pyw = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Python", "pythoncore-3.14-64", "pythonw.exe");
			var ping = File.Exists(pyw) ? $"\"{pyw}\" -c \"import time; time.sleep(30)\"" :
				$"\"{Path.Combine(Environment.SystemDirectory, "PING.EXE")}\" -n 31 127.0.0.1";
			HiddenDesktop.OnPlayersDesktop = _ => [];
			using (var quiet = new HiddenDesktop("MO2-Tool-Runner-test-a")) {
				using var p = quiet.Start(ping, Path.GetTempPath());
				Thread.Sleep(800);
				Check(quiet.Leak == null && !p.HasExited, "a tool that stays hidden was ended");
				Procs.KillTree(p.Id);
			}
			HiddenDesktop.OnPlayersDesktop = pids => pids.Count > 0 ? [(nint)1] : [];
			using (var leaky = new HiddenDesktop("MO2-Tool-Runner-test-b")) {
				using var p = leaky.Start(ping, Path.GetTempPath());
				var sw = Stopwatch.StartNew();
				while (!p.HasExited && sw.Elapsed < TimeSpan.FromSeconds(5)) {
					Thread.Sleep(100);
				}
				Check(p.HasExited, "a tool that showed on his screen was not ended");
				Check(leaky.Leak?.Contains("player's screen", StringComparison.Ordinal) == true, "no reason given: " + leaky.Leak);
			}
		} finally {
			HiddenDesktop.OnPlayersDesktop = keep;
			HiddenDesktop.GuardPoll = TimeSpan.FromMilliseconds(500);
		}
	}

	static void T_BlockedPutBackKeepsTheRestoreButton()
	{
		using var f = new FakeMo2();
		var plugins = Path.Combine(f.ProfileDir, "plugins.txt");
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			File.AppendAllText(plugins, "Stray.esp\r\n");
			File.SetAttributes(plugins, FileAttributes.ReadOnly);
		}));
		f.WriteSetup(("landscapefixer", []));
		try {
			var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
			Check(r.Verdict.Contains("could NOT be put back", StringComparison.Ordinal), "the failed put-back is said: " + r.Verdict);
			var j = Journal.Interrupted(Runner.RunsDir(f.Data));
			Check(j is { NeedsRestore: true }, "the run is offered for Restore");
			File.SetAttributes(plugins, FileAttributes.Normal);
			Equal(RunState.Restored, Runner.Restore(j!).State, "Restore works once the file is free");
			Check(!f.Text("plugins.txt").Contains("Stray.esp", StringComparison.Ordinal), "plugins.txt as before");
			Check(Journal.Interrupted(Runner.RunsDir(f.Data)) == null, "nothing left to restore");
		} finally {
			File.SetAttributes(plugins, FileAttributes.Normal);
		}
	}

	static void T_RetiredModsStayOffAfterTheRun()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp");
			ctx.Journal.Retire.Add("Replaced");
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A", ["replaces"] = new JsonArray("Replaced") }));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, r.Verdict);
		var p = ProfileOf(f);
		Check(!p.IsOn("Replaced"), "the replaced mod stays off");
		Check(!f.Text("plugins.txt").Contains("*Old.esp", StringComparison.Ordinal), "its plugin unticked");
	}

	// an old output that cannot come back (a file of it locked): the run says so (never "back as it was"), the mod stays off while
	// it is out - and once Restore puts it back, it is on again (it was never "replaced for good")
	static void T_OutputStillOutIsOffUntilItComesBack()
	{
		using var f = new FakeMo2();
		FileStream? held = null;
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => {
			StepKit.ParkMod(ctx, "landscapefixer", "Output A");
			var parked = ctx.Journal.Parked.Last(p => p.Mod == "Output A");
			held = new FileStream(Path.Combine(parked.To, "OutA.esp"), FileMode.Open, FileAccess.Read, FileShare.None);
			throw new StepFailedException("the tool crashed");
		}));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		try {
			var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
			Equal(RunState.Failed, r.State, "failed");
			Check(r.Verdict.Contains("except Output A", StringComparison.Ordinal), "the verdict names what is still out: " + r.Verdict);
			Check(!ProfileOf(f).IsOn("Output A"), "kept off while its old output is out");
			var j = Journal.Interrupted(Runner.RunsDir(f.Data));
			Check(j is { NeedsRestore: true }, "Restore is offered");
			Check(!j!.Retire.Contains("Output A"), "never marked replaced for good");
			held!.Dispose();
			held = null;
			Equal(RunState.Restored, Runner.Restore(Journal.Load(j.Folder)).State, "Restore once the file is free");
			Check(File.Exists(Path.Combine(f.Mods, "Output A", "OutA.esp")), "its old output is back");
			var p = ProfileOf(f);
			Check(p.IsOn("Output A") && p.IsOn("Output B") && p.IsOn("Needs B"), "the profile as it was, Output A on again");
		} finally {
			held?.Dispose();
		}
	}

	// a learned-settings record that names a file outside its own folder (damaged or edited by hand) is refused, nothing read
	static void T_LearnedSettingsStayInTheirFolder()
	{
		using var f = new FakeMo2();
		var live = Path.Combine(f.Root, "tool.ini");
		File.WriteAllText(live, "mine=1");
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A", ["executable"] = "SomeTool" }));
		var setup = Setup.Load(f.SetupFile);
		ToolSettings.Keep(setup, "landscapefixer", "SomeTool", [live]);
		var rec = Path.Combine(ToolSettings.Folder(setup, "landscapefixer"), "learned.json");
		var o = JsonNode.Parse(File.ReadAllText(rec))!.AsObject();
		o["Files"]![0]!["Stored"] = @"..\..\..\..\Setups\Test.json";
		File.WriteAllText(rec, o.ToJsonString());
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => throw new InvalidOperationException("must not run")));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Failed, r.State, "refused");
		Check(r.Verdict.Contains("outside their folder", StringComparison.Ordinal), "said why: " + r.Verdict);
		Equal("mine=1", File.ReadAllText(live), "the player's file untouched");
	}

	// his checklist 270: the preview names the tools, what goes off and why, and what is replaced for good - and changes nothing
	static void T_PreviewSaysWhatARunDoes()
	{
		using var f = new FakeMo2();
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }),
			("synthesis", new JsonObject { ["output_mod"] = "Output B", ["replaces"] = new JsonArray("Replaced") }));
		var before = Profile.OrderFiles.Select(n => Path.Combine(f.ProfileDir, n)).Where(File.Exists).Select(File.ReadAllBytes).ToList();
		var setup = Setup.Load(f.SetupFile);
		var p = RunPreview.Build(setup, Mo2Instance.Load(f.Exe, f.Instance), Runner.Plan(setup, null, null, null));
		Equal(2, p.Steps.Count, "two tools");
		var off = p.OffForTheRun.ToDictionary(o => o.Mod, o => o.Why);
		Check(off.TryGetValue("Output A", out var a) && a.Contains("Landscape Fixer", StringComparison.Ordinal), "Output A off, made by SnowFixer");
		Check(off.ContainsKey("Output B") && off.ContainsKey("Replaced"), "Output B and the replaced mod off");
		Check(off.TryGetValue("Needs B", out var nb) && nb.Contains("OutB.esp", StringComparison.Ordinal), "a mod needing Output B's plugin off with it, said why");
		Check(!off.ContainsKey("Base Mod"), "nothing else");
		Equal("Replaced", p.ReplacedForGood.Single(), "the replaced mod is kept off after");
		var lines = p.Lines(k => k == "landscapefixer" ? TimeSpan.FromMinutes(3) : null);
		Check(lines.Any(l => l.Contains("usually 3 min", StringComparison.Ordinal)) && lines.Any(l => l.Contains("never timed", StringComparison.Ordinal)),
			"the usual times and the untimed step: " + string.Join(" | ", lines));
		var after = Profile.OrderFiles.Select(n => Path.Combine(f.ProfileDir, n)).Where(File.Exists).Select(File.ReadAllBytes).ToList();
		Check(before.Zip(after).All(x => x.First.SequenceEqual(x.Second)) && before.Count == after.Count, "the profile byte for byte as before");
		Check(!Directory.Exists(Runner.RunsDir(f.Data)) || Directory.GetDirectories(Runner.RunsDir(f.Data)).Length == 0, "no run record made");
	}

	// his checklist 270: only the last few runs' backups stay - this setup's oldest go, another setup's never
	static void T_OnlyTheLastBackupsAreKept()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		var setup = f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }));
		setup.Root["keepBackups"] = 2;
		setup.Save();
		for (var d = 1; d <= 4; ++d) {
			var old = Path.Combine(f.Backups, $"2026-01-0{d} 00-00-00 Test");
			Directory.CreateDirectory(old);
			var ro = Path.Combine(old, "old.txt");
			File.WriteAllText(ro, "x");
			File.SetAttributes(ro, FileAttributes.ReadOnly);
		}
		Directory.CreateDirectory(Path.Combine(f.Backups, "2026-01-01 00-00-00 Other"));
		Directory.CreateDirectory(Path.Combine(f.Backups, "my own folder"));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, r.Verdict);
		var left = Directory.GetDirectories(f.Backups).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
		Equal(4, left.Count, "this run's, the newest old one, the other setup's, the player's own folder: " + string.Join(" | ", left));
		Check(left.Contains("2026-01-04 00-00-00 Test") && !left.Contains("2026-01-03 00-00-00 Test"), "the newest old one stays, older ones go");
		Check(left.Contains("2026-01-01 00-00-00 Other") && left.Contains("my own folder"), "another setup's and a folder not of a run stay");
	}

	// the rename (his order 2026-11-03): saves, tasks and stamps made under the old name are still the runner's own
	static void T_OldNameIsStillOurs()
	{
		Check(Brand.Made(InGame.SaveClean), "a new save of ours");
		Check(Brand.Made("MO2 Tool Runner - clean save"), "a save made under the old name");
		Check(!Brand.Made("Save9 - Lydia"), "a player's save");
		Check(!Brand.Made(Brand.Name + "clean save"), "only the name followed by \" - \"");
		Check(Brand.AllNames.First() == Brand.Name && Brand.AllNames.Contains("MO2 Tool Runner"), "both names known, the new one first");
	}

	// runs at a set time are gone (his checklist answer 451): only a task an older version made is recognised, and removed
	static void T_OldTimedRunsAreClearedAway()
	{
		Check(Schedule.IsOurs("Skyrim Utility Patcher - Bottled Shaders 1A2B3C"), "a task of ours");
		Check(Schedule.IsOurs("\\MO2 Tool Runner - Bottled Shaders 1A2B3C"), "one made under the old name");
		Check(!Schedule.IsOurs("MicrosoftEdgeUpdateTaskMachineCore"), "anyone else's task");
		Check(!Schedule.IsOurs("MO2 Tool Runner"), "only the name followed by \" - \"");
		Check(Schedule.IsOurs("mo2 tool runner - Bottled 1A2B3C"), "any case");
		Check(Schedule.StartsOurs("<Exec><Command>\"C:\\Some Folder\\MO2 Tool Runner.exe\"</Command></Exec>"), "a task starting the old program");
		Check(Schedule.StartsOurs("<Exec><Command>C:\\x\\Skyrim Utility Patcher.exe</Command></Exec>"), "or the new one");
		Check(!Schedule.StartsOurs("<Exec><Command>cmd.exe</Command></Exec>"), "a task of the player's own that only shares the name");
		// Windows itself, only where asked (harmless tasks a year ahead, removed at once)
		if (Environment.GetEnvironmentVariable("SUP_SCHEDULE_PROBE") != "1") {
			return;
		}
		const string probe = "MO2 Tool Runner - zz offline leftover", theirs = "MO2 Tool Runner - zz offline not ours";
		try {
			Check(Schedule.AddProbe(probe) && Schedule.Exists(probe), "a leftover task made");
			Check(Schedule.AddProbe(theirs, "cmd.exe") && Schedule.Exists(theirs), "a same-named task of the player's made");
			Check(Schedule.RemoveLeftovers(n => n == probe || n == theirs).SequenceEqual([probe]), "only ours is removed");
			Check(!Schedule.Exists(probe) && Schedule.Exists(theirs), "Windows no longer has ours, still has theirs");
		} finally {
			Schedule.DeleteProbe(probe);
			Schedule.DeleteProbe(theirs);
		}
	}

	// the rename: the takeover's own check for an old copy, as the program runs it (no stand-in): a running program file under
	// the old name keeps the old folder where it is; once it ends, the folder is taken over
	static void T_DataFolderWaitsForARunningOldCopy()
	{
		var parent = Path.Combine(Path.GetTempPath(), "SUP old copy " + Guid.NewGuid().ToString("N"));
		var fake = Path.Combine(parent, "bin", Brand.OldNames[0] + ".exe");
		Directory.CreateDirectory(Path.Combine(parent, Brand.OldNames[0] + " Data", "Setups"));
		Directory.CreateDirectory(Path.GetDirectoryName(fake)!);
		File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), fake);
		using var p = Process.Start(new ProcessStartInfo(fake, "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
		try {
			Equal(Path.Combine(parent, Brand.OldNames[0] + " Data"), DataFolder.Adopt(parent, " Data"), "an old copy runs: its folder is used");
			Check(!Directory.Exists(Path.Combine(parent, Brand.Name + " Data")), "nothing moved");
			p.Kill();
			p.WaitForExit();
			// the check looks at the whole PC: 2026-11-04 a full run failed here once while the test alone passed - most likely
			// another old-name program for a moment (a second test run beside this one; not measured) - waited for up to 10 s
			var now = Path.Combine(parent, Brand.Name + " Data");
			var got = DataFolder.Adopt(parent, " Data");
			for (var i = 0; i < 20 && got != now; i++) {
				Thread.Sleep(500);
				got = DataFolder.Adopt(parent, " Data");
			}
			Equal(now, got, "it ended: the folder is taken over");
		} finally {
			if (!p.HasExited) {
				p.Kill();
			}
			p.WaitForExit();
			Directory.Delete(parent, true);
		}
	}

	// the rename: an old data folder is taken over and every path inside it repointed - escaping, letters outside English and
	// an apostrophe in the path never matter, a sibling folder sharing the name is left, a cut-off repoint is finished
	static void T_DataFolderTakenOverUnderTheNewName()
	{
		var parent = Path.Combine(Path.GetTempPath(), "SUP adopt " + Guid.NewGuid().ToString("N"), "Bob's José");
		try {
			string Old(string a_rest = "") => Path.Combine(parent, Brand.OldNames[0] + " Data") + a_rest;
			string New(string a_rest = "") => Path.Combine(parent, Brand.Name + " Data") + a_rest;
			void Write(string a_file, string a_json)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(a_file)!);
				File.WriteAllText(a_file, a_json);
			}
			var sibling = Path.Combine(parent, Brand.OldNames[0] + " Data2", "x");
			var relaxed = new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
			Write(Old(@"\Setups\A.json"), new JsonObject { ["backups"] = Old(@"\Backups"), ["scratch"] = Old(), ["other"] = sibling,
				["list"] = new JsonArray(Old(@"\Runs\1")) }.ToJsonString(relaxed));
			Write(Old(@"\Runs\1\journal.json"), new JsonObject { ["folder"] = Old(@"\Runs\1") }.ToJsonString());
			Equal(Old(), DataFolder.Adopt(parent, " Data", () => true), "an old copy running: the old folder is used, nothing moved");
			Check(Directory.Exists(Old()) && !Directory.Exists(New()), "left where it was");
			Directory.CreateDirectory(New());
			Equal(New(), DataFolder.Adopt(parent, " Data", () => false), "an empty new folder does not hide the old one");
			Check(!Directory.Exists(Old()), "the old folder moved");
			var a = JsonNode.Parse(File.ReadAllText(New(@"\Setups\A.json")))!;
			Equal(New(@"\Backups"), a["backups"]!.ToString(), "a path inside (written with literal letters)");
			Equal(New(), a["scratch"]!.ToString(), "the folder itself");
			Equal(sibling, a["other"]!.ToString(), "a sibling folder sharing the name is left");
			Equal(New(@"\Runs\1"), a["list"]![0]!.ToString(), "a path in a list");
			Equal(New(@"\Runs\1"), JsonNode.Parse(File.ReadAllText(New(@"\Runs\1\journal.json")))!["folder"]!.ToString(), "a path written escaped");
			Check(!File.Exists(New(@"\.renamed from.txt")), "the repoint finished, its mark gone");
			// a repoint cut off half way: the mark makes the next start finish it
			Write(New(@"\Setups\B.json"), new JsonObject { ["backups"] = Old(@"\Backups") }.ToJsonString());
			File.WriteAllText(New(@"\.renamed from.txt"), Old());
			Equal(New(), DataFolder.Adopt(parent, " Data", () => false), "the new folder in use");
			Equal(New(@"\Backups"), JsonNode.Parse(File.ReadAllText(New(@"\Setups\B.json")))!["backups"]!.ToString(), "the cut-off repoint finished");
			// a new folder in use is never replaced by an old one that turns up beside it
			Write(Old(@"\Setups\C.json"), "{}");
			Equal(New(), DataFolder.Adopt(parent, " Data", () => false), "a new folder with files is kept");
			Check(File.Exists(Old(@"\Setups\C.json")), "the old one left alone");
		} finally {
			Directory.Delete(Path.GetDirectoryName(parent)!, true);
		}
	}

	// the rename: the run lock is held under the old name too, so an old copy of the program never runs beside this one
	static void T_RunLockCoversTheOldName()
	{
		var oldLock = "Local\\" + Brand.OldNames[0] + " run" + Runner.LockScope;
		using (var hold = Runner.HoldIfFree()) {
			Check(hold != null, "free: taken");
			var open = Mutex.TryOpenExisting(oldLock, out var m);
			m?.Dispose();
			Check(open, "the old name's lock is held too");
		}
		using var taken = new ManualResetEventSlim();
		using var done = new ManualResetEventSlim();
		var t = new Thread(() => {
			using var m = new Mutex(true, oldLock);
			taken.Set();
			done.Wait();
			m.ReleaseMutex();
		});
		t.Start();
		taken.Wait();
		try {
			using var hold = Runner.HoldIfFree();
			Check(hold == null, "an old copy holding its lock: no run");
			Check(Runner.HoldIfFree() is null, "still none");
		} finally {
			done.Set();
			t.Join();
		}
		using var after = Runner.HoldIfFree();
		Check(after != null, "free again once the old copy let go");
	}

	// his lesson 2026-10-27 (checklist 271): the mods a LOD step builds from are found off their plugins' record groups, each with
	// its own page; the setup's own outputs (made from them) are left out
	static void T_LodGuideFindsTheModsItBuildsFrom()
	{
		using var f = new FakeMo2();
		static void WithGroups(string a_path, params string[] a_groups)
		{
			FakeMo2.Plugin(a_path, false);
			using var fs = new FileStream(a_path, FileMode.Append);
			foreach (var g in a_groups) {
				var h = new byte[24 + 8];
				Encoding.ASCII.GetBytes("GRUP").CopyTo(h, 0);
				BitConverter.GetBytes((uint)h.Length).CopyTo(h, 4);
				Encoding.ASCII.GetBytes(g).CopyTo(h, 8);
				fs.Write(h);
			}
		}
		// a top-level group holding one record (a_nested: inside one more group, as a worldspace's cells are)
		static void WithRecord(string a_path, string a_group, string a_record, uint a_flags, bool a_nested)
		{
			FakeMo2.Plugin(a_path, false);
			var rec = new byte[24 + 4];
			Encoding.ASCII.GetBytes(a_record).CopyTo(rec, 0);
			BitConverter.GetBytes(4u).CopyTo(rec, 4);
			BitConverter.GetBytes(a_flags).CopyTo(rec, 8);
			var inner = rec;
			if (a_nested) {
				inner = new byte[24 + rec.Length];
				Encoding.ASCII.GetBytes("GRUP").CopyTo(inner, 0);
				BitConverter.GetBytes((uint)inner.Length).CopyTo(inner, 4);
				Encoding.ASCII.GetBytes("CELL").CopyTo(inner, 8);
				rec.CopyTo(inner, 24);
			}
			var top = new byte[24 + inner.Length];
			Encoding.ASCII.GetBytes("GRUP").CopyTo(top, 0);
			BitConverter.GetBytes((uint)top.Length).CopyTo(top, 4);
			Encoding.ASCII.GetBytes(a_group).CopyTo(top, 8);
			inner.CopyTo(top, 24);
			using var fs = new FileStream(a_path, FileMode.Append);
			fs.Write(top);
		}
		f.Mod("Grass Mod");
		WithGroups(Path.Combine(f.Mods, "Grass Mod", "Grass.esp"), "WEAP", "GRAS");
		File.WriteAllText(Path.Combine(f.Mods, "Grass Mod", "meta.ini"), "[General]\r\ngameName=SkyrimSE\r\nmodid=12345\r\n");
		f.Mod("Tree Mod");
		WithGroups(Path.Combine(f.Mods, "Tree Mod", "Trees.esp"), "TREE");
		WithGroups(Path.Combine(f.Mods, "Output A", "OutA.esp"), "GRAS");
		f.Mod("Static Trees");
		WithRecord(Path.Combine(f.Mods, "Static Trees", "StaticTrees.esp"), "STAT", "STAT", 0x40, false);
		f.Mod("Plain Statics");
		WithRecord(Path.Combine(f.Mods, "Plain Statics", "PlainStatics.esp"), "STAT", "STAT", 0, false);
		f.Mod("Land Mod");
		WithRecord(Path.Combine(f.Mods, "Land Mod", "Land.esp"), "WRLD", "LAND", 0, true);
		f.Mod("Unticked Plugin");
		WithGroups(Path.Combine(f.Mods, "Unticked Plugin", "Off.esp"), "GRAS");
		var ml = Path.Combine(f.ProfileDir, "modlist.txt");
		File.WriteAllText(ml, File.ReadAllText(ml).Replace("+Needs B", "+Grass Mod\r\n+Tree Mod\r\n+Static Trees\r\n+Plain Statics\r\n+Land Mod\r\n+Unticked Plugin\r\n+Needs B", StringComparison.Ordinal));
		// only plugins switched on count (Off.esp is installed but not ticked in plugins.txt)
		File.AppendAllText(Path.Combine(f.ProfileDir, "plugins.txt"), "*Grass.esp\r\n*Trees.esp\r\n*StaticTrees.esp\r\n*PlainStatics.esp\r\n*Land.esp\r\nOff.esp\r\n");
		Equal(new HashSet<string> { "WEAP", "GRAS" }.SetEquals(Plugins.TopGroups(Path.Combine(f.Mods, "Grass Mod", "Grass.esp"))), true, "its record groups read off their headers");
		f.WriteSetup(("grass", new JsonObject { ["output_mod"] = "Output A" }));
		var setup = Setup.Load(f.SetupFile);
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var grass = LodGuide.Find(setup, mo2, "grass");
		Equal("Grass Mod", grass.Single().Mod, "the grass mod, not the setup's own output: " + string.Join(", ", grass.Select(h => h.Mod)));
		Equal("https://www.nexusmods.com/skyrimspecialedition/mods/12345", grass.Single().Page, "its own page");
		var dyn = LodGuide.Find(setup, mo2, "dyndolod");
		Check(dyn.Any(h => h.Mod == "Tree Mod" && h.Page == null) && dyn.Any(h => h.Mod == "Grass Mod"), "DynDOLOD: grass and trees, a mod with no page said so");
		Check(dyn.Any(h => h.Mod == "Static Trees") && dyn.All(h => h.Mod != "Plain Statics"), "a static flagged Has Tree LOD counts as a tree, a plain static does not");
		Check(grass.All(h => h.Mod != "Unticked Plugin"), "a plugin not switched on is not counted");
		Equal("Land Mod", LodGuide.Find(setup, mo2, "xlodgen").Single().Mod, "xLODGen: a landscape record inside a worldspace");
		Equal(0, LodGuide.Find(setup, mo2, "landscapefixer").Count, "not a LOD step");
	}

	// TexGen / DynDOLOD keep their window's values in no file, but write them into their debug log: learned from there, and a
	// later run that used other values is named in the run's notes (mo2-setup's finding 2026-10-27)
	static void T_ToolWindowValuesReadOffItsLog()
	{
		using var f = new FakeMo2();
		var exe = Path.Combine(f.Root, "Tools", "DynDOLOD", "TexGenx64.exe");
		Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(exe)!, "Logs"));
		File.WriteAllText(exe, "");
		var log = Path.Combine(Path.GetDirectoryName(exe)!, "Logs", "TexGen_SSE_Debug_log.txt");
		File.WriteAllText(log, "[00:01] [81BC] [ListVars] <Debug: GrassHDBillboardsAmbient: 80>\r\n[00:01] [81BC] [Other] <Debug: x: y>\r\n" +
			"[00:15] [81BC] [ListVars] <Debug: GrassHDBillboardsAmbient: 100>\r\n[00:15] [81BC] [ListVars] <Debug: GrassHDBillboards: True>\r\n");
		var vars = ToolSettings.ReadVars(log);
		Equal("100", vars["GrassHDBillboardsAmbient"], "the last run's value wins");
		Equal(2, vars.Count, "only ListVars lines");
		Equal(log, ToolSettings.VarsLog(exe, "texgen"), "its debug log is found beside the tool");
		Equal(null, ToolSettings.VarsLog(exe, "landscapefixer"), "a tool without one has none");
		StepRegistry.Replace(new FakeStep("texgen", (ctx, s) => {
			Thread.Sleep(50);
			File.WriteAllText(log, "[00:01] [81BC] [ListVars] <Debug: GrassHDBillboardsAmbient: 60>\r\n[00:01] [81BC] [ListVars] <Debug: GrassHDBillboards: True>\r\n");
		}));
		f.WriteSetup(("texgen", new JsonObject { ["exe"] = exe }));
		var setup = Setup.Load(f.SetupFile);
		ToolSettings.Keep(setup, "texgen", "TexGen", [], vars);
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "a different value never fails the run: " + r.Verdict);
		var j = Journal.All(Runner.RunsDir(f.Data)).First();
		Check(j.Notes.Any(n => n.Contains("GrassHDBillboardsAmbient 60 (learned 100)", StringComparison.Ordinal)), "the difference is named: " + string.Join(" | ", j.Notes));
		Check(j.Notes.All(n => !n.Contains("GrassHDBillboards True", StringComparison.Ordinal)), "an equal value is not");
	}

	// the window looks every second: a run record is read again only when its file changed, a removed run drops out, a broken
	// record is skipped (not the whole list)
	static void T_RunRecordsAreReadAgainOnlyWhenChanged()
	{
		using var f = new FakeMo2();
		var runs = Path.Combine(f.Root, "Runs");
		Journal Make(string a_id, RunState a_state)
		{
			var j = new Journal { Id = a_id, Folder = Path.Combine(runs, a_id), State = a_state, Verdict = a_id };
			Directory.CreateDirectory(j.Folder);
			j.Save();
			return j;
		}
		Make("2026-10-01 10-00-00", RunState.Finished);
		var second = Make("2026-10-02 10-00-00", RunState.Failed);
		var cache = new JournalCache(runs);
		var a = cache.Read();
		Equal(2, a.Count, "both runs");
		Equal("2026-10-02 10-00-00", a[0].Id, "newest first");
		var b = cache.Read();
		Check(ReferenceEquals(a[0], b[0]) && ReferenceEquals(a[1], b[1]), "an unchanged record is not read again");
		second.Verdict = "changed and longer";
		second.Save();
		var c = cache.Read();
		Equal("changed and longer", c[0].Verdict, "a changed record is read again");
		Check(ReferenceEquals(a[1], c[1]), "the other one still not");
		File.WriteAllText(Path.Combine(Make("2026-10-03 10-00-00", RunState.Running).Folder, "journal.json"), "{ broken");
		Equal(2, cache.Read().Count, "a broken record is skipped, the rest kept");
		Directory.Delete(second.Folder, true);
		var d = cache.Read();
		Equal(1, d.Count, "a removed run drops out");
		Equal("2026-10-01 10-00-00", d[0].Id, "the one left");
	}

	// ------------------------------------------------------------------ the PC-level pre-checks (SystemChecks)
	static void SetLight(string a_path)
	{
		var b = File.ReadAllBytes(a_path);
		b[9] |= 0x02;
		File.WriteAllBytes(a_path, b);
	}

	static void T_PluginLimitCountsByKind()
	{
		using var f = new FakeMo2();
		f.Mod("Lights", ("Light.esl", false, []), ("LightFlag.esp", false, []), ("LightMaster.esm", true, []), ("Unticked.esp", false, []), ("Big.esp", false, []));
		SetLight(Path.Combine(f.Mods, "Lights", "LightFlag.esp"));
		SetLight(Path.Combine(f.Mods, "Lights", "LightMaster.esm"));
		FakeMo2.Plugin(Path.Combine(f.Game, "Data", "ccTest.esl"), false);
		FakeMo2.Plugin(Path.Combine(f.Game, "Data", "Update.esm"), true);
		File.WriteAllText(Path.Combine(f.Game, "Skyrim.ccc"), "ccTest.esl\r\nccAbsent.esm\r\n");
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "+Lights\r\n" + f.Text("modlist.txt"));
		File.AppendAllText(Path.Combine(f.ProfileDir, "plugins.txt"), "*Light.esl\r\n*LightFlag.esp\r\n*LightMaster.esm\r\nUnticked.esp\r\n*Big.esp\r\n*Gone.esp\r\n*Update.esm\r\n");
		var c = SystemChecks.CountPlugins(ProfileOf(f));
		// full: Skyrim.esm, Update.esm (once, though plugins.txt names it too), the six of the fake, Big.esp; Gone.esp is nowhere
		Equal(9, c.Full, "full plugins");
		// light: the .esl, the flagged .esp, the flagged .esm, the Creation Club .esl that Skyrim.ccc names
		Equal(4, c.Light, "light plugins");
		Check(!Plugins.IsLight(Path.Combine(f.Mods, "Lights", "Big.esp")), "an unflagged .esp is full");
		Check(Plugins.IsLight(Path.Combine(f.Mods, "Lights", "LightMaster.esm")), "an ESL-flagged .esm is light");
		Equal(0, SystemChecks.PluginLimit(c).Count(), "far from both limits");
		Check(SystemChecks.PluginLimit(new(255, 0)).Single().Blocking, "255 full plugins stop the run");
		Check(!SystemChecks.PluginLimit(new(254, 0)).Single().Blocking, "254 full plugins only warn (the outputs add more)");
		Equal(0, SystemChecks.PluginLimit(new(249, 4086)).Count(), "249 full and 4086 light: fine");
		Check(!SystemChecks.PluginLimit(new(10, 4096)).Single().Blocking, "4096 light plugins only warn");
		Check(SystemChecks.PluginLimit(new(10, 4097)).Single().Blocking, "4097 light plugins stop the run");
		var unread = SystemChecks.PluginLimit(new(250 - 10, 10, 20)).Single();
		Check(!unread.Blocking && unread.Text.Contains("could be over", StringComparison.Ordinal), "unreadable plugins are said, and what they could mean");
		// a plugin held open with no sharing cannot be read: neither light nor full
		var locked = Path.Combine(f.Mods, "Lights", "LightFlag.esp");
		using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None)) {
			Equal<bool?>(null, Plugins.LightOrNot(locked), "an unreadable plugin is neither kind");
			Equal(1, SystemChecks.CountPlugins(ProfileOf(f)).Unreadable, "and counted apart");
		}
	}

	static void T_AdminOnOneSideOnly()
	{
		Equal(0, SystemChecks.Admin(false, false, false).Count(), "both normal");
		Check(SystemChecks.Admin(false, true, false).Single().Blocking, "Mod Organizer as administrator, the runner not: stop");
		Check(SystemChecks.Admin(false, false, true).Single().Blocking, "Mod Organizer set to always run as administrator: stop");
		Equal(2, SystemChecks.Admin(false, true, true).Count(), "both named");
		var self = SystemChecks.Admin(true, true, true).ToList();
		Check(self.Count == 1 && !self[0].Blocking, "the runner as administrator: one warning (whatever it starts runs as administrator too)");
	}

	static void T_ASecondMo2IsNamed()
	{
		var ours = @"C:\Games\MO2\ModOrganizer.exe";
		Equal(0, SystemChecks.OtherMo2([@"c:\games\mo2\MODORGANIZER.exe"], ours).Count(), "our own, any case");
		var i = SystemChecks.OtherMo2([ours, @"D:\Other MO2\ModOrganizer.exe", @"D:\Other MO2\ModOrganizer.exe"], ours).Single();
		Check(i.Blocking && i.Text.Contains(@"D:\Other MO2\ModOrganizer.exe", StringComparison.Ordinal), "another install stops the run, named once");
	}

	static void T_RestartBatteryAndLongPaths()
	{
		Equal(0, SystemChecks.Power(false, null).Count(), "plugged in, nothing pending");
		Check(!SystemChecks.Power(true, null).Single().Blocking, "a pending restart warns");
		var b = SystemChecks.Power(false, new(true, 40)).Single();
		Check(!b.Blocking && b.Text.Contains("40 %", StringComparison.Ordinal), "on battery warns with its level");
		Check(!SystemChecks.Power(false, new(true, 255)).Single().Text.Contains('%', StringComparison.Ordinal), "an unknown level is left out");
		var deep = Path.Combine(Path.GetTempPath(), new string('x', SystemChecks.LongFolder));
		Check(!SystemChecks.LongPaths([deep, deep, "", Path.GetTempPath()]).Single().Blocking, "a long folder warns once");
	}

	static void T_TroubleFoldersAreWarned()
	{
		SystemChecks.TroubleInput In(string a_game = @"C:\Games\Skyrim", string a_base = @"C:\Modding\MO2", string a_mods = @"C:\Modding\MO2\mods",
			string a_out = "", string a_docs = @"C:\Users\p\Documents", bool a_cfa = false, Func<string, bool>? a_net = null) =>
			new([("the game", a_game), ("Mod Organizer's instance", a_base), ("Mod Organizer's mods", a_mods), ("xlodgen's output folder", a_out)], a_docs,
				[@"C:\Program Files", @"C:\Program Files (x86)"], [@"C:\Users\p\OneDrive"], a_cfa, a_net ?? (_ => false));
		Equal(0, SystemChecks.TroubleFolders(In()).Count(), "a clean setup: nothing");
		var pf = SystemChecks.TroubleFolders(In(a_game: @"C:\Program Files (x86)\Steam\steamapps\common\Skyrim Special Edition")).Single();
		Check(!pf.Blocking && pf.Text.StartsWith("the game (", StringComparison.Ordinal) && pf.Text.Contains(@"inside C:\Program Files (x86)", StringComparison.Ordinal),
			"the game in Program Files warns");
		Equal(0, SystemChecks.TroubleFolders(In(a_game: @"C:\Program Files Extra\Skyrim")).Count(), "a folder that only starts with the same letters is not inside");
		Check(SystemChecks.TroubleFolders(In(a_docs: @"C:\Users\p\OneDrive\Documents")).Single().Text.Contains("Documents", StringComparison.Ordinal), "Documents in OneDrive warns");
		// the instance in OneDrive with its mods inside it: one warning, naming the instance only
		var one = SystemChecks.TroubleFolders(In(a_base: @"C:\Users\p\OneDrive\MO2", a_mods: @"C:\Users\p\OneDrive\MO2\mods")).Single();
		Check(one.Text.Contains("Mod Organizer's instance", StringComparison.Ordinal) && !one.Text.Contains("mods (", StringComparison.Ordinal), "one cause told once");
		Check(SystemChecks.TroubleFolders(In(a_out: @"C:\Users\p\OneDrive\LOD")).Single().Text.StartsWith("xlodgen's output folder", StringComparison.Ordinal), "a step's output in the cloud warns");
		Check(SystemChecks.TroubleFolders(In(a_game: @"C:\Users\p\OneDrive\Skyrim")).Single().Text.StartsWith("the game", StringComparison.Ordinal), "the game in the cloud warns");
		var net = SystemChecks.TroubleFolders(In(a_net: d => d.StartsWith(@"C:\Modding", StringComparison.Ordinal))).Single();
		Check(net.Text.Contains("Mod Organizer's instance (", StringComparison.Ordinal) && !net.Text.Contains("Mod Organizer's mods (", StringComparison.Ordinal) &&
			net.Text.Contains(" is on a network drive", StringComparison.Ordinal), "the network named once for the instance and the mods inside it");
		Check(SystemChecks.TroubleFolders(In(a_cfa: true)).Single().Text.Contains("controlled folder access", StringComparison.Ordinal), "controlled folder access warns");
		Check(SystemChecks.TroubleFolders(In(a_cfa: true, a_docs: @"C:\Users\p\OneDrive\Documents")).All(i => !i.Blocking), "every one only warns");
		// the real network probe on local paths
		// Windows' own folder: on the local disk on every PC (the temp folder can be set to a share)
		var local = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
		Check(!SystemChecks.IsNetworkPath(local), "Windows' folder is local");
		Check(!SystemChecks.IsNetworkPath(@"\\?\" + local), "a long-form local path is local");
		Check(SystemChecks.IsNetworkPath(@"\\server\share\mods") && SystemChecks.IsNetworkPath(@"\\?\UNC\server\share\mods"), "a share is a network folder");
		Check(SystemChecks.IsNetworkPath(@"\\.\UNC\server\share\mods"), "the device form of a share is a network folder");
		Check(!SystemChecks.IsNetworkPath(@"\\?\Volume{0b1c2d3e-0000-0000-0000-100000000000}\mods"), "a volume named by its id is local");
		// the same folder under two names is named once; cloud folders are taken once, a folder inside another left out
		var twice = SystemChecks.TroubleFolders(In(a_mods: @"C:\Users\p\OneDrive\x", a_out: @"C:\Users\p\OneDrive\x\")).Single();
		Equal(1, twice.Text.Split(@"OneDrive\x").Length - 1, "one folder, one mention");
		Equal(@"C:\Users\p\Dropbox", string.Join("|", SystemChecks.Roots([@"C:\Users\p\Dropbox\", @"c:\users\p\dropbox", @"C:\Users\p\Dropbox\Sub", ""])), "the cloud roots");
		// a probe that throws leaves only its own part out (every probe stubbed: the test never reads this PC)
		var notes = new List<ToolRunner.Steps.Issue>();
		var was = SystemChecks.ControlledFolderAccess;
		var clouds = SystemChecks.CloudFolders;
		var docs = SystemChecks.Documents;
		var pfs = SystemChecks.ProgramFolders;
		try {
			SystemChecks.ControlledFolderAccess = () => throw new System.Security.SecurityException("locked down");
			SystemChecks.CloudFolders = () => throw new InvalidOperationException("a settings file of the wrong shape");
			SystemChecks.Documents = () => @"C:\Users\p\Documents";
			SystemChecks.ProgramFolders = () => [@"C:\Program Files"];
			var input = SystemChecks.ReadTrouble([("the game", @"C:\Program Files\Skyrim")], notes);
			Check(!input.ControlledAccess && input.CloudFolders.Count == 0, "the failing probes give nothing");
			Equal(2, notes.Count, "each failing probe told once");
			Check(SystemChecks.TroubleFolders(input).Single().Text.Contains("Program Files", StringComparison.Ordinal), "the probes that read still warn");
		} finally {
			SystemChecks.ControlledFolderAccess = was;
			SystemChecks.CloudFolders = clouds;
			SystemChecks.Documents = docs;
			SystemChecks.ProgramFolders = pfs;
		}
	}

	static void T_PrecheckCarriesThePcChecks()
	{
		using var f = new FakeMo2();
		var setup = f.WriteSetup();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		Equal(0, Preflight.Run(setup, mo2, []).Count(i => i.Blocking), "a clean fake: nothing blocks");
		var was = SystemChecks.Mo2Paths;
		try {
			SystemChecks.Mo2Paths = () => [Path.Combine(f.Root, "Another", "ModOrganizer.exe")];
			Check(Preflight.Run(setup, mo2, []).Any(i => i.Blocking && i.Text.Contains("another Mod Organizer", StringComparison.Ordinal)), "the check reaches the pre-check");
		} finally {
			SystemChecks.Mo2Paths = was;
		}
		var clouds = SystemChecks.CloudFolders;
		try {
			SystemChecks.CloudFolders = () => [f.Root];
			Check(Preflight.Run(setup, mo2, []).Any(i => i.Text.Contains("cloud app", StringComparison.Ordinal)), "the trouble folders reach the pre-check");
		} finally {
			SystemChecks.CloudFolders = clouds;
		}
		var unread = SystemChecks.Mo2Unreadable;
		try {
			SystemChecks.Mo2Unreadable = _ => true;
			var issues = Preflight.Run(setup, mo2, []);
			Check(issues.Any(i => i.Blocking && i.Text.StartsWith(SystemChecks.UnreadableText, StringComparison.Ordinal)), "an unreadable Mod Organizer stops the run");
		} finally {
			SystemChecks.Mo2Unreadable = unread;
		}
		// Mod Organizer set to always run as administrator, through the whole check; and its probe failing leaves the others
		var flagged = SystemChecks.AdminFlagged;
		try {
			SystemChecks.AdminFlagged = _ => true;
			Check(SystemChecks.Run(mo2, ProfileOf(f), []).Single(i => i.Blocking).Text.Contains("always run as administrator", StringComparison.Ordinal), "the flag stops the run");
			SystemChecks.Mo2Unreadable = _ => throw new System.ComponentModel.Win32Exception(5);
			Check(SystemChecks.Run(mo2, ProfileOf(f), []).Any(i => i.Blocking && i.Text.Contains("always run as administrator", StringComparison.Ordinal)),
				"a failing process probe does not hide the flag");
		} finally {
			SystemChecks.AdminFlagged = flagged;
			SystemChecks.Mo2Unreadable = unread;
		}
		// a switched-off step's folder is not the run's business
		var offOut = Path.Combine(f.Root, new string('q', SystemChecks.LongFolder));
		var off = f.WriteSetup(("pandora", new JsonObject { ["output_dir"] = offOut }));
		((JsonObject)((JsonArray)off.Root["steps"]!)[0]!)["on"] = false;
		off.Save();
		Check(!Preflight.Run(Setup.Load(off.File), mo2, []).Any(i => i.Text.Contains(offOut, StringComparison.Ordinal)), "a switched-off step's output is not checked");
		// a step's output folder deep down is warned about too
		var deepOut = Path.Combine(f.Root, new string('o', SystemChecks.LongFolder));
		var withOut = f.WriteSetup(("pandora", new JsonObject { ["output_dir"] = deepOut }));
		Check(Preflight.Run(withOut, mo2, []).Any(i => i.Text.Contains(deepOut, StringComparison.Ordinal)), "a step's output folder is checked for its length");
	}

	// a blocking PC check refuses the run before anything is touched
	static void T_ASecondMo2RefusesTheRun()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => throw new InvalidOperationException("must not run")));
		f.WriteSetup(("landscapefixer", []));
		var before = f.Text("modlist.txt");
		var was = SystemChecks.Mo2Paths;
		try {
			SystemChecks.Mo2Paths = () => [Path.Combine(f.Root, "Another MO2", "ModOrganizer.exe")];
			var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
			Check(r.Folder == null && r.Verdict.StartsWith("not started", StringComparison.Ordinal) && r.Verdict.Contains("another Mod Organizer", StringComparison.Ordinal), r.Verdict);
			Equal(before, f.Text("modlist.txt"), "the profile untouched");
		} finally {
			SystemChecks.Mo2Paths = was;
		}
	}

	// a player's folder can be deeper than Windows' old 260-letter limit: the profile's own files are still read, changed and put back
	static void T_LongPathProfileWorks()
	{
		using var f = new FakeMo2("\r\n", string.Join('\\', Enumerable.Repeat("a long folder name for the test", 9)));
		Check(f.ProfileDir.Length > 300, "the fake sits past 260 letters");
		var p = ProfileOf(f);
		var snap = p.Snapshot();
		p.SetMods([], ["Base Mod"]);
		Check(!p.IsOn("Base Mod"), "a mod switched off in a long path");
		p.Restore(snap);
		Check(p.IsOn("Base Mod"), "and put back");
		Equal(7, SystemChecks.CountPlugins(p).Full, "its plugins counted");
	}

	// ------------------------------------------------------------------ run again from the failed step, a queue of setups
	static Journal RunOf(RunState a_state, params (string Key, StepState State)[] a_steps) => new() {
		SetupFile = @"C:\Setups\Test.json", State = a_state, Steps = a_steps.Select(s => new StepRecord { Key = s.Key, State = s.State }).ToList(),
	};

	static void T_RetryStartsAtTheStepThatDidNotFinish()
	{
		var failed = RunOf(RunState.Failed, ("synthesis", StepState.Done), ("texgen", StepState.Failed), ("dyndolod", StepState.Skipped));
		Equal("texgen", Retry.FromStep(failed), "the failed step");
		var req = Retry.Request(failed, @"C:\Data")!;
		Equal("texgen", req.From, "from the failed step");
		Equal("dyndolod", req.Until, "to where the run ended");
		Equal<string?>(null, req.Only, "not one step only");
		Equal("dyndolod", Retry.FromStep(RunOf(RunState.Stopped, ("texgen", StepState.Done), ("dyndolod", StepState.Stopped))), "a stopped step");
		// "stop after this step": the run writes the steps it never reached as Skipped - the first of them is where it carries on
		Equal("dyndolod", Retry.FromStep(RunOf(RunState.Stopped, ("synthesis", StepState.Done), ("texgen", StepState.Done), ("dyndolod", StepState.Skipped))), "after a stop after");
		// refused before its first step (Mod Organizer would not close): every step Skipped - from the start
		Equal("texgen", Retry.FromStep(RunOf(RunState.Failed, ("texgen", StepState.Skipped), ("dyndolod", StepState.Skipped))), "nothing ran yet");
		Equal("dyndolod", Retry.FromStep(RunOf(RunState.Restored, ("texgen", StepState.Done), ("dyndolod", StepState.Running))), "put back after a crash");
		Equal<string?>(null, Retry.FromStep(RunOf(RunState.Finished, ("texgen", StepState.Done))), "a finished run has nothing to run again");
		Equal<string?>(null, Retry.FromStep(RunOf(RunState.Running, ("texgen", StepState.Running))), "a run not finished is put back first");
		Equal<string?>(null, Retry.FromStep(RunOf(RunState.Failed, ("texgen", StepState.Done))), "every step done");
	}

	static void T_RetryRunsOnlyWhatIsLeft()
	{
		using var f = new FakeMo2();
		var setup = f.WriteSetup(("pandora", new JsonObject()), ("playsave", new JsonObject()));
		var j = RunOf(RunState.Failed, ("pandora", StepState.Done), ("playsave", StepState.Failed));
		j.SetupFile = setup.File;
		var req = Retry.Request(j, f.Data)!;
		Equal("playsave", string.Join(",", Runner.Plan(setup, req.Only, req.From, req.Until).Select(s => s.Key)), "the plan of a run again holds only the steps left");
	}

	static void T_QueueRunsInOrderAndEndsWhereItShould()
	{
		using var f = new FakeMo2();
		var data = f.Data;
		var files = new[] { "A", "B", "C" }.Select(n => Path.Combine(data, "Setups", n + ".json")).ToList();
		List<string> Ran(Dictionary<string, RunState> a_results, bool a_keep, out List<QueueRun.Item> a_items)
		{
			var ran = new List<string>();
			a_items = QueueRun.Run(files, data, a_keep, r => {
				var name = Path.GetFileNameWithoutExtension(r.SetupFile);
				ran.Add(name);
				var st = QueueRun.Read(data);
				Check(st != null && st.Setups[st.Current] == r.SetupFile, "the queue file names the setup that runs");
				Check(r.From == null && r.Only == null, "each setup runs whole");
				// nothing else may start between or during the queue's runs: the lock is the queue's for its whole length
				var other = Task.Run(Runner.HoldIfFree).GetAwaiter().GetResult();
				Check(other == null, "the run lock is held by the queue");
				return new RunOutcome(a_results.TryGetValue(name, out var s) ? s : RunState.Finished, name + " verdict", "x");
			});
			Check(!File.Exists(QueueRun.File(data)), "the queue file is gone at the end");
			return ran;
		}
		Equal("A,B,C", string.Join(",", Ran([], false, out var all)), "all three in order");
		Check(all.All(i => i.Outcome?.State == RunState.Finished), "each one finished");
		Equal("A,B", string.Join(",", Ran(new() { ["B"] = RunState.Failed }, false, out var stopped)), "a failed run ends the queue");
		Check(QueueRun.Summary(stopped).Last() == "C: not run", "the setup never run is said");
		Check(File.ReadAllText(QueueRun.LastFile(data)).Contains("C: not run", StringComparison.Ordinal), "the last queue's summary is kept for the window");
		Equal("A,B,C", string.Join(",", Ran(new() { ["B"] = RunState.Failed }, true, out _)), "keep going goes past a failed run");
		Equal("A", string.Join(",", Ran(new() { ["A"] = RunState.Stopped }, true, out _)), "a stopped run always ends the queue");
		Equal<QueueRun.State?>(null, QueueRun.Read(data), "no queue read once it ended");
	}

	// the window's pictures read the way the window reads them: an Icon.ico WPF could not decode crashed the program at start
	// (2026-11-03) - a picture like that fails here, never on a player's screen. Covered: every picture compiled into the program
	// and every picture App.xaml's resources make; NOT a picture a window names by its own address (use the Logo resource way)
	static void T_EveryWindowPictureLoads()
	{
		string[] kinds = [".png", ".ico", ".jpg", ".jpeg", ".bmp", ".gif"];
		var read = new List<string>();
		Exception? failed = null;
		// every pixel copied out, past the size in its header (a PNG merely cut short is NOT refused - WPF draws what it has,
		// measured 2026-11-03 - so that is no crash and not what this catches)
		static void Whole(System.Windows.Media.Imaging.BitmapSource a_b, string a_what)
		{
			Check(a_b.PixelWidth > 0 && a_b.PixelHeight > 0, a_what + " has a size");
			var stride = (a_b.PixelWidth * a_b.Format.BitsPerPixel + 7) / 8;
			a_b.CopyPixels(new byte[stride * a_b.PixelHeight], stride, 0);
		}
		var t = new Thread(() => {
			try {
				var asm = typeof(ToolRunner.App.App).Assembly;
				// each picture compiled into the program
				using (var s = asm.GetManifestResourceStream(asm.GetName().Name + ".g.resources") ?? throw new InvalidOperationException("the program has no resources")) {
					using var r = new System.Resources.ResourceReader(s);
					foreach (System.Collections.DictionaryEntry e in r) {
						var name = (string)e.Key;
						if (kinds.Any(k => name.EndsWith(k, StringComparison.OrdinalIgnoreCase)) && e.Value is Stream pic) {
							using (pic) {
								foreach (var frame in System.Windows.Media.Imaging.BitmapDecoder.Create(pic, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
									System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames) {
									Whole(frame, name);
								}
							}
							read.Add(name);
						}
					}
				}
				// the program's own resources made as its window makes them (App.xaml names its own assembly in each picture's
				// address, so they read the same from here), each picture among them read. A process holds one WPF Application:
				// this test makes it, so no other test may
				Check(System.Windows.Application.Current == null, "no WPF Application made before this test (only one per process)");
				var app = new ToolRunner.App.App();
				app.InitializeComponent();
				// the gold accent brushes over the Fluent theme's (Windows' accent, blue on most PCs): both sets load, and the one for
				// the mode in use is last, so it wins
				Check(app.Resources.MergedDictionaries.Any(d => d.Contains("TextControlElevationBorderFocusedBrush")), "the accent brushes load with App.xaml");
				ToolRunner.App.Theme.Apply(ToolRunner.App.Theme.Default, false);
				Equal("Accent.xaml", ToolRunner.App.Theme.AccentInUse, "the dark set after the built-in look");
				var ours = app.Resources.MergedDictionaries.Last(d => d.Source?.OriginalString.EndsWith("/Accent.xaml", StringComparison.Ordinal) == true);
				Check(ReferenceEquals(app.Resources["TextControlElevationBorderFocusedBrush"], ours["TextControlElevationBorderFocusedBrush"]),
					"the text box's focus line is the program's own brush, not the Fluent theme's");
				foreach (var k in app.Resources.Keys) {
					if (app.Resources[k] is System.Windows.Media.Imaging.BitmapSource b) {
						Whole(b, $"the {k} picture");
						read.Add(k.ToString() ?? "");
					}
				}
			} catch (Exception e) when (e is not OutOfMemoryException) {
				failed = e;
			}
		});
		t.SetApartmentState(ApartmentState.STA);
		t.Start();
		t.Join();
		if (failed != null) {
			throw new InvalidOperationException(failed.Message, failed);
		}
		Check(read.Contains("Logo") && read.Any(n => n.EndsWith(".png", StringComparison.OrdinalIgnoreCase)), "the header logo and its file are among them: " + string.Join(", ", read));
	}

	// a start with no window of its own that hits an error ends with exit 1 and its error in crash.txt - no window and no box of
	// it ever shows (2026-11-03: two snapshot tries put a crash box on his screen and waited on it forever). The real program,
	// built beside the tests, is started on a hidden desktop so even a broken build never reaches the player's screen; its
	// leak guard watches his screen too
	static void T_AWindowlessErrorShowsNothing()
	{
		Check(File.Exists(Path.Combine(AppContext.BaseDirectory, Brand.Name + ".exe")), "the program sits beside the tests");
		// a copy of the built program in a scratch folder of this test's own: its data folder (and the crash.txt it writes) is the
		// copy's, removed after - nothing is left in a folder that outlives the test (CodeRabbit on PR #22)
		// never under the temp folder: a program there keeps its data in local app data (AppPaths), so its crash.txt would be elsewhere
		var scratch = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!, "SUP windowless " + Guid.NewGuid().ToString("N"));
		var exe = Path.Combine(scratch, Brand.Name + ".exe");
		var crash = Path.Combine(scratch, Brand.Name + " Data", "crash.txt");
		var keep = HiddenDesktop.GuardPoll;
		HiddenDesktop.GuardPoll = TimeSpan.FromMilliseconds(50);
		try {
			// the copy inside the cleanup's reach: a copy that fails part way leaves nothing behind
			foreach (var f in Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories)) {
				var rel = Path.GetRelativePath(AppContext.BaseDirectory, f);
				if (!rel.StartsWith(Brand.Name + " Data", StringComparison.OrdinalIgnoreCase)) {
					Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(scratch, rel))!);
					File.Copy(f, Path.Combine(scratch, rel));
				}
			}
			using var desk = new HiddenDesktop("SUP-test-windowless");
			// a folder Windows refuses to make: the snapshot's first step throws
			using var p = desk.Start($"\"{exe}\" --snapshot \"{Path.Combine(Path.GetTempPath(), "bad<name>")}\"", AppContext.BaseDirectory);
			// each window named the moment it is seen (gone by the time a message is written); one with no size is Windows' own
			// input helper (UAC_InputIndicatorOverlayWnd, 0x0 - measured 2026-11-04), nothing a player can see
			var shown = new Dictionary<nint, string>();
			var sw = Stopwatch.StartNew();
			while (!p.HasExited && sw.Elapsed < TimeSpan.FromSeconds(30)) {
				foreach (var h in desk.Windows()) {
					if (!shown.ContainsKey(h) && Native.IsWindowVisible(h) && Native.GetWindowThreadProcessId(h, out var pid) != 0 && pid == p.Id &&
					    Native.GetWindowRect(h, out var r) && r.Right > r.Left && r.Bottom > r.Top) {
						var inside = string.Join(" / ", Native.ChildWindows(h).Select(Native.WindowText).Where(t => t.Length > 0));
						shown[h] = $"{Native.ClassName(h)} '{Native.WindowText(h)}' {r.Right - r.Left}x{r.Bottom - r.Top} [{inside}]";
					}
				}
				Thread.Sleep(20);
			}
			if (!p.HasExited) {
				Procs.KillTree(p.Id);
			}
			Check(desk.Leak == null, "it showed on the player's screen: " + desk.Leak);
			Check(shown.Count == 0, "it showed " + string.Join("; ", shown.Values));
			Check(sw.Elapsed < TimeSpan.FromSeconds(30), "it was still running after 30 s");
			Equal(1, p.ExitCode, "its exit code");
			Check(File.Exists(crash) && File.ReadAllText(crash).Contains("bad<name>", StringComparison.Ordinal), "its error was written in crash.txt");
		} finally {
			HiddenDesktop.GuardPoll = keep;
			// the copy's files are free once it ended (a moment after its exit)
			for (var i = 0; i < 20 && Directory.Exists(scratch); i++) {
				try {
					Directory.Delete(scratch, true);
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					Thread.Sleep(250);
				}
			}
		}
	}

	// ------------------------------------------------------------------ his finds 2026-11-06
	// a failed run left its outputs sitting empty in Mod Organizer: an output this tool made that holds only its meta.ini leaves the
	// mod list and its folder moves into the run's backup; one with files, or a mod the tool did not make, stays
	static void T_AnEmptyOutputLeavesTheModList()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		p.EnsureMod("Empty Out");
		p.EnsureMod("Full Out");
		File.WriteAllText(Path.Combine(f.Mods, "Full Out", "Built.esp"), "x");
		f.Mod("His Empty");
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "-His Empty\r\n" + f.Text("modlist.txt"));
		var park = Path.Combine(f.Backups, "run");
		Check(p.TakeOutEmptyOutput("Empty Out", park), "the empty output was taken out");
		Check(!p.TakeOutEmptyOutput("Full Out", park), "an output with files stays");
		Check(!p.TakeOutEmptyOutput("His Empty", park), "a mod the tool did not make stays, empty or not");
		var list = f.Text("modlist.txt");
		Check(!list.Contains("Empty Out", StringComparison.Ordinal), "its mod-list line is gone");
		Check(list.Contains("-Full Out", StringComparison.Ordinal) && list.Contains("-His Empty", StringComparison.Ordinal), "the others keep their lines");
		Check(!Directory.Exists(Path.Combine(f.Mods, "Empty Out")), "its folder left the mods folder");
		Check(File.Exists(Path.Combine(park, "empty outputs", "Empty Out", "meta.ini")), "its folder is in the run's backup, never erased");
		Check(!p.Added.Contains("Empty Out") && p.Added.Contains("Full Out"), "it is no longer kept as an added mod");
		// a folder that cannot move (a file of it held open) keeps its line too - never a folder Mod Organizer would list as new;
		// and a take-out cut off after its folder moved finishes by taking out the line that was left (CodeRabbit on #24)
		p.EnsureMod("Held Out");
		using (new FileStream(Path.Combine(f.Mods, "Held Out", "meta.ini"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
			var held = false;
			try {
				p.TakeOutEmptyOutput("Held Out", park);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				held = true;
			}
			Check(held, "a folder that cannot move says so");
		}
		Check(Directory.Exists(Path.Combine(f.Mods, "Held Out")) && f.Text("modlist.txt").Contains("-Held Out", StringComparison.Ordinal),
			"the held folder and its line are as they were");
		Directory.CreateDirectory(Path.Combine(park, "empty outputs"));
		Directory.Move(Path.Combine(f.Mods, "Held Out"), Path.Combine(park, "empty outputs", "Held Out"));
		Check(p.TakeOutEmptyOutput("Held Out", park), "the line a cut-off take-out left is taken out");
		Check(!f.Text("modlist.txt").Contains("Held Out", StringComparison.Ordinal), "no line of a folder that is gone");
	}

	// the run never closes Mod Organizer while the player is working in it: in front AND touched within the quiet time
	static void T_PlayerUsingModOrganizerIsSeen()
	{
		int[] mo2 = [500, 600];
		Check(Mo2Control.PlayerUsing(mo2, 600, TimeSpan.FromSeconds(5)), "in front and just touched = in use");
		Check(!Mo2Control.PlayerUsing(mo2, 700, TimeSpan.FromSeconds(5)), "another program in front = not in use");
		Check(!Mo2Control.PlayerUsing(mo2, 600, Mo2Control.PlayerQuiet + TimeSpan.FromSeconds(1)), "in front but left alone = not in use");
		Check(!Mo2Control.PlayerUsing([], 0, TimeSpan.Zero), "no Mod Organizer = not in use");
	}

	// every window's frame is painted in the theme's colors: Windows reads a color as 0x00BBGGRR
	static void T_WindowFrameColorsAreInWindowsOrder()
	{
		Equal(0x00332211, ToolRunner.App.WindowFrame.Ref(System.Windows.Media.Color.FromRgb(0x11, 0x22, 0x33)), "the color Windows is handed");
		Equal(0x00FFFFFF, ToolRunner.App.WindowFrame.Ref(System.Windows.Media.Colors.White), "white");
	}

	// "the tool keeps trying to fight you when you're doing something in mo2": what the player ticks, unticks or installs
	// while a run goes on is told apart from the run's own switches (and from a step's switch-and-put-back), so the put-back
	// keeps it instead of undoing it
	static void T_ThePlayersChangesDuringARunAreTold()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		p.WatchForOthers();
		p.SetMods([], ["Output A"]);
		var was = p.OursNow();
		var snap = p.Snapshot();
		p.SetMods([], ["Output B"]);
		p.Restore(snap);
		p.OursBack(was);
		Equal(0, p.ChangedByOthers().Count, "the run's own switches and a step's put-back are not the player's");
		f.Mod("Players New");
		var list = f.Text("modlist.txt").Replace("+Base Mod", "-Base Mod");
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "+Players New\r\n" + list);
		var theirs = p.ChangedByOthers();
		Equal(2, theirs.Count, "two changes of the player's");
		Check(theirs.TryGetValue("Base Mod", out var b) && b == '-', "the mod the player unticked");
		Check(theirs.TryGetValue("Players New", out var n) && n == '+', "the mod the player installed and ticked");
		Check(!theirs.ContainsKey("Output A"), "the run's own switch is not the player's");
		Check(Profile.StatesOf(snap).ContainsKey("Base Mod") && !Profile.StatesOf(snap).ContainsKey("Players New"), "a snapshot's states read back");
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "-Folder Mod Organizer Found\r\n" + f.Text("modlist.txt"));
		Check(!p.ChangedByOthers().ContainsKey("Folder Mod Organizer Found"), "a folder Mod Organizer lists as off by itself is not the player's change");
	}

	// his order 2026-11-07: only Landscape Fixer - a setup still naming SnowFixer or AutoBlend is told so in plain words
	static void T_OnlyLandscapeFixerIsSupported()
	{
		using var f = new FakeMo2();
		var s = f.WriteSetup(("snowfixer", []), ("autoblend", []));
		var p = s.Problems();
		Check(p.Count(x => x.Contains("Landscape Fixer replaced it", StringComparison.Ordinal)) == 2, "both old steps named: " + string.Join(" | ", p));
		Check(!p.Any(x => x.StartsWith("unknown step", StringComparison.Ordinal)), "not called unknown");
		Check(!StepCatalog.Known("snowfixer") && !StepCatalog.Known("autoblend") && StepCatalog.Known("landscapefixer"), "only Landscape Fixer is a step");
		Equal("Testing MO2.json", LandscapeFixerStep.InstanceFile(@"C:\Modding\Testing MO2\ModOrganizer.ini"), "its settings file is named after the instance's folder");
	}

	static void T_LauncherToolsAreToldThisRunsProfile()
	{
		var had = "{ \"Mo2ProfileName\": \"Another\", \"OutputLocation\": \"E:\\\\Other\", \"UiTheme\": \"system\" }";
		var at = System.Text.Json.Nodes.JsonNode.Parse(ToolRunner.Steps.LauncherStep.PointedAt(had, @"C:\M", "Test", @"C:\G", @"C:\M\mods\Out"))!;
		Equal("Test", (string?)at["Mo2ProfileName"], "this run's profile");
		Equal(@"C:\M\mods\Out", (string?)at["OutputLocation"], "this run's output");
		Equal("system", (string?)at["UiTheme"], "the player's other settings stay");
		Equal("Test", (string?)System.Text.Json.Nodes.JsonNode.Parse(ToolRunner.Steps.LauncherStep.PointedAt(null, @"C:\M", "Test", @"C:\G", @"C:\O"))!["Mo2ProfileName"], "a tool never run before");
	}

	static void T_SynthesisUsesTheGameModOrganizerRuns()
	{
		var at = SynthesisStep.WithDataFolder("{ \"DataPathOverride\": null, \"a\": 1 }", @"C:\Game's Copy\Data");
		Check(at.Contains(@"C:\\Game", StringComparison.Ordinal) && !at.Contains("null", StringComparison.Ordinal), "the folder written, its backslashes escaped");
		var own = "{ \"DataPathOverride\": \"E:\\\\Mine\" }";
		Equal(own, SynthesisStep.WithDataFolder(own, @"C:\Other"), "a data folder the player set stays");
	}

	// ------------------------------------------------------------------ after the run: sleep or shut down (his checklist 461)
	static void T_PowerAfterDecidesAndCanBeCancelled()
	{
		using var f = new FakeMo2();
		var setup = f.WriteSetup(("pandora", new JsonObject()));
		Equal(PowerAfter.Kind.None, PowerAfter.Read(setup), "off unless set");
		Check(PowerAfter.OnlyFinished(setup), "only after a finished run, unless set");
		setup.Root["afterRunPower"] = "shutdown";
		Equal(PowerAfter.Kind.Shutdown, PowerAfter.Read(setup), "shut down");
		Equal(PowerAfter.Kind.Shutdown, PowerAfter.Decide(PowerAfter.Kind.Shutdown, true, RunState.Finished), "a finished run");
		Equal(PowerAfter.Kind.None, PowerAfter.Decide(PowerAfter.Kind.Shutdown, true, RunState.Failed), "a failed run keeps the PC on");
		Equal(PowerAfter.Kind.Sleep, PowerAfter.Decide(PowerAfter.Kind.Sleep, false, RunState.Failed), "unless the setup says any run");
		Equal(PowerAfter.Kind.None, PowerAfter.Decide(PowerAfter.Kind.Sleep, false, RunState.Stopped), "a run the player stopped never does");
		// the notice: the window sees it, the action comes after it; a cancel stops it
		var done = new List<PowerAfter.Kind>();
		var waits = 0;
		Check(PowerAfter.Carry(PowerAfter.Kind.Sleep, f.Data, RunLog.Null, done.Add, _ => {
			if (waits++ == 0) {
				Check(PowerAfter.Look(f.Data) is { Action: "Sleep" }, "the notice is there for the window");
			}
		}), "done after the notice");
		Equal(1, done.Count, "the action once");
		Equal(60, waits, "a minute of notice, a second at a time");
		Equal<PowerAfter.Pending?>(null, PowerAfter.Look(f.Data), "the notice is gone");
		done.Clear();
		waits = 0;
		Check(!PowerAfter.Carry(PowerAfter.Kind.Shutdown, f.Data, RunLog.Null, done.Add, _ => {
			if (++waits == 10) {
				PowerAfter.Cancel(f.Data);
			}
		}), "cancelled");
		Equal(0, done.Count, "nothing done");
		Check(!File.Exists(PowerAfter.CancelFile(f.Data)) && !File.Exists(PowerAfter.File(f.Data)), "nothing left behind");
		// a cancel left from before never stops the next one
		PowerAfter.Cancel(f.Data);
		Check(PowerAfter.Carry(PowerAfter.Kind.Sleep, f.Data, RunLog.Null, done.Add, _ => { }), "an old cancel is not this one's");
		Check(!PowerAfter.Carry(PowerAfter.Kind.None, f.Data, RunLog.Null, done.Add, _ => throw new InvalidOperationException("no wait for nothing")), "none does nothing");
	}

	// ------------------------------------------------------------------ each step's last run (his checklist 450)
	static StepRecord Ran(string a_key, StepState a_state, DateTime? a_started, int a_minutes = 5, string a_note = "") => new() {
		Key = a_key, Name = StepCatalog.Name(a_key), State = a_state, Started = a_started, Ended = a_started?.AddMinutes(a_minutes), Note = a_note,
	};

	static void T_LastRunsFindEachStepsLastRunAndLastFinish()
	{
		var t = new DateTime(2026, 11, 1, 20, 0, 0, DateTimeKind.Local);
		const string setup = @"C:\Setups\Test.json";
		var older = new Journal { SetupFile = setup, Started = t, Folder = "older", Steps = [Ran("texgen", StepState.Done, t), Ran("dyndolod", StepState.Done, t.AddMinutes(6))] };
		var newer = new Journal { SetupFile = setup.ToUpperInvariant(), Started = t.AddDays(1), Folder = "newer",
			Steps = [Ran("texgen", StepState.Failed, t.AddDays(1), 2, "it said no"), Ran("dyndolod", StepState.Skipped, null), Ran("pandora", StepState.Pending, null)] };
		var other = new Journal { SetupFile = @"C:\Setups\Other.json", Started = t.AddDays(2), Steps = [Ran("pandora", StepState.Done, t.AddDays(2))] };
		// the order runs come in does not matter
		var of = LastRuns.Of([older, other, newer], setup);
		Equal(StepState.Failed, of["texgen"].Latest?.State, "texgen's last run is the newer one that failed");
		Equal("older", of["texgen"].LastDone?.RunFolder, "and it last finished in the older run");
		Equal("older", of["dyndolod"].Latest?.RunFolder, "a step the newer run never reached keeps its older run as its last");
		Check(!of.ContainsKey("pandora"), "a step that never started in this setup has no last run (another setup's run is not its)");
		var failed = new LastRuns.Section("texgen", of["texgen"].Latest, of["texgen"].LastDone, []);
		Check(failed.Current && !failed.OutOfDate, "no change since it last finished: its output is current, even after a failed run");
		Check(LastRuns.Line(failed).Contains("failed", StringComparison.Ordinal) && LastRuns.Line(failed).Contains("last finished", StringComparison.Ordinal) &&
		      LastRuns.Line(failed).EndsWith(" - current", StringComparison.Ordinal), "the line: failed last time, finished before, current: " + LastRuns.Line(failed));
		var detail = string.Join("\n", LastRuns.Detail(failed, "Test", ["one line it wrote"]));
		Check(detail.Contains("it said no", StringComparison.Ordinal) && detail.Contains("its output is from then", StringComparison.Ordinal) &&
		      detail.Contains("What it did:\n  one line it wrote", StringComparison.Ordinal), "the card names the failure, the finished run and what it did: " + detail);
		var never = new LastRuns.Section("pandora", null, null, null);
		Equal("never run", LastRuns.Line(never), "a step that never ran");
		var unfinished = new LastRuns.Section("texgen", of["texgen"].Latest, null, null);
		Check(LastRuns.Line(unfinished).EndsWith("it has not finished yet", StringComparison.Ordinal) && !unfinished.Current && !unfinished.OutOfDate,
			"a step that never finished is neither current nor out of date");
	}

	static void T_LastRunsCountOnlyChangesMadeOutsideInTheProfile()
	{
		var since = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Local);
		Mo2Changes.Change Row(int a_min, string a_who, string a_mod, string a_what, params string[] a_profiles) =>
			new(since.AddMinutes(a_min), a_who, a_mod, a_what, a_profiles);
		var rows = new[] {
			Row(-1, Mo2Changes.Outside, "Before", "ticked", "Test"),
			Row(1, Mo2Changes.Outside, "Grass Mod", "added (ticked, under Grass)", "test", "Other"),
			Row(2, "run of Tuesday", "By The Run", "unticked", "Test"),
			Row(3, Mo2Changes.Outside, "Elsewhere", "ticked", "Other"),
			Row(4, Mo2Changes.Outside, "Test", "profile added", "Test"),
			Row(5, Mo2Changes.Outside, "DynDOLOD Output", "unticked", "Test"),
			Row(6, Mo2Changes.Outside, "Tree Mod", "moved within Trees", "Test"),
			Row(7, Mo2Changes.Outside, "Looked At", "ticked", "Test"),
			Row(8, Mo2Changes.Outside, "Looked At", "unticked", "Test"),
		};
		var watched = since.AddDays(-1);
		var changes = LastRuns.ChangesSince(rows, [("New Mod", "removed (was under Misc)")], "Test", since, watched, m => m == "DynDOLOD Output")!;
		Equal("Grass Mod,Tree Mod,New Mod", string.Join(",", changes.Select(c => c.Mod)),
			"only changes made outside the runner, in this profile (any case), after it finished, not the setup's own mods, not one undone again - then the ones not kept yet");
		Equal("Looked At ticked", string.Join(",", LastRuns.ChangesSince(rows.Take(8), [], "Test", since, watched, _ => false)!.Where(c => c.Mod == "Looked At").Select(c => c.Mod + " " + c.What)),
			"a tick not undone still counts");
		Equal("1 added, 1 moved, 1 removed", LastRuns.Counted(changes), "counted by kind");
		Equal<IReadOnlyList<(string, string)>?>(null, LastRuns.ChangesSince(rows, null, "Test", since, watched, _ => false), "a mod list that could not be read now: not known, never \"current\"");
		// the history's edges (CodeRabbit on #24): watched only since after it finished, or the kept rows cut short after it
		// finished - not known; a mod removed and installed again is a new copy (it counts), one installed and removed again is not
		Equal<IReadOnlyList<(string, string)>?>(null, LastRuns.ChangesSince(rows, [], "Test", since, since.AddMinutes(1), _ => false),
			"Mod Organizer watched only since after it finished: not known");
		Equal<IReadOnlyList<(string, string)>?>(null, LastRuns.ChangesSince(rows, [], "Test", since, null, _ => false), "never watched: not known");
		var full = Enumerable.Range(0, Mo2Changes.KeepRows).Select(i => Row(10 + i, Mo2Changes.Outside, "Busy Mod", i % 2 == 0 ? "ticked" : "unticked", "Test")).ToList();
		Equal<IReadOnlyList<(string, string)>?>(null, LastRuns.ChangesSince(full, [], "Test", since, watched, _ => false), "the kept rows cut short after it finished: not known");
		Equal(0, LastRuns.ChangesSince(full, [], "Test", since.AddSeconds(19 * 60 + 30), watched, _ => false)!.Count,
			"kept rows that still reach back past it are judged (an even run of ticks and unticks: back as it was)");
		var installs = new[] {
			Row(9, Mo2Changes.Outside, "Reinstalled", "removed (was under Misc)", "Test"),
			Row(10, Mo2Changes.Outside, "Reinstalled", "added (ticked, under Misc)", "Test"),
			Row(11, Mo2Changes.Outside, "Tried Out", "added (ticked, under Misc)", "Test"),
			Row(12, Mo2Changes.Outside, "Tried Out", "removed (was under Misc)", "Test"),
		};
		Equal("Reinstalled,Reinstalled", string.Join(",", LastRuns.ChangesSince(installs, [], "Test", since, watched, _ => false)!.Select(c => c.Mod)),
			"removed and installed again counts (its files may differ); installed and removed again does not");
		var t = since.AddHours(-1);
		var done = new LastRuns.Ran(t, "run", "DynDOLOD", StepState.Done, t, t.AddMinutes(24), "");
		var stale = new LastRuns.Section("dyndolod", done, done, changes);
		Check(stale.OutOfDate && LastRuns.Line(stale).EndsWith("3 changes in Mod Organizer since", StringComparison.Ordinal), "out of date: " + LastRuns.Line(stale));
		var card = string.Join("\n", LastRuns.Detail(stale, "Test", []));
		Check(card.Contains("Out of date: 3 changes in the profile Test since it finished (1 added, 1 moved, 1 removed), e.g. Grass Mod added", StringComparison.Ordinal) &&
		      card.Contains("Run it again", StringComparison.Ordinal) && !card.Contains("What it did", StringComparison.Ordinal), "the card says what changed: " + card);
		Check(LastRuns.Line(stale with { ChangedSince = null }).EndsWith("changes since: not known", StringComparison.Ordinal), "unknown is said as unknown");
		// its last finished run built into another mod than the setup names now: out of date, whatever changed
		var moved = new LastRuns.Section("dyndolod", done with { Output = "Old Output" }, done with { Output = "Old Output" }, [], "New Output");
		Check(moved.OutOfDate && !moved.Current && moved.BuiltElsewhere == "Old Output", "built into another mod than the setup names: out of date");
		Check(LastRuns.Line(moved).EndsWith("it built into Old Output then; the setup now names New Output", StringComparison.Ordinal), "the line says where: " + LastRuns.Line(moved));
		Check(string.Join("\n", LastRuns.Detail(moved, "Test", [])).Contains("Run it again to build there", StringComparison.Ordinal), "the card says what to do");
		Check(new LastRuns.Section("dyndolod", done, done, [], "New Output").Current, "a run from before outputs were recorded is judged by its changes only");
	}

	static void T_LastRunsReadTheStepsOwnLinesOfItsLog()
	{
		string[] log = [
			"21:00:00  === Synthesis ===",
			"21:00:01  synthesis said hello",
			"21:01:00  === Synthesis done in 1.0 min ===",
			"21:01:01  ",
			"21:01:02  === TexGen ===",
			"21:01:02  ",
			"21:01:03    a detail line nobody needs here",
			"21:01:04  ! a warning it gave",
			"21:02:00  made 1,234 textures",
			"21:06:00  === TexGen done in 5.0 min ===",
			"21:06:01  === DynDOLOD ===",
			"21:06:02  dyndolod line",
		];
		Equal("! a warning it gave|made 1,234 textures|=== TexGen done in 5.0 min ===", string.Join("|", LastRuns.LogLines(log, "TexGen")),
			"its own lines from its start to its done line, no detail lines, nothing of the steps around it");
		Equal("made 1,234 textures|=== TexGen done in 5.0 min ===", string.Join("|", LastRuns.LogLines(log, "TexGen", 2)), "at most the last few");
		string[] failed = ["10:00:00  === DynDOLOD ===", "10:00:05  starting", "10:01:00  !!! DynDOLOD FAILED: out of memory", "10:01:01  the run stopped"];
		Equal("starting|!!! DynDOLOD FAILED: out of memory", string.Join("|", LastRuns.LogLines(failed, "DynDOLOD")), "a failed step ends at its FAILED line");
		Equal(0, LastRuns.LogLines(log, "Pandora").Count, "a step the log never names has no lines");
	}

	static void T_LastRunsOwnModsAreTheSetupsOutputs()
	{
		var setup = Setup.FromJson(new JsonObject {
			["lodOutputs"] = new JsonArray("Old LOD"),
			["steps"] = new JsonArray(
				new JsonObject { ["step"] = "texgen", ["settings"] = new JsonObject { ["output_mod"] = "TexGen Output", ["replaces"] = new JsonArray("Older TexGen") } },
				new JsonObject { ["step"] = "shaders", ["settings"] = new JsonObject { ["caches"] = new JsonArray(
					new JsonObject { ["shader_mod"] = "Shader Mod", ["output_mod"] = "Shader Cache", ["settings_mod"] = "Shader Settings" }) } }),
		});
		var own = LastRuns.OwnMods(setup);
		Check(own.SetEquals(["Old LOD", "TexGen Output", "Older TexGen", "Shader Cache", "Shader Settings"]), "outputs, what they replace, the LOD outputs, each cache and its settings: " + string.Join(", ", own));
		Check(own.Contains("texgen output") && !own.Contains("Shader Mod"), "names in any case; the shader mod itself is the player's (its change matters)");
	}

	// the whole section off the disk: a run record, Mod Organizer's looks, the profile changed after the last look (read only)
	static void T_LastRunsBuildOffTheDisk()
	{
		using var f = new FakeMo2();
		var setup = f.WriteSetup(("texgen", new JsonObject { ["output_mod"] = "Output A" }), ("dyndolod", new JsonObject()));
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var t = DateTime.Now.AddMinutes(-30);
		var j = new Journal { SetupFile = setup.File, Started = t, Ended = t.AddMinutes(6), State = RunState.Finished, Folder = Path.Combine(Runner.RunsDir(f.Data), "2026-11-02 21-00-00 Test"),
			Steps = [Ran("texgen", StepState.Done, t)] };
		Directory.CreateDirectory(j.Folder);
		j.Save();
		File.WriteAllLines(Path.Combine(j.Folder, "log.txt"), [$"{t:HH:mm:ss}  === TexGen ===", $"{t:HH:mm:ss}  made the textures", $"{t:HH:mm:ss}  === TexGen done in 5.0 min ==="]);
		var never = LastRuns.Build(setup, mo2, Journal.All(Runner.RunsDir(f.Data)), f.Data);
		Equal<IReadOnlyList<(string, string)>?>(null, never.Single(s => s.Key == "texgen").ChangedSince, "Mod Organizer never looked at: not known");
		Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside);
		Equal<IReadOnlyList<(string, string)>?>(null, LastRuns.Build(setup, mo2, Journal.All(Runner.RunsDir(f.Data)), f.Data).Single(s => s.Key == "texgen").ChangedSince,
			"watched only since after it finished: not known (what changed before the first look is not on record)");
		// its next run finishes while Mod Organizer is watched
		var t2 = DateTime.Now;
		j.Started = t2;
		j.Steps = [Ran("texgen", StepState.Done, t2, 0)];
		j.Save();
		var looked = LastRuns.Build(setup, mo2, Journal.All(Runner.RunsDir(f.Data)), f.Data);
		var texgen = looked.Single(s => s.Key == "texgen");
		Check(texgen.Current, "nothing changed since the look: current");
		Equal<LastRuns.Ran?>(null, looked.Single(s => s.Key == "dyndolod").Latest, "a step that never ran");
		Equal("made the textures|=== TexGen done in 5.0 min ===", string.Join("|", LastRuns.LogOf(texgen)), "what it did, off its run's log");
		var list = Path.Combine(f.ProfileDir, "modlist.txt");
		var before = File.ReadAllText(list);
		File.WriteAllText(list, before.Replace("+Base Mod", "-Base Mod", StringComparison.Ordinal).Replace("+Output A", "-Output A", StringComparison.Ordinal));
		var pending = LastRuns.Build(setup, mo2, Journal.All(Runner.RunsDir(f.Data)), f.Data).Single(s => s.Key == "texgen");
		Equal("Base Mod unticked", string.Join(",", pending.ChangedSince!.Select(c => c.Mod + " " + c.What)), "a change not looked at yet counts; the setup's own output does not");
		Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside);
		var kept = LastRuns.Build(setup, mo2, Journal.All(Runner.RunsDir(f.Data)), f.Data).Single(s => s.Key == "texgen");
		Equal("Base Mod unticked", string.Join(",", kept.ChangedSince!.Select(c => c.Mod + " " + c.What)), "once kept, it still counts (once)");
		j.Steps[0].Output = "Old Output";
		j.Save();
		var elsewhere = LastRuns.Build(setup, mo2, Journal.All(Runner.RunsDir(f.Data)), f.Data).Single(s => s.Key == "texgen");
		Check(elsewhere.BuiltElsewhere == "Old Output" && elsewhere.OutputNow == "Output A" && elsewhere.OutOfDate, "the output its run recorded against the setup's now");
		j.Steps[0].Output = null;
		j.Save();
		// a kept history that does not read, or one set aside and started over since the step finished: not known
		var changesDir = Mo2Changes.Dir(f.Data, mo2);
		var changes = Path.Combine(changesDir, "changes.json");
		var good = File.ReadAllText(changes);
		File.WriteAllText(changes, "{ not json");
		Equal<IReadOnlyList<(string, string)>?>(null, LastRuns.Build(setup, mo2, Journal.All(Runner.RunsDir(f.Data)), f.Data).Single(s => s.Key == "texgen").ChangedSince,
			"an unreadable history: not known");
		File.WriteAllText(changes, good);
		File.WriteAllText(Path.Combine(changesDir, $"changes unreadable {DateTime.Now.AddMinutes(1):yyyy-MM-dd HH-mm-ss}.json"), "x");
		Equal<IReadOnlyList<(string, string)>?>(null, LastRuns.Build(setup, mo2, Journal.All(Runner.RunsDir(f.Data)), f.Data).Single(s => s.Key == "texgen").ChangedSince,
			"a history started over after the step finished: not known");
		Equal(before.Replace("+Base Mod", "-Base Mod", StringComparison.Ordinal).Replace("+Output A", "-Output A", StringComparison.Ordinal), File.ReadAllText(list),
			"the profile is only read");
		var logs = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
		Equal(string.Join("|", LastRuns.LogOf(texgen)), string.Join("|", LastRuns.LogOf(texgen, logs)), "the same lines when each log is read once");
		Equal(1, logs.Count, "and the log is kept for the next step of that run");
		// a run going (or one that stopped before its last look) switches mods itself: what changed since the look is not known
		var going = new Journal { SetupFile = setup.File, Started = DateTime.Now, State = RunState.Running, Folder = Path.Combine(Runner.RunsDir(f.Data), "2026-11-02 22-00-00 Test") };
		Directory.CreateDirectory(going.Folder);
		going.Save();
		Equal<IReadOnlyList<(string, string)>?>(null, LastRuns.Build(setup, mo2, Journal.All(Runner.RunsDir(f.Data)), f.Data).Single(s => s.Key == "texgen").ChangedSince,
			"not known while a run is unfinished");
	}

	static void T_ToolErrorsSayWhatAKnownErrorMeans()
	{
		var mem = ToolErrors.Explain("dyndolod", "DynDOLOD: EOutOfMemory: OUT OF MEMORY while reading textures");
		Check(mem != null && mem.What.Contains("memory", StringComparison.Ordinal) && mem.Help == "https://dyndolod.info/FAQ", "a sheson tool's out of memory, any letter case, with its FAQ");
		Check(ToolErrors.Explain("synthesis", "Out of memory") == null, "a tool's own error is only matched for that tool");
		Check(ToolErrors.Explain("texgen", "the tool crashed") == null, "an error no page names keeps the tool's own words");
		var master = ToolErrors.Explain("snowfixer", "Snow.esp: Missing Master Skyrim2.esm");
		Check(master != null && master.Help == null, "a missing master is known for every step");
		var lines = ToolErrors.Simple("DynDOLOD", StepState.Failed, "Out of memory", mem);
		Equal(4, lines.Count, "what, what to do, help, the tool's words: " + string.Join(" | ", lines));
		Check(lines[0] == "DynDOLOD did not finish: " + mem!.What && lines[1].StartsWith("What to do: ", StringComparison.Ordinal) &&
			lines[2] == "Help: https://dyndolod.info/FAQ" && lines[3] == "The tool's own words: Out of memory", "the lines in order");
		var plain = ToolErrors.Simple("TexGen", StepState.Failed, "the tool crashed", null);
		Check(plain.Count == 2 && plain[0] == "TexGen did not finish: the tool crashed" && plain[1].Contains("detailed log", StringComparison.Ordinal), "an unknown error points to the detailed log");
		Equal("TexGen was stopped.", ToolErrors.Simple("TexGen", StepState.Stopped, "cancelled", mem).Single(), "a stop is only a stop");
	}

	static void T_RunLogKeepsASimpleLogBesideTheDetailedOne()
	{
		var dir = Path.Combine(Path.GetTempPath(), "SUP simple log " + Guid.NewGuid().ToString("N"));
		try {
			using (var log = new RunLog(dir)) {
				log.Say("the detailed line");
				log.Detail("a detail");
				log.Simple("the simple line");
			}
			var simple = File.ReadAllLines(Path.Combine(dir, RunLog.SimpleName));
			var detailed = File.ReadAllText(Path.Combine(dir, "log.txt"));
			Check(simple.Length == 1 && System.Text.RegularExpressions.Regex.IsMatch(simple[0], @"^\d\d:\d\d  the simple line$"), "the simple log holds only its own line, with the time: " + string.Join(" | ", simple));
			Check(detailed.Contains("the detailed line", StringComparison.Ordinal) && !detailed.Contains("the simple line", StringComparison.Ordinal), "the detailed log keeps its own lines");
			Equal(Path.Combine(dir, RunLog.SimpleName), ToolRunner.App.MainViewModel.LogFile(dir, false), "the window shows the simple log by default");
			Equal(Path.Combine(dir, "log.txt"), ToolRunner.App.MainViewModel.LogFile(dir, true), "and the detailed one when ticked");
			File.Delete(Path.Combine(dir, RunLog.SimpleName));
			Equal(Path.Combine(dir, "log.txt"), ToolRunner.App.MainViewModel.LogFile(dir, false), "a run older than the simple log shows its detailed log");
		} finally {
			Directory.Delete(dir, true);
		}
	}

	static void T_FailedRunsSimpleLogSaysWhatToDo()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("landscapefixer", (ctx, s) => WriteOutput(ctx, "landscapefixer", "Output A", "OutA.esp")));
		StepRegistry.Replace(new FakeStep("pgpatcher", (ctx, s) => throw new StepFailedException("PGPatcher.esp requires master Gone.esm")));
		f.WriteSetup(("landscapefixer", new JsonObject { ["output_mod"] = "Output A" }), ("pgpatcher", []));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Failed, r.State, "failed");
		var simple = File.ReadAllLines(Path.Combine(r.Folder!, RunLog.SimpleName)).Select(l => l[7..]).ToList();
		var all = string.Join(" | ", simple);
		Check(simple[0].Contains("started: 2 step(s)", StringComparison.Ordinal), "the run's start: " + all);
		Check(simple.Any(l => l.StartsWith("1/2 ", StringComparison.Ordinal) && l.EndsWith(" started", StringComparison.Ordinal)) &&
			simple.Any(l => l == "2/2 PGPatcher started"), "each step's start, numbered: " + all);
		Check(simple.Any(l => l.Contains(" finished in ", StringComparison.Ordinal)), "the first step's end: " + all);
		Check(simple.Any(l => l == "PGPatcher did not finish: A plugin needs a master plugin that is not loaded."), "the failure in plain words: " + all);
		Check(simple.Any(l => l.StartsWith("What to do: Install and tick the missing master", StringComparison.Ordinal)), "what to do: " + all);
		Check(simple.Any(l => l == "The tool's own words: PGPatcher.esp requires master Gone.esm"), "the tool's own words: " + all);
		Check(simple[^1].StartsWith("Failed: stopped at PGPatcher", StringComparison.Ordinal), "the verdict last: " + all);
		var detailed = File.ReadAllText(Path.Combine(r.Folder!, "log.txt"));
		Check(detailed.Contains("! what it means: A plugin needs a master", StringComparison.Ordinal), "the detailed log says what it means too");
	}

	// ------------------------------------------------------------------ CodeRabbit on PR #24
	// Synthesis is pointed at the game MO2 runs in every profile whose data folder is unset - null or left out - while a folder the
	// player set is kept, and its type marker stays first (Synthesis's reader needs it there)
	static void T_SynthesisDataFolderIsSetWhereverItIsUnset()
	{
		var text = """
			{ "Version": 2, "Profiles": [
			  { "$type": "SynthesisProfile", "Nickname": "A", "DataPathOverride": null },
			  { "$type": "SynthesisProfile", "Nickname": "B" },
			  { "$type": "SynthesisProfile", "Nickname": "C", "DataPathOverride": "E:\\Theirs\\Data" } ] }
			""";
		var set = JsonNode.Parse(SynthesisStep.WithDataFolder(text, "G:\\Game\\Data"))!;
		Equal("G:\\Game\\Data", set["Profiles"]?[0]?["DataPathOverride"]?.ToString(), "a null one is set");
		Equal("G:\\Game\\Data", set["Profiles"]?[1]?["DataPathOverride"]?.ToString(), "a left-out one is set");
		Equal("E:\\Theirs\\Data", set["Profiles"]?[2]?["DataPathOverride"]?.ToString(), "the player's own is kept");
		Equal("$type", (set["Profiles"]?[1] as JsonObject)?.First().Key, "the type marker stays first");
		var done = SynthesisStep.WithDataFolder(SynthesisStep.WithDataFolder(text, "G:\\Game\\Data"), "G:\\Game\\Data");
		Equal(SynthesisStep.WithDataFolder(text, "G:\\Game\\Data"), done, "a second pass changes nothing");
	}

	// a translation file of another shape (a list, a plain value, "run" not an object) is passed over, never stopping the step
	static void T_LandscapeFixerSkipsOddTranslationFiles()
	{
		var dir = Path.Combine(Path.GetTempPath(), "SUP odd translations " + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try {
			File.WriteAllText(Path.Combine(dir, "list.json"), "[1, 2]");
			File.WriteAllText(Path.Combine(dir, "value.json"), "\"just text\"");
			File.WriteAllText(Path.Combine(dir, "run.json"), "{ \"run\": [\"Generate\"] }");
			File.WriteAllText(Path.Combine(dir, "de.json"), "{ \"run\": { \"generate\": \"Erzeugen\" } }");
			Equal("Erzeugen, Generate, Start", string.Join(", ", LauncherStep.StartButtonNames(dir, ["Generate", "Start"])), "the good file is read, the odd ones passed over");
		} finally {
			try {
				Directory.Delete(dir, true);
			} catch (IOException) {
			}
		}
	}

	// ------------------------------------------------------------------ the audit of 2026-11-07
	// the boxes Mod Organizer shows while closing are answered: its quit question and notices by their words (whole words, so a
	// player's box about "disclosure" is not one), and its one-button notice in any language; another box of the player's is left
	static void T_Mo2CloseBoxesAreKnownByWordsOrOneButton()
	{
		Check(Mo2Control.CloseBox("Are you sure you want to quit?"), "the quit question");
		Check(Mo2Control.CloseBox("Mod Organizer is closing"), "a word's other forms count");
		Check(Mo2Control.CloseBox("Fehler beim Speichern", 1), "a one-button notice in another language");
		Check(!Mo2Control.CloseBox("Read the disclosure first", 2), "a word inside another word is not one");
		Check(!Mo2Control.CloseBox("Install this mod now?", 2), "the player's own question is left alone");
	}

	// Landscape Fixer's start button is found by its own words in every language it ships (its translation files beside it), so a
	// player who runs it in German is understood too; a broken file or no folder falls back to the English names
	static void T_LandscapeFixerKnowsItsButtonInEveryLanguage()
	{
		var dir = Path.Combine(Path.GetTempPath(), "SUP translations " + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try {
			File.WriteAllText(Path.Combine(dir, "en.json"), "{ \"run\": { \"generate\": \"Generate\" } }");
			File.WriteAllText(Path.Combine(dir, "de.json"), "{ \"run\": { \"generate\": \"Generieren\" } }");
			File.WriteAllText(Path.Combine(dir, "xx.json"), "{ not json");
			File.WriteAllText(Path.Combine(dir, "yy.json"), "{ \"run\": { \"generate\": 5 } }");
			var names = ToolRunner.Steps.LauncherStep.StartButtonNames(dir, ["Generate", "Start"]);
			Check(names.Contains("Generieren") && names.Contains("Generate") && names.Contains("Start"), "every language's word: " + string.Join(", ", names));
			Equal(3, names.Count, "each name once, a broken or odd file left out");
			Equal("Generate|Start", string.Join("|", ToolRunner.Steps.LauncherStep.StartButtonNames(Path.Combine(dir, "gone"), ["Generate", "Start"])), "no folder: the English names");
		} finally {
			Directory.Delete(dir, true);
		}
	}

	// a tool Mod Organizer never started: the lines Mod Organizer logged after the request that name the tool, or are its
	// warnings / errors, go into the failure (never the older part of its log)
	static void T_Mo2LogLinesSinceTheRequestAreNamed()
	{
		var file = Path.Combine(Path.GetTempPath(), "SUP mo2 log " + Guid.NewGuid().ToString("N") + ".log");
		try {
			File.WriteAllText(file, "12:00:00.000 [W] an old warning\nLandscapeFixer old line\n");
			var from = new FileInfo(file).Length;
			File.AppendAllText(file, "12:01:00.000 [I] loading plugins\n12:01:02.000 [E] failed to spawn \"LandscapeFixer.exe\": access denied\n12:01:03.000 [I] ready\n");
			var lines = Mo2Control.LogLinesSince(file, from, "LandscapeFixer", "LandscapeFixer.exe");
			Equal(1, lines.Count, "only the new line that matters: " + string.Join(" | ", lines));
			Check(lines[0].Contains("access denied", StringComparison.Ordinal), "the reason itself");
			Equal(2, Mo2Control.LogLinesSince(file, new FileInfo(file).Length + 100, "LandscapeFixer", "LandscapeFixer.exe").Count(l => l.Contains("LandscapeFixer", StringComparison.Ordinal)),
				"a log started again since the request is read whole");
			Equal(0, Mo2Control.LogLinesSince(file + ".gone", 0, "x", "x.exe").Count, "no log, no lines");
		} finally {
			File.Delete(file);
		}
	}

	// his checklist 490: a screen reader hears each step whole in words (its glyph and color say it to the eye), and list rows by
	// their words - never a type's name or a file path
	static void T_ScreenReadersHearEachStepInWords()
	{
		Equal("done in 4m", ToolRunner.App.A11y.StepStatus(true, false, StepState.Done, "4m", "5m", ""), "a finished step");
		Equal("running for 2m", ToolRunner.App.A11y.StepStatus(true, false, StepState.Running, "2m", "", ""), "the running step");
		Equal("failed, PGPatcher.esp requires master Gone.esm", ToolRunner.App.A11y.StepStatus(true, false, StepState.Failed, "1m", "", "PGPatcher.esp requires master Gone.esm"), "a failure with its reason");
		Equal("switched off, usually takes 24m", ToolRunner.App.A11y.StepStatus(false, false, null, "", "24m", ""), "a switched-off step");
		Equal("left out for a new game", ToolRunner.App.A11y.StepStatus(true, true, null, "", "", ""), "a save step on a new game");
		Equal("Bottled", new ToolRunner.App.SetupItem("Bottled", @"C:\Data\Setups\Bottled.json").ToString(), "a setup is read by its name, not its path");
		var row = new ToolRunner.App.HistoryRow("Tue Oct 27  21:02", "Bottled", "1h 41m", "finished: 12 step(s)", true, @"C:\Runs\1").ToString();
		Check(row.Contains("finished", StringComparison.Ordinal) && !row.Contains(@"C:\Runs", StringComparison.Ordinal), "a past run in words: " + row);
		var said = new List<(string, bool)>();
		ToolRunner.App.A11y.Heard = (t, i) => said.Add((t, i));
		try {
			ToolRunner.App.A11y.Say(null!, "Your turn in the game", true);
			ToolRunner.App.A11y.Say(null!, "   ");
			ToolRunner.App.A11y.Say(null!, new string('x', 900));
		} finally {
			ToolRunner.App.A11y.Heard = null;
		}
		Equal(2, said.Count, "blank text is never said");
		Check(said[0].Item2 && said[0].Item1 == "Your turn in the game", "an important line as important");
		Check(said[1].Item1.Length < 620, "a long text is cut for the reader");
	}

	// his checklist 484: a clean removal sends only the program's own data and its own run backups to the Recycle Bin; setups stay
	// when asked; nothing goes while a run did not finish
	static void T_CleanRemovalTakesOnlyItsOwn()
	{
		using var f = new FakeMo2();
		var setup = f.WriteSetup(("pandora", new JsonObject()));
		var setupsDir = Path.GetDirectoryName(f.SetupFile)!;
		var own = Path.Combine(setup.BackupDir, "2026-11-01 20-00-00 " + setup.Name);
		var other = Path.Combine(setup.BackupDir, "2026-11-01 20-00-00 Someone Else");
		var stray = Path.Combine(setup.BackupDir, "my notes");
		foreach (var d in new[] { own, other, stray }) {
			Directory.CreateDirectory(d);
		}
		Check(Files.SamePath(setupsDir, Path.Combine(f.Data, "Setups")), "the fake setup lives in the data folder's Setups");
		// its ProgramData work folder (FasterNGIO's wrapper) - a stand-in, the real one is never touched by a test
		var keepRoots = DataRemoval.WorkRoots;
		var work = Path.Combine(f.Root, "ProgramData SkyrimUtilityPatcher");
		Directory.CreateDirectory(work);
		DataRemoval.WorkRoots = [work, Path.Combine(f.Root, "not there")];
		try {
			CleanRemovalPlans(f, setup, setupsDir, own, other, stray, work);
		} finally {
			DataRemoval.WorkRoots = keepRoots;
		}
	}

	static void CleanRemovalPlans(FakeMo2 f, Setup setup, string setupsDir, string own, string other, string stray, string work)
	{
		Check(DataRemoval.Make(f.Data, true).Remove.Contains(work, StringComparer.OrdinalIgnoreCase), "its ProgramData work folder goes too");
		Check(!DataRemoval.Make(f.Data, true).Remove.Any(p => p.EndsWith("not there", StringComparison.Ordinal)), "a work folder that does not exist is not listed");
		// a setup deleted since: its run record still names its backup, which goes too - a record pointing at a folder not named
		// like a run backup removes nothing (CodeRabbit on #24)
		var goneBackup = Path.Combine(setup.BackupDir, "2026-10-30 19-00-00 Gone Setup");
		Directory.CreateDirectory(Path.Combine(goneBackup, "profile Test"));
		// named like a run backup but holding no copy of a profile: not one, never taken
		var lookalike = Path.Combine(f.Root, "2026-10-30 20-00-00 Lookalike");
		Directory.CreateDirectory(lookalike);
		foreach (var (id, name, backup) in new[] { ("2026-10-30 19-00-00 Gone Setup", "Gone Setup", goneBackup), ("2026-10-30 19-30-00 Odd", "Odd", stray),
			         ("2026-10-30 20-00-00 Lookalike", "Lookalike", lookalike) }) {
			var done = new Journal { Id = id, Folder = Path.Combine(Runner.RunsDir(f.Data), id), SetupFile = Path.Combine(setupsDir, name + ".json"), SetupName = name,
				Profile = "Test", BackupDir = backup, Started = DateTime.Now, State = RunState.Finished, Pid = 0 };
			Directory.CreateDirectory(done.Folder);
			done.Save();
		}
		var all = DataRemoval.Make(f.Data, false);
		Equal<string?>(null, all.Refused, "nothing refused");
		Check(all.Remove.Contains(goneBackup, StringComparer.OrdinalIgnoreCase), "a deleted setup's run backup goes, found through its run record");
		Check(!all.Remove.Contains(lookalike, StringComparer.OrdinalIgnoreCase), "a folder a record names that holds no profile copy stays");
		Equal(all.Remove.Count, all.Remove.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "each folder listed once");
		Check(all.Remove.Contains(own, StringComparer.OrdinalIgnoreCase), "this setup's run backup goes");
		Check(!all.Remove.Contains(other, StringComparer.OrdinalIgnoreCase) && !all.Remove.Contains(stray, StringComparer.OrdinalIgnoreCase), "another setup's backup and a stray folder stay");
		Check(all.Remove.Any(p => Files.SamePath(p, f.Data)), "the data folder goes whole");
		var keep = DataRemoval.Make(f.Data, true);
		Check(keep.Kept.Any(p => Files.SamePath(p, setupsDir)) && !keep.Remove.Any(p => Files.SamePath(p, setupsDir) || Files.SamePath(p, f.Data)), "the setups kept, so the data folder itself stays");
		var binned = new List<string>();
		Equal(0, DataRemoval.Carry(keep, p => { binned.Add(p); return null; }).Count, "all of it went");
		Check(binned.Count == keep.Remove.Count(p => File.Exists(p) || Directory.Exists(p)), "each entry sent once");
		var j = new Journal { Id = "x", Folder = Path.Combine(Runner.RunsDir(f.Data), "2026-11-02 21-00-00 " + setup.Name), SetupFile = f.SetupFile, SetupName = setup.Name,
			Started = DateTime.Now, State = RunState.Running, Pid = 0 };
		Directory.CreateDirectory(j.Folder);
		j.Save();
		var refused = DataRemoval.Make(f.Data, false);
		Check(refused.Refused != null, "a run that did not finish refuses the removal");
		var threw = false;
		try {
			DataRemoval.Carry(refused, _ => throw new InvalidOperationException("never reached"));
		} catch (InvalidOperationException e) when (e.Message == refused.Refused) {
			threw = true;
		}
		Check(threw, "carrying a refused plan says why and sends nothing");
	}

	// his checklist 482: the bug-report zip holds the run's logs, its setup and the versions, with the player's names masked
	// everywhere (paths included) - and a name inside an ordinary word is left alone
	static void T_BugReportMasksThePlayersNames()
	{
		var names = new List<(string, string)> { ("Izzy-PC", "<this PC>"), ("Izzy", "<you>") };
		Equal(@"C:\Users\<you>\Documents on <this PC>; Izzyland stays", BugReport.Mask(@"C:\Users\izzy\Documents on IZZY-PC; Izzyland stays", names), "whole names, any case");
		var realFolder = BugReport.UserFolder;
		try {
			// a short user name is never in the names list, but its user folder is masked in every way a path is written
			BugReport.UserFolder = @"C:\Users\Li";
			Equal(@"<your user folder>\x, <your user folder>\\y, <your user folder>/z, C:\Users\Lisa stays",
				BugReport.Mask(@"C:\Users\Li\x, C:\\Users\\Li\\y, C:/Users/Li/z, C:\Users\Lisa stays", []), "the user folder, however short its name");
		} finally {
			BugReport.UserFolder = realFolder;
		}
		Equal(@"""C:\\Users\\<you>\\x""", BugReport.Mask(@"""C:\\Users\\Jos\u00E9\\x""", [("José", "<you>")]), "a name as a JSON file escapes it");
		using var f = new FakeMo2();
		var setup = f.WriteSetup(("pandora", new JsonObject()));
		var me = Environment.UserName;
		var folder = Path.Combine(Runner.RunsDir(f.Data), "2026-11-02 21-00-00 " + setup.Name);
		Directory.CreateDirectory(folder);
		var j = new Journal { Id = "r", Folder = folder, SetupFile = f.SetupFile, SetupName = setup.Name, Started = DateTime.Now, State = RunState.Failed };
		j.Save();
		File.WriteAllText(Path.Combine(folder, "log.txt"), $"21:00:00  started from C:\\Users\\{me}\\Desktop on {Environment.MachineName}\n");
		File.WriteAllText(Path.Combine(folder, RunLog.SimpleName), "21:00  1/1 Pandora started\n");
		var zip = BugReport.Make(f.Data, Journal.Load(folder), Path.Combine(f.Root, "reports"), "1.0");
		Check(File.Exists(zip) && !File.Exists(zip + ".tmp"), "the zip is there, no half file beside it");
		using var z = System.IO.Compression.ZipFile.OpenRead(zip);
		var got = z.Entries.Select(e => e.FullName).ToList();
		foreach (var want in new[] { "about.txt", "run/log.txt", "run/" + RunLog.SimpleName, "run/journal.json", "setup.json" }) {
			Check(got.Contains(want), $"{want} in the zip: {string.Join(", ", got)}");
		}
		foreach (var e in z.Entries) {
			using var r = new StreamReader(e.Open());
			var text = r.ReadToEnd();
			if (me.Length >= 3) {
				Check(!System.Text.RegularExpressions.Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + System.Text.RegularExpressions.Regex.Escape(me) + @"(?![\p{L}\p{N}])",
					System.Text.RegularExpressions.RegexOptions.IgnoreCase), $"{e.FullName} never names the Windows user");
			}
			if (e.FullName == "run/log.txt") {
				// the user folder itself is masked first (a short user name too), so the path reads as the folder's mask
				var userFolderMasked = text.Contains(@"<your user folder>\Desktop", StringComparison.Ordinal) || text.Contains(@"C:\Users\<you>\Desktop", StringComparison.Ordinal);
				Check(userFolderMasked && text.Contains("<this PC>", StringComparison.Ordinal), "the log's path and PC name masked: " + text);
			}
			if (e.FullName == "about.txt") {
				Check(text.Contains(Brand.Name + " 1.0", StringComparison.Ordinal) && text.Contains("Windows:", StringComparison.Ordinal), "the versions: " + text);
			}
		}
	}

	// his checklist 478: a setup's run backups are listed newest first with their size, and the clean-up keeps the newest few -
	// never another setup's folder or one an unfinished run still needs
	static void T_BackupCleanUpKeepsTheNewest()
	{
		using var f = new FakeMo2();
		var setup = f.WriteSetup(("pandora", new JsonObject()));
		string Run(string a_when) => Path.Combine(setup.BackupDir, a_when + " " + setup.Name);
		foreach (var w in new[] { "2026-11-01 20-00-00", "2026-11-02 20-00-00", "2026-11-03 20-00-00", "2026-11-04 20-00-00" }) {
			Directory.CreateDirectory(Run(w));
			File.WriteAllBytes(Path.Combine(Run(w), "x.bin"), new byte[1000]);
		}
		var stranger = Path.Combine(setup.BackupDir, "2026-11-01 20-00-00 Other Setup");
		Directory.CreateDirectory(stranger);
		var listed = Runner.BackupsOf(setup);
		Equal(4, listed.Count, "only this setup's own run folders");
		Check(listed[0].Dir.Name.StartsWith("2026-11-04", StringComparison.Ordinal) && listed.All(b => b.Bytes == 1000), "newest first, each with its size");
		// the oldest is still needed by a run that did not finish
		var j = new Journal { Id = "u", Folder = Path.Combine(Runner.RunsDir(f.Data), "u"), SetupFile = f.SetupFile, SetupName = setup.Name, Started = DateTime.Now,
			State = RunState.Running, BackupDir = Run("2026-11-01 20-00-00") };
		Directory.CreateDirectory(j.Folder);
		j.Save();
		var removed = Runner.PruneBackupsOf(setup, Runner.RunsDir(f.Data), null, 2, RunLog.Null);
		Equal(1, removed.Count, "one removed: " + string.Join(", ", removed));
		Check(!Directory.Exists(Run("2026-11-02 20-00-00")), "the old one nobody needs went");
		Check(Directory.Exists(Run("2026-11-01 20-00-00")) && Directory.Exists(Run("2026-11-03 20-00-00")) && Directory.Exists(Run("2026-11-04 20-00-00")),
			"the newest two and the one an unfinished run needs stay");
		Check(Directory.Exists(stranger), "another setup's folder is never touched");
		Equal(0, Runner.PruneBackupsOf(setup, Runner.RunsDir(f.Data), null, 0, RunLog.Null).Count, "0 = keep them all");
	}

	// his checklist 475: each step's help is its tool's own page (never a guess), and the save steps get DynDOLOD's clean-save help
	// his checklist 471: a newer version of the program is told at most once a day (one look on the web, then the remembered answer),
	// a version set aside with "Not now" is not told again but the next newer one is, the switch turns the look off, and a look that
	// fails stays quiet
	static void T_ANewVersionIsToldOnceADay()
	{
		var dir = Path.Combine(Path.GetTempPath(), "SUP new version " + Guid.NewGuid().ToString("N"));
		var looks = 0;
		var newest = "1.1";
		var fail = false;
		ProgramUpdate.Probe = _ => {
			++looks;
			return fail ? throw new System.Net.Http.HttpRequestException("offline") : Task.FromResult("v" + newest);
		};
		try {
			var now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
			string? Ask(string a_current, DateTime a_at) => ProgramUpdate.Newer(dir, a_current, a_at, CancellationToken.None).GetAwaiter().GetResult();
			Equal("v1.1", Ask("1.0", now), "a newer version is told");
			Equal(1, looks, "one look on the web");
			Equal("v1.1", Ask("1.0", now.AddHours(5)), "told again from what was remembered");
			Equal(1, looks, "no second look within a day");
			Equal(null, Ask("1.1", now.AddHours(6)), "the version you have is nothing new");
			ProgramUpdate.SetAside(dir, "v1.1");
			Equal(null, Ask("1.0", now.AddHours(7)), "a version set aside is not told again");
			newest = "1.2";
			Equal("v1.2", Ask("1.0", now.AddDays(1).AddHours(1)), "the next newer one is told after a day");
			Equal(2, looks, "a day later it looks again");
			ProgramUpdate.SetOff(dir, true);
			Equal(null, Ask("1.0", now.AddDays(3)), "the switch turns it off");
			Equal(2, looks, "switched off, it never looks");
			ProgramUpdate.SetOff(dir, false);
			fail = true;
			Equal(null, Ask("1.0", now.AddDays(4)), "a failed look says nothing");
			var afterFail = looks;
			Equal(null, Ask("1.0", now.AddDays(4).AddHours(3)), "still nothing the same day");
			Equal(afterFail, looks, "a failed look counts as the day's look - never asked again each start");
			Check(ProgramUpdate.Page.StartsWith("https://", StringComparison.Ordinal), "its download page is a web address");
			// switched off while a look is out on the web: the look's answer neither undoes the switch nor is told (CodeRabbit on #25)
			fail = false;
			newest = "1.3";
			ProgramUpdate.Probe = _ => {
				++looks;
				ProgramUpdate.SetOff(dir, true);
				return Task.FromResult("v" + newest);
			};
			Equal(null, Ask("1.0", now.AddDays(6)), "switched off during the look: nothing told");
			Check(ProgramUpdate.IsOff(dir), "and the switch stays off");
		} finally {
			ProgramUpdate.Probe = null;
			try {
				Directory.Delete(dir, true);
			} catch (IOException) {
			}
		}
	}

	// his checklist 466: a shared setup keeps its steps and options and loses everything of this PC - its Mod Organizer, backup
	// folder, every value holding a path, and the commands it runs before and after a run; it names what the other player sets
	static void T_ASharedSetupCarriesNothingOfThisPc()
	{
		var setup = Setup.FromJson(JsonNode.Parse("""
			{ "name": "My Setup", "mo2": { "exe": "C:\\Games\\MO2\\ModOrganizer.exe", "instance": "D:\\MO2\\Instances\\SSE", "profile": "Bottled" },
			  "backups": "D:\\Backups", "beforeRun": ["del /q C:\\Temp\\x"], "afterRun": [], "idleMinutes": 3,
			  "steps": [ { "key": "pgpatcher", "on": true, "output": "PGPatcher Output", "tool_writes": "C:\\Users\\Someone\\pg" },
			             { "key": "dyndolod", "on": false, "output": "DynDOLOD Output", "args": "-o:\"\\\\nas\\lod\"", "lodWorlds": ["Tamriel", "E:\\x"] } ] }
			""")!.AsObject());
		var r = SetupShare.Share(setup, "1.0");
		var text = r.Setup.ToJsonString();
		Check(!SetupShare.HoldsPath(text.Replace("\\\\", "\\")), "no path of this PC is left: " + text);
		Equal("", r.Setup["mo2"]?["exe"]?.ToString(), "Mod Organizer is the other player's to set");
		Equal("Bottled", r.Setup["mo2"]?["profile"]?.ToString(), "the profile name is kept as a hint");
		Check(r.Setup["beforeRun"] == null && r.Setup["backups"] == null, "no commands, no backup folder");
		Equal(3, r.Setup["idleMinutes"]?.GetValue<int>(), "its options are kept");
		Equal(2, (r.Setup["steps"] as JsonArray)?.Count, "every step is kept");
		Equal("PGPatcher Output", r.Setup["steps"]?[0]?["output"]?.ToString(), "a step's own options are kept");
		Equal(1, (r.Setup["steps"]?[1]?["lodWorlds"] as JsonArray)?.Count, "a list keeps its other items");
		Check(r.Removed.Any(x => x.Contains("before", StringComparison.Ordinal)) && r.Removed.Any(x => x.Contains("tool_writes", StringComparison.Ordinal)),
			"what was taken out is named: " + string.Join("; ", r.Removed));
		Check((r.Setup["shared"]?["setYourself"] as JsonArray)?.Count == r.Removed.Count, "the copy itself says what to set");
		// CodeRabbit on #25: more path forms, names masked in values only, the file name masked, never written over its source,
		// and a PGPatcher settings set named as the other player's to make
		foreach (var p in new[] { @"%ProgramFiles(x86)%\Tool", "//nas/private/tool", @"\\nas\lod\x" }) {
			Check(SetupShare.HoldsPath(p), "a path of this PC: " + p);
		}
		Check(!SetupShare.HoldsPath("https://www.nexusmods.com/skyrimspecialedition/mods/32382") && !SetupShare.HoldsPath("Tamriel"), "a web address or a word is no path");
		var dir = Path.Combine(Path.GetTempPath(), "SUP share " + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try {
			var source = Path.Combine(dir, "Steps Of Izzy (shared).json");
			File.WriteAllText(source, """
				{ "name": "Steps Of Izzy", "mo2": { "profile": "Izzy Profile" },
				  "steps": [ { "step": "pgpatcher", "on": true, "settings": { "settings": "PBR", "output_mod": "Izzy Output" } } ] }
				""");
			var named = Setup.Load(source);
			List<(string Name, string Mask)> names = [("Izzy", "<you>"), ("Steps", "<this PC>")];
			var shared = SetupShare.Share(named, "1.0", names);
			Check(shared.Setup["steps"] is JsonArray { Count: 1 }, "a key that is also a name stays the setup's own key");
			Equal("<you> Profile", shared.Setup["mo2"]?["profile"]?.ToString(), "a name in a value is masked");
			Equal("pgpatcher", shared.Setup["steps"]?[0]?["step"]?.ToString(), "a step's own key is no name");
			Check(shared.Removed.Any(x => x.Contains("PGPatcher settings set \"PBR\"", StringComparison.Ordinal)), "the settings set it names is the other player's to make: " +
				string.Join("; ", shared.Removed));
			var (file, _) = SetupShare.Write(named, dir, "1.0", names);
			Check(!Path.GetFileName(file).Contains("Izzy", StringComparison.OrdinalIgnoreCase), "the file name masked too: " + Path.GetFileName(file));
			Check(File.ReadAllText(source).Contains("Izzy Output", StringComparison.Ordinal), "the setup itself is untouched");
			File.WriteAllText(Path.Combine(dir, "Mine (shared).json"), """{ "name": "Mine", "mo2": { "exe": "C:\\MO2\\ModOrganizer.exe" } }""");
			var mine = Setup.Load(Path.Combine(dir, "Mine (shared).json"));
			var (again, _) = SetupShare.Write(mine, dir, "1.0", names);
			Check(!Files.SamePath(again, mine.File) && File.ReadAllText(mine.File).Contains("ModOrganizer.exe", StringComparison.Ordinal),
				"a setup named \"... (shared)\" shared into its own folder is never written over: " + Path.GetFileName(again));
		} finally {
			try {
				Directory.Delete(dir, true);
			} catch (IOException) {
			}
		}
	}

	// his checklist 457: a save's character is read off the start of its header (the UESP layout), every character with a save is
	// listed most recently played first, and a picked character limits the in-game steps to that character's saves
	static void T_TheCharacterIsReadOffTheSave()
	{
		var dir = Path.Combine(Path.GetTempPath(), "SUP saves " + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try {
			void Save(string a_file, string a_name, DateTime a_at)
			{
				using (var fs = File.Create(Path.Combine(dir, a_file)))
				using (var w = new BinaryWriter(fs, Encoding.Latin1)) {
					w.Write(Encoding.ASCII.GetBytes("TESV_SAVEGAME"));
					w.Write(100u);
					w.Write(12u);
					w.Write(7u);
					w.Write((ushort)a_name.Length);
					w.Write(Encoding.Latin1.GetBytes(a_name));
					w.Write(new byte[64]);
				}
				File.SetLastWriteTimeUtc(Path.Combine(dir, a_file), a_at);
			}
			var t0 = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
			Save("Save1_Lydia.ess", "Lydia", t0);
			Save("Save2_Björn.ess", "Björn", t0.AddHours(1));
			Save("Save3_Lydia.ess", "Lydia", t0.AddHours(2));
			File.WriteAllText(Path.Combine(dir, "broken.ess"), "not a save");
			Equal("Björn", SaveHeader.PlayerName(Path.Combine(dir, "Save2_Björn.ess")), "the name is read, its letters too");
			Equal(null, SaveHeader.PlayerName(Path.Combine(dir, "broken.ess")), "a file that is no save gives no name");
			Equal("Lydia, Björn", string.Join(", ", SaveHeader.Characters(dir)), "every character once, the most recently played first");
		} finally {
			try {
				Directory.Delete(dir, true);
			} catch (IOException) {
			}
		}
	}

	// the in-game steps with a character picked: that character's newest save even when another character played since, a save
	// of another character never loaded (named or an earlier clean save), and the save to play starting from the picked one's
	// (CodeRabbit on #25)
	static void T_TheInGameStepsUseThePickedCharactersSaves()
	{
		using var f = new FakeMo2();
		File.WriteAllText(Path.Combine(f.ProfileDir, "settings.ini"), "[General]\r\nLocalSaves=true\r\n");
		var p = ProfileOf(f);
		var saves = InGame.SavesDir(p);
		Directory.CreateDirectory(saves);
		void Save(string a_file, string a_name, DateTime a_at)
		{
			using (var fs = File.Create(Path.Combine(saves, a_file + ".ess")))
			using (var w = new BinaryWriter(fs, Encoding.Latin1)) {
				w.Write(Encoding.ASCII.GetBytes("TESV_SAVEGAME"));
				w.Write(100u);
				w.Write(12u);
				w.Write(7u);
				w.Write((ushort)a_name.Length);
				w.Write(Encoding.Latin1.GetBytes(a_name));
				w.Write(new byte[64]);
			}
			File.SetLastWriteTimeUtc(Path.Combine(saves, a_file + ".ess"), a_at);
		}
		var t0 = DateTime.UtcNow.AddDays(-2);
		Save("Save2_Bjorn", "Björn", t0.AddHours(1));
		Save("Save3_Lydia", "Lydia", t0.AddHours(2));
		Save("Skyrim Utility Patcher - clean save", "Lydia", t0.AddHours(3));
		Equal("Save3_Lydia", InGame.NewestPlayerSave(p), "no character picked: anyone's newest (never one the runner made)");
		Equal("Save2_Bjorn", InGame.NewestPlayerSave(p, null, "björn"), "the picked character's newest, though another played since");
		Equal("Lydia", InGame.OtherCharacter(p, "Save3_Lydia", "Björn"), "a save of another character is told apart");
		Equal<string?>(null, InGame.OtherCharacter(p, "Save2_Bjorn", "BJÖRN"), "the picked character's own save, in any case");
		Equal<string?>(null, InGame.OtherCharacter(p, "Save3_Lydia", null), "no character picked: no conflict");
		Equal<DateTime?>(null, InGame.OldCleanSave(p, "Björn"), "an earlier clean save of another character is not used");
		Check(InGame.OldCleanSave(p, "Lydia") != null, "one of the picked character's is");
		var player = InGame.NewestPlayerSave(p, null, "Björn");
		Equal("Save2_Bjorn", InGame.PlaySaveFrom(null, null, InGame.OldCleanSave(p, "Björn"), player, InGame.SaveTime(p, player)).Save,
			"the save to play starts from the picked character's save, not the newer clean save of another");
		// the steps themselves pass the picked character on - each case below stops before any game would start
		var setup = f.WriteSetup(("cleansave", new JsonObject()), ("playsave", new JsonObject()));
		setup.Root["character"] = "Björn";
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var runs = Runner.RunsDir(f.Data);
		var j = new Journal { Id = "character", Folder = Path.Combine(runs, "0000 character"), SetupFile = setup.File, SetupName = setup.Name, Profile = "Test", Started = DateTime.Now };
		Directory.CreateDirectory(j.Folder);
		using var log = new RunLog(j.Folder);
		var ctx = new RunContext { Setup = setup, Mo2 = mo2, Profile = p, Control = new Mo2Control(mo2, "Test", log), Log = log, Journal = j,
			Cancel = CancellationToken.None, BackupFolder = Path.Combine(f.Backups, "character") };
		Equal("Björn", InGame.Character(ctx), "the setup's character is read");
		string Refused(Action a_step)
		{
			try {
				a_step();
			} catch (StepFailedException e) {
				return e.Message;
			}
			return "(not refused)";
		}
		var named = new StepSettings(new JsonObject { ["save"] = "Save3_Lydia" });
		Check(Refused(() => InGame.Deactivate(ctx, named)).Contains("is Lydia's, not Björn's", StringComparison.Ordinal), "the clean save refuses a named save of another character");
		Check(Refused(() => InGame.PlaySave(ctx, named)).Contains("is Lydia's, not Björn's", StringComparison.Ordinal), "so does the save to play");
		File.Delete(Path.Combine(saves, "Save2_Bjorn.ess"));
		Equal("no save to load", Refused(() => InGame.PlaySave(ctx, new StepSettings(new JsonObject()))),
			"with no save of the picked character, the save to play never falls back to another character's clean save");
		// the clean save's own pick on the automatic route (DevBench ticked - a stand-in file, nothing runs): no save of the picked
		// character is refused by name before anything starts, never another character's newest taken instead
		var dev = Path.Combine(f.Mods, "DevBench", "SKSE", "Plugins");
		Directory.CreateDirectory(dev);
		File.WriteAllBytes(Path.Combine(dev, "devbench.dll"), []);
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "+DevBench\r\n" + f.Text("modlist.txt"));
		Check(DevBench.InProfile(p), "(DevBench ticked: the automatic route)");
		var auto = Refused(() => InGame.Deactivate(ctx, new StepSettings(new JsonObject())));
		Check(auto.Contains("no save of Björn", StringComparison.Ordinal), "the clean save's own pick keeps to the picked character: " + auto);
	}

	// the log box: only whole new lines, a letter never cut in two, a big log only its last part, and a replaced log started over
	// even when the new one has already grown past where the last look stopped (CodeRabbit on #24)
	static void T_TheLogBoxFollowsAReplacedLog()
	{
		var dir = Path.Combine(Path.GetTempPath(), "SUP log tail " + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		var log = Path.Combine(dir, "log.txt");
		try {
			var tail = new LogTail();
			Equal((null, false), tail.Read(null), "no run, nothing to empty");
			File.WriteAllText(log, "12:00:00  run A begins\n12:00:01  half a li");
			Equal(("12:00:00  run A begins\n", true), tail.Read(log), "a new log: the box emptied, the whole lines shown");
			File.AppendAllText(log, "ne - Björn\n");
			Equal(("12:00:01  half a line - Björn\n", false), tail.Read(log), "the rest of a line once it is whole, its letters too");
			Equal((null, false), tail.Read(log), "nothing new, nothing shown");
			File.WriteAllText(log, "13:30:00  run B begins, a much longer first line than run A had\n13:30:01  next\n");
			Equal(("13:30:00  run B begins, a much longer first line than run A had\n13:30:01  next\n", true), tail.Read(log),
				"a replaced log that already grew past the old place starts over");
			File.WriteAllText(log, "1\n");
			Equal(("1\n", true), tail.Read(log), "a shorter replaced log starts over");
			tail.Restart();
			Equal(("1\n", true), tail.Read(log), "asked again (the other kind of log) - from the start");
			File.WriteAllText(log, string.Concat(Enumerable.Repeat("0123456789abcdef0123456789abcdef0123456789abcdef012345678\n", 5000)));
			var (big, reset) = tail.Read(log);
			Check(reset && big != null && big.Length <= LogTail.Window && big.StartsWith("0123", StringComparison.Ordinal) && big.EndsWith('\n'),
				"a big log: only its last part, from a whole line");
		} finally {
			try {
				Directory.Delete(dir, true);
			} catch (IOException) {
			}
		}
	}

	// the run's command watcher let go of while its look is still going (a Stop's OnStop ending a slow tool): Dispose returns after
	// its wait, the look finishes later and sets the wait's event on the timer's thread - which must still be there (a released
	// event would throw on that thread and end the program), and the look arms no new look (CodeRabbit on #24)
	static void T_TheCommandWatcherLetsGoWhileALookIsHeld()
	{
		var dir = Path.Combine(Path.GetTempPath(), "SUP commands " + Guid.NewGuid().ToString("N"));
		var keep = Commands.DisposeWait;
		Commands.DisposeWait = TimeSpan.FromMilliseconds(200);
		using var entered = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		using var finished = new ManualResetEventSlim();
		var looks = 0;
		try {
			Directory.CreateDirectory(dir);
			using var c = new Commands(new Journal { Folder = dir }, () => { }) {
				OnStop = () => {
					Interlocked.Increment(ref looks);
					entered.Set();
					release.Wait(TimeSpan.FromSeconds(10));
					finished.Set();
				},
			};
			Commands.Send(dir, "stop");
			c.Start();
			Check(entered.Wait(TimeSpan.FromSeconds(5)), "the look began (the stop seen)");
			var sw = Stopwatch.StartNew();
			c.Dispose();
			Check(sw.Elapsed < TimeSpan.FromSeconds(3), $"Dispose returned after its wait, not the look's end ({sw.ElapsedMilliseconds} ms)");
			release.Set();
			Check(finished.Wait(TimeSpan.FromSeconds(5)), "the held look finished");
			// the timer sets the event right after the callback returns - give it time, then the program must still be here
			Thread.Sleep(1500);
			Check(c.Stopped, "the stop was taken");
			Equal(1, Volatile.Read(ref looks), "no new look after Dispose");
		} finally {
			Commands.DisposeWait = keep;
			try {
				Directory.Delete(dir, true);
			} catch (IOException) {
			}
		}
	}

	// the settings search names the other steps whose settings hold its word - in a setting's name, its label or its value, a list's
	// items too - never the step already open (CodeRabbit on #24)
	static void T_TheSettingsSearchNamesOtherSteps()
	{
		var steps = new List<JsonObject> {
			new() { ["step"] = "texgen", ["settings"] = new JsonObject { ["output_mod"] = "TexGen Output" } },
			new() { ["step"] = "dyndolod", ["settings"] = new JsonObject { ["lodWorlds"] = new JsonArray("Tamriel", "Solstheim") } },
			new() { ["step"] = "pandora", ["settings"] = new JsonObject() },
		};
		Equal("TexGen", string.Join(", ", ToolRunner.App.SettingsPanel.StepsWith(steps, "texgen out", -1)), "a value");
		Equal("DynDOLOD", string.Join(", ", ToolRunner.App.SettingsPanel.StepsWith(steps, "solstheim", -1)), "a list's item, any case");
		Equal("TexGen", string.Join(", ", ToolRunner.App.SettingsPanel.StepsWith(steps, "output_mod", -1)), "a setting's own name");
		Equal("", string.Join(", ", ToolRunner.App.SettingsPanel.StepsWith(steps, "TexGen Output", 0)), "never the step already open");
		Equal(0, ToolRunner.App.SettingsPanel.StepsWith(steps, " ", -1).Count, "no word, no steps");
	}

	// a tool that never appeared keeps Mod Organizer's whole log since the request: every line after the place the request was made,
	// the whole log when it was started again meanwhile, the last lines only when there are many
	static void T_AFailedStartKeepsMo2sWholeLog()
	{
		var file = Path.Combine(Path.GetTempPath(), "SUP mo2 log " + Guid.NewGuid().ToString("N") + ".log");
		try {
			File.WriteAllText(file, "[I] before the request\r\n");
			var at = new FileInfo(file).Length;
			File.AppendAllText(file, "[I] RootBuilder: build complete\r\n[E] bridge: MO2 is starting\r\n");
			Equal("[I] RootBuilder: build complete|[E] bridge: MO2 is starting", string.Join("|", Mo2Control.RawLinesSince(file, at)), "every line since, info lines too");
			Equal(3, Mo2Control.RawLinesSince(file, at + 10_000).Count, "a log started again meanwhile: the whole of it");
			Equal("[E] bridge: MO2 is starting", string.Join("|", Mo2Control.RawLinesSince(file, 0, 1)), "only the last lines when there are many");
			Equal(0, Mo2Control.RawLinesSince(file + ".none", 0).Count, "no log, no lines");
		} finally {
			File.Delete(file);
		}
	}

	// a tool whose program lives in a mod switched off in the profile (Mod Organizer cannot start it from there - measured on
	// Landscape Fixer 2026-11-08): that mod is switched on for the tool's run and off again after; one holding a plugin is left to
	// the player and named; a program outside the mods folder, or in a mod that is on, changes nothing
	static void T_AToolInASwitchedOffModIsSwitchedOnForItsRun()
	{
		using var f = new FakeMo2();
		f.Mod("Tool Mod");
		File.WriteAllText(Path.Combine(f.Mods, "Tool Mod", "tool.exe"), "");
		f.Mod("Plugin Tool", ("PluginTool.esp", false, []));
		File.WriteAllText(Path.Combine(f.Mods, "Plugin Tool", "tool2.exe"), "");
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "-Tool Mod\r\n-Plugin Tool\r\n" + f.Text("modlist.txt"));
		string Bin(string a_mod, string a_exe) => Path.Combine(f.Mods, a_mod, a_exe).Replace('\\', '/');
		var ini = Path.Combine(f.Instance, "ModOrganizer.ini");
		File.WriteAllText(ini, File.ReadAllText(ini).Replace("[customExecutables]\r\nsize=1\r\n", "[customExecutables]\r\nsize=3\r\n" +
			$"2\\title=Tool\r\n2\\binary={Bin("Tool Mod", "tool.exe")}\r\n2\\arguments=\r\n3\\title=Tool2\r\n3\\binary={Bin("Plugin Tool", "tool2.exe")}\r\n3\\arguments=\r\n",
			StringComparison.Ordinal));
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var p = ProfileOf(f);
		Equal("Tool Mod", StepKit.ToolMod(mo2, p, "Tool"), "a program inside a switched-off mod");
		Equal<string?>(null, StepKit.ToolMod(mo2, p, "SKSE"), "a program outside the mods folder");
		Equal<string?>(null, StepKit.ToolMod(mo2, p, "No Such Tool"), "an executable Mod Organizer does not have");
		var setup = f.WriteSetup(("pandora", new JsonObject()));
		var j = new Journal { Id = "tool mod", Folder = Path.Combine(Runner.RunsDir(f.Data), "0000 tool mod"), SetupFile = setup.File, SetupName = setup.Name, Profile = "Test", Started = DateTime.Now };
		Directory.CreateDirectory(j.Folder);
		using var log = new RunLog(j.Folder);
		var ctx = new RunContext { Setup = setup, Mo2 = mo2, Profile = p, Control = new Mo2Control(mo2, "Test", log), Log = log, Journal = j,
			Cancel = CancellationToken.None, BackupFolder = Path.Combine(f.Backups, "tool mod") };
		using (StepKit.ToolModOn(ctx, new StepSettings(new JsonObject { ["executable"] = "Tool" }))) {
			Check(p.IsOn("Tool Mod"), "on for the tool's run");
			Equal<string?>(null, StepKit.ToolMod(mo2, p, "Tool"), "(and then no longer a switched-off mod)");
		}
		Check(!p.IsOn("Tool Mod"), "off again after");
		var refused = "";
		try {
			StepKit.ToolModOn(ctx, new StepSettings(new JsonObject { ["executable"] = "Tool2" }))?.Dispose();
		} catch (StepFailedException e) {
			refused = e.Message;
		}
		Check(refused.Contains("\"Plugin Tool\"", StringComparison.Ordinal) && !p.IsOn("Plugin Tool"), "a tool mod with a plugin is named and left off: " + refused);
		Equal<Toggle?>(null, StepKit.ToolModOn(ctx, new StepSettings(new JsonObject { ["executable"] = "SKSE" })), "nothing to switch for a program outside the mods folder");
		Equal("Plugin Tool", string.Join(",", StepKit.ToolMods(ctx, new StepSettings(new JsonObject { ["executable"] = "Tool2", ["enable_during"] = new JsonArray("Plugin Tool") }))),
			"a mod the setup itself names for the step is its own choice, plugin or not - and still switched on, for the steps that do not read that list");
	}

	// his checklist 464: before the first step that loads a save the player's saves are copied into the run's backup - saves and co-saves,
	// nothing else, once a run, the original untouched; the setup can switch it off; no room, no folder, a Stop or a copy that cannot be
	// made never stops the run or leaves a part behind
	static void T_TheSavesAreCopiedBeforeAnInGameStep()
	{
		using var f = new FakeMo2();
		File.WriteAllText(Path.Combine(f.ProfileDir, "settings.ini"), "[General]\r\nLocalSaves=true\r\n");
		var p = ProfileOf(f);
		var saves = InGame.SavesDir(p);
		Directory.CreateDirectory(saves);
		File.WriteAllText(Path.Combine(saves, "Save1.ess"), "save");
		File.WriteAllText(Path.Combine(saves, "Save1.skse"), "co-save");
		File.WriteAllText(Path.Combine(saves, "notes.txt"), "not a save");
		var setup = f.WriteSetup(("cleansave", new JsonObject()));
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var j = new Journal { Id = "saves", Folder = Path.Combine(Runner.RunsDir(f.Data), "0000 saves"), SetupFile = setup.File, SetupName = setup.Name, Profile = "Test", Started = DateTime.Now };
		Directory.CreateDirectory(j.Folder);
		using var log = new RunLog(j.Folder);
		var backup = Path.Combine(f.Backups, "saves run");
		var ctx = new RunContext { Setup = setup, Mo2 = mo2, Profile = p, Control = new Mo2Control(mo2, "Test", log), Log = log, Journal = j,
			Cancel = CancellationToken.None, BackupFolder = backup };
		var copy = InGame.CopySaves(ctx);
		Check(copy != null && File.Exists(Path.Combine(copy, "Save1.ess")) && File.Exists(Path.Combine(copy, "Save1.skse")), "the saves and their co-saves are copied");
		Check(!File.Exists(Path.Combine(copy!, "notes.txt")), "nothing else");
		Check(File.Exists(Path.Combine(saves, "Save1.ess")), "the original stays");
		Check(!Directory.Exists(copy + ".part"), "no part left");
		File.WriteAllText(Path.Combine(saves, "Save2.ess"), "made in the run's game");
		Equal(copy, InGame.CopySaves(ctx), "once a run");
		Check(!File.Exists(Path.Combine(copy!, "Save2.ess")), "a later save is no part of the copy");
		setup.Root["copySaves"] = false;
		Directory.Delete(copy!, true);
		Equal<string?>(null, InGame.CopySaves(ctx), "switched off in the setup: nothing copied");
		setup.Root["copySaves"] = true;
		// not enough room beside what the run needs: said, nothing copied, and the run goes on
		var keepFree = InGame.FreeBytes;
		try {
			InGame.FreeBytes = _ => 0;
			Equal<string?>(null, InGame.CopySaves(ctx), "no room: no copy");
			Check(!Directory.Exists(Path.Combine(backup, InGame.SavesCopyFolder + ".part")), "no part left");
		} finally {
			InGame.FreeBytes = keepFree;
		}
		// a Stop during the copy leaves no part behind
		using var stop = new CancellationTokenSource();
		stop.Cancel();
		var stopping = new RunContext { Setup = setup, Mo2 = mo2, Profile = p, Control = ctx.Control, Log = log, Journal = j, Cancel = stop.Token, BackupFolder = backup };
		Throws<OperationCanceledException>(() => InGame.CopySaves(stopping), "a Stop ends the copy");
		Check(!Directory.Exists(Path.Combine(backup, InGame.SavesCopyFolder + ".part")), "and leaves no part behind");
		// the backup folder cannot be made (a file stands where it should be): said, and the run goes on
		Directory.Delete(backup, true);
		File.WriteAllText(backup, "a file in the way");
		Equal<string?>(null, InGame.CopySaves(ctx), "a copy that cannot be made never stops the run");
		File.Delete(backup);
		// no saves folder: said, and the run goes on
		Directory.Delete(saves, true);
		Equal<string?>(null, InGame.CopySaves(ctx), "no saves folder: nothing to copy");
		Check(Files.ReadSharedText(log.TextPath!).Contains("no saves folder", StringComparison.Ordinal), "and the log says so");
	}

	// his checklist 438: Steam's own record of the game says whether it is updating it (refused), has an update waiting (a warning)
	// or is done (nothing); a copy outside a steamapps folder, or with no record, has nothing to read
	static void T_SteamUpdatingTheGameIsSeen()
	{
		var root = Path.Combine(Path.GetTempPath(), "SUP steam " + Guid.NewGuid().ToString("N"));
		var game = Path.Combine(root, "steamapps", "common", "Skyrim Special Edition");
		Directory.CreateDirectory(game);
		var manifest = Path.Combine(root, "steamapps", "appmanifest_489830.acf");
		try {
			Equal(0, SystemChecks.SteamUpdate(game, true).Count(), "no record: nothing to read");
			void State(int a_flags) => File.WriteAllText(manifest, "\"AppState\"\n{\n\t\"appid\"\t\t\"489830\"\n\t\"installdir\"\t\t\"Skyrim Special Edition\"\n\t\"StateFlags\"\t\t\"" + a_flags + "\"\n}\n");
			State(4);
			Equal(0, SystemChecks.SteamUpdate(game, true).Count(), "fully installed: nothing to say");
			State(1048580);
			Check(SystemChecks.SteamUpdate(game, true).Single().Blocking, "downloading: the run does not start");
			var closed = SystemChecks.SteamUpdate(game, false).Single();
			Check(!closed.Blocking && closed.Text.Contains("half written", StringComparison.Ordinal), "Steam closed in the middle of it: a warning that says so");
			State(1026);
			Check(!SystemChecks.SteamUpdate(game, true).Single().Blocking, "an update queued: a warning");
			State(1542);
			Check(!SystemChecks.SteamUpdate(game, true).Single().Blocking, "an update paused on purpose: a warning, the run may start");
			State(6);
			Check(!SystemChecks.SteamUpdate(game, true).Single().Blocking, "an update waiting: a warning");
			Equal(0, SystemChecks.SteamUpdate(Path.Combine(root, "Game Root"), true).Count(), "a copy outside steamapps: nothing to read");
			Directory.CreateDirectory(Path.Combine(root, "steamapps", "common", "Other Copy"));
			Equal(0, SystemChecks.SteamUpdate(Path.Combine(root, "steamapps", "common", "Other Copy"), true).Count(), "another folder in the same library: the record is not of it");
			Equal(1, SystemChecks.SteamUpdate(game + Path.DirectorySeparatorChar, true).Count(), "a trailing slash reads the same (the update still waiting)");
		} finally {
			Directory.Delete(root, true);
		}
	}

	// his checklist 458: what holds a teleport - a beast form or a mount that cannot be left stops the step in the end; a kill move, a
	// conversation, a menu or a scene are waited out and go on, so a wrong reading never fails a run; nothing held = no wait
	static void T_WhatHoldsATeleportIsKnown()
	{
		InGame.PlayerState S(bool a_mounted = false, bool a_beast = false, bool a_menu = false, bool a_controlsOff = false, bool a_busy = false) =>
			new(a_mounted, a_beast, a_menu, a_controlsOff, a_busy);
		Check(InGame.WhatHoldsTeleport(S()) == null, "free to go");
		Check(InGame.WhatHoldsTeleport(S(a_beast: true)) is { Stop: true }, "a beast form stops in the end");
		Check(InGame.WhatHoldsTeleport(S(a_mounted: true)) is { Stop: true }, "a mount that cannot be left stops in the end");
		Check(InGame.WhatHoldsTeleport(S(a_menu: true)) is { Stop: false }, "a conversation or menu is waited out and goes on");
		Check(InGame.WhatHoldsTeleport(S(a_controlsOff: true)) is { Stop: false }, "a scene too");
		Check(InGame.WhatHoldsTeleport(S(a_busy: true)) is { Stop: false }, "a kill move or bleeding out too");
		Check(InGame.WhatHoldsTeleport(S(a_beast: true, a_menu: true))?.Why.Contains("werewolf", StringComparison.Ordinal) == true, "the most serious is named");
		Check(InGame.IsBeastRace("0x000CDD84", null), "the werewolf race");
		Check(InGame.IsBeastRace("0x0200283a", "0x0200283A"), "the vampire lord race as the game resolved it, any letter case");
		Check(!InGame.IsBeastRace("0x00013746", "0x0200283A") && !InGame.IsBeastRace("0x0300283A", "0x0200283A") && !InGame.IsBeastRace(null, null),
			"a normal race, the same low digits at another slot, or nothing are no beast");
		// the wait itself
		var keep = (InGame.TeleportWait, InGame.TeleportShortWait, InGame.TeleportPoll, InGame.DismountEvery);
		try {
			InGame.TeleportWait = InGame.TeleportShortWait = TimeSpan.FromMilliseconds(30);
			InGame.TeleportPoll = TimeSpan.FromMilliseconds(5);
			InGame.DismountEvery = TimeSpan.FromHours(1);
			var asked = 0;
			void Dismount() => ++asked;
			Equal<string?>(null, InGame.WaitUntilFree(() => S(), Dismount, RunLog.Null, CancellationToken.None), "nothing holds it: go on");
			var reads = 0;
			Equal<string?>(null, InGame.WaitUntilFree(() => S(a_mounted: ++reads == 1), Dismount, RunLog.Null, CancellationToken.None), "off the mount: go on");
			Equal(1, asked, "asked to get off once");
			Check(InGame.WaitUntilFree(() => S(a_beast: true), Dismount, RunLog.Null, CancellationToken.None)?.Contains("werewolf", StringComparison.Ordinal) == true, "a form that does not end: the step stops, named");
			asked = 0;
			Check(InGame.WaitUntilFree(() => S(a_mounted: true), Dismount, RunLog.Null, CancellationToken.None)?.Contains("mount", StringComparison.Ordinal) == true, "a mount that cannot be left: the same");
			Equal(1, asked, "and it is not asked again and again");
			Equal<string?>(null, InGame.WaitUntilFree(() => S(a_menu: true), Dismount, RunLog.Null, CancellationToken.None), "a menu that does not close: waited out, then on");
			// holds that keep taking turns end too
			InGame.TeleportWait = InGame.TeleportShortWait = TimeSpan.FromMilliseconds(200);
			var turn = 0;
			var turns = System.Diagnostics.Stopwatch.StartNew();
			InGame.WaitUntilFree(() => ++turn % 2 == 0 ? S(a_menu: true) : S(a_mounted: true), Dismount, RunLog.Null, CancellationToken.None);
			Check(turns.Elapsed >= InGame.TeleportWait * 2, "only the wait's own end stops holds that take turns");
			// each hold has its own limit: a menu after a long form still gets its time
			InGame.TeleportWait = TimeSpan.FromSeconds(5);
			InGame.TeleportShortWait = TimeSpan.FromMilliseconds(30);
			var clock = System.Diagnostics.Stopwatch.StartNew();
			Equal<string?>(null, InGame.WaitUntilFree(() => clock.ElapsedMilliseconds < 60 ? S(a_beast: true) : clock.ElapsedMilliseconds < 80 ? S(a_menu: true) : S(), Dismount, RunLog.Null, CancellationToken.None), "both end: go on");
			Check(clock.ElapsedMilliseconds >= 80, "the menu was waited out, not passed on the form's time");
			using var stop = new CancellationTokenSource();
			stop.Cancel();
			Throws<OperationCanceledException>(() => InGame.WaitUntilFree(() => S(a_menu: true), Dismount, RunLog.Null, stop.Token), "a Stop ends the wait");
		} finally {
			(InGame.TeleportWait, InGame.TeleportShortWait, InGame.TeleportPoll, InGame.DismountEvery) = keep;
		}
	}

	// his checklist 459: a save that did not happen is said in the player's words, and names the way forward (the real proof of the force
	// is in-game, on the full run's list)
	static void T_ASaveThatDidNotHappenIsSaid()
	{
		var first = InGame.SaveNotMade("Skyrim Utility Patcher - clean save", false);
		Check(first.Contains("clean save", StringComparison.Ordinal) && first.Contains("may be blocking saves", StringComparison.Ordinal) && first.Contains("Force the save", StringComparison.Ordinal),
			"names the save, the likely cause and the setting: " + first);
		var forced = InGame.SaveNotMade("x", true);
		Check(forced.Contains("switched back on", StringComparison.Ordinal) && !forced.Contains("Force the save", StringComparison.Ordinal) && forced.Contains("guided", StringComparison.Ordinal),
			"after the force: what is left to do, not the same advice again: " + forced);
		Check(InGame.SaveWait <= TimeSpan.FromMinutes(5), "a blocked save is said, never waited on for long");
	}

	// the old timed runs' tasks are looked for once per data folder (Windows' task list took 35 s at every start); only a clean pass
	// leaves the marker
	static void T_OldTimedRunsAreLookedForOnce()
	{
		var dir = Path.Combine(Path.GetTempPath(), "SUP once " + Guid.NewGuid().ToString("N"));
		var looks = 0;
		try {
			Schedule.RemoveLeftoversOnce(dir, () => { ++looks; return false; });
			Equal(1, looks, "the first start looks");
			Check(!File.Exists(Path.Combine(dir, Schedule.ClearedMarker)), "a pass that was not clean leaves no marker");
			Schedule.RemoveLeftoversOnce(dir, () => { ++looks; return true; });
			Equal(2, looks, "so the next start looks again");
			Check(File.Exists(Path.Combine(dir, Schedule.ClearedMarker)), "a clean pass leaves it");
			Schedule.RemoveLeftoversOnce(dir, () => { ++looks; return true; });
			Equal(2, looks, "and no start looks after that");
		} finally {
			if (Directory.Exists(dir)) {
				Directory.Delete(dir, true);
			}
		}
	}

	// his checklist 456: only what each tool's own document says - DynDOLOD.esp and Occlusion.esp last, Occlusion.esp after DynDOLOD.esp,
	// PG_1.esp last but for those, the Bashed Patch before them, DynDOLOD.esm the last master; Synthesis, PGPatcher.esp and Landscape
	// Fixer have no documented place; always a warning
	static void T_OutputPluginsAreCheckedAgainstTheirDocuments()
	{
		List<Issue> C(params string[] a_order) => PluginPlaces.Check(a_order);
		Equal(0, C("Skyrim.esm", "DynDOLOD.esm", "Some.esp", "Bashed Patch, 0.esp", "Synthesis.esp", "PG_1.esp", "DynDOLOD.esp", "Occlusion.esp").Count, "LOOT's own order is clean");
		Equal(0, C("Some.esp", "Other.esp").Count, "none of them ticked: nothing to say");
		Equal(0, C("PGPatcher.esp", "LandscapeFixer.esp", "Synthesis.esp", "Some.esp").Count, "no documented place: never warned");
		Check(C("DynDOLOD.esp", "Occlusion.esp").Count == 0 && C("Occlusion.esp", "DynDOLOD.esp").Single().Text.Contains("right after", StringComparison.Ordinal), "Occlusion.esp after DynDOLOD.esp");
		var late = C("DynDOLOD.esp", "Late Mod.esp");
		Check(late.Single().Text.Contains("Late Mod.esp", StringComparison.Ordinal) && !late[0].Blocking, "a plugin after DynDOLOD.esp is named, and only warned about");
		Check(C("PG_1.esp", "Late Mod.esp").Single().Text.Contains("PGPatcher", StringComparison.Ordinal), "a plugin after PG_1.esp");
		Equal(0, C("PG_1.esp", "PG_2.esp", "DynDOLOD.esp", "Occlusion.esp").Count, "PG plugins and the DynDOLOD outputs after them are fine");
		Check(C("PG_1.esp", "Bashed Patch, 0.esp").Any(i => i.Text.StartsWith("Bashed Patch", StringComparison.Ordinal)), "the Bashed Patch after one that must load last");
		Equal(0, C("Bashed Patch, 0.esp", "Some.esp").Count, "a plugin after the Bashed Patch may say it must load last: not warned");
		Check(C("DynDOLOD.esm", "Late.esm").Single().Text.Contains("Late.esm", StringComparison.Ordinal), "a master after DynDOLOD.esm");
		Equal(0, C("Early.esm", "DynDOLOD.esm", "Some.esp").Count, "plugins after DynDOLOD.esm are fine");
	}

	static void T_EveryToolStepHasItsHelpPage()
	{
		foreach (var s in StepCatalog.All.Where(s => s.Key is not ("shaders" or "xlodgen"))) {
			var help = ToolRunner.App.SettingsPanel.HelpPage(s.Key);
			Check(help is { } h && h.Url.StartsWith("https://", StringComparison.Ordinal) && !h.Url.Contains("?tab=files", StringComparison.Ordinal), $"{s.Name} has its page: {help?.Url}");
		}
		Equal("https://dyndolod.info/Help/Clean-Save", ToolRunner.App.SettingsPanel.HelpPage("cleansave")?.Url, "the clean save's help");
	}

	// his checklist 470: a game that closed under an in-game step names the crash log written since the run began - Crash Logger's
	// and Trainwreck's in the SKSE folder, .NET Script Framework's in overwrite - and never an older one
	static void T_AGameCrashNamesItsCrashLog()
	{
		using var f = new FakeMo2();
		var skse = Path.Combine(f.Root, "SKSE");
		var trainwreck = Path.Combine(skse, "Crashlogs");
		var nsf = Path.Combine(f.Overwrite, "NetScriptFramework", "Crash");
		foreach (var d in new[] { skse, trainwreck, nsf }) {
			Directory.CreateDirectory(d);
		}
		var since = DateTime.Now.AddMinutes(-10);
		var old = Path.Combine(skse, "crash-2026-01-01-00-00-00.log");
		File.WriteAllText(old, "old");
		File.SetLastWriteTime(old, since.AddHours(-1));
		Equal<string?>(null, InGame.CrashLog(skse, f.Overwrite, since), "a crash log from before the run is not this run's");
		Check(InGame.CrashNote(skse, f.Overwrite, since).Contains("no crash log", StringComparison.Ordinal), "said when there is none");
		var tw = Path.Combine(trainwreck, "crash-2026-11-07-21-00-00.log");
		File.WriteAllText(tw, "x");
		File.SetLastWriteTime(tw, since.AddMinutes(2));
		Equal<string?>(tw, InGame.CrashLog(skse, f.Overwrite, since), "Trainwreck's, in its own folder");
		var net = Path.Combine(nsf, "Crash_2026_11_07_21_05_00.txt");
		File.WriteAllText(net, "x");
		File.SetLastWriteTime(net, since.AddMinutes(5));
		Equal<string?>(net, InGame.CrashLog(skse, f.Overwrite, since), "the newest wins (.NET Script Framework's, in overwrite)");
		Check(InGame.CrashNote(skse, f.Overwrite, since).EndsWith(net, StringComparison.Ordinal), "the note names it");
	}

	// his checklist 455: an older output of the same tool still ticked would win over the fresh one (a new output sits at the bottom
	// of the left pane) - named before the run; once the step replaces it (kept off by the run) it is no longer named
	static void T_ASecondOutputOfTheSameToolIsNamed()
	{
		using var f = new FakeMo2();
		f.Mod("Old DynDOLOD");
		File.WriteAllText(Path.Combine(f.Mods, "Old DynDOLOD", "DynDOLOD.esp"), "x");
		f.Mod("Old Grass");
		Directory.CreateDirectory(Path.Combine(f.Mods, "Old Grass", "grass"));
		File.WriteAllText(Path.Combine(f.Mods, "Old Grass", "grass", "Tamrielx0000y0000.cgid"), "x");
		f.Mod("Unticked DynDOLOD");
		File.WriteAllText(Path.Combine(f.Mods, "Unticked DynDOLOD", "DynDOLOD.esm"), "x");
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "+Old DynDOLOD\r\n+Old Grass\r\n-Unticked DynDOLOD\r\n" + f.Text("modlist.txt"));
		var setup = f.WriteSetup(("dyndolod", new JsonObject { ["output_mod"] = "DynDOLOD Output" }), ("grass", new JsonObject { ["output_mod"] = "Grass Out" }));
		var mo2 = Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance);
		var states = ProfileOf(f).States();
		var dyn = Duplicates.Check(setup, mo2, states, "dyndolod").ToList();
		Equal(1, dyn.Count, "one rival: " + string.Join(" | ", dyn.Select(i => i.Text)));
		Check(dyn[0].Text.Contains("Old DynDOLOD", StringComparison.Ordinal) && dyn[0].Text.Contains("DynDOLOD.esp", StringComparison.Ordinal) && !dyn[0].Blocking, "named with its file, as a note");
		Check(Duplicates.Check(setup, mo2, states, "grass").Any(i => i.Text.Contains("Old Grass", StringComparison.Ordinal)), "a grass cache of its kind counts too");
		// the step replaces it: the run keeps it off, so it is no rival
		var replaced = f.WriteSetup(("dyndolod", new JsonObject { ["output_mod"] = "DynDOLOD Output", ["replaces"] = new JsonArray("Old DynDOLOD") }));
		Equal(0, Duplicates.Check(replaced, mo2, states, "dyndolod").Count(), "a replaced mod is not named");
		Equal<string?>(null, Duplicates.Holds(Path.Combine(f.Mods, "Old DynDOLOD"), "pandora"), "a step with no own file is never guessed");
	}

	// the window knows a run is alive by its engine's process name: since the rename of 2026-11-03 it knew only the old names, so
	// every real run read as dead - Stop now, Stop after, a guided step's Done and the close guard did nothing (his finds of
	// 2026-11-04). The tests' own process matched, which hid it: here the real names are asked, and the program's name is held
	// to its exe's name so a future rename cannot split them again
	static void T_TheEngineIsKnownByItsRealName()
	{
		foreach (var n in new[] { "Skyrim Utility Patcher", "skyrim utility patcher", "MO2 Tool Runner", "Patcher Unification Project", "Runner.Tests" }) {
			Check(Journal.IsRunnerName(n), $"\"{n}\" is the program");
		}
		foreach (var n in new[] { "ModOrganizer", "Skyrim Utility Patcher Helper", "SkyrimSE", "" }) {
			Check(!Journal.IsRunnerName(n), $"\"{n}\" is not the program");
		}
		Equal(Brand.Name, typeof(ToolRunner.App.App).Assembly.GetName().Name, "the program's name is its exe's name");
		Check(Journal.IsRunner(Environment.ProcessId), "this process (the tests) counts as the program");
	}

	// a setup keeps its newest 100 run records; an unfinished one and the running one are never removed, another setup's never
	static void T_OldRunRecordsAreTrimmed()
	{
		using var f = new FakeMo2();
		var runs = Runner.RunsDir(f.Data);
		Journal Make(int a_day, int a_minute, string a_setup, RunState a_state)
		{
			var id = $"2026-{1 + a_day / 28:00}-{1 + a_day % 28:00} 10-{a_minute:00}-00 x";
			var j = new Journal { Id = id, Folder = Path.Combine(runs, id), SetupFile = a_setup, SetupName = "x", Started = DateTime.Now, State = a_state };
			Directory.CreateDirectory(j.Folder);
			j.Save();
			return j;
		}
		var oldestUnfinished = Make(0, 0, f.SetupFile, RunState.Running);
		for (var i = 1; i <= 104; ++i) {
			Make(i, 0, f.SetupFile, RunState.Finished);
		}
		var other = Make(0, 30, @"C:\Other.json", RunState.Finished);
		var removed = Runner.PruneRunRecords(runs, f.SetupFile, null, 100, RunLog.Null);
		Equal(4, removed.Count, "105 records of this setup, 100 kept, the unfinished one kept too");
		Check(Directory.Exists(oldestUnfinished.Folder), "the run that did not finish stays (its put-back needs it)");
		Check(Directory.Exists(other.Folder), "another setup's record stays");
		Equal(101, Journal.All(runs).Count(j => j.SetupFile == f.SetupFile), "100 + the unfinished one");
	}

	// a PC low on memory is told before a run, never stopped (the LOD tools may still manage)
	static void T_LowMemoryIsToldNotBlocked()
	{
		var small = SystemChecks.Memory((6, 3)).ToList();
		Check(small.Count == 1 && !small[0].Blocking && small[0].Text.Contains("6 GB", StringComparison.Ordinal), "a 6 GB PC is told: " + string.Join(" | ", small.Select(i => i.Text)));
		var busy = SystemChecks.Memory((32, 1.5)).ToList();
		Check(busy.Count == 1 && !busy[0].Blocking && busy[0].Text.Contains("free", StringComparison.Ordinal), "little free memory is told");
		Equal(0, SystemChecks.Memory((32, 16)).Count(), "a roomy PC hears nothing");
		Equal(0, SystemChecks.Memory(null).Count(), "no reading, no note");
		Check(SystemChecks.MemoryProbe() is { } m && m.Total > 0, "the stand-in reads (the real probe is Windows' own)");
	}

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	static extern uint GetGuiResources(nint a_process, uint a_flags);

	// what this process holds: the managed bytes that survive a full, compacting collection (GC.GetTotalMemory read negative on .NET
	// 10 here - the collector's own record of what it kept is used instead), kernel handles, GDI and USER objects
	static (long Managed, int Handles, uint Gdi, uint User) Held()
	{
		GC.Collect(2, GCCollectionMode.Forced, true, true);
		GC.WaitForPendingFinalizers();
		GC.Collect(2, GCCollectionMode.Forced, true, true);
		var kept = GC.GetGCMemoryInfo(GCKind.FullBlocking).PromotedBytes;
		using var me = System.Diagnostics.Process.GetCurrentProcess();
		me.Refresh();
		return (kept, me.HandleCount, GetGuiResources(me.Handle, 0), GetGuiResources(me.Handle, 1));
	}

	// the memory audit of 2026-11-07: every hot path a long run and the open window loop over (the process looks, the window and
	// box reads through UI Automation, the run records, a process tree's CPU, the run's command watcher) run thousands of times;
	// what the process holds afterwards must come back to where it started - a leak of a handle, a GDI / USER object or managed
	// memory shows here as growth that a collection does not take back. Set SUP_SOAK=<rounds> for a longer soak.
	static void T_HotPathsLeakNothing()
	{
		var rounds = int.TryParse(Environment.GetEnvironmentVariable("SUP_SOAK"), out var r) && r > 0 ? r : 600;
		using var f = new FakeMo2();
		var runs = Runner.RunsDir(f.Data);
		for (var i = 0; i < 40; ++i) {
			var id = $"2026-11-{1 + i % 28:00} 1{i % 10}-00-00 soak";
			var j = new Journal { Id = id, Folder = Path.Combine(runs, id), SetupFile = f.SetupFile, SetupName = "soak", Started = DateTime.Now, State = RunState.Finished,
				Steps = [new StepRecord { Key = "dyndolod", State = StepState.Done }] };
			Directory.CreateDirectory(j.Folder);
			j.Save();
		}
		var cache = new JournalCache(runs);
		var explorer = Procs.Pids("explorer.exe");
		var me = Environment.ProcessId;
		// each hot path on its own, so a growth names its path
		var parts = new (string Name, int Every, Action Run)[] {
			("process looks", 1, () => {
				_ = Procs.Running("ModOrganizer.exe");
				_ = Mo2Control.GameImages.Any(Procs.Running);
				_ = Procs.Pids("explorer.exe");
				_ = StepKit.PidsOf("svchost.exe")();
			}),
			("run records", 1, () => _ = cache.Read()),
			("process tree CPU", 1, () => {
				_ = Procs.TreeCpu(me);
				_ = Procs.StartTime(me);
				_ = Journal.IsRunner(me);
			}),
			("window list", 1, () => _ = Native.TopWindowsOf(explorer)),
			("UI Automation boxes", 1, () => _ = Uia.Boxes(explorer)),
		};
		// every path warmed up first: UI Automation's first use starts its own threads and connections (about 100 handles, once)
		foreach (var (_, every, run) in parts) {
			for (var i = 0; i < 100; i += every) {
				run();
			}
		}
		var grew = new List<string>();
		var before = Held();
		foreach (var (name, every, run) in parts) {
			var p0 = Held();
			var marks = new List<string>();
			for (var i = 1; i <= rounds; i += every) {
				run();
				if (i % Math.Max(1, rounds / 3) == 0) {
					var m = Held();
					marks.Add($"{m.Managed / 1024} KB / {m.Handles} handles at {i}");
				}
			}
			var p1 = Held();
			Console.WriteLine($"      {name}: {rounds / every} calls, managed {p0.Managed / 1024} -> {p1.Managed / 1024} KB, handles {p0.Handles} -> {p1.Handles}  ({string.Join(", ", marks)})");
			// a growth is a leak when it goes on to the end: a cache fills and stops, so the last third must not grow like the first
			if (p1.Managed - p0.Managed > 1024 * 1024 || p1.Handles - p0.Handles > 20) {
				grew.Add($"{name} grew {(p1.Managed - p0.Managed) / 1024} KB and {p1.Handles - p0.Handles} handles");
			}
		}
		// the run's command watcher made and let go of, as every run does
		for (var i = 0; i < 50; ++i) {
			using var c = new Commands(new Journal { Folder = f.Data }, () => { });
			c.Start();
		}
		var after = Held();
		// for the record (not judged - it is .NET's own client; measured after the judged part, since its first use starts its own
		// threads): what UI Automation elements cost when made and dropped - the reason Uia.Boxes makes one only for a window that can
		// be a box, and the reason a whole window's text is read seldom
		var windows = Native.TopWindowsOf(explorer);
		if (windows.Count > 0) {
			_ = Uia.FromHandle(windows[0]);
			var u0 = Held();
			for (var i = 0; i < 300; ++i) {
				_ = Uia.FromHandle(windows[i % windows.Count]);
			}
			var u1 = Held();
			var tray = windows.Select(Uia.FromHandle).OfType<System.Windows.Automation.AutomationElement>().OrderByDescending(e => Uia.All(e).Count).First();
			var n = Uia.All(tray).Count;
			var t0 = Held();
			for (var i = 0; i < 30; ++i) {
				_ = Uia.TextOf(tray, int.MaxValue);
			}
			var t1 = Held();
			Console.WriteLine($"      (.NET's UI Automation client: 300 elements made and dropped kept {(u1.Managed - u0.Managed) / 1024} KB; a window's whole text " +
				$"({n} elements) read 30 times kept {(t1.Managed - t0.Managed) / 1024} KB)");
		}
		Console.WriteLine($"      soak of {rounds} rounds: managed {before.Managed} -> {after.Managed} bytes, handles {before.Handles} -> {after.Handles}, " +
			$"GDI {before.Gdi} -> {after.Gdi}, USER {before.User} -> {after.User}");
		Check(grew.Count == 0, "no hot path keeps memory or handles: " + string.Join("; ", grew));
		Check(after.Handles - before.Handles < 40, $"kernel handles come back (grew {after.Handles - before.Handles})");
		Check(after.Gdi <= before.Gdi + 5, $"GDI objects come back (grew {(int)after.Gdi - (int)before.Gdi})");
		Check(after.User <= before.User + 5, $"USER objects come back (grew {(int)after.User - (int)before.User})");
	}
}
