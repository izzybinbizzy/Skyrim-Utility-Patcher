using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ToolRunner;

// Shareable setups (his checklist 466): a copy of a setup another player can import - its steps, their order and their options
// kept, everything that belongs to this PC taken out: the Mod Organizer it points at, the backup folder, every value that holds a
// path of this PC, and the commands it runs before and after a run (a shared file must never run something on another PC). The
// player's Windows and PC names are masked in what is left. The copy says what the importing player sets themselves.
public static partial class SetupShare
{
	// a drive path (C:\... or a bare C:), a UNC path (\\server\... or //server/...), a %VARIABLE% path (%ProgramFiles(x86)% too) or a
	// home path (~\...), anywhere in a value - a web address (https://...) is none
	[GeneratedRegex(@"(?i)(\b[a-z]:([\\/]|$|\s)|\\\\[^\\\s]+\\|(^|[\s""'=])//[^/\s]+/|%[a-z0-9_()]+%[\\/]|(^|\s|"")~[\\/])")]
	private static partial Regex PathLike();

	public static bool HoldsPath(string a_value) => PathLike().IsMatch(a_value);

	public sealed record Result(JsonObject Setup, List<string> Removed);

	public static Result Share(Setup a_setup, string a_version, List<(string Name, string Mask)>? a_names = null)
	{
		var root = (JsonObject)a_setup.Root.DeepClone();
		var removed = new List<string>();
		if (root["mo2"] is JsonObject mo2) {
			if (mo2.ContainsKey("exe")) {
				mo2["exe"] = "";
				removed.Add("which Mod Organizer it runs (mo2.exe)");
			}
			if (mo2.Remove("instance")) {
				removed.Add("the Mod Organizer instance (mo2.instance)");
			}
		}
		if (root.Remove("backups")) {
			removed.Add("the backup folder (backups)");
		}
		foreach (var key in new[] { "beforeRun", "afterRun" }) {
			if (root[key] is JsonArray { Count: > 0 }) {
				removed.Add($"the commands run {(key == "beforeRun" ? "before" : "after")} a run ({key}) - a shared setup never runs a command on another PC");
			}
			root.Remove(key);
		}
		Scrub(root, "", removed);
		// a PGPatcher settings set is a file beside the setup, not in it: the other player makes their own of that name
		foreach (var set in a_setup.Steps.Where(s => s.Key == "pgpatcher").Select(s => new StepSettings(s.Settings).Str("settings")).OfType<string>()
			         .Where(s => s.Trim() != "").Distinct(StringComparer.OrdinalIgnoreCase)) {
			removed.Add($"the PGPatcher settings set \"{set}\" it names - its file is not in the copy: make one of that name beside the setup " +
				$"(pgpatcher settings\\{set}.json), or name your own set");
		}
		// the player's names masked in the values only - a key is the setup's own wording, and a name equal to one ("Steps") must
		// not rename it; nor is a step's own key a name
		MaskValues(root, a_names ?? BugReport.Names());
		root["shared"] = new JsonObject {
			["by"] = $"{Brand.Name} {a_version}",
			["on"] = DateTime.Now.ToString("yyyy-MM-dd", InvariantCulture),
			["setYourself"] = new JsonArray(removed.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
		};
		return new Result(root, removed);
	}

	static void MaskValues(JsonNode? a_node, List<(string Name, string Mask)> a_names)
	{
		switch (a_node) {
		case JsonObject o:
			foreach (var (key, value) in o.ToList()) {
				if (value is JsonValue v && v.TryGetValue<string>(out var s)) {
					if (key != "step") {
						o[key] = BugReport.Mask(s, a_names);
					}
				} else {
					MaskValues(value, a_names);
				}
			}
			break;
		case JsonArray a:
			for (var i = 0; i < a.Count; ++i) {
				if (a[i] is JsonValue v && v.TryGetValue<string>(out var s)) {
					a[i] = BugReport.Mask(s, a_names);
				} else {
					MaskValues(a[i], a_names);
				}
			}
			break;
		}
	}

	// every string value that holds a path of this PC is taken out (a list keeps its other items); what went is named
	static void Scrub(JsonNode a_node, string a_where, List<string> a_removed)
	{
		switch (a_node) {
		case JsonObject o:
			foreach (var (key, value) in o.ToList()) {
				var where = a_where == "" ? key : a_where + "." + key;
				if (value is JsonValue v && v.TryGetValue<string>(out var s) && HoldsPath(s)) {
					o.Remove(key);
					a_removed.Add($"a path of the sharing PC ({where})");
				} else if (value != null) {
					Scrub(value, where, a_removed);
				}
			}
			break;
		case JsonArray a:
			for (var i = a.Count - 1; i >= 0; --i) {
				if (a[i] is JsonValue v && v.TryGetValue<string>(out var s) && HoldsPath(s)) {
					a.RemoveAt(i);
					a_removed.Add($"a path of the sharing PC ({a_where}[{i}])");
				} else if (a[i] is { } child) {
					Scrub(child, a_where + $"[{i}]", a_removed);
				}
			}
			break;
		}
	}

	// writes the shared copy into a_outDir as "<name> (shared).json" - the name masked as the copy is, and never over the setup
	// itself (a setup already named "... (shared)" shared into its own folder); returns its path and what was taken out
	public static (string File, List<string> Removed) Write(Setup a_setup, string a_outDir, string a_version, List<(string Name, string Mask)>? a_names = null)
	{
		var names = a_names ?? BugReport.Names();
		var r = Share(a_setup, a_version, names);
		Directory.CreateDirectory(a_outDir);
		var name = string.Concat(BugReport.Mask(a_setup.Name, names).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)).Trim();
		name = name == "" ? "setup" : name;
		var file = Path.Combine(a_outDir, $"{name} (shared).json");
		if (a_setup.File != "" && Files.SamePath(file, a_setup.File)) {
			file = Path.Combine(a_outDir, $"{name} (shared copy).json");
		}
		Files.WriteAtomic(file, r.Setup.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
		return (file, r.Removed);
	}
}
