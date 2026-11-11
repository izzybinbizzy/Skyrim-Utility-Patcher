using System.Diagnostics;
using ToolRunner.Steps;

namespace ToolRunner;

public sealed record RunRequest(string SetupFile, string DataDir, string? Only = null, string? From = null, string? Until = null);

public sealed record RunOutcome(RunState State, string Verdict, string? Folder);

// One run, start to end: refuse what is not safe, keep the run record on disk, switch the outputs off, run each step inside
// its own put-back, put back a step that did not finish, and leave the profile as it was with this run's outputs on.
public static class Runner
{
	public const string MutexName = "Local\\" + Brand.Name + " run";

	// the run lock under every name the program had: a copy of the old version never runs beside this one
	static IEnumerable<string> MutexNames => Brand.AllNames.Select(n => "Local\\" + n + " run" + LockScope);

	// the offline tests take a lock of their own, so a real run on the same PC never meets them (and they never block it)
	public static string LockScope { get; set; } = "";

	public static string RunsDir(string a_dataDir) => Path.Combine(a_dataDir, "Runs");

	// the steps a request runs, in the setup's order
	public static List<Setup.StepEntry> Plan(Setup a_setup, string? a_only, string? a_from, string? a_until)
	{
		var order = a_setup.EnabledSteps;
		foreach (var k in new[] { a_only, a_from, a_until }.OfType<string>()) {
			if (a_setup.LeftOutForNewGame(k) && a_setup.Steps.Any(s => s.Key == k && s.On)) {
				throw new ArgumentException($"{StepCatalog.Name(k)} is left out: this setup is set to a new game (untick \"New game\" to run it)");
			}
			if (order.All(s => s.Key != k)) {
				throw new ArgumentException($"\"{k}\" is not a step of this setup - its steps: {string.Join(", ", order.Select(s => s.Key))}");
			}
		}
		if (a_only != null) {
			return order.Where(s => s.Key == a_only).ToList();
		}
		var i = a_from == null ? 0 : order.FindIndex(s => s.Key == a_from);
		var j = a_until == null ? order.Count - 1 : order.FindIndex(s => s.Key == a_until);
		if (i > j) {
			throw new ArgumentException("the first step comes after the last one");
		}
		return order.Skip(i).Take(j - i + 1).ToList();
	}

	public static RunOutcome Run(RunRequest a_req, Func<Journal, IGuide>? a_guide = null)
	{
		using var hold = HoldIfFree();
		return hold == null ? new RunOutcome(RunState.Failed, "another run is going - wait for it, or stop it in the window", null) : RunLocked(a_req, a_guide);
	}

	// the run lock, for a run, a put-back, or a moment's work that must not overlap one (a look at what MO2 changed); null while
	// another holds it. Released by the thread that took it.
	public static IDisposable? HoldIfFree()
	{
		var held = new List<Mutex>();
		try {
			foreach (var name in MutexNames) {
				var m = new Mutex(false, name, out _);
				bool got;
				try {
					got = m.WaitOne(0);
				} catch (AbandonedMutexException) {
					got = true;
				}
				if (!got) {
					m.Dispose();
					new Held(held).Dispose();
					return null;
				}
				held.Add(m);
			}
		} catch {
			// a lock that would not open (access, a bad name) never leaves the ones already taken held
			new Held(held).Dispose();
			throw;
		}
		return new Held(held);
	}

	sealed class Held(List<Mutex> a_m) : IDisposable
	{
		public void Dispose()
		{
			for (var i = a_m.Count - 1; i >= 0; i--) {
				a_m[i].ReleaseMutex();
				a_m[i].Dispose();
			}
		}
	}

	static RunOutcome Refuse(string a_why) => new(RunState.Failed, "not started: " + a_why, null);

	// how a step's activity (and the run's own "doing") begins while the run waits for the player: the window shows what follows
	// in its waiting-for-you band
	public const string WaitingPrefix = "WAITING FOR YOU: ";

	// a run folder's log opened; null with why (a_why) when the folder cannot be written - said in words by the caller, never a crash
	static RunLog? OpenLog(string a_folder, out string a_why)
	{
		a_why = "";
		try {
			return new RunLog(a_folder);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			a_why = e.Message;
			return null;
		}
	}

