using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ToolRunner;

// Shareable setups (his checklist 466): a copy of a setup another player can import - its steps, their order and their options
// kept, everything that belongs to this PC taken out: the Mod Organizer it points at, the backup folder, every value that holds a
// path of this PC, and the commands it runs before and after a run (a shared file must never run something on another PC). The
// player's Windows and PC names are masked in what is left. The copy says what the importing player sets themselves.
public static partial class SetupShare
{
	// a drive path (C:\... or a bare C:), a UNC path (\\server\...), a %VARIABLE% path or a home path (~\...), anywhere in a value
	[GeneratedRegex(@"(?i)(\b[a-z]:([\\/]|$|\s)|\\\\[^\\\s]+\\|%[a-z_]+%[\\/]|(^|\s|"")~[\\/])")]
	private static partial Regex PathLike();

	public static bool HoldsPath(string a_value) => PathLike().IsMatch(a_value);

	public sealed record Result(JsonObject Setup, List<string> Removed);

	public static Result Share(Setup a_setup, string a_version)
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
		var names = BugReport.Names();
		var masked = JsonNode.Parse(BugReport.Mask(root.ToJsonString(), names))!.AsObject();
		masked["shared"] = new JsonObject {
			["by"] = $"{Brand.Name} {a_version}",
			["on"] = DateTime.Now.ToString("yyyy-MM-dd", InvariantCulture),
			["setYourself"] = new JsonArray(removed.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
		};
		return new Result(masked, removed);
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

	// writes the shared copy into a_outDir as "<name> (shared).json"; returns its path and what was taken out
	public static (string File, List<string> Removed) Write(Setup a_setup, string a_outDir, string a_version)
	{
		var r = Share(a_setup, a_version);
		Directory.CreateDirectory(a_outDir);
		var name = string.Concat(a_setup.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)).Trim();
		var file = Path.Combine(a_outDir, $"{(name == "" ? "setup" : name)} (shared).json");
		Files.WriteAtomic(file, r.Setup.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
		return (file, r.Removed);
	}
}
