using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToolRunner;

public enum ShaderRebuild
{
	WhenChanged,
	Always,
	Never,
}

// One setup = one MO2 profile and how the runner treats it: which steps, in which order, each step's own settings, and the
// run-wide options. Stored as JSON; keys the runner does not know (and every "_note" key) are kept as they are on save.
public sealed class Setup
{
	public const int Schema = 2;

	public string File { get; private set; } = "";
	public JsonObject Root { get; private set; } = new();

	public string Name => Str(Root, "name") ?? Path.GetFileNameWithoutExtension(File);
	public string Mo2Exe => Str(Obj("mo2"), "exe") ?? "";
	public string? Mo2Instance => Str(Obj("mo2"), "instance");
	public string Profile => Str(Obj("mo2"), "profile") ?? "";
	public string BackupDir => Str(Root, "backups") ?? "";
	public int IdleMinutes => Int(Root, "idleMinutes") ?? 2;
	public int MinFreeGb => Int(Root, "minFreeGb") ?? 20;
	public bool AlertNotification => Bool(Obj("alerts"), "notification") ?? true;
	public bool AlertSound => Bool(Obj("alerts"), "sound") ?? true;
	public bool AlertFlash => Bool(Obj("alerts"), "flash") ?? false;
	public string LodOffBefore => Str(Root, "lodOutputsOffBefore") ?? "texgen";

	public ShaderRebuild ShaderRebuild => Str(Root, "shaderRebuild")?.ToLowerInvariant() switch {
		"always" => ShaderRebuild.Always,
		"never" => ShaderRebuild.Never,
		_ => ShaderRebuild.WhenChanged,
	};

	public IReadOnlyList<string> LodOutputs => List(Root, "lodOutputs");
	public IReadOnlyList<string> BeforeRun => List(Root, "beforeRun");
	public IReadOnlyList<string> AfterRun => List(Root, "afterRun");

	// what the "What MO2 changed" view leaves out (a profile only tests use, test mods)
	public IReadOnlyList<string> ChangesHideProfiles => List(Obj("changes"), "hideProfiles");
	public IReadOnlyList<string> ChangesHideMods => List(Obj("changes"), "hideModsStartingWith");

	public IReadOnlyDictionary<string, string> PlaceAfter =>
		(Root["placeAfter"] as JsonObject)?.Where(p => p.Value is JsonValue).ToDictionary(p => p.Key, p => p.Value!.ToString(), StringComparer.OrdinalIgnoreCase) ??
		new Dictionary<string, string>();

	public IReadOnlyList<Profile.PlaceRule> Placement =>
		(Root["placement"] as JsonArray)?.OfType<JsonArray>().Where(a => a.Count >= 2)
			.Select(a => new Profile.PlaceRule(a[0]!.ToString(), a[1]!.ToString(), a.Count > 2 && a[2]?.ToString() == "active")).ToList() ?? [];

	public sealed record StepEntry(string Key, bool On, JsonObject Settings);

	public List<StepEntry> Steps =>
		(Root["steps"] as JsonArray)?.OfType<JsonObject>().Select(o => new StepEntry(Str(o, "step") ?? "", Bool(o, "on") ?? true,
			o["settings"] as JsonObject ?? new JsonObject())).Where(s => s.Key != "").ToList() ?? [];

	// a new game (the player starts a new character after the run): there is no save of theirs to keep clean or to bring
	// back, so the clean save and the save to play are left out of every run while it is on (dyndolod.info/Help/Clean-Save)
	public bool NewGame => Bool(Root, "newGame") ?? false;

	public static readonly IReadOnlyList<string> SaveSteps = ["cleansave", "playsave"];

	public bool LeftOutForNewGame(string a_key) => NewGame && SaveSteps.Contains(a_key);

	public List<StepEntry> EnabledSteps => Steps.Where(s => s.On && !LeftOutForNewGame(s.Key)).ToList();

	public JsonObject? StepSettings(string a_key) => Steps.FirstOrDefault(s => s.Key == a_key)?.Settings;

	JsonObject Obj(string a_key) => Root[a_key] as JsonObject ?? new JsonObject();

	// an emptied setting counts as not set (its default applies) - "" never runs PGPatcher without --autostart
	public static string? Str(JsonObject a_o, string a_key) =>
		a_o[a_key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

	public static int? Int(JsonObject a_o, string a_key) =>
		a_o[a_key] is JsonValue v ? (v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) ? (int)d : null) : null;

	public static double? Num(JsonObject a_o, string a_key) =>
		a_o[a_key] is JsonValue v ? (v.TryGetValue<double>(out var d) ? d : v.TryGetValue<int>(out var i) ? i : null) : null;