	static RunOutcome RunLocked(RunRequest a_req, Func<Journal, IGuide>? a_guide)
	{
		Setup setup;
		try {
			setup = Setup.Load(a_req.SetupFile);
		} catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException) {
			return Refuse("the setup does not read: " + e.Message);
		}
		var problems = setup.Problems();
		if (problems.Count > 0) {
			return Refuse(string.Join("; ", problems));
		}
		var runs = RunsDir(a_req.DataDir);
		// the newest run's record must read before "no run was interrupted" can be believed
		if (Journal.NewestUnreadable(runs) is { } dim) {
			return Refuse(dim.Why is System.Text.Json.JsonException or InvalidDataException
				? $"the record of the last run ({Path.GetFileName(dim.Folder)}) is damaged, so it cannot be told whether that run finished - look at your profile in Mod Organizer, then move that run's folder out of {runs} to go on"
				: $"the record of the last run ({Path.GetFileName(dim.Folder)}) cannot be read right now ({dim.Why.Message}) - try again in a moment");
		}
		// any setup's interrupted run (two setups can share a profile, and the window refuses the same way)
		if (Journal.Interrupted(runs) is { } open) {
			return Refuse($"the run of {open.Started.ToString("MMM d HH:mm", InvariantCulture)} ({open.SetupName}) did not finish - put the profile back first (Restore)");
		}
		List<Setup.StepEntry> todo;
		try {
			todo = Plan(setup, a_req.Only, a_req.From, a_req.Until);
		} catch (ArgumentException e) {
			return Refuse(e.Message);
		}
		Mo2Instance mo2;
		try {
			mo2 = Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance);
		} catch (Exception e) when (e is IOException) {
			return Refuse(e.Message);
		}
		List<Issue> check;
		try {
			check = Preflight.Run(setup, mo2, todo.Select(s => s.Key).ToList());
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or InvalidOperationException) {
			// a check that cannot look (a drive that is not there, a folder it may not read) refuses the run in words - nothing has
			// been touched, and it never ends as a crash
			return Refuse("the check before the run could not finish: " + e.Message);
		}
		var blocking = check.Where(i => i.Blocking).ToList();
		if (blocking.Count > 0) {
			return Refuse(string.Join("; ", blocking.Select(i => $"{StepCatalog.Name(i.Step)}: {i.Text}")));
		}

		var id = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", InvariantCulture) + " " + setup.Name;
		var journal = new Journal {
			Id = id, Folder = Path.Combine(runs, id), SetupFile = Path.GetFullPath(a_req.SetupFile), SetupName = setup.Name, Profile = setup.Profile,
			Mo2Exe = setup.Mo2Exe, Mo2Instance = setup.Mo2Instance, BackupDir = Path.Combine(setup.BackupDir, id), Pid = Environment.ProcessId,
			PidStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
			Started = DateTime.Now, State = RunState.Running,
			Steps = todo.Select(s => new StepRecord { Key = s.Key, Name = StepCatalog.Name(s.Key), State = StepState.Pending, Output = new StepSettings(s.Settings).OutputMod }).ToList(),
		};
		Directory.CreateDirectory(journal.Folder);
		journal.Save();
		using var opened = OpenLog(journal.Folder, out var noLog);
		if (opened == null) {
			// the record says "running" already: closed again, or once this program has ended the window would offer to put back
			// a run that never touched the profile
			journal.State = RunState.Failed;
			journal.Verdict = "stopped before anything was changed: the run's log could not be opened (" + noLog + ")";
			journal.Ended = DateTime.Now;
			journal.Pid = 0;
			journal.TrySave();
			return new RunOutcome(RunState.Failed, journal.Verdict, journal.Folder);
		}
		RunLog log = opened;
		log.Mirror(Path.Combine(runs, "current log.txt"));
		using var cts = new CancellationTokenSource();
		var commands = new Commands(journal, cts.Cancel);
		Mo2Control? forThisRun = null;
		// one view of "is Mod Organizer open" for the run and for its order files: the run's own control, which also knows the copy
		// it started a moment ago (two views once disagreed inside a second, and a put-back failed on it)
		var profile = new Profile(mo2, setup.Profile, () => forThisRun?.Running ?? new Mo2Control(mo2, setup.Profile, RunLog.Null).Running);
		var control = new Mo2Control(mo2, setup.Profile, log) {
			LowerPriority = Setup.Bool(setup.Root, "lowerPriority") == true,
			CompanionOn = Setup.Bool(setup.Root, "keepMo2Open") == true && Mo2MenuEntry.Present(mo2),
		};
		forThisRun = control;
		control.StopAsked = cts.Token;
		journal.Mo2KeptOpen = control.CompanionOn;
		control.CompanionGaveUp = () => {
			journal.Mo2KeptOpen = false;
			journal.Save();
		};
		// what the run waits on the player for goes into the record, where the window reads it
		control.WaitingForPlayer = a_what => {
			journal.Doing = a_what == "" ? "" : WaitingPrefix + a_what;
			journal.TrySave();
		};
		var ctx = new RunContext {
			Setup = setup, Mo2 = mo2, Profile = profile, Control = control, Log = log, Journal = journal, Cancel = cts.Token,
			BackupFolder = journal.BackupDir, Guide = a_guide?.Invoke(journal) ?? new FileGuide(journal, commands.TakeContinue, commands.ClearContinue),
		};
		log.Say($"Tool Runner - {setup.Name} (profile {setup.Profile}), steps: {string.Join(", ", todo.Select(s => StepCatalog.Name(s.Key)))}");
		log.Simple($"{setup.Name} started: {todo.Count} step(s) - {string.Join(", ", todo.Select(s => StepCatalog.Name(s.Key)))}");
		foreach (var w in check) {
			log.Warn($"{StepCatalog.Name(w.Step)}: {w.Text}");
			log.Simple($"Note: {StepCatalog.Name(w.Step)}: {w.Text}");
		}
		// Stop ends this run's own game and tools at once - only a process it started, still the same process (started after
		// this run began - a PID can be reused), and never Mod Organizer itself (the put-back closes that the normal way)
		commands.OnStop = () => {
			foreach (var pid in control.Started) {
				// the very process that was started (its start time as recorded then), never one that got its PID since
				if (control.Started.SameProcess(pid) && Procs.StartTime(pid) is { } t && t >= journal.Started.ToUniversalTime().AddSeconds(-2) &&
				    !Procs.Image(pid).Equals("ModOrganizer.exe", StringComparison.OrdinalIgnoreCase)) {
					log.Warn($"Stop: ending {Procs.Image(pid)} (process {pid}), started by this run");
					Procs.KillTree(pid);
				}
			}
		};
		Dictionary<string, byte[]>? snap0 = null;
		var total = Stopwatch.StartNew();
		// true once a finished run's profile is back: from there nothing may put it back a second time, and what goes wrong is a
		// note on a finished run. afterRan: the setup's after-run commands were reached (they never run twice, and never not at all)
		var finished = false;
		var afterRan = false;
		try {
			Native.KeepAwake(true);
			commands.Start();
			// what changed outside the runner is looked at before the setup's own before-run commands can change anything (Mod
			// Organizer may still be open: its files as saved); what those commands change, and Mod Organizer's last save as it
			// closes, is the run's (MO2 is closed only after them - one may be the claim that makes closing it safe)
			NoteChanges(mo2, a_req.DataDir, Mo2Changes.Outside, log);
			foreach (var cmd in setup.BeforeRun) {
				Hook(ctx, cmd, "before the run", cts.Token);
			}
			// the one close of an MO2 the player opened that the player asked for: pressing Start (the preview says MO2 closes);
			// from here on an MO2 the player opens is waited for, never closed (Mo2Control.WaitForTheirs)
			control.AllowCloseTheirs = true;
			try {
				// a Stop pressed while Mod Organizer is still closing ends the wait (nothing is changed yet)
				control.Close(null, cts.Token);
			} finally {
				control.AllowCloseTheirs = false;
			}
			NoteChanges(mo2, a_req.DataDir, RunWho(journal), log);
			ToolSettings.UndoLeftSwitch(setup, profile, log);
			BackupProfile(ctx);
			// the output mods this run is about to make go on record BEFORE the first is made: a failure or a crash from here
			// on finds them there and takes the empty ones out again (a failed run leaves no empty output in Mod Organizer)
			var outputs = todo.Select(s => new StepSettings(s.Settings).OutputMod).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			var listed = profile.States();
			journal.Added = outputs.Where(m => !listed.ContainsKey(m)).ToList();
			journal.Save();
			foreach (var om in outputs) {
				profile.EnsureMod(om, log);
			}
			journal.Added = [.. profile.Added];
			snap0 = profile.Snapshot();
			profile.OursFile = Path.Combine(journal.Folder, Profile.WatchFileName);
			profile.WatchForOthers();
			journal.SaveSnapshot(snap0);
			journal.Save();

			var keys = todo.Select(s => s.Key).ToList();
			var lead = keys.TakeWhile(k => k == "shaders").ToList();
			var playsave = keys.Contains("playsave");
			var outputsOff = false;
			foreach (var s in todo) {
				if (s.Key == "playsave") {
					continue;
				}
				if (!outputsOff && !lead.Contains(s.Key)) {
					if (keys.Contains("cleansave")) {
						RunPhase(ctx, "cleansave", "the clean save, first half: to QASmoke with the profile as it is", () => InGame.Deactivate(ctx, new StepSettings(s.Key == "cleansave" ? s.Settings : setup.StepSettings("cleansave")!)));
						control.Close();
					}
					OutputsOff(ctx);
					outputsOff = true;
				}
				if (!RunStep(ctx, s)) {
					return Finish(ctx, snap0, commands.Stopped ? RunState.Stopped : RunState.Failed);
				}
				// "stop after this step" holds while anything is still to come - a later tool, or the save to play, which starts the
				// game: asked during the last tool it once started the game all the same
				if (journal.StopAfterStep && journal.Steps.FirstOrDefault(r => r.State == StepState.Pending) is { } next) {
					log.Say("stopped after this step, as asked");
					foreach (var r in journal.Steps.Where(r => r.State == StepState.Pending)) {
						r.State = StepState.Skipped;
					}
					return Finish(ctx, snap0, RunState.Stopped, $"stopped after {StepCatalog.Name(s.Key)}, as asked - carry on from {next.Name}");
				}
			}
			var outcome = Finish(ctx, snap0, RunState.Finished);
			if (outcome.State != RunState.Finished) {
				return outcome;
			}
			finished = true;
			// still the run going, to the window, until the last chore is done (Stop ends the chores; the profile is back already)
			journal.FinishingUp = true;
			journal.TrySave();
			if (playsave) {
				var ps = todo.First(t => t.Key == "playsave");
				// still the running run to the window while its game is up (the profile is already back - nothing to restore)
				journal.InGameAfter = true;
				journal.Save();
				var made = false;
				try {
					made = RunStep(ctx, ps);
				} finally {
					journal.InGameAfter = false;
					journal.NeedsRestore = journal.Kept.Any(k => !k.Done);
					journal.Save();
				}
				if (!made) {
					control.Close();
					journal.State = RunState.Failed;
					journal.Verdict = $"every tool finished; the save to play was not made: {journal.Step("playsave")?.Note}";
					log.Simple("Done, except the save to play: " + journal.Verdict);
					SaySaves(ctx);
					journal.Ended = DateTime.Now;
					journal.TrySave();
					PruneBackups(ctx);
					return new RunOutcome(journal.State, journal.Verdict, journal.Folder);
				}
				control.Close();
			}
			// The run has finished and the profile is back. Each chore from here stands on its own: one that fails is a note on a
			// finished run - never a second put-back, never the after-run commands twice
			void Chore(string a_what, Action a_do)
			{
				try {
					a_do();
				} catch (OperationCanceledException) {
					log.Warn($"{a_what}: left out, Stop was pressed");
				} catch (Exception e) {
					log.Warn($"{a_what} did not work: {Plain(e)}");
					journal.Notes.Add($"{a_what} did not work: {Plain(e)}");
				}
			}
			Chore("clearing the scratch folder", () => RecycleScratch(ctx, todo));
			afterRan = true;
			// each one runs, whatever the one before it did and whether or not Stop is pressed: they give back what the before-run
			// commands took. A Stop only shortens how long each is waited for
			foreach (var cmd in setup.AfterRun) {
				journal.Doing = "the setup's command after the run";
				journal.TrySave();
				Chore("the command after the run", () => Hook(ctx, cmd, "after the run", CancellationToken.None, cts.Token));
			}
			Chore("the look at Mod Organizer's overwrite", () => ReportOverwrite(ctx));
			Chore("tidying old backups", () => PruneBackups(ctx));
			Chore("noting the tools' versions for the next run", () => ToolVersions.Remember(setup, mo2, journal.Steps.Where(r => r.State == StepState.Done).Select(r => r.Key)));
			// his checklist 456: said at the end too - a run's own placement may have just moved them; a look that fails changes nothing
			Chore("the look at the output plugins' places", () => {
				foreach (var i in PluginPlaces.Check(profile.ActivePlugins())) {
					log.Warn(i.Text);
					log.Simple("Note: " + i.Text);
				}
			});
			journal.Verdict = $"finished: {todo.Count} step(s) in {total.Elapsed.TotalMinutes.ToString("0", InvariantCulture)} min";
			journal.Ended = DateTime.Now;
			journal.TrySave();
			log.Say("ALL DONE: " + journal.Verdict);
			log.Simple("ALL DONE: " + journal.Verdict);
			// his checklist 454: the old outputs packed into one zip, as the very last thing and only after a run that finished -
			// it can take minutes, the window says so, and Stop ends it at once (the run stays finished)
			if (Setup.Bool(setup.Root, "zipBackups") != false && !journal.Parked.Any(p => !p.Replaced && !p.PutBack)) {
				Chore("packing the old outputs", () => RunUndo.Pack(ctx.BackupFolder, log, a_doing => {
					journal.Doing = a_doing;
					journal.TrySave();
				}, cts.Token));
			}
			return new RunOutcome(RunState.Finished, journal.Verdict, journal.Folder);
		} catch (Exception e) when (snap0 == null) {
			// nothing was switched yet: the run stops with the profile untouched (a Stop pressed this early reads as stopped)
			var state = commands.Stopped ? RunState.Stopped : RunState.Failed;
			journal.State = state;
			journal.Verdict = "stopped before anything was changed: " + (commands.Stopped ? "Stop was pressed" : Plain(e));
			// an output mod made a moment ago is still empty: out of the mod list again (Mod Organizer is closed by now)
			var left = new List<string>();
			foreach (var a in journal.Added.ToList()) {
				try {
					if (profile.TakeOutEmptyOutput(a, journal.BackupDir, log)) {
						journal.Added.Remove(a);
					} else if (profile.States().ContainsKey(a)) {
						left.Add(a);
					}
				} catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException or Mo2BusyException or InvalidOperationException) {
					left.Add(a);
				}
			}
			journal.Ended = DateTime.Now;
			journal.TrySave();
			log.Error(journal.Verdict);
			log.Simple("The run " + journal.Verdict + (left.Count == 0 ? ". Nothing in Mod Organizer was changed." :
				". Your mods are as they were; the new, empty output mod(s) it had just added are still in the list: " + string.Join(", ", left) + "."));
			return new RunOutcome(state, journal.Verdict, journal.Folder);
		} catch (Exception e) when (finished) {
			// after the put-back of a finished run (the save to play's own ending, a record the disk refused): the run stays finished
			log.Warn("after the run had finished: " + Plain(e));
			journal.Notes.Add("after the run had finished: " + Plain(e));
			if (journal.State == RunState.Finished && journal.Verdict == "") {
				journal.Verdict = $"finished: {todo.Count} step(s) in {total.Elapsed.TotalMinutes.ToString("0", InvariantCulture)} min";
			}
			journal.Ended ??= DateTime.Now;
			journal.TrySave();
			return new RunOutcome(journal.State, journal.Verdict, journal.Folder);
		} catch (Exception e) {
			log.Error("the run stopped: " + Plain(e));
			return Finish(ctx, snap0, commands.Stopped ? RunState.Stopped : RunState.Failed, Plain(e));
		} finally {
			commands.Dispose();
			Native.KeepAwake(false);
			// the run is over: it no longer has an owner (a run that still needs its profile put back is then offered at once)
			journal.Pid = 0;
			journal.FinishingUp = false;
			journal.Doing = "";
			journal.TrySave();
			// after a run that did not finish (or whose ending never reached them) the setup's after-run commands still run, once:
			// they give back what the before-run commands took. No Stop can cut them short here - the run is already over
			if (!afterRan) {
				foreach (var cmd in setup.AfterRun) {
					try {
						Hook(ctx, cmd, "after the run", CancellationToken.None);
					} catch (StepFailedException e) {
						log.Warn(e.Message);
					}
				}
			}
			NoteChanges(mo2, a_req.DataDir, RunWho(journal), log);
		}
	}

	public static string RunWho(Journal a_j) => $"the run of {a_j.Started.ToString("MMM d HH:mm", InvariantCulture)} ({a_j.SetupName})";

	// Mod Organizer as it is now against the last look, kept as a_who's changes (History > What MO2 changed) - never a reason
	// to stop a run
	static void NoteChanges(Mo2Instance a_mo2, string a_dataDir, string a_who, RunLog a_log)
	{
		try {
			var rows = Mo2Changes.Record(a_mo2, a_dataDir, a_who);
			if (rows.Count > 0) {
				a_log.Detail($"{rows.Count} change(s) in Mod Organizer {(a_who == Mo2Changes.Outside ? "made outside the runner since its last look" : "by this run")} - History > What MO2 changed");
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) {
			a_log.Warn("could not note what Mod Organizer changed: " + e.Message);
		}
	}

	// an error in words for the player. The engine's own (a step that failed, Mod Organizer busy) and Windows' own about a file
	// read as they are; anything else is a fault of the program, said as one - its type helps a bug report, the log has the rest
	public static string Plain(Exception a_e) => a_e switch {
		OperationCanceledException => "stopped on request",
		StepFailedException or Mo2BusyException or IOException or UnauthorizedAccessException => a_e.Message,
		_ when ThrownHere(a_e) => a_e.Message,
		_ => $"something unexpected went wrong ({a_e.GetType().Name}: {a_e.Message}) - the run's detailed log has more",
	};

	// thrown by the engine's own code, so worded by it for the player ("Windows could not start ...", "not in the mod list: ...");
	// the same type thrown inside .NET reads "Sequence contains no matching element"
	static bool ThrownHere(Exception a_e)
	{
		try {
			return a_e.TargetSite?.DeclaringType?.Assembly == typeof(Runner).Assembly;
		} catch (Exception e) when (e is NotSupportedException or InvalidOperationException) {
			return false;
		}
	}

	static void RunPhase(RunContext a_ctx, string a_step, string a_title, Action a_work)
	{
		a_ctx.Log.Say("");
		a_ctx.Log.Say($"=== {a_title} ===");
		var r = a_ctx.Journal.Step(a_step);
		if (r != null) {
			r.State = StepState.Running;
			r.Started ??= DateTime.Now;
			r.Activity = a_title;
			a_ctx.Journal.Save();
		}
		try {
			a_work();
		} catch (Exception e) when (r != null) {
			r.State = e is OperationCanceledException ? StepState.Stopped : StepState.Failed;
			r.Note = Plain(e);
			r.Ended = DateTime.Now;
			r.Activity = "";
			a_ctx.Journal.TrySave();
			throw;
		}
	}

	// every output the setup's steps make goes OFF before the first step (his rule 2026-10-02: no tool reads its own or a later
	// step's output) - except the shader caches (no tool reads one, and with it off every game start recompiles) - plus every
	// mod a step replaces and the setup's LOD outputs
	static void OutputsOff(RunContext a_ctx)
	{
		var off = OutputsToTurnOff(a_ctx.Setup, a_ctx.Profile.States()).Select(o => o.Mod).ToList();
		// a ticked mod whose plugin needs one of those as a master goes off with them (a tool refuses a plugin with a missing master)
		var deps = a_ctx.Profile.Dependents(off);
		a_ctx.Log.Say("every output OFF before the first tool:");
		foreach (var (m, why) in deps) {
			a_ctx.Log.Detail($"also off for the run: {m} ({why})");
		}
		a_ctx.Control.Close(null, a_ctx.Cancel);
		a_ctx.Profile.SetMods([], off.Concat(deps.Keys), a_ctx.Log);
	}

	// the ticked mods a run switches off before its first tool, each with why (the run itself and its preview use this one list)
	public static List<(string Mod, string Why)> OutputsToTurnOff(Setup a_setup, IReadOnlyDictionary<string, char> a_states)
	{
		var outList = new List<(string Mod, string Why)>();
		void Add(string a_mod, string a_why)
		{
			if (a_states.TryGetValue(a_mod, out var f) && f == '+' && !outList.Any(o => string.Equals(o.Mod, a_mod, StringComparison.OrdinalIgnoreCase))) {
				outList.Add((a_mod, a_why));
			}
		}
		var all = a_setup.EnabledSteps.Where(s => s.Key != "shaders").Select(s => (Name: StepCatalog.Name(s.Key), S: new StepSettings(s.Settings))).ToList();
		foreach (var (name, s) in all) {
			if (s.OutputMod is { } om) {
				Add(om, "made by " + name);
			}
		}
		foreach (var (name, s) in all) {
			foreach (var m in s.List("replaces")) {
				Add(m, "replaced by " + name);
			}
		}
		foreach (var m in a_setup.LodOutputs) {
			Add(m, "a LOD output of this setup");
		}
		return outList;
	}

	// his checklist 270 ("all 3."): only the last few runs' backups are kept - the setup's "keepBackups" (default 5, 0 = keep
	// all). Only THIS setup's run folders go, oldest first, never the running one or one an unfinished run still needs.
	// a run that did not finish may leave the runner's own save (QASmoke, DynDOLOD off) as the newest one: said in its verdict
	static void SaySaves(RunContext a_ctx)
	{
		if (InGame.SaveWarning(a_ctx) is { } w) {
			a_ctx.Journal.Verdict += " - " + w;
			a_ctx.Journal.Notes.Add(w);
			a_ctx.Log.Warn(w);
		}
	}

	public static void PruneBackups(RunContext a_ctx)
	{
		// cleanup only: whatever goes wrong here is a warning, never a failed run
		try {
			PruneBackupsUnguarded(a_ctx);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) {
			a_ctx.Log.Warn("old runs' backups not tidied: " + e.Message);
		}
	}

	static void PruneBackupsUnguarded(RunContext a_ctx)
	{
		PruneBackupsOf(a_ctx.Setup, Path.GetDirectoryName(a_ctx.Journal.Folder)!, a_ctx.BackupFolder, Setup.Int(a_ctx.Setup.Root, "keepBackups") ?? 5, a_ctx.Log);
		PruneRunRecords(Path.GetDirectoryName(a_ctx.Journal.Folder)!, a_ctx.Journal.SetupFile, a_ctx.Journal.Folder, RunsKept, a_ctx.Log);
	}

	// the run records a setup keeps (History shows as many): the window looks at every record once a second, and a program used
	// daily for a year would otherwise keep hundreds of folders and logs it never shows
	public const int RunsKept = 100;

	// all but the newest a_keep run records of this setup removed - never the running one, never one that did not finish (its
	// put-back still needs it); returns the folders removed
	public static List<string> PruneRunRecords(string a_runsDir, string a_setupFile, string? a_current, int a_keep, RunLog a_log)
	{
		var removed = new List<string>();
		var mine = Journal.All(a_runsDir).Where(j => string.Equals(j.SetupFile, a_setupFile, StringComparison.OrdinalIgnoreCase)).ToList();
		foreach (var j in mine.Skip(a_keep).Where(j => !j.Unfinished && (a_current == null || !Files.SamePath(j.Folder, a_current)))) {
			try {
				var d = new DirectoryInfo(j.Folder);
				foreach (var f in d.EnumerateFiles("*", SearchOption.AllDirectories).Where(f => f.IsReadOnly)) {
					f.IsReadOnly = false;
				}
				d.Delete(true);
				removed.Add(j.Folder);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				a_log.Warn($"an old run record could not be removed ({e.Message}): {j.Folder}");
			}
		}
		if (removed.Count > 0) {
			a_log.Detail($"{removed.Count} old run record(s) removed (the newest {a_keep} of this setup are kept)");
		}
		return removed;
	}

	// this setup's own run backups (by the run-folder name the runner gives them), newest first, with their size in bytes
	public static List<(DirectoryInfo Dir, long Bytes)> BackupsOf(Setup a_setup) => BackupDirsOf(a_setup).Select(d => (d, Files.SizeOf(d.FullName))).ToList();

	// the same folders, newest first, without measuring them (pruning needs no sizes - a big LOD backup is never walked for it)
	public static List<DirectoryInfo> BackupDirsOf(Setup a_setup)
	{
		var root = a_setup.BackupDir;
		if (root == "" || !Directory.Exists(root)) {
			return [];
		}
		var own = new System.Text.RegularExpressions.Regex(@"^\d{4}-\d\d-\d\d \d\d-\d\d-\d\d " + System.Text.RegularExpressions.Regex.Escape(a_setup.Name) + "$");
		return new DirectoryInfo(root).GetDirectories().Where(d => own.IsMatch(d.Name)).OrderByDescending(d => d.Name, StringComparer.Ordinal).ToList();
	}

	// all but the newest a_keep of this setup's run backups removed - never the running one (a_current) or one an unfinished run
	// still needs; returns the folders removed. Settings' "remove old backups now" calls it with the run lock held
	public static List<string> PruneBackupsOf(Setup a_setup, string a_runsDir, string? a_current, int a_keep, RunLog a_log)
	{
		var removed = new List<string>();
		if (a_keep <= 0) {
			return removed;
		}
		var needed = Journal.All(a_runsDir).Where(j => j.Unfinished && !string.IsNullOrWhiteSpace(j.BackupDir)).Select(j => Path.GetFullPath(j.BackupDir)).ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (a_current != null) {
			needed.Add(Path.GetFullPath(a_current));
		}
		// the run an undo would take back: later runs that left nothing never push its backup out
		if (Journal.All(a_runsDir).Where(j => !j.Undone && RunUndo.LeftSomething(j) && !string.IsNullOrWhiteSpace(j.BackupDir) && string.Equals(j.SetupFile, a_setup.File, StringComparison.OrdinalIgnoreCase))
			    .OrderByDescending(j => j.Started).FirstOrDefault() is { } last) {
			needed.Add(Path.GetFullPath(last.BackupDir));
		}
		foreach (var d in BackupDirsOf(a_setup).Skip(a_keep).Where(b => !needed.Contains(b.FullName))) {
			try {
				foreach (var f in d.EnumerateFiles("*", SearchOption.AllDirectories).Where(f => f.IsReadOnly)) {
					f.IsReadOnly = false;
				}
				d.Delete(true);
				removed.Add(d.FullName);
				a_log.Detail("an old run's backup removed (only the last " + a_keep + " are kept): " + d.Name);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				a_log.Warn($"an old run's backup could not be removed ({e.Message}): {d.FullName}");
			}
		}
		return removed;
	}

	// one step: run, put its parked output back if it did not finish, its built output switched on, the plugins placed
	static bool RunStep(RunContext a_ctx, Setup.StepEntry a_s)
	{
		var r = a_ctx.Journal.Step(a_s.Key)!;
		var step = StepRegistry.For(a_s.Key);
		a_ctx.OutputUntouched = false;
		a_ctx.Log.Say("");
		a_ctx.Log.Say($"=== {StepCatalog.Name(a_s.Key)} ===");
		a_ctx.Log.Simple($"{a_ctx.Journal.Steps.IndexOf(r) + 1}/{a_ctx.Journal.Steps.Count} {StepCatalog.Name(a_s.Key)} started");
		r.State = StepState.Running;
		r.Started ??= DateTime.Now;
		r.Activity = "starting";
		a_ctx.Journal.Save();
		Dictionary<string, DateTime>? overwriteBefore = null;
		var stepBegan = DateTime.UtcNow;
		List<string> learned = [];
		var sw = Stopwatch.StartNew();
		try {
			// inside the try: a folder of overwrite that cannot be listed fails this step like any other fault, with its record closed
			overwriteBefore = OverwriteFiles(a_ctx);
			a_ctx.Cancel.ThrowIfCancellationRequested();
			if (step == null) {
				throw new StepFailedException("this step is not available in this version");
			}
			learned = ToolSettings.Apply(a_ctx, a_s.Key);
			var ranFrom = DateTime.Now;
			step.Run(a_ctx, new StepSettings(a_s.Settings));
			// a note for the player, never what decides a step that has finished
			try {
				ToolSettings.CheckValues(a_ctx, a_s.Key, ranFrom);
			} catch (Exception e) when (e is not OperationCanceledException) {
				a_ctx.Log.Warn("the values the tool ran with could not be compared with the learned ones: " + Plain(e));
			}
			StepKit.CloseTool(a_ctx, new StepSettings(a_s.Settings).Str("image"));
			foreach (var live in learned) {
				StepKit.PutFilesBack(a_ctx, live);
			}
			QuietGame.PutBack(a_ctx);
			// Every file the step changed only for its own run goes back now (a tool's settings pointed at this run, DynDOLOD's
			// preset): left to the run's end, a player who opened the tool hours into a run found it still set for the run. What
			// a step changed for good is settled by the step itself and stays. A file that will not go back yet is tried again at
			// the put-back, and said there
			StepKit.PutFilesBack(a_ctx);
		} catch (Exception e) {
			var stopped = e is OperationCanceledException;
			r.State = stopped ? StepState.Stopped : StepState.Failed;
			r.Note = Plain(e);
			r.Ended = DateTime.Now;
			r.Activity = "";
			a_ctx.Journal.TrySave();
			a_ctx.Log.Error($"{StepCatalog.Name(a_s.Key)} {(stopped ? "STOPPED" : "FAILED")}: {r.Note}");
			var why = stopped ? null : ToolErrors.Explain(a_s.Key, r.Note);
			if (why != null) {
				a_ctx.Log.Warn($"what it means: {why.What} What to do: {why.Todo}{(why.Help != null ? " Help: " + why.Help : "")}");
			}
			foreach (var line in ToolErrors.Simple(StepCatalog.Name(a_s.Key), r.State, r.Note, why)) {
				a_ctx.Log.Simple(line);
			}
			try {
				StepKit.CloseTool(a_ctx, new StepSettings(a_s.Settings).Str("image"));
				InGame.CloseGame(a_ctx);
			} catch (Exception e2) when (e2 is IOException or InvalidOperationException) {
				a_ctx.Log.Warn("could not close the tool: " + e2.Message);
			}
			PutBackParked(a_ctx, a_s.Key);
			StepKit.PutFilesBack(a_ctx);
			a_ctx.QuietFiles.Clear();
			// overwrite could not even be listed before the step: nothing in it can be told as the step's, so nothing is moved
			if (overwriteBefore != null) {
				MoveNewOverwrite(a_ctx, overwriteBefore, stepBegan);
			}
			return false;
		}
		foreach (var m in new[] { a_ctx.OutputUntouched ? null : new StepSettings(a_s.Settings).OutputMod }.OfType<string>().Concat(a_ctx.PendingEnable)) {
			StepKit.MarkReplaced(a_ctx, m);
			if (!a_ctx.Journal.Built.Contains(m)) {
				a_ctx.Journal.Built.Add(m);
			}
		}
		foreach (var p in a_ctx.Journal.Parked.Where(p => p.Step == a_s.Key && !p.PutBack)) {
			p.Replaced = true;
		}
		// The step is done from here, and on record as done BEFORE Mod Organizer is closed to tick its output: a Stop (or a
		// Mod Organizer that is busy) in those seconds ends the run with a step that finished - never one left "running" for
		// good - and the put-back ticks what it built
		r.State = StepState.Done;
		r.Ended = DateTime.Now;
		r.Activity = "";
		r.Progress = null;
		a_ctx.Journal.Save();
		a_ctx.Log.Say($"=== {StepCatalog.Name(a_s.Key)} done in {sw.Elapsed.TotalMinutes:0.0} min ===");
		a_ctx.Log.Simple($"{StepCatalog.Name(a_s.Key)} finished in {sw.Elapsed.TotalMinutes:0.0} min");
		ApplyAfter(a_ctx, a_ctx.Cancel);
		return true;
	}

	// a_ct: what may end the wait for Mod Organizer to close - a step's Stop; nothing at the put-back, which a Stop has often
	// just asked for and must still tick what was built
	static void ApplyAfter(RunContext a_ctx, CancellationToken a_ct)
	{
		var st = a_ctx.Profile.States();
		var en = a_ctx.PendingEnable.Distinct().Where(m => st.TryGetValue(m, out var f) && f != '+').ToList();
		a_ctx.PendingEnable.Clear();
		if (en.Count > 0 || a_ctx.Setup.Placement.Count > 0) {
			a_ctx.Control.Close(null, a_ct);
		}
		if (en.Count > 0) {
			a_ctx.Log.Detail("new outputs switched on:");
			a_ctx.Profile.SetMods(en, [], a_ctx.Log);
			a_ctx.Profile.ActivatePlugins(en, a_ctx.Setup.PlaceAfter);
		}
		if (a_ctx.Setup.Placement.Count > 0) {
			var moved = a_ctx.Profile.PlacePlugins(a_ctx.Setup.Placement);
			a_ctx.Log.Detail("plugin placement: " + (moved.Count > 0 ? "updated " + string.Join(", ", moved) : "already where they go"));
		}
	}

	// a step that did not finish: its parked old output goes back into its mod (what it half wrote goes to the backup)
	public static List<string> PutBackParked(RunContext a_ctx, string? a_step)
	{
		var failed = new List<string>();
		foreach (var p in a_ctx.Journal.Parked.Where(p => (a_step == null || p.Step == a_step) && !p.Replaced && !p.PutBack && !p.Lost).Reverse()) {
			try {
				// Its copy in the backup is not there. On a drive that is gone it may come back with the drive: not put back, and
				// asked again. In a backup that is there it was cleared away: nothing can bring it back
				var gone = !Directory.Exists(p.To);
				if (gone && Path.GetPathRoot(Path.GetFullPath(p.To)) is { Length: > 0 } drive && !Directory.Exists(drive)) {
					throw new IOException($"the backup's drive {drive} is not there");
				}
				if (Directory.Exists(p.From)) {
					var half = Path.Combine(a_ctx.BackupFolder, "unfinished", p.Mod);
					var moved = Directory.EnumerateFileSystemEntries(p.From).Any(e => !Path.GetFileName(e).Equals("meta.ini", StringComparison.OrdinalIgnoreCase));
					if (moved) {
						Files.MoveContents(p.From, half, n => n.Equals("meta.ini", StringComparison.OrdinalIgnoreCase));
						// nothing in a mod folder is ever erased: what could not be moved stops the put-back, named
						var left = Directory.EnumerateFileSystemEntries(p.From).Where(e => !Path.GetFileName(e).Equals("meta.ini", StringComparison.OrdinalIgnoreCase)).ToList();
						if (left.Count > 0) {
							throw new IOException($"{left.Count} half-written file(s) could not be moved out, e.g. {Path.GetFileName(left[0])}");
						}
					}
				}
				if (gone) {
					// never said as "put back", never ticked holding nothing (PutBack keeps it off), and never counted as still
					// out either - that would keep every later put-back from ending
					p.Lost = true;
					var lost = p.IsFolder ? $"{p.Mod}: what stood there before the run is no longer in this run's backup ({p.To}), so it could not be put back"
						: $"{p.Mod}: its old output is no longer in this run's backup ({p.To}), so it could not be put back - the mod is left switched off";
					a_ctx.Journal.Notes.Add(lost);
					a_ctx.Journal.TrySave();
					a_ctx.Log.Error(lost);
					a_ctx.Log.Simple("Note: " + lost);
					continue;
				}
				Files.MoveContents(p.To, p.From);
				p.PutBack = true;
				// the move is made: a record the disk refuses right now is not a put-back that failed
				a_ctx.Journal.TrySave();
				a_ctx.Log.Detail($"{p.Mod}: its old output put back");
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				failed.Add(p.Mod);
				a_ctx.Log.Error($"{p.Mod}: its old output could NOT be put back ({e.Message}) - it is in {p.To}");
			}
		}
		return failed;
	}

	// an old output that was no longer in the backup is not "back as it was": named in the verdict ("" when there is none)
	static string LostNote(Journal a_j)
	{
		var lost = a_j.Parked.Where(p => p.Lost && !p.IsFolder).Select(p => p.Mod).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var folders = a_j.Parked.Where(p => p.Lost && p.IsFolder).Select(p => p.Mod).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		return (lost.Count == 0 ? "" : $" - except {string.Join(", ", lost)}: the old output was no longer in the backup, so {(lost.Count == 1 ? "it is" : "they are")} switched off") +
			(folders.Count == 0 ? "" : $" - what stood in {string.Join(", ", folders)} before the run was no longer in the backup");
	}

	// the order files back as they were before the run, then this run's outputs on, the replaced mods off, the plugins placed
	static RunOutcome Finish(RunContext a_ctx, Dictionary<string, byte[]>? a_snap, RunState a_state, string? a_why = null)
	{
		var j = a_ctx.Journal;
		// the put-back comes first: a record the disk will not take right now is said, and never what leaves a profile half switched
		void Keep()
		{
			if (!j.TrySave()) {
				a_ctx.Log.Warn("the run's record could not be written just now (is the disk full?) - the put-back goes on");
			}
		}
		foreach (var r in j.Steps.Where(r => r.State == StepState.Pending)) {
			r.State = StepState.Skipped;
		}
		// a step still on record as running was cut off between its own beginning and its own ending (the run stopped while Mod
		// Organizer closed around it): closed here, never left "running" in the history for good
		foreach (var r in j.Steps.Where(r => r.State == StepState.Running)) {
			r.State = a_ctx.Cancel.IsCancellationRequested ? StepState.Stopped : StepState.Failed;
			r.Ended ??= DateTime.Now;
			r.Activity = "";
			r.Progress = null;
			if (r.Note == "") {
				r.Note = a_why ?? "";
			}
		}
		var notBack = PutBackParked(a_ctx, null);
		var filesNotBack = StepKit.PutFilesBack(a_ctx);
		if (notBack.Count > 0 || filesNotBack.Count > 0) {
			// a mod whose old output could not come back is kept OFF (never ticked half empty, PutBack) and Restore stays on offer
			j.NeedsRestore = true;
			Keep();
		}
		try {
			PutBack(a_ctx, a_snap);
		} catch (Exception e) {
			j.State = RunState.Failed;
			j.NeedsRestore = true;
			j.Verdict = $"the profile could NOT be put back ({Plain(e)}) - press \"Put my profile back\" once Mod Organizer can close";
			Keep();
			a_ctx.Log.Error(j.Verdict);
			a_ctx.Log.Simple("Stopped: " + j.Verdict);
			return new RunOutcome(j.State, j.Verdict, j.Folder);
		}
		if (notBack.Count > 0 || filesNotBack.Count > 0) {
			// the run did not end cleanly, whatever its steps did: said so, never "finished" or "back as it was"
			j.State = RunState.Failed;
			j.Ended = DateTime.Now;
			j.Verdict = "the profile is back except " + string.Join(", ", notBack.Concat(filesNotBack.Select(Path.GetFileName)).OfType<string>()) +
				" (kept off / in the backup) - press \"Put my profile back\" once the files are free";
			Keep();
			a_ctx.Log.Error(j.Verdict);
			a_ctx.Log.Simple("Stopped: " + j.Verdict);
			return new RunOutcome(j.State, j.Verdict, j.Folder);
		}
		var failed = j.Steps.FirstOrDefault(r => r.State is StepState.Failed or StepState.Stopped);
		// stopped between two steps: said as after the last one that finished, never "at the start" of a run that built things
		var where = failed != null ? "at " + failed.Name : j.Steps.LastOrDefault(r => r.State == StepState.Done) is { } last ? "after " + last.Name : "at the start";
		var except = LostNote(j);
		j.State = a_state == RunState.Finished ? RunState.Running : a_state;
		j.Verdict = a_state switch {
			RunState.Finished => "",
			RunState.Stopped => a_why != null ? a_why + except : $"stopped {where} - the profile is back as it was, with this run's finished outputs on{except}; carry on from {failed?.Name}",
			_ => $"stopped {where}: {failed?.Note ?? a_why} - the profile is back as it was, with this run's finished outputs on{except}",
		};
		if (a_state != RunState.Finished) {
			SaySaves(a_ctx);
			j.Ended = DateTime.Now;
			a_ctx.Log.Say(j.Verdict);
			a_ctx.Log.Simple((a_state == RunState.Stopped ? "Stopped: " : "Failed: ") + j.Verdict);
			// a failed or stopped run's backups are pruned too (they piled up until the next finished run - the fourth review, L7);
			// a folder an unfinished run still names is never touched
			Keep();
			PruneBackups(a_ctx);
		} else {
			j.State = RunState.Finished;
		}
		Keep();
		return new RunOutcome(j.State, j.Verdict, j.Folder);
	}

	// the player's own changes laid back over a put-back's order files: their mods ticked / unticked (a mod they installed gets its
	// line), the plugins of a mod they ticked as they had them where they had chosen (else ticked, as Mod Organizer does), and the
	// plugins of a mod they unticked off unless another ticked mod still provides them
	public static void ReapplyTheirs(RunContext a_ctx, IReadOnlyDictionary<string, char> a_theirs, IReadOnlyDictionary<string, bool> a_marks)
	{
		var now = a_ctx.Profile.States();
		foreach (var m in a_theirs.Keys.Where(m => !now.ContainsKey(m) && Directory.Exists(a_ctx.Mo2.ModDir(m)))) {
			a_ctx.Profile.EnsureMod(m, a_ctx.Log);
		}
		var listed = a_ctx.Profile.States();
		var on = a_theirs.Where(p => p.Value == '+' && listed.ContainsKey(p.Key)).Select(p => p.Key).ToList();
		var off = a_theirs.Where(p => p.Value == '-' && listed.ContainsKey(p.Key)).Select(p => p.Key).ToList();
		a_ctx.Profile.SetMods(on, off, a_ctx.Log);
		a_ctx.Profile.ActivatePlugins(on);
		var theirPlugins = on.SelectMany(m => Plugins.InMod(a_ctx.Mo2.ModDir(m))).ToHashSet(StringComparer.OrdinalIgnoreCase);
		a_ctx.Profile.SetPluginMarks(a_marks.Where(p => theirPlugins.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));
		a_ctx.Profile.DeactivatePlugins(off, true);
	}

	public static void PutBack(RunContext a_ctx, Dictionary<string, byte[]>? a_snap)
	{
		// the run is over: Mod Organizer closes as after any run
		a_ctx.Control.CompanionOn = false;
		a_ctx.Control.Close();
		// what the player changed in Mod Organizer while the run went on (a mod ticked, unticked or installed) is kept, never
		// undone by the put-back - except on the mods this run owns (its outputs and the mods it replaces), which it decides
		var own = a_ctx.Journal.Added.Concat(a_ctx.Journal.Built).Concat(a_ctx.Journal.Retire).Concat(a_ctx.Journal.ParkedMods)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var theirs = a_ctx.Profile.ChangedByOthers().Where(p => !own.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
		if (a_snap != null && a_snap.Count > 0) {
			a_ctx.Log.Say("the profile as it was before the run, with this run's finished outputs on:");
			foreach (var a in a_ctx.Journal.Added) {
				a_ctx.Profile.Added.Add(a);
			}
			var before = Profile.StatesOf(a_snap);
			// a mod the player installed during the run keeps its line (its state is set again below)
			foreach (var m in theirs.Keys.Where(m => !before.ContainsKey(m))) {
				a_ctx.Profile.Added.Add(m);
			}
			// the player's plugin ticks as they left them, read before the order files go back
			var marks = theirs.Count > 0 ? a_ctx.Profile.PluginMarks() : [];
			a_ctx.Profile.Restore(a_snap);
			if (theirs.Count > 0) {
				a_ctx.Log.Warn($"you changed {theirs.Count} mod(s) in Mod Organizer during the run - kept as you left them: " +
					string.Join(", ", theirs.Select(p => (p.Value == '+' ? "on " : "off ") + p.Key)));
				ReapplyTheirs(a_ctx, theirs, marks);
			}
		}
		// an output left empty (its step failed, was stopped, or never ran) never stays in the mod list
		foreach (var a in a_ctx.Journal.Added.ToList()) {
			try {
				if (a_ctx.Profile.TakeOutEmptyOutput(a, a_ctx.BackupFolder, a_ctx.Log)) {
					a_ctx.Journal.Added.Remove(a);
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				a_ctx.Log.Warn($"{a}: is empty but could not be taken out ({e.Message})");
			}
		}
		// the order files are back: a record the disk refuses right now must not be what stops the outputs being ticked
		if (!a_ctx.Journal.TrySave()) {
			a_ctx.Log.Warn("the run's record could not be written just now (is the disk full?) - the put-back goes on");
		}
		var st = a_ctx.Profile.States();
		// replaced by this run for good, or its old output not back in it: still in the backup (off only while it is out), or
		// lost from it (a mod never goes on holding nothing)
		// (a folder's record carries a label, which may read like some mod's name and is never taken for one)
		var retire = a_ctx.Journal.Retire.Concat(a_ctx.Journal.Parked.Where(p => !p.Replaced && !p.PutBack && !p.IsFolder).Select(p => p.Mod))
			.Distinct(StringComparer.OrdinalIgnoreCase).Where(st.ContainsKey).ToList();
		if (retire.Count > 0) {
			a_ctx.Log.Detail("kept off (replaced by this run, or its old output not back in it): " + string.Join(", ", retire));
			a_ctx.Profile.SetMods([], retire, a_ctx.Log);
			a_ctx.Profile.DeactivatePlugins(retire);
		}
		// a mod whose old output is not back in it is not switched on again a line later because an earlier step of this run had
		// built it once
		var stillOut = a_ctx.Journal.Parked.Where(p => !p.Replaced && !p.PutBack && !p.IsFolder).Select(p => p.Mod).ToHashSet(StringComparer.OrdinalIgnoreCase);
		a_ctx.PendingEnable.AddRange(a_ctx.Journal.Built.Where(m => st.ContainsKey(m) && !stillOut.Contains(m)));
		// a Stop is often why this put-back runs: it never ends the wait for Mod Organizer here
		ApplyAfter(a_ctx, CancellationToken.None);
	}

	// an interrupted run (a crash, Stop now, a power cut): every unfinished output back, the profile as before + the finished
	// outputs on - the window's Restore button and the command line's --restore
	public static RunOutcome Restore(Journal a_j)
	{
		if (a_j.OwnerAlive) {
			return new RunOutcome(a_j.State, "that run is still going", a_j.Folder);
		}
		// an undo that did not end: only ending it puts things right
		if (a_j.Undoing) {
			return Undo(a_j);
		}
		// one put-back at a time, never beside a run
		using var hold = HoldIfFree();
		return hold == null ? new RunOutcome(a_j.State, "a run or another put-back is going - try again when it is done", a_j.Folder) : RestoreLocked(a_j);
	}

	static RunOutcome RestoreLocked(Journal a_j)
	{
		// when the cut-off run last wrote its record, read before this put-back writes it again: the time that run died
		var lastWritten = File.GetLastWriteTime(a_j.File);
		Setup setup;
		try {
			setup = Setup.Load(a_j.SetupFile);
		} catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException) {
			return new RunOutcome(RunState.Failed, "the setup does not read: " + e.Message, a_j.Folder);
		}
		// a Mod Organizer that moved, a run folder that cannot be written: said in words, never a crash of the command line
		Mo2Instance mo2;
		try {
			mo2 = Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return new RunOutcome(RunState.Failed, "could not put the profile back: " + e.Message, a_j.Folder);
		}
		using var opened = OpenLog(a_j.Folder, out var noLog);
		if (opened == null) {
			return new RunOutcome(RunState.Failed, "could not put the profile back: the run's log could not be opened (" + noLog + ")", a_j.Folder);
		}
		RunLog log = opened;
		// the player pressed Restore: the Mod Organizer the cut-off run left open (or their own) is closed, as Start closes it
		var control = new Mo2Control(mo2, a_j.Profile, log) { AllowCloseTheirs = true };
		var ctx = new RunContext {
			Setup = setup, Mo2 = mo2, Profile = new Profile(mo2, a_j.Profile, () => control.Running), Control = control, Log = log, Journal = a_j,
			Cancel = CancellationToken.None, BackupFolder = a_j.BackupDir,
		};
		log.Say("RESTORE: putting the profile back after the run that did not finish");
		// what the cut-off run last wrote: anything else in the mod list now - changed during the run or since - is the player's,
		// kept by the put-back as at the end of a run (an older run left no such file: nothing is kept, as before)
		ctx.Profile.OursFile = Path.Combine(a_j.Folder, Profile.WatchFileName);
		if (!a_j.Mo2KeptOpen && ctx.Profile.LoadOurs()) {
			log.Detail("the run's own switches read back - your own changes in Mod Organizer are kept");
		}
		// the run folder is <data>\Runs\<run>. What changed since the last look (the cut-off run's own changes, and anything
		// done after it) is noted first under a neutral name, so the put-back's look holds only the put-back
		var data = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(a_j.Folder)));
		if (data != null) {
			NoteChanges(mo2, data, "seen before putting back " + RunWho(a_j), log);
		}
		List<string> notBack;
		List<string> filesNotBack;
		try {
			control.Close();
			notBack = PutBackParked(ctx, null);
			filesNotBack = StepKit.PutFilesBack(ctx);
			PutBack(ctx, a_j.LoadSnapshot());
		} catch (Exception e) {
			log.Error("restore failed: " + Plain(e));
			return new RunOutcome(RunState.Failed, "could not put the profile back: " + Plain(e), a_j.Folder);
		}
		if (data != null) {
			NoteChanges(mo2, data, "putting back " + RunWho(a_j), log);
		}
		if (notBack.Count > 0 || filesNotBack.Count > 0) {
			// something is still out: Restore stays on offer (the window keeps its button) and says what
			a_j.NeedsRestore = true;
			a_j.Verdict = "put back except " + string.Join(", ", notBack.Concat(filesNotBack.Select(Path.GetFileName)).OfType<string>()) +
				" (kept off / in the backup) - press \"Put my profile back\" again once the files are free";
			a_j.Save();
			log.Error(a_j.Verdict);
			return new RunOutcome(RunState.Failed, a_j.Verdict, a_j.Folder);
		}
		// a step the crash cut off ends when its run record was last written - never left open (an open game step would read as
		// the runner's game running "until now" and hide every save the player made since - the fifth review, N1). The time was
		// read at the top: by now this put-back has written the record itself
		var died = a_j.Ended ?? lastWritten;
		foreach (var r in a_j.Steps.Where(r => r.State is StepState.Running or StepState.Pending)) {
			if (r.State == StepState.Running) {
				r.Ended ??= died;
			}
			r.State = r.State == StepState.Running ? StepState.Stopped : StepState.Skipped;
		}
		a_j.State = RunState.Restored;
		a_j.NeedsRestore = false;
		a_j.Ended ??= DateTime.Now;
		a_j.Verdict = "put back after an interrupted run - the profile is as it was, with the outputs that finished on" + LostNote(a_j);
		a_j.Save();
		log.Say(a_j.Verdict);
		return new RunOutcome(RunState.Restored, a_j.Verdict, a_j.Folder);
	}

	// his checklist 454: the last run undone - each output back to what it was before the run (the new one set aside in the run's
	// backup, never erased), outputs the run made new taken out, the order files as they were before it; whatever the player changed
	// in Mod Organizer since the run began stays
	public static RunOutcome Undo(Journal a_j)
	{
		using var hold = HoldIfFree();
		if (hold == null) {
			return new RunOutcome(a_j.State, "a run or a put-back is going - try again when it is done", a_j.Folder);
		}
		var data = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(a_j.Folder)));
		if (RunUndo.WhyNot(a_j, Journal.All(Path.GetDirectoryName(Path.GetFullPath(a_j.Folder))!).ToList()) is { } why) {
			return new RunOutcome(a_j.State, why, a_j.Folder);
		}
		Setup setup;
		try {
			setup = Setup.Load(a_j.SetupFile);
		} catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException) {
			return new RunOutcome(a_j.State, "the setup does not read: " + e.Message, a_j.Folder);
		}
		Mo2Instance mo2;
		try {
			mo2 = Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return new RunOutcome(a_j.State, "nothing was undone: " + e.Message, a_j.Folder);
		}
		using var opened = OpenLog(a_j.Folder, out var noLog);
		if (opened == null) {
			return new RunOutcome(a_j.State, "nothing was undone: the run's log could not be opened (" + noLog + ")", a_j.Folder);
		}
		RunLog log = opened;
		var control = new Mo2Control(mo2, a_j.Profile, log) { AllowCloseTheirs = true };
		var ctx = new RunContext {
			Setup = setup, Mo2 = mo2, Profile = new Profile(mo2, a_j.Profile, () => control.Running) { OursFile = Path.Combine(a_j.Folder, Profile.WatchFileName) }, Control = control, Log = log,
			Journal = a_j, Cancel = CancellationToken.None, BackupFolder = a_j.BackupDir,
		};
		ctx.Profile.LoadOurs();
		log.Say("UNDO: putting things back as they were before this run");
		try {
			control.Close();
			if (data != null) {
				NoteChanges(mo2, data, Mo2Changes.Outside, log);
			}
			// a zip that does not read has changed nothing and holds nothing up
			RunUndo.Unpack(a_j.BackupDir);
			// read once: pressed again, the list may already be the old one and no longer shows them
			if (a_j.UndoTheirs == null) {
				var found = ctx.Profile.ChangedByOthers();
				foreach (var m in a_j.Added.Concat(a_j.Built).Concat(a_j.Retire).Concat(a_j.ParkedMods)) {
					found.Remove(m);
				}
				a_j.UndoMarks = found.Count > 0 ? ctx.Profile.PluginMarks() : [];
				a_j.UndoTheirs = found;
			}
			var theirs = a_j.UndoTheirs;
			var marks = a_j.UndoMarks ?? [];
			a_j.Undoing = true;
			a_j.Save();
			var aside = Path.Combine(a_j.BackupDir, "undone");
			foreach (var p in a_j.Parked.Where(p => p.Replaced && !p.PutBack).Reverse().ToList()) {
				if (!p.SetAside && (!p.IsFolder || p.Moved)) {
					RunUndo.SetAside(p.From, aside, p.Mod);
					p.SetAside = true;
					a_j.Save();
				}
				if (Directory.Exists(p.To)) {
					Files.MoveContents(p.To, p.From);
				}
				p.PutBack = true;
				a_j.Save();
				log.Detail($"{p.Mod}: back as it was before this run");
			}
			// a mod a step changed in place (ACMOS's roads painted into a terrain LOD no step of this run rebuilt) keeps everything else it holds:
			// only those files go back. Where a step rebuilt the mod, its park record brought the whole mod back above
			var parkedMods = a_j.Parked.Where(p => p.Replaced && !p.IsFolder).ToList();
			var inPlace = a_j.Kept.Where(k => k.Settled && !parkedMods.Any(p => Files.IsUnder(k.Path, p.From))).ToList();
			foreach (var k in inPlace) {
				if (k.Backup != null && File.Exists(k.Backup)) {
					Files.WriteAtomic(k.Path, File.ReadAllBytes(k.Backup));
					log.Detail($"{Path.GetFileName(k.Path)}: back as it was before this run");
				}
			}
			// only what had no old output: one that had came back above, and pressed again it holds that old output
			foreach (var m in a_j.Built.Except(parkedMods.Select(p => p.Mod), StringComparer.OrdinalIgnoreCase).Where(m => Directory.Exists(mo2.ModDir(m)) && !inPlace.Any(k => Files.IsUnder(k.Path, mo2.ModDir(m))))) {
				RunUndo.SetAside(mo2.ModDir(m), aside, m);
				log.Detail($"{m}: this run's output set aside");
			}
			foreach (var a in a_j.Added) {
				ctx.Profile.Added.Add(a);
			}
			ctx.Profile.Restore(a_j.LoadSnapshot());
			if (theirs.Count > 0) {
				log.Warn($"you changed {theirs.Count} mod(s) in Mod Organizer since this run began - kept as you left them: " +
					string.Join(", ", theirs.Select(p => (p.Value == '+' ? "on " : "off ") + p.Key)));
				ReapplyTheirs(ctx, theirs, marks);
			}
			foreach (var a in a_j.Added.ToList()) {
				ctx.Profile.TakeOutEmptyOutput(a, a_j.BackupDir, log);
			}
		} catch (Exception e) {
			log.Error("undo failed: " + Plain(e));
			var left = a_j.Undoing ? "it stopped half way, and nothing new starts until it is undone again: what moved is on record, its files are in the run's backup" : "nothing was changed";
			if (a_j.Undoing) {
				a_j.State = RunState.Failed;
				a_j.Verdict = "the undo stopped half way (" + Plain(e) + ") - undo this run again once that is free";
			}
			a_j.Save();
			return new RunOutcome(RunState.Failed, "the run could not be undone whole: " + Plain(e) + " - " + left, a_j.Folder);
		}
		a_j.Undone = true;
		a_j.Undoing = false;
		a_j.UndoTheirs = null;
		a_j.UndoMarks = null;
		a_j.State = RunState.Restored;
		a_j.Verdict = "undone - your outputs and your mod and plugin lists are as they were before this run (the mods you ticked, unticked or installed since stay as you left them; this run's outputs are in its backup, under \"undone\")";
		a_j.Save();
		log.Say(a_j.Verdict);
		if (data != null) {
			NoteChanges(mo2, data, "undoing " + RunWho(a_j), log);
		}
		return new RunOutcome(RunState.Restored, a_j.Verdict, a_j.Folder);
	}

	static void BackupProfile(RunContext a_ctx)
	{
		var dst = Path.Combine(a_ctx.BackupFolder, "profile " + a_ctx.Profile.Name);
		Directory.CreateDirectory(dst);
		foreach (var f in Directory.GetFiles(a_ctx.Profile.Dir)) {
			Files.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
		}
		a_ctx.Log.Detail($"profile {a_ctx.Profile.Name} backed up");
	}

	// a tool's scratch folder (the setup's "scratch") is recycled after a whole run - its output is in its mod by then
	static void RecycleScratch(RunContext a_ctx, List<Setup.StepEntry> a_todo)
	{
		var scratch = Setup.Str(a_ctx.Setup.Root, "scratch");
		if (string.IsNullOrWhiteSpace(scratch)) {
			return;
		}
		foreach (var d in a_todo.Select(s => new StepSettings(s.Settings).Str("output_dir")).OfType<string>().Distinct()) {
			if (!Files.IsUnder(d, scratch) || !Directory.Exists(d)) {
				continue;
			}
			// its output went into its mod, so the folder is normally empty; one that still holds files is left (never a box on the
			// player's screen, never an erase)
			if (Files.CountFiles(d) > 0) {
				a_ctx.Log.Warn($"scratch folder left as it is - it still holds {Files.CountFiles(d)} file(s): {d}");
				continue;
			}
			try {
				Files.DeleteDir(d);
				a_ctx.Log.Detail("empty scratch folder removed: " + d);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				a_ctx.Log.Warn($"scratch folder not removed ({e.Message}): {d}");
			}
		}
	}

	// overwrite's files and their times (what a step adds there is told apart from what was there before)
	public static Dictionary<string, DateTime> OverwriteFiles(RunContext a_ctx)
	{
		var d = a_ctx.Mo2.OverwriteDir;
		return Directory.Exists(d) ? Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)
			.ToDictionary(f => f, File.GetLastWriteTimeUtc, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
	}

	// What a step made in overwrite: a file that was there and changed, or one that was not there and was written or created
	// since the step began. A path new to overwrite that carries a date from before the step is NOT the step's (his run of
	// 2026-11-10: a crash logger renamed its own older logs at each game start, and they were taken as the run's) - it stays.
	public static (List<string> Made, List<string> Older) NewInOverwrite(IReadOnlyDictionary<string, DateTime> a_before, IReadOnlyDictionary<string, DateTime> a_now, DateTime a_beganUtc,
		Func<string, DateTime>? a_created = null)
	{
		var made = new List<string>();
		var older = new List<string>();
		var since = a_beganUtc.AddSeconds(-2);
		foreach (var (f, t) in a_now) {
			if (a_before.TryGetValue(f, out var was)) {
				if (was != t) {
					made.Add(f);
				}
			} else if (t >= since || (a_created ?? File.GetCreationTimeUtc)(f) >= since) {
				made.Add(f);
			} else {
				older.Add(f);
			}
		}
		return (made, older);
	}

	// a failed step's new files in overwrite would win over the output put back: moved into the run's backup ("unfinished\overwrite")
	// - only what the step made, and a crash report among them is named with where it went
	static void MoveNewOverwrite(RunContext a_ctx, Dictionary<string, DateTime> a_before, DateTime a_beganUtc)
	{
		var ow = a_ctx.Mo2.OverwriteDir;
		List<string> fresh, older;
		try {
			(fresh, older) = NewInOverwrite(a_before, OverwriteFiles(a_ctx), a_beganUtc);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			a_ctx.Log.Warn("overwrite could not be looked at after the failed step: " + e.Message);
			return;
		}
		var reports = new List<string>();
		var stayed = new List<string>();
		var why = "";
		// each file on its own: one a program still holds (a log the tool is letting go of) never keeps the others in overwrite,
		// where they would win over the output just put back
		foreach (var f in fresh) {
			try {
				var dst = Path.Combine(a_ctx.BackupFolder, "unfinished", "overwrite", Path.GetRelativePath(ow, f));
				Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
				Files.Move(f, dst, true);
				if (InGame.IsCrashReport(Path.GetFileName(f))) {
					reports.Add(dst);
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				stayed.Add(Path.GetRelativePath(ow, f));
				why = why == "" ? e.Message : why;
			}
		}
		if (fresh.Count > stayed.Count) {
			a_ctx.Log.Detail($"{fresh.Count - stayed.Count} file(s) the step left in Mod Organizer's overwrite moved to the backup (unfinished\\overwrite)");
		}
		foreach (var r in reports) {
			a_ctx.Log.Warn("the game's crash report went with them - it is now at " + r);
			a_ctx.Log.Simple("Note: the game's crash report is in the run's backup: " + r);
		}
		if (stayed.Count > 0) {
			var text = $"{stayed.Count} file(s) the failed step left in Mod Organizer's overwrite could not be moved out ({why}) - they are still there and win over the output that was put back: " +
				string.Join(", ", stayed.Take(6)) + (stayed.Count > 6 ? " ..." : "");
			a_ctx.Log.Warn(text);
			a_ctx.Log.Simple("Note: " + text);
			a_ctx.Journal.Notes.Add(text);
			a_ctx.Journal.TrySave();
		}
		if (older.Count > 0) {
			a_ctx.Log.Detail($"{older.Count} file(s) new to overwrite carry a date from before the step and were left where they are (not this run's): " +
				string.Join(", ", older.Take(4).Select(Path.GetFileName)) + (older.Count > 4 ? " ..." : ""));
		}
	}

	static void ReportOverwrite(RunContext a_ctx)
	{
		var n = Files.CountFiles(a_ctx.Mo2.OverwriteDir);
		if (n > 0) {
			var tops = Directory.EnumerateFileSystemEntries(a_ctx.Mo2.OverwriteDir).Select(Path.GetFileName).Take(8);
			a_ctx.Note($"Mod Organizer's overwrite holds {n} file(s) after the run ({string.Join(", ", tops)})");
		}
	}

	// how long a setup's own command may take, and how long once a Stop only hurries it (the offline tests shorten both)
	public static TimeSpan HookLimit { get; set; } = TimeSpan.FromMinutes(10);
	public static TimeSpan HookHurry { get; set; } = TimeSpan.FromSeconds(15);

	static string Said(TimeSpan a_t) => a_t.TotalMinutes >= 1 ? a_t.TotalMinutes.ToString("0.#", InvariantCulture) + " minutes" : a_t.TotalSeconds.ToString("0", InvariantCulture) + " seconds";

	// A setup's own command before / after a run (his: the shared game claim); it must end with exit code 0, within its limit
	// (then its whole tree is ended). What it printed is read as it comes and never waited for: a program the command left
	// running keeps the command's output open for as long as it lives.
	// a_ct: a Stop that ends the command at once - a before-run command, where nothing is changed yet.
	// a_hurry: a Stop that only shortens its limit - an after-run command gives back what the before-run commands took, so a
	// Stop never skips it and never cuts it off at once; it is just not waited for long
	static void Hook(RunContext a_ctx, string a_command, string a_when, CancellationToken a_ct, CancellationToken a_hurry = default)
	{
		a_ct.ThrowIfCancellationRequested();
		a_ctx.Log.Detail($"{a_when}: {a_command}");
		// cmd reads its own quoting, not Windows' argument escaping: /s /c "<the command as written>" (its quotes kept as they are)
		var psi = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + a_command + "\"") {
			UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
		};
		using var p = Process.Start(psi) ?? throw new StepFailedException("could not start: " + a_command);
		// one a Stop ends at once is one of the run's own processes: Stop's sweep ends it too
		if (a_ct.CanBeCanceled) {
			a_ctx.Control.Started.Add(p.Id);
		}
		var said = new List<string>();
		var readers = new[] { p.StandardOutput, p.StandardError }.Select(r => Task.Run(async () => {
			try {
				while (await r.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } ln) {
					lock (said) {
						said.Add(ln);
						if (said.Count > 40) {
							said.RemoveAt(0);
						}
					}
				}
			} catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) {
				// the pipe closed under the read (the process object let go): what was read stays
			}
		})).ToArray();
		using (var limit = CancellationTokenSource.CreateLinkedTokenSource(a_ct)) {
			limit.CancelAfter(HookLimit);
			var hurried = false;
			// let go before the limit itself: a Stop that comes later finds nothing of this wait
			using (a_hurry.Register(() => {
				hurried = true;
				limit.CancelAfter(HookHurry);
			})) {
				try {
					p.WaitForExitAsync(limit.Token).GetAwaiter().GetResult();
				} catch (OperationCanceledException) {
					Procs.KillTree(p.Id);
					a_ct.ThrowIfCancellationRequested();
					throw new StepFailedException(hurried ? $"the {a_when} command did not end within {Said(HookHurry)} of Stop and was ended: {a_command}"
						: $"the {a_when} command did not end within {Said(HookLimit)}: {a_command}");
				}
			}
		}
		// ended by Stop's own sweep a moment before this wait saw the Stop: stopped, not "exit code -1"
		a_ct.ThrowIfCancellationRequested();
		Task.WaitAll(readers, TimeSpan.FromSeconds(2));
		string[] last;
		lock (said) {
			last = said.Where(l => l.Trim() != "").TakeLast(5).ToArray();
		}
		foreach (var ln in last) {
			a_ctx.Log.Detail("| " + ln.TrimEnd());
		}
		if (p.ExitCode != 0) {
			throw new StepFailedException($"the {a_when} command ended with exit code {p.ExitCode}: {a_command}");
		}
	}
}

