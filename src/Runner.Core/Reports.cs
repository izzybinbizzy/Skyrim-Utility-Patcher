using System.Text.Json;
using System.Text.Json.Nodes;
using ToolRunner.Steps;

namespace ToolRunner;

// What the command line prints with --json: one JSON object per command, for a script, a mod list's own installer or another
// program - the same facts the plain lines carry, under names that stay. "schema" goes up only when a name changes or goes;
// new names may be added under the same number. Times are local, ISO 8601; sizes are bytes; a step is named by its key in a
// setup ("dyndolod") and by its plain name ("DynDOLOD").
public static class Reports
{
	public const int Schema = 1;

	static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	public static string Text(JsonObject a_o) => a_o.ToJsonString(Indented);

	static JsonObject Head(string a_command, string? a_setup = null)
	{
		var o = new JsonObject { ["schema"] = Schema, ["program"] = Brand.Name, ["command"] = a_command };
		if (a_setup != null) {
			o["setup"] = a_setup;
		}
		return o;
	}

	static string Iso(DateTime a_t) => a_t.ToString("yyyy-MM-ddTHH:mm:ss", InvariantCulture);

	static JsonArray List(IEnumerable<string> a_items) => new([.. a_items.Select(i => (JsonNode?)JsonValue.Create(i))]);

	// a refusal or a note, with the catalog's answer where there is one
	static JsonObject Problem(string a_step, string a_text, bool a_blocks)
	{
		var why = Explain.For(a_step, a_text);
		return new JsonObject {
			["step"] = a_step, ["stepName"] = a_step == "" ? "" : StepCatalog.Name(a_step), ["blocks"] = a_blocks, ["text"] = a_text,
			["code"] = why?.Code, ["means"] = why?.What, ["todo"] = why?.Todo, ["help"] = why?.Help,
		};
	}

	public static JsonObject Check(Setup a_setup, IReadOnlyList<Issue> a_issues)
	{
		var o = Head("check", a_setup.Name);
		o["ready"] = !a_issues.Any(i => i.Blocking);
		o["issues"] = new JsonArray([.. a_issues.Select(i => (JsonNode?)Problem(i.Step, i.Text, i.Blocking))]);
		return o;
	}

	static JsonObject PreviewBody(RunPreview a_p, Func<string, TimeSpan?> a_usual)
	{
		var o = new JsonObject {
			["steps"] = new JsonArray([.. a_p.Steps.Select(s => (JsonNode?)new JsonObject {
				["key"] = s.Key, ["name"] = s.Name, ["output"] = s.Output, ["outputIsNew"] = s.OutputIsNew,
				["usualSeconds"] = a_usual(s.Key) is { } t ? (double?)Math.Round(t.TotalSeconds) : null,
			})]),
			["offForTheRun"] = new JsonArray([.. a_p.OffForTheRun.Select(m => (JsonNode?)new JsonObject { ["mod"] = m.Mod, ["why"] = m.Why })]),
			["replacedForGood"] = List(a_p.ReplacedForGood),
			["startsTheGame"] = List(a_p.StartsTheGame),
			["closesMo2"] = a_p.ClosesMo2,
			["leftOutForNewGame"] = List(a_p.LeftOutForNewGame),
			["commandsBefore"] = List(a_p.CommandsBefore),
			["commandsAfter"] = List(a_p.CommandsAfter),
		};
		if (a_p.Forecast is { } f) {
			o["disk"] = new JsonArray([.. f.Disks.Select(d => (JsonNode?)new JsonObject {
				["disk"] = d.Shown, ["freeBytes"] = d.Free, ["neededBytes"] = d.Needs, ["wantedBytes"] = d.Wants,
				["parts"] = new JsonArray([.. d.Parts.Select(p => (JsonNode?)new JsonObject { ["step"] = p.Step, ["what"] = p.What, ["bytes"] = p.Bytes, ["needed"] = p.Needed })]),
			})]);
			o["sizeNotKnown"] = List(f.NotKnown);
		}
		return o;
	}

	public static JsonObject Preview(Setup a_setup, RunPreview a_p, Func<string, TimeSpan?> a_usual)
	{
		var o = Head("preview", a_setup.Name);
		foreach (var (k, v) in PreviewBody(a_p, a_usual).ToList()) {
			o[k] = v?.DeepClone();
		}
		return o;
	}

	public static JsonObject DryRun(DryRunReport a_r, Func<string, TimeSpan?> a_usual)
	{
		var o = Head("dryrun", a_r.SetupName == "" ? Path.GetFileNameWithoutExtension(a_r.SetupFile) : a_r.SetupName);
		o["wouldStart"] = a_r.Starts;
		o["refusals"] = new JsonArray([.. a_r.Refusals.Select(r => (JsonNode?)Problem(r.Step, r.Text, true))]);
		o["notes"] = new JsonArray([.. a_r.Notes.Select(r => (JsonNode?)Problem(r.Step, r.Text, false))]);
		o["preview"] = a_r.Preview == null ? null : PreviewBody(a_r.Preview, a_usual);
		return o;
	}

