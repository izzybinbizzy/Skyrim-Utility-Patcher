namespace ToolRunner;

// A run that has ended, summed up: how it ended and how long it took, which steps finished and which did not, what it built and
// how big, what it keeps off, what it noted on the way, and what to do next. The verdict is one line; this is the card under it
// in the window, the last lines of the simple log, and what the command line prints after a run.
public sealed class RunSummary
{
	public sealed record Row(string Label, string Text);

	public string Title { get; private init; } = "";
	// good, warn or bad: how the window colors the title
	public string Kind { get; private init; } = "";
	public List<Row> Rows { get; } = [];

	public static string Span(TimeSpan a_t) => a_t.TotalMinutes < 1 ? "less than a minute" :
		a_t.TotalMinutes < 60 ? $"{Math.Max(1, (int)Math.Round(a_t.TotalMinutes))} min" : $"{(int)a_t.TotalHours} h {a_t.Minutes:00} min";

	static string Names(IEnumerable<StepRecord> a_steps) => string.Join(", ", a_steps.Select(s => s.Name));

	public static RunSummary Of(Journal a_j)
	{
		// the last moment the run itself wrote down, for a run that was cut off
		var ended = a_j.Ended ?? a_j.Steps.Select(s => s.Ended ?? s.Started).Where(t => t != null).Max() ?? a_j.Started;
		var took = Span(ended - a_j.Started);
		var cutOff = a_j.State == RunState.Running || a_j.NeedsRestore || a_j.Undoing;
		var s = new RunSummary {
			Title = cutOff ? "Did not finish" : a_j.State switch {
				RunState.Finished => $"Finished in {took}",
				RunState.Stopped => $"Stopped after {took}",
				RunState.Restored => a_j.Undone ? "Undone" : "Put back",
				_ => $"Stopped by a problem after {took}",
			},
			Kind = cutOff ? "bad" : a_j.State switch { RunState.Finished or RunState.Restored => "good", RunState.Stopped => "warn", _ => "bad" },
		};
		var done = a_j.Steps.Where(x => x.State == StepState.Done).ToList();
		var failed = a_j.Steps.Where(x => x.State == StepState.Failed).ToList();
		var stopped = a_j.Steps.Where(x => x.State == StepState.Stopped).ToList();
		var notReached = a_j.Steps.Where(x => x.State is StepState.Pending or StepState.Running or StepState.Skipped).ToList();
		var steps = $"{done.Count} of {a_j.Steps.Count} finished";
		if (failed.Count > 0) {
			steps += $"; {Names(failed)} did not finish";
		}
		if (stopped.Count > 0) {
			steps += $"; {Names(stopped)} was stopped";
		}
		if (notReached.Count > 0) {
			steps += $"; not reached: {Names(notReached)}";
		}
		s.Rows.Add(new Row("Steps", steps));
		foreach (var f in failed) {
			s.Rows.Add(new Row("Why", $"{f.Name}: {f.Note}"));
			if (Explain.For(f.Key, f.Note) is { } why) {
				s.Rows.Add(new Row("What to do", why.Todo + (why.Help == null ? "" : " Help: " + why.Help)));
			}
		}
		var from = Retry.FromStep(a_j) is { } key ? a_j.Step(key)?.Name ?? StepCatalog.Name(key) : null;
		var next = cutOff ? (a_j.Undoing ? "Undo this run again (History) to finish what the undo began." : "Press \"Put my profile back\": outputs may still be switched off.") :
			a_j.State switch {
				RunState.Finished when a_j.Step("playsave")?.State == StepState.Done => $"Load the save \"{InGame.SaveReady}\" and play.",
				RunState.Finished => "Nothing left to do: your profile is as it was, with the new outputs on.",
				RunState.Restored => a_j.Undone ? "Your outputs and lists are as they were before this run." : "Your profile is as it was, with the outputs that finished on.",
				_ when from != null => $"Fix what stopped it, then press \"Run again from {from}\": the steps that finished are not run again.",
				_ => "Your profile is back as it was.",
			};
		s.Rows.Add(new Row("Next", next));
		var sizes = a_j.Steps.Where(x => x.Output != null && x.OutputBytes != null).GroupBy(x => x.Output!, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(g => g.Key, g => g.Last().OutputBytes!.Value, StringComparer.OrdinalIgnoreCase);
		if (a_j.Built.Count > 0 && !a_j.Undone) {
			s.Rows.Add(new Row(a_j.Built.Count == 1 ? "New output" : "New outputs",
				string.Join(", ", a_j.Built.Select(m => sizes.TryGetValue(m, out var b) ? $"{m} ({DiskForecast.Size(b)})" : m)) + " - on in Mod Organizer"));
		}
		if (a_j.Retire.Count > 0 && !a_j.Undone) {
			s.Rows.Add(new Row("Kept off", string.Join(", ", a_j.Retire) + " - replaced by what this run made"));
		}
		var timed = done.Where(x => x.Started != null && x.Ended != null).OrderByDescending(x => x.Ended!.Value - x.Started!.Value).Take(3).ToList();
		if (done.Count >= 4) {
			s.Rows.Add(new Row("Took longest", string.Join(", ", timed.Select(x => $"{x.Name} {Span(x.Ended!.Value - x.Started!.Value)}"))));
		}
		foreach (var n in a_j.Notes.Take(5)) {
			s.Rows.Add(new Row("Note", n));
		}
		if (a_j.Notes.Count > 5) {
			s.Rows.Add(new Row("Note", $"{a_j.Notes.Count - 5} more in the run's log"));
		}
		if (a_j.Parked.Any(p => p.Replaced && !p.PutBack) && !a_j.Undone && a_j.BackupDir != "") {
			s.Rows.Add(new Row("Old outputs", $"kept in this run's backup, {a_j.BackupDir} (History > Undo this run brings them back)"));
		}
		return s;
	}

	public List<string> Lines() => [Title, .. Rows.Select(r => $"  {r.Label}: {r.Text}")];
}

// What is going right now, for a script or another program (the command line's "status"): read off the run records, the queue
// and the pending sleep or shut-down - nothing is asked of a running run, and nothing is written.
public static class RunStatus
{
	// State: "running" (a step works), "waiting" (the run waits for the player), "finishing" (the profile is back, the last chores
	// go), "interrupted" (a run was cut off and its profile is not back) or "idle"
	public sealed record Snapshot(string State, Journal? Run, StepRecord? Step, string Waiting, Journal? Interrupted, Journal? Last, QueueRun.State? Queue, PowerAfter.Pending? Power)
	{
		// closing the window while this holds stops nothing and leaves nothing half done
		public bool SafeToClose => Run == null && Queue == null;
	}