// The window talks to a running run through files in its folder: "stop" (stop now, safely), "stop after" (after the running
// step) and "continue" (the player did a guided in-game part).
// a_cancel cancels the run (the run owns its cancellation source; this only asks it). One look at a time: the next is set only
// once the last has finished, and Dispose waits for a look still going - so a Stop can never reach a cancellation source the run
// already let go (a throw there, on a timer thread, would end the whole program)
public sealed class Commands(Journal a_journal, Action a_cancel) : IDisposable
{
	readonly Journal _j = a_journal;
	readonly Action _cancel = a_cancel;
	readonly Lock _gate = new();
	Timer? _timer;
	bool _disposed;
	volatile bool _continue;

	public bool Stopped { get; private set; }

	// what a Stop does at once, beside cancelling the run: ends the game and tools this run started (his 2026-11-06 find:
	// "the stop now button does not work" - a cancel alone waited for the step's next look, and a game kept compiling)
	public Action? OnStop { get; set; }

	public static string Dir(string a_runFolder) => Path.Combine(a_runFolder, "commands");

	public static void Send(string a_runFolder, string a_command)
	{
		Directory.CreateDirectory(Dir(a_runFolder));
		Files.WriteAtomic(Path.Combine(Dir(a_runFolder), a_command + ".txt"), DateTime.Now.ToString("O"));
	}

