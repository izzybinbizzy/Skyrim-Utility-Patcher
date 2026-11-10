using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using ToolRunner.Steps;

namespace ToolRunner;

// His checklist 445: a tool's update can rename or move the buttons this program presses. Each release names the versions it was proven with, and the
// pre-check says when a player's tool is newer; "Test my tools" opens each tool that is driven through its window to the window a run presses, and
// looks for what it would press. Nothing is pressed and nothing runs.
public static class ToolProof
{
	// the versions of the last real runs (the testing Mod Organizer, 2026-11-07 / 08)
	public static readonly IReadOnlyDictionary<string, string> ProvenWith = new Dictionary<string, string>(StringComparer.Ordinal) {
		["synthesis"] = "0.36.6",
		["landscapefixer"] = "2.0.1",
		["pgpatcher"] = "2.3.1",
		["xlodgen"] = "4.1.5",
		["texgen"] = "3.0.0.218",
		["dyndolod"] = "3.0.0.218",
		["pandora"] = "5.0.0",
		["grass"] = "1.6.0",
	};

	public static Issue? Newer(string a_step, string a_yours) =>
		a_yours != "" && ProvenWith.TryGetValue(a_step, out var proven) && ToolUpdates.IsNewer(proven, a_yours) ?
			new Issue(a_step, false, $"yours is {a_yours}, newer than the {proven} this version was proven with: if it stops at a window, Settings > Test my tools shows what changed") : null;

	// how long a tool may take to reach the window a run presses (the LOD tools read the whole load order first)
	static readonly TimeSpan LongLoad = TimeSpan.FromMinutes(20), ShortLoad = TimeSpan.FromMinutes(3);

	public sealed record Look(string Step, string Title, string? Inner, string? InnerClass, IReadOnlyList<string> Buttons, string? ButtonRx, TimeSpan Wait);

	public static readonly IReadOnlyList<Look> Looks = [
		new("synthesis", "Synthesis", null, null, [], SynthesisStep.GoRx, ShortLoad),
		new("landscapefixer", "^Landscape Fixer", null, null, ["Generate", "Start"], null, ShortLoad),
		new("xlodgen", "LODGen", "^LODGen Options$", null, ["Generate"], null, LongLoad),
		new("texgen", "TexGen", "Options|Expert", "TfrmTexGen", ["Start", "OK"], null, LongLoad),
		new("dyndolod", "DynDOLOD", "Options|Expert", "TfrmDynDOLOD", ["Start", "OK"], null, LongLoad),
	];

	public sealed record Result(string Step, bool Good, string Text);

	// what it would press, in a window as it stands (the names of the tool's own translations too); null = found
	public static string? Missing(Look a_look, IReadOnlyList<(string Name, string Id, bool Usable)> a_buttons, IReadOnlyCollection<string>? a_otherNames = null)
	{
		var names = a_look.Buttons.Concat(a_otherNames ?? []).ToList();
		var hit = a_buttons.Any(b => b.Usable && (names.Contains(b.Name.Trim(), StringComparer.OrdinalIgnoreCase) ||
			(a_look.ButtonRx != null && Regex.IsMatch(b.Id + " " + b.Name, a_look.ButtonRx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))));
		return hit ? null : "it shows no " + (names.Count > 0 ? names[0] : "Run") + " button; its buttons: " + string.Join(", ", a_buttons.Select(b => b.Name.Trim()).Where(n => n != "").DefaultIfEmpty("none"));
	}

