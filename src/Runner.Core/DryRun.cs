namespace ToolRunner;

// What a start would do, without starting: every refusal a run would meet (the same gate a run goes through, Runner.CheckStart),
// each with what to do about it, the check's notes, and what the run would then change. Nothing is written anywhere and nothing
// is started - not Mod Organizer, not a tool, not the setup's own commands. The Check button says whether the tools are found
// and the preview says what a run switches; this is both in one answer to "what happens if I press START now?".
public sealed class DryRunReport
{
	public required string SetupFile { get; init; }
	public string SetupName { get; init; } = "";
	public List<(string Step, string Text)> Refusals { get; } = [];
	public List<(string Step, string Text)> Notes { get; } = [];
	// null when the run is refused before its steps and Mod Organizer are known
	public RunPreview? Preview { get; init; }
	public bool Starts => Refusals.Count == 0;

	static string Said((string Step, string Text) a_r) => a_r.Step == "" ? a_r.Text : $"{StepCatalog.Name(a_r.Step)}: {a_r.Text}";

	// in plain words, for the window's box and the command line; a_usual gives a step's usual time when there is one
	public List<string> Lines(Func<string, TimeSpan?> a_usual)
	{
		var l = new List<string>();
		if (Starts) {
			l.Add("A start now would go ahead.");
		} else {
			l.Add(Refusals.Count == 1 ? "A start now would be REFUSED:" : $"A start now would be REFUSED, for {Refusals.Count} reasons:");
			foreach (var r in Refusals) {
				var lines = Explain.Lines(r.Step, Said(r));
				l.Add("  - " + lines[0]);
				l.AddRange(lines.Skip(1).Select(x => "    " + x));
			}
		}
		if (Notes.Count > 0) {
			l.Add("");
			l.Add("The check's notes (a run goes on with these):");
			l.AddRange(Notes.Select(n => "  - " + Said(n)));
		}
		if (Preview != null) {
			l.Add("");
			l.AddRange(Preview.Lines(a_usual));
		} else {
			l.Add("");
			l.Add("Nothing has been changed.");
		}
		return l;
	}
}

public static class DryRun
{
	public static DryRunReport Make(RunRequest a_req)
	{
		// the run lock as a start would take it: looked at, and let go when this ends. While another run goes, that is the one
		// answer - the rest of the check would only describe that run's own Mod Organizer and tools
		using var hold = Runner.HoldIfFree();
		if (hold == null) {
			var busy = new DryRunReport { SetupFile = a_req.SetupFile };
			busy.Refusals.Add(("", "another run is going - wait for it, or stop it in the window"));
			return busy;
		}
		var gate = Runner.CheckStart(a_req);
		RunPreview? preview = null;
		var notes = new List<(string, string)>();
		if (gate is { Setup: { } setup, Mo2: { } mo2, Todo.Count: > 0 }) {
			try {
				preview = RunPreview.Build(setup, mo2, gate.Todo);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException) {
				notes.Add(("", "what the run would change could not be worked out: " + e.Message));
			}
		}
		var report = new DryRunReport { SetupFile = a_req.SetupFile, SetupName = gate.Setup?.Name ?? "", Preview = preview };
		report.Refusals.AddRange(gate.Refusals);
		report.Notes.AddRange(gate.Check.Where(i => !i.Blocking).Select(i => (i.Step, i.Text)));
		report.Notes.AddRange(notes);
		return report;
	}
}