	public static Snapshot Take(string a_dataDir, string? a_setupFile = null)
	{
		var runs = Journal.All(Runner.RunsDir(a_dataDir)).ToList();
		var run = runs.FirstOrDefault(j => j.Live && j.OwnerAlive);
		var interrupted = runs.FirstOrDefault(j => j.Unfinished && !j.OwnerAlive);
		var last = runs.FirstOrDefault(j => !ReferenceEquals(j, run) && (a_setupFile == null || string.Equals(j.SetupFile, a_setupFile, StringComparison.OrdinalIgnoreCase)));
		var step = run?.Steps.FirstOrDefault(s => s.State == StepState.Running);
		var asked = new[] { run?.Doing ?? "", step?.Activity ?? "" }.FirstOrDefault(t => t.StartsWith(Runner.WaitingPrefix, StringComparison.Ordinal));
		var waiting = asked == null ? "" : asked[Runner.WaitingPrefix.Length..];
		var state = run == null ? (interrupted != null ? "interrupted" : "idle") : waiting != "" ? "waiting" : run.FinishingUp || run.InGameAfter ? "finishing" : "running";
		return new Snapshot(state, run, step, waiting, interrupted, last, QueueRun.Read(a_dataDir), PowerAfter.Look(a_dataDir));
	}

	// in a few plain lines, for a person at a command window
	public static List<string> Lines(Snapshot a_s)
	{
		var l = new List<string>();
		if (a_s.Run is { } r) {
			var n = r.Steps.Count(x => x.State is StepState.Done or StepState.Skipped);
			l.Add($"{a_s.State}: {r.SetupName}, started {r.Started.ToString("MMM d HH:mm", InvariantCulture)} ({RunSummary.Span(DateTime.Now - r.Started)} ago), {n} of {r.Steps.Count} step(s) done");
			if (a_s.Step is { } step) {
				l.Add($"  now: {step.Name}{(step.Progress is { } p ? $" {p * 100:0} %" : "")}{(a_s.Waiting == "" && step.Activity != "" ? " - " + step.Activity : "")}");
			} else if (r.Doing != "" && a_s.Waiting == "") {
				l.Add("  now: " + r.Doing);
			}
			if (a_s.Waiting != "") {
				l.Add("  waits for you: " + a_s.Waiting);
			}
			if (r.StopAfterStep) {
				l.Add("  asked to stop after this step");
			}
		} else if (a_s.Interrupted is { } cut) {
			l.Add($"interrupted: the run of {cut.Started.ToString("MMM d HH:mm", InvariantCulture)} ({cut.SetupName}) did not finish - put the profile back (restore)");
		} else {
			l.Add("idle: no run is going");
		}
		if (a_s.Queue is { Running: { } now } q) {
			l.Add($"  queue: setup {q.Current + 1} of {q.Setups.Count} ({Path.GetFileNameWithoutExtension(now)})");
		}
		if (a_s.Power is { } power) {
			l.Add($"  the PC will {(power.Action == nameof(PowerAfter.Kind.Sleep) ? "go to sleep" : "shut down")} at {power.At.ToString("HH:mm:ss", InvariantCulture)} (cancel: stop)");
		}
		if (a_s.Run == null && a_s.Last is { } last) {
			l.Add($"  last run: {last.SetupName}, {last.Started.ToString("MMM d HH:mm", InvariantCulture)} - {(last.Verdict == "" ? last.State.ToString().ToLowerInvariant() : last.Verdict)}");
		}
		l.Add(a_s.SafeToClose ? "  safe to close" : "  closing the window stops the run (the profile is put back first)");
		return l;
	}
}