	// one tool opened through Mod Organizer, looked at, closed
	public static Result Test(Setup a_setup, Look a_look, RunLog a_log, CancellationToken a_ct)
	{
		using var hold = Runner.HoldIfFree() ?? throw new StepFailedException("a run is going - test your tools when it is done");
		var s = new StepSettings(a_setup.StepSettings(a_look.Step) ?? throw new StepFailedException("this setup has no such step"));
		var mo2 = Mo2Instance.Load(a_setup.Mo2Exe, a_setup.Mo2Instance);
		var title = s.Str("executable") ?? throw new StepFailedException("this step has no Mod Organizer executable");
		var exe = mo2.Executable(title)?.Binary ?? throw new StepFailedException($"Mod Organizer has no executable called \"{title}\"");
		var image = s.Str("image") ?? Path.GetFileName(exe);
		var tool = StepCatalog.Name(a_look.Step);
		var control = new Mo2Control(mo2, a_setup.Profile, a_log) { AllowCloseTheirs = true };
		control.Close(null, a_ct);
		var profile = new Profile(mo2, a_setup.Profile, () => control.Running);
		// a test changes nothing in the profile (a run has a record to put a switch back from; this has none)
		if (StepKit.ToolMod(mo2, profile, title) is { } toolMod) {
			return new Result(a_look.Step, false, $"{tool}: not opened - its program is in the mod \"{toolMod}\", which is switched off in this profile. A run switches it on for itself; tick it to test the tool here.");
		}
		try {
			using var proc = control.RunExecutable(title, image, null, a_ct, TimeSpan.FromMinutes(5));
			var pids = StepKit.PidsOf(image);
			var main = Uia.WaitWindow(pids, t => Regex.IsMatch(t, a_look.Title, RegexOptions.None, TimeSpan.FromSeconds(1)), a_look.Wait, a_ct);
			if (main == null) {
				return new Result(a_look.Step, false, $"{tool}: its window did not open within {a_look.Wait.TotalMinutes:0} minutes");
			}
			var window = main;
			if (a_look.Inner != null) {
				var sw = Stopwatch.StartNew();
				AutomationElement? inner = null;
				while (inner == null) {
					a_ct.ThrowIfCancellationRequested();
					inner = Uia.Children(main).FirstOrDefault(c => Uia.Type(c) == ControlType.Window &&
						(Regex.IsMatch(Uia.Name(c), a_look.Inner, RegexOptions.None, TimeSpan.FromSeconds(1)) || Uia.ClassName(c) == a_look.InnerClass));
					if (inner == null) {
						if (sw.Elapsed > a_look.Wait) {
							return new Result(a_look.Step, false, $"{tool}: the window with its options did not open within {a_look.Wait.TotalMinutes:0} minutes. It shows: {Uia.TextOf(main, 300)}");
						}
						// a box of the tool's own (a new version, a warning) stands in the way of what a run would answer: named, never pressed
						if (Uia.Boxes(pids()).FirstOrDefault() is { } box) {
							return new Result(a_look.Step, false, $"{tool}: it stopped at a box before its options: {box.Text}");
						}
						Waits.Pause(TimeSpan.FromSeconds(2), a_ct);
					}
				}
				window = inner;
			}
			var buttons = new List<(string, string, bool)>();
			var missing = "";
			var look = Stopwatch.StartNew();
			var exeDir = Path.GetDirectoryName(exe);
			var others = a_look.Step == "landscapefixer" && exeDir != null ? LandscapeFixerStep.StartButtonNames(Path.Combine(exeDir, "LandscapeFixer_translations"), []) : [];
			// a window lays itself out for a moment after it opens
			do {
				a_ct.ThrowIfCancellationRequested();
				buttons = [.. Uia.All(window, ControlType.Button).Select(b => (Uia.Name(b), Uia.AutomationId(b), Uia.Usable(b)))];
				missing = Missing(a_look, buttons, others) ?? "";
				if (missing != "") {
					Thread.Sleep(1500);
				}
			} while (missing != "" && look.Elapsed < TimeSpan.FromSeconds(30));
			return new Result(a_look.Step, missing == "", missing == "" ? $"{tool}: what a run presses is there" : $"{tool}: {missing}");
		} finally {
			StepKit.CloseTool(control, image, a_log);
			try {
				control.Close(null, CancellationToken.None);
			} catch (Exception e) when (e is Mo2BusyException or IOException or UnauthorizedAccessException or InvalidOperationException) {
				a_log.Warn($"Mod Organizer could not be closed ({e.Message})");
			}
		}
	}

	// every tool of this setup driven through its window, one after another; a tool that could not be opened says so and the next goes on
	public static List<Result> TestAll(Setup a_setup, RunLog a_log, CancellationToken a_ct)
	{
		var results = new List<Result>();
		foreach (var look in Looks.Where(l => a_setup.StepSettings(l.Step) != null)) {
			a_ct.ThrowIfCancellationRequested();
			try {
				results.Add(Test(a_setup, look, a_log, a_ct));
			} catch (Exception e) when (e is StepFailedException or Mo2BusyException or IOException or UnauthorizedAccessException or InvalidOperationException) {
				results.Add(new Result(look.Step, false, $"{StepCatalog.Name(look.Step)}: {e.Message}"));
			}
		}
		return results;
	}
}