	public void Start()
	{
		lock (_gate) {
			_timer ??= new Timer(_ => Poll(), null, 500, Timeout.Infinite);
		}
	}

	void Poll()
	{
		try {
			PollOnce();
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) {
		} finally {
			lock (_gate) {
				if (!_disposed) {
					_timer?.Change(500, Timeout.Infinite);
				}
			}
		}
	}

	void PollOnce()
	{
		var d = Dir(_j.Folder);
		if (!Directory.Exists(d)) {
			return;
		}
		if (File.Exists(Path.Combine(d, "stop.txt")) && !Stopped) {
			Stopped = true;
			_cancel();
			try {
				OnStop?.Invoke();
			} catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) {
			}
		}
		if (File.Exists(Path.Combine(d, "stop after.txt")) && !_j.StopAfterStep) {
			// no save here: this is a timer thread, and the run's own thread saves the record often
			_j.StopAfterStep = true;
		}
		if (File.Exists(Path.Combine(d, "continue.txt"))) {
			_continue = true;
			Files.TryDelete(Path.Combine(d, "continue.txt"));
		}
	}

	// how long Dispose waits for a look still going (the offline tests shorten it)
	public static TimeSpan DisposeWait { get; set; } = TimeSpan.FromSeconds(10);

	public bool TakeContinue() => _continue;

	public void ClearContinue() => _continue = false;

	public void Dispose()
	{
		Timer? t;
		lock (_gate) {
			if (_disposed) {
				return;
			}
			_disposed = true;
			t = _timer;
			_timer = null;
		}
		if (t == null) {
			return;
		}
		// a look still going finishes before the run lets go of what it uses. The event is released only once the timer has set it:
		// a look still going after the wait sets it later, and a released event there would throw on a timer thread and end the program
#pragma warning disable CA2000
		var done = new ManualResetEvent(false);
#pragma warning restore CA2000
		if (!t.Dispose(done) || done.WaitOne(DisposeWait)) {
			done.Dispose();
		}
	}
}
