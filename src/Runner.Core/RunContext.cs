using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ToolRunner;

// What a step works with during a run.
public sealed class RunContext
{
	public required Setup Setup { get; init; }
	public required Mo2Instance Mo2 { get; init; }
	public required Profile Profile { get; init; }
	public required Mo2Control Control { get; init; }
	public required RunLog Log { get; init; }
	// the game's own words for its load boxes, read the first time a box is not in English
	public GameWords? GameWords { get; set; }
	// the settings files the small, muted game window changed for this step
	public List<string> QuietFiles { get; } = [];
	public required Journal Journal { get; init; }
	public required CancellationToken Cancel { get; init; }
	public required string BackupFolder { get; init; }
	public IGuide Guide { get; init; } = new NoGuide();
	public DevBench DevBench { get; init; } = new();

	// outputs to tick once the step's mods are put back (applied by the runner after each step)
	public List<string> PendingEnable { get; } = [];
	public string? CleanSaveName { get; set; }

	// when this run last started its game: a crash log is that start's only if written after it (an earlier start's crash, retried
	// since, is never named as the cause of a later close)
	public DateTime? GameLaunched { get; set; }
	public Dictionary<string, string> CleanSaveExtraOff { get; set; } = [];
	public string Mo2Dir => Path.GetDirectoryName(Mo2.Exe)!;

	// this step made nothing of its output mod (a guided tool wrote elsewhere): the mod is not this run's, so a put-back and Undo leave it as it was
	public bool OutputUntouched { get; set; }

	public void SetAfter(params string[] a_mods) => PendingEnable.AddRange(a_mods);

	// one output of a step that makes several is complete: on record as built at once, so it stays - and is switched on at the
	// put-back - even when a later part of the same step fails or is stopped (a step's failure used to lose its finished part)
	public void Finished(string a_mod)
	{
		Steps.StepKit.MarkReplaced(this, a_mod);
		if (!Journal.Built.Contains(a_mod, StringComparer.OrdinalIgnoreCase)) {
			Journal.Built.Add(a_mod);
		}
		Journal.Save();
		SetAfter(a_mod);
	}

	// a step's own wait (a tool settling, a pause between two tries): a Stop ends it at once - never Thread.Sleep in a step
	public void Pause(TimeSpan a_for) => Waits.Pause(a_for, Cancel);

	// what a step does now, for the window: saved at most once a second (a command-line tool prints thousands of lines); a
	// "WAITING FOR YOU" text always at once. Only the run's own thread calls it.
	public void Activity(string a_step, string a_text, double? a_progress = null)
	{
		var s = Journal.Step(a_step);
		if (s != null && (s.Activity != a_text || s.Progress != a_progress)) {
			s.Activity = a_text;
			s.Progress = a_progress;
			if (DateTime.UtcNow - _lastActivitySave > TimeSpan.FromSeconds(1) || a_text.StartsWith("WAITING", StringComparison.Ordinal)) {
				_lastActivitySave = DateTime.UtcNow;
				Journal.Save();
			}
		}
	}

	DateTime _lastActivitySave = DateTime.MinValue;

	public void Note(string a_text)
	{
		Journal.Notes.Add(a_text);
		Journal.Save();
	}
}

// how a step asks the player to do something by hand (no DevBench): the window shows the text and a Done button
public interface IGuide
{
	// returns when the player pressed Done; throws OperationCanceledException on Stop
	void Ask(string a_step, string a_text, CancellationToken a_ct);
}

public sealed class NoGuide : IGuide
{
	public void Ask(string a_step, string a_text, CancellationToken a_ct) =>
		throw new StepFailedException("this step needs your hands in the game and nobody can answer here: " + a_text);
}

// the guide over the run's command file: the journal says what is asked, the window writes "continue"
public sealed class FileGuide(Journal a_journal, Func<bool> a_continued, Action a_clear) : IGuide
{
	public void Ask(string a_step, string a_text, CancellationToken a_ct)
	{
		a_clear();
		var s = a_journal.Step(a_step);
		if (s != null) {
			s.Activity = "WAITING FOR YOU: " + a_text;
			a_journal.Save();
		}
		while (!a_continued()) {
			a_ct.ThrowIfCancellationRequested();
			Thread.Sleep(500);
		}
		a_clear();
		if (s != null) {
			s.Activity = "";
			a_journal.Save();
		}
	}
}

// settings of one step with plain fallbacks
public sealed class StepSettings(JsonObject a_o)
{
	public JsonObject Raw { get; } = a_o;
	public string? Str(string a_key) => Setup.Str(Raw, a_key);
	public string Req(string a_key) => Str(a_key) is { Length: > 0 } s ? s : throw new StepFailedException($"the step's setting \"{a_key}\" is not set");
	public int Int(string a_key, int a_default) => Setup.Int(Raw, a_key) ?? a_default;
	public double Num(string a_key, double a_default) => Setup.Num(Raw, a_key) ?? a_default;
	public bool Bool(string a_key, bool a_default) => Setup.Bool(Raw, a_key) ?? a_default;
	public List<string> List(string a_key) => Setup.List(Raw, a_key);
	public string? OutputMod => Str("output_mod") ?? Str("then_move_to_mod");
	public TimeSpan Deadline(int a_defaultMin) => TimeSpan.FromMinutes(Int("deadline_min", a_defaultMin));
}

