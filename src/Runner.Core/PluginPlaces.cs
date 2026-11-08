using System.Text.RegularExpressions;
using ToolRunner.Steps;

namespace ToolRunner;

// His checklist 456: the tools' output plugins against the place each tool's OWN documents give them, and nothing else - a warning, never
// a refusal, naming the plugin that is in the way. LOOT sorts all of these by itself, so the advice is LOOT's.
//   DynDOLOD.esm the ESM with the highest priority, DynDOLOD.esp and Occlusion.esp the last plugins, Occlusion.esp after DynDOLOD.esp
//     (dyndolod.info/Help/Load-Overwrite-Orders);
//   PG_1.esp and its fellows loaded last except for the DynDOLOD and TexGen outputs (PGPatcher's wiki, Basic Usage);
//   the Bashed Patch last unless a plugin says it must load last (Wrye Bash's readme) - so it is in the way of the plugins above only,
//     and those rules name it, whatever its number.
// Synthesis.esp, PGPatcher.esp and LandscapeFixer.esp have no documented place: they are not checked.
public static partial class PluginPlaces
{
	[GeneratedRegex(@"^PG_\d+\.esp$", RegexOptions.IgnoreCase)]
	private static partial Regex PgPlugin();

	const string DynEsp = "DynDOLOD.esp", DynEsm = "DynDOLOD.esm", Occlusion = "Occlusion.esp";

	static bool Is(string a_a, string a_b) => a_a.Equals(a_b, StringComparison.OrdinalIgnoreCase);

	// a_active: the active plugins in load order
	public static List<Issue> Check(IReadOnlyList<string> a_active)
	{
		var outList = new List<Issue>();
		int At(string a_name)
		{
			for (var i = 0; i < a_active.Count; ++i) {
				if (Is(a_active[i], a_name)) {
					return i;
				}
			}
			return -1;
		}
		// each rule names the first plugin in its way (a list out of order would name hundreds), and no plugin is named twice
		var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		void Say(string a_plugin, string a_text)
		{
			if (named.Add(a_plugin)) {
				outList.Add(new Issue("", false, a_text + " - LOOT sorts it there"));
			}
		}
		bool LastOnes(string a_p) => Is(a_p, DynEsp) || Is(a_p, Occlusion);

		var dyn = At(DynEsp);
		var occ = At(Occlusion);
		if (dyn >= 0 && occ >= 0 && occ < dyn) {
			Say(Occlusion, $"{Occlusion} loads before {DynEsp}, and DynDOLOD's help says it goes right after it");
		}
		foreach (var (name, at) in new[] { (DynEsp, dyn), (Occlusion, occ) }.Where(p => p.Item2 >= 0)) {
			if (a_active.Skip(at + 1).FirstOrDefault(p => !LastOnes(p)) is { } later) {
				Say(later, $"{later} loads after {name}, and DynDOLOD's help says {name} is among the last plugins");
			}
		}
		for (var i = 0; i < a_active.Count; ++i) {
			if (PgPlugin().IsMatch(a_active[i]) && a_active.Skip(i + 1).FirstOrDefault(p => !LastOnes(p) && !PgPlugin().IsMatch(p)) is { } later) {
				Say(later, $"{later} loads after {a_active[i]}, and PGPatcher's guide says it loads last, except for the DynDOLOD and TexGen outputs");
			}
		}
		// by its extension: the master flag inside a .esp would mean opening every plugin that loads after it, for a warning
		var esm = At(DynEsm);
		if (esm >= 0 && a_active.Skip(esm + 1).FirstOrDefault(p => p.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".esl", StringComparison.OrdinalIgnoreCase)) is { } master) {
			Say(master, $"{master} loads after {DynEsm}, and DynDOLOD's help says {DynEsm} is the master with the highest priority");
		}
		return outList;
	}
}