	public static JsonObject Updates(Setup a_setup, IReadOnlyList<ToolUpdates.Result> a_rows)
	{
		var o = Head("updates", a_setup.Name);
		o["anyNewer"] = a_rows.Any(r => r.Newer);
		o["tools"] = new JsonArray([.. a_rows.Select(r => (JsonNode?)new JsonObject {
			["step"] = r.Step, ["tool"] = r.Tool, ["yours"] = r.Yours, ["newest"] = r.Newest, ["newer"] = r.Newer, ["page"] = r.Page, ["problem"] = r.Problem,
		})]);
		return o;
	}

	// a_looked: how many new changes a look just noted (null = no look was asked for, or none could be made while a run goes)
	public static JsonObject Changes(Setup a_setup, DateTime? a_since, int? a_looked, IEnumerable<Mo2Changes.Change> a_newestFirst)
	{
		var o = Head("changes", a_setup.Name);
		o["watchedSince"] = a_since is { } s ? Iso(s) : null;
		o["newChanges"] = a_looked;
		o["changes"] = new JsonArray([.. a_newestFirst.Select(r => (JsonNode?)new JsonObject {
			["when"] = Iso(r.When), ["mod"] = r.Mod, ["what"] = r.What, ["profiles"] = List(r.Profiles), ["who"] = r.Who,
		})]);
		return o;
	}

	public static JsonObject Tools(IReadOnlyList<ToolFinder.Row> a_rows, IReadOnlyList<string>? a_added)
	{
		var o = Head("tools");
		o["tools"] = new JsonArray([.. a_rows.Select(r => (JsonNode?)new JsonObject {
			["step"] = r.Tool.Step, ["name"] = r.Tool.Name,
			["state"] = r.State switch { ToolFinder.Found.Listed => "listed", ToolFinder.Found.NotListed => "foundNotListed", _ => "notFound" },
			["binary"] = r.Binary, ["page"] = r.Tool.Page, ["where"] = r.Tool.Where,
		})]);
		o["added"] = a_added == null ? null : List(a_added);
		return o;
	}

	static JsonObject Run(Journal a_j) => new() {
		["id"] = a_j.Id, ["setup"] = a_j.SetupName, ["setupFile"] = a_j.SetupFile, ["profile"] = a_j.Profile, ["state"] = a_j.State.ToString().ToLowerInvariant(),
		["started"] = Iso(a_j.Started), ["ended"] = a_j.Ended is { } e ? Iso(e) : null, ["verdict"] = a_j.Verdict, ["folder"] = a_j.Folder, ["backup"] = a_j.BackupDir,
		["stopAfterStep"] = a_j.StopAfterStep, ["needsRestore"] = a_j.NeedsRestore, ["retryFrom"] = Retry.FromStep(a_j),
		["notes"] = List(a_j.Notes),
		["steps"] = new JsonArray([.. a_j.Steps.Select(s => (JsonNode?)new JsonObject {
			["key"] = s.Key, ["name"] = s.Name, ["state"] = s.State.ToString().ToLowerInvariant(), ["started"] = s.Started is { } a ? Iso(a) : null,
			["ended"] = s.Ended is { } b ? Iso(b) : null, ["note"] = s.Note, ["activity"] = s.Activity, ["progress"] = s.Progress, ["output"] = s.Output, ["outputBytes"] = s.OutputBytes,
		})]),
	};

	public static JsonObject Status(RunStatus.Snapshot a_s, string a_version)
	{
		var o = Head("status");
		o["version"] = a_version;
		o["state"] = a_s.State;
		o["safeToClose"] = a_s.SafeToClose;
		o["waitingFor"] = a_s.Waiting == "" ? null : a_s.Waiting;
		o["currentStep"] = a_s.Step?.Key;
		o["run"] = a_s.Run == null ? null : Run(a_s.Run);
		o["interrupted"] = a_s.Interrupted == null ? null : Run(a_s.Interrupted);
		o["lastRun"] = a_s.Last == null ? null : Run(a_s.Last);
		o["queue"] = a_s.Queue == null ? null : new JsonObject { ["setups"] = List(a_s.Queue.Setups), ["current"] = a_s.Queue.Current };
		o["power"] = a_s.Power == null ? null : new JsonObject { ["action"] = a_s.Power.Action.ToLowerInvariant(), ["at"] = Iso(a_s.Power.At) };
		return o;
	}

	// a run's end, as "run --json" prints it once the run is over (a refused start has no folder and no steps)
	public static JsonObject Outcome(RunOutcome a_outcome, Journal? a_run)
	{
		var o = Head("run", a_run?.SetupName);
		o["state"] = a_outcome.State.ToString().ToLowerInvariant();
		o["started"] = a_outcome.Folder != null;
		o["verdict"] = a_outcome.Verdict;
		var why = a_outcome.Folder == null ? Explain.For("", a_outcome.Verdict) : null;
		o["code"] = why?.Code;
		o["todo"] = why?.Todo;
		o["run"] = a_run == null ? null : Run(a_run);
		o["summary"] = a_run == null ? null : new JsonArray([.. RunSummary.Of(a_run).Rows.Select(r => (JsonNode?)new JsonObject { ["label"] = r.Label, ["text"] = r.Text })]);
		return o;
	}
}