// Mods on / off for one step (MO2 closed) and ALWAYS put back afterwards: the order files are saved before and written back
// byte for byte (a tool that regenerates a plugin makes it vanish for a moment, and MO2 would re-add it elsewhere).
public sealed class Toggle : IDisposable
{
	readonly RunContext _ctx;
	readonly Dictionary<string, byte[]> _snap;
	readonly string _label;

	// a_ct: a Stop while it waits to close MO2 (a download, the player's own program) ends the wait
	public Toggle(RunContext a_ctx, IEnumerable<string> a_enable, IEnumerable<string> a_disable, string a_label, CancellationToken a_ct = default)
	{
		_ctx = a_ctx;
		_label = a_label;
		// a Stop already asked for: nothing is switched and nothing started
		a_ct.ThrowIfCancellationRequested();
		var st = a_ctx.Profile.States();
		var en = a_enable.Where(m => st.ContainsKey(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var dis = a_disable.Where(m => st.ContainsKey(m) && !en.Contains(m, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		foreach (var m in a_enable.Concat(a_disable).Where(m => !st.ContainsKey(m)).Distinct()) {
			a_ctx.Log.Detail($"(not in this profile, skipped: {m})");
		}
		a_ctx.Control.Close(null, a_ct);
		_snap = a_ctx.Profile.Snapshot();
		_oursBefore = a_ctx.Profile.OursNow();
		if (en.Count > 0 || dis.Count > 0) {
			a_ctx.Log.Detail($"mods for {a_label}:");
			try {
				a_ctx.Profile.SetMods(en, dis, a_ctx.Log);
				a_ctx.Profile.ActivatePlugins(en, a_ctx.Setup.PlaceAfter);
			} catch {
				// half switched: the order files back byte for byte at once, as the put-back promises
				a_ctx.Profile.Restore(_snap);
				a_ctx.Profile.OursBack(_oursBefore);
				throw;
			}
		}
	}

	readonly Dictionary<string, char>? _oursBefore;

	public void Dispose()
	{
		try {
			_ctx.Control.Close();
		} catch (Mo2BusyException e) {
			_ctx.Log.Error($"could not close Mod Organizer to put the mods back after {_label}: {e.Message}");
			throw;
		}
		// what the player changed in Mod Organizer while the step's switches were on (CodeRabbit on PR #24): read before the order
		// files go back, laid back over them after, and still counted as the player's (the expectation goes back to before the
		// step), so the run's final put-back sees and keeps it too
		var theirs = _ctx.Profile.ChangedByOthers();
		var marks = theirs.Count > 0 ? _ctx.Profile.PluginMarks() : [];
		try {
			_ctx.Profile.Restore(_snap);
		} catch (Mo2OpenException) {
			// a Mod Organizer the step started a moment ago was not there for the close above and is there now (his run of
			// 2026-11-10: a Stop seen right after the start left the step FAILED on "Mod Organizer is open"): closed, then once more
			_ctx.Log.Detail("Mod Organizer came up while the mods were being put back - closing it, then putting them back");
			_ctx.Control.Close();
			_ctx.Profile.Restore(_snap);
		}
		_ctx.Profile.OursBack(_oursBefore);
		if (theirs.Count > 0) {
			var expected = _ctx.Profile.OursNow();
			_ctx.Log.Detail($"kept what you changed in Mod Organizer during {_label}: " + string.Join(", ", theirs.Select(p => (p.Value == '+' ? "on " : "off ") + p.Key)));
			Runner.ReapplyTheirs(_ctx, theirs, marks);
			_ctx.Profile.OursBack(expected);
		}
	}
}

// waiting on a tool: its own "done" signal (poll), a deadline, and its whole process tree's CPU - a tool that uses no CPU for
// the setup's idle minutes is looked at at once (his rule: a finished tool is noticed within 2 minutes)
public static class Waits
{
	public enum Poll
	{
		Wait,
		Done,
	}

	// the one wait of the engine's steps: it ends the moment a Stop is asked, and says so by throwing as every other look at Stop
	// does (his finds of 2026-11-04, 11-06 and 11-10: "pressing stop now still doesn't stop it" - each time a wait that could
	// not see it). The offline test NoStepWaitIsBlindToStop holds the steps to it.
	public static void Pause(TimeSpan a_for, CancellationToken a_ct)
	{
		if (a_for > TimeSpan.Zero) {
			a_ct.WaitHandle.WaitOne(a_for);
		}
		a_ct.ThrowIfCancellationRequested();
	}

	// the offline tests only: a short idle limit
	public static TimeSpan? IdleForTests { get; set; }

	// how often a tool's whole window text is read for the idle rule (the tests shorten it with their idle limit)
	public static TimeSpan ProgressEvery { get; set; } = TimeSpan.FromSeconds(30);

	// a_progress: for a tool driven through its window - what its window says; a change counts as work, and so does CPU its CHILD
	// processes use (a long patcher shows nothing new for minutes - the fourth review, M2), never the window process's own (a
	// window drawing itself uses CPU while the tool does nothing - Synthesis sat 4 min on its main screen, 2026-10-27)
	public static void Tool(RunContext a_ctx, string a_step, Process a_proc, TimeSpan a_deadline, Func<Poll> a_poll,
		Func<bool>? a_onIdle = null, TimeSpan? a_every = null, Func<string>? a_progress = null)
	{
		string? lastSeen = null;
		var every = a_every ?? TimeSpan.FromSeconds(2);
		var sw = Stopwatch.StartNew();
		var idleLimit = IdleForTests ?? TimeSpan.FromMinutes(Math.Max(1, a_ctx.Setup.IdleMinutes));
		var lastCpu = TimeSpan.Zero;
		IReadOnlyDictionary<int, TimeSpan> lastChildren = new Dictionary<int, TimeSpan>();
		var lastBusy = sw.Elapsed;
		var lastSample = TimeSpan.FromSeconds(-10);
		// a window's whole text is read at most every ProgressEvery: each read makes the tool walk its own window for UI Automation
		// (work in the tool's process), and .NET's client keeps a little of every element it made; the idle rule needs minutes,
		// not seconds
		var lastProgress = TimeSpan.FromSeconds(-1000);
		while (true) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			if (a_poll() == Poll.Done) {
				return;
			}
			if (sw.Elapsed > a_deadline) {
				throw new StepFailedException($"{StepCatalog.Name(a_step)} passed its {a_deadline.TotalMinutes:0} minute limit");
			}
			if (sw.Elapsed - lastSample >= TimeSpan.FromSeconds(5)) {
				lastSample = sw.Elapsed;
				bool alive;
				try {
					alive = !a_proc.HasExited;
				} catch (InvalidOperationException) {
					alive = false;
				}
				if (alive) {
					// a window whose text cannot be read (empty) is judged by the tool's CPU, so a working tool is never called idle
					string? seen = null;
					// at least three reads inside the idle limit, so a window that changes is never taken for an idle one
					if (a_progress != null && sw.Elapsed - lastProgress >= TimeSpan.FromTicks(Math.Min(ProgressEvery.Ticks, idleLimit.Ticks / 3))) {
						lastProgress = sw.Elapsed;
						seen = a_progress();
					} else if (a_progress != null) {
						seen = lastSeen;
					}
					if (!string.IsNullOrEmpty(seen)) {
						if (seen != lastSeen) {
							lastSeen = seen;
							lastBusy = sw.Elapsed;
						}
						var children = Procs.ChildCpu(a_proc.Id);
						if (Procs.CpuGain(lastChildren, children) > TimeSpan.FromMilliseconds(300)) {
							lastBusy = sw.Elapsed;
						}
						lastChildren = children;
					} else {
						var cpu = Procs.TreeCpu(a_proc.Id);
						if (cpu - lastCpu > TimeSpan.FromMilliseconds(300)) {
							lastBusy = sw.Elapsed;
						}
						lastCpu = cpu;
					}
					if (sw.Elapsed - lastBusy > idleLimit) {
						if (a_onIdle?.Invoke() == true) {
							lastBusy = sw.Elapsed;
						} else {
							var boxes = Uia.Boxes(Procs.Tree(a_proc.Id));
							var said = boxes.Count > 0 ? " It shows: " + string.Join(" / ", boxes.Select(b => b.Text)) :
								lastSeen is { Length: > 0 } s ? " Its window shows: " + (s.Length > 300 ? "..." + s[^300..] : s) : " It shows no question.";
							throw new StepFailedException($"{StepCatalog.Name(a_step)} sat idle for {idleLimit.TotalMinutes:0} minutes without finishing.{said}");
						}
					}
				}
			}
			Thread.Sleep(every);
		}
	}

	// wait for a process to end on its own (with a deadline); the stall rule as above
	public static int? Exit(RunContext a_ctx, string a_step, Process a_proc, TimeSpan a_deadline)
	{
		Tool(a_ctx, a_step, a_proc, a_deadline, () => {
			try {
				return a_proc.HasExited ? Poll.Done : Poll.Wait;
			} catch (InvalidOperationException) {
				return Poll.Done;
			}
		});
		try {
			a_proc.WaitForExit(5000);
			return a_proc.ExitCode;
		} catch (InvalidOperationException) {
			return null;
		}
	}
}