	public static bool? Bool(JsonObject a_o, string a_key) =>
		a_o[a_key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

	public static List<string> List(JsonObject a_o, string a_key) =>
		(a_o[a_key] as JsonArray)?.Select(n => n?.ToString() ?? "").Where(s => s != "").ToList() ?? [];

	static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
	static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	public static Setup Load(string a_file) => FromText(Files.ReadSharedText(a_file), a_file);

	// the setup as these bytes say (the preview reads the file once and shows exactly what then runs)
	public static Setup Parse(byte[] a_bytes, string a_file)
	{
		using var r = new StreamReader(new MemoryStream(a_bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return FromText(r.ReadToEnd(), a_file);
	}

	static Setup FromText(string a_text, string a_file)
	{
		var node = JsonNode.Parse(a_text, documentOptions: ReadOptions) as JsonObject ??
		           throw new InvalidDataException("not a setup file (no JSON object): " + a_file);
		return new Setup { File = a_file, Root = node };
	}

	public static Setup FromJson(JsonObject a_root, string a_file = "") => new() { Root = a_root, File = a_file };

	// what is wrong with it, in plain words (empty = fine)
	public List<string> Problems()
	{
		var p = new List<string>();
		if (Mo2Exe == "") p.Add("Mod Organizer's program is not set");
		if (Profile == "") p.Add("the MO2 profile is not set");
		if (BackupDir == "") p.Add("the backup folder is not set");
		// whole local paths only: a relative one would follow whatever folder the run was started from
		foreach (var (what, dir) in Folders()) {
			if (dir != "" && (!Path.IsPathFullyQualified(dir) || dir.StartsWith(@"\\", StringComparison.Ordinal))) {
				p.Add($"{what} must be a whole path on this PC (like D:\\Modding\\...): {dir}");
			}
		}
		if (Steps.Count == 0) p.Add("no steps");
		if (EnabledSteps.Count == 0) p.Add(Steps.Any(s => s.On) ? "only the save steps are switched on, and they are left out for a new game" : "every step is switched off");
		var dup = Steps.GroupBy(s => s.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
		if (dup.Count > 0) p.Add("a step is listed twice: " + string.Join(", ", dup));
		// SnowFixer and AutoBlend are no longer supported: Landscape Fixer, by the same author, replaced both
		foreach (var gone in Steps.Select(s => s.Key).Where(k => k is "snowfixer" or "autoblend").Distinct()) {
			p.Add($"{(gone == "snowfixer" ? "SnowFixer" : "AutoBlend")} is no longer supported - Landscape Fixer replaced it: add Landscape Fixer and take this step out");
		}
		var unknown = Steps.Where(s => !StepCatalog.Known(s.Key) && s.Key is not "snowfixer" and not "autoblend").Select(s => s.Key).ToList();
		if (unknown.Count > 0) p.Add("unknown step(s): " + string.Join(", ", unknown));
		// A setup file comes from anywhere (a guide, a Discord). What it gives as a mod's name is joined to Mod Organizer's mods
		// folder as it stands: with a folder part in it ("..\..\profiles\Default") or a whole path, a meta.ini was written there,
		// a line put into the mod list and everything in that folder moved into the run's backup. Only a plain folder name is a mod
		foreach (var (where, name) in ModNames().Where(m => !PlainName(m.Name)).DistinctBy(m => m.Name).Take(5)) {
			p.Add($"{where} is not a mod's name (one plain folder name: no \\ / : and no \"..\"): {name}");
		}
		return p;
	}

	// the settings whose value the engine takes as a mod's name (or, "settings", as a file's own name in a fixed folder)
	static readonly string[] NameKeys = ["output_mod", "then_move_to_mod", "tool_writes", "pgpatcher_mod", "shader_mod", "settings_mod", "settings",
		"replaces", "enable_during", "disable_during"];

	// every such value in the setup (a cache list's too), with where it stands
	public IEnumerable<(string Where, string Name)> ModNames()
	{
		static IEnumerable<(string, string)> Walk(JsonNode? a_node, string a_step)
		{
			if (a_node is JsonArray list) {
				foreach (var x in list.SelectMany(e => Walk(e, a_step))) {
					yield return x;
				}
			} else if (a_node is JsonObject o) {
				foreach (var (key, value) in o) {
					if (!NameKeys.Contains(key)) {
						foreach (var x in Walk(value, a_step)) {
							yield return x;
						}
						continue;
					}
					foreach (var one in value is JsonArray many ? many.ToList() : [value]) {
						if (one is JsonValue v && v.TryGetValue<string>(out var name) && name != "") {
							yield return ($"{a_step}'s \"{key}\"", name);
						}
					}
				}
			}
		}
		return List(Root, "lodOutputs").Select(m => ("\"lodOutputs\"", m)).Concat(Steps.SelectMany(s => Walk(s.Settings, s.Key)));
	}

	// one plain folder (or file) name, as Windows takes one
	public static bool PlainName(string a_name) =>
		a_name.Trim() != "" && a_name is not "." and not ".." && !a_name.EndsWith('.') && !a_name.EndsWith(' ') &&
		a_name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) < 0 && !a_name.Any(char.IsControl);

	// A setup that came from somewhere else never runs a command on this PC: its own commands before and after a run are taken
	// out (the sharing side takes them out too, but a file passed around need not have come through it). Returns how many went
	public int TakeOutCommands()
	{
		var n = BeforeRun.Count + AfterRun.Count;
		Root.Remove("beforeRun");
		Root.Remove("afterRun");
		return n;
	}

	// the folders the setup itself names (empty ones included): its backups, its scratch, each step's output (a_running: only the
	// steps a run would run - not one switched off, nor a save step left out for a new game)
	public IEnumerable<(string What, string Dir)> Folders(bool a_running = false) =>
		new[] { ("the backup folder", BackupDir), ("the scratch folder", Str(Root, "scratch") ?? "") }
			.Concat((a_running ? EnabledSteps : Steps).Select(s => ($"{s.Key}'s output folder", Str(s.Settings, "output_dir") ?? "")));

	public void Save(string? a_file = null)
	{
		File = a_file ?? File;
		Root["schema"] = Schema;
		Files.WriteAtomic(File, Root.ToJsonString(WriteOptions));
	}

	public Setup Clone() => FromJson((JsonObject)Root.DeepClone(), File);

}
