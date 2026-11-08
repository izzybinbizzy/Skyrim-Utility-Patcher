using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToolRunner.Steps;

// His checklist 436: LOOT's sort, run the way Mod Organizer's own Sort button runs it (its source, loot.cpp): its lootcli.exe under the virtual folder, told the
// game, the profile's list and two files to write. LOOT's own program through Mod Organizer is unsupported by LOOT (its FAQ); this one ships with Mod Organizer.
// lootcli only writes the sorted list, so the run lays that onto the order files itself and keeps it. It runs before the tools, with the outputs off, so every
// tool builds on the order the player plays. Off in a new setup: it changes the player's load order.
public sealed class LootStep : IStep
{
	public const string StepKey = "loot";
	public const string Image = "lootcli.exe";
	public const string SortedFile = "loot sorted.txt";
	public const string ReportFile = "loot report.json";
	public const int NotesShown = 8;
	const int NoteLength = 200;

	public string Key => StepKey;

	public static string Program(Mo2Instance a_mo2) => Path.Combine(Path.GetDirectoryName(a_mo2.Exe)!, "loot", Image);

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		if (!File.Exists(Program(a_c.Mo2))) {
			yield return new Issue(Key, true, $"Mod Organizer's LOOT is not there ({Program(a_c.Mo2)}) - reinstall Mod Organizer, or switch this step off");
		}
	}

	public static string Arguments(Mo2Instance a_mo2, string a_profile, string a_sorted, string a_report, bool a_skipUpdate)
	{
		var list = Path.Combine(a_mo2.ProfileDir(a_profile), "loadorder.txt");
		var language = string.IsNullOrEmpty(a_mo2.Language) ? "en" : a_mo2.Language;
		return $"--game skyrimse --gamePath {Mo2Control.Quote(a_mo2.GameDir)} --pluginListPath {Mo2Control.Quote(list)} --out {Mo2Control.Quote(a_report)} " +
			$"--pluginListOutputPath {Mo2Control.Quote(a_sorted)} --language {language}" + (a_skipUpdate ? " --skipUpdateMasterlist" : "");
	}

	public void Run(RunContext a_ctx, StepSettings a_s)
	{
		var sorted = Path.Combine(a_ctx.Journal.Folder, SortedFile);
		var report = Path.Combine(a_ctx.Journal.Folder, ReportFile);
		// the masterlist is fetched from LOOT's own site: a player who is offline gets a second try on the copy they have
		if (!Sort(a_ctx, a_s, sorted, report, false)) {
			a_ctx.Log.Warn("LOOT wrote no sorted list - trying again with the masterlist it already has");
			if (!Sort(a_ctx, a_s, sorted, report, true)) {
				throw new StepFailedException("LOOT wrote no sorted list, so your order was not changed. Mod Organizer's own Sort button shows why (a rule that goes in a circle, or no masterlist yet)");
			}
		}
		a_ctx.Control.Close(null, a_ctx.Cancel);
		Lay(a_ctx, sorted, report);
	}

	// its exit code says nothing (it ends 0 when the masterlist could not be fetched): the list is the verdict
	static bool Sort(RunContext a_ctx, StepSettings a_s, string a_sorted, string a_report, bool a_skipUpdate)
	{
		File.Delete(a_sorted);
		File.Delete(a_report);
		a_ctx.Activity(StepKey, "LOOT is sorting your plugins");
		using var proc = a_ctx.Control.RunExecutable(Program(a_ctx.Mo2), Image, Arguments(a_ctx.Mo2, a_ctx.Setup.Profile, a_sorted, a_report, a_skipUpdate), a_ctx.Cancel, null, true);
		Waits.Tool(a_ctx, StepKey, proc, a_s.Deadline(10), () => proc.HasExited ? Waits.Poll.Done : Waits.Poll.Wait);
		return ReadSorted(a_sorted).Count > 0;
	}

	// laid onto the lists with Mod Organizer closed, and noted: every later put-back of this run keeps it, and only Undo takes it back
	public static void Lay(RunContext a_ctx, string a_sorted, string a_report)
	{
		var order = ReadSorted(a_sorted);
		if (order.Count == 0) {
			throw new StepFailedException("LOOT's sorted list is empty, so your order was not changed");
		}
		var unseen = NotSeen(a_ctx.Profile, order);
		if (unseen.Count > 0) {
			throw new StepFailedException($"LOOT did not see {unseen.Count} ticked plugin(s), {unseen[0]} first, so your order was not changed");
		}
		var changed = a_ctx.Profile.SortPlugins(order);
		a_ctx.Journal.Sorted = true;
		a_ctx.Journal.Save();
		var said = changed.Count > 0 ? "LOOT sorted your plugins. The new order stays; Undo the last run takes it back." : "LOOT left your plugins in the order they were.";
		a_ctx.Log.Say(said);
		a_ctx.Log.Simple(said);
		foreach (var note in ReportNotes(a_report)) {
			a_ctx.Log.Warn(note);
		}
	}

	// the put-back brings the order files back as they were before the run: the sort that already finished goes on top again
	public static void KeepIn(RunContext a_ctx)
	{
		try {
			var order = ReadSorted(Path.Combine(a_ctx.Journal.Folder, SortedFile));
			if (order.Count == 0) {
				a_ctx.Log.Warn("LOOT's sorted list is gone from the run's folder, so your plugins are in the order they had before the run");
				return;
			}
			a_ctx.Profile.SortPlugins(order);
			a_ctx.Log.Detail("LOOT's order kept");
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			a_ctx.Log.Warn("LOOT's order could not be laid back, so your plugins are in the order they had before the run: " + e.Message);
		}
	}

	public static List<string> ReadSorted(string a_file)
	{
		if (!File.Exists(a_file)) {
			return [];
		}
		return Files.ReadSharedText(a_file).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
	}

	// a ticked plugin with a file behind it that LOOT did not list: it did not look through the virtual folder, and its list is of no use
	public static List<string> NotSeen(Profile a_profile, IReadOnlyList<string> a_order)
	{
		var seen = a_order.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var there = a_profile.PluginPaths();
		return a_profile.ActivePlugins().Where(p => there.ContainsKey(p) && !seen.Contains(p)).ToList();
	}

	// what LOOT's report says is wrong, not its advice (a player reads LOOT itself for that), at most NotesShown lines
	public static List<string> ReportNotes(string a_report)
	{
		JsonObject? root;
		try {
			root = File.Exists(a_report) ? JsonNode.Parse(Files.ReadSharedText(a_report)) as JsonObject : null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			return [];
		}
		var notes = Errors(root?["messages"], "").ToList();
		foreach (var plugin in (root?["plugins"] as JsonArray ?? []).OfType<JsonObject>()) {
			var name = Text(plugin["name"]) ?? "?";
			var missing = (plugin["missingMasters"] as JsonArray ?? []).Select(Text).OfType<string>().ToList();
			if (missing.Count > 0) {
				notes.Add($"LOOT: {name} is missing {string.Join(", ", missing)}");
			}
			var clashes = (plugin["incompatibilities"] as JsonArray ?? []).Select(n => Text(n?["name"])).OfType<string>().ToList();
			if (clashes.Count > 0) {
				notes.Add($"LOOT: {name} does not go with {string.Join(", ", clashes)}");
			}
			notes.AddRange(Errors(plugin["messages"], name + ": "));
		}
		return notes.Count <= NotesShown ? notes : [.. notes.Take(NotesShown), $"LOOT: and {notes.Count - NotesShown} more, which its own report shows"];
	}

	static IEnumerable<string> Errors(JsonNode? a_messages, string a_prefix) =>
		(a_messages as JsonArray ?? []).OfType<JsonObject>().Where(m => Text(m["type"]) == "error").Select(m => Text(m["text"])).OfType<string>().Select(t => "LOOT: " + a_prefix + (t.Length > NoteLength ? t[..NoteLength] + "..." : t));

	static string? Text(JsonNode? a_node) => a_node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
