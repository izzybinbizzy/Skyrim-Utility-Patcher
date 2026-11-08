namespace ToolRunner;

// His checklist 469: "a detailed professional log for nerds and a simple step by step for casuals" - a tool's known error turned
// into what it means and what to do, with the tool's own help page. Read off each tool's own documentation (2026-11-04):
// sheson's tools (TexGen, DynDOLOD, xLODGen, TreeLODGen) list their exceptions at dyndolod.info/Messages/Exceptions and their
// memory advice in dyndolod.info/FAQ ("High memory usage / Out of memory"); Synthesis keeps its questions in its GitHub
// Discussions. Only what a page names is here; anything else keeps the tool's own words.
public static class ToolErrors
{
	public sealed record Explanation(string What, string Todo, string? Help);

	sealed record Known(string[] Steps, string[] Any, Explanation Says);

	const string Exceptions = "https://dyndolod.info/Messages/Exceptions";
	static readonly string[] Sheson = ["texgen", "dyndolod", "xlodgen", "treelodgen"];

	static readonly Known[] Table = [
		new(Sheson, ["Out of memory", "EOutOfMemory"], new("The tool ran out of memory.",
			"Use its x64 version and let Windows manage the page file, close other programs, and run it again. TexGen and DynDOLOD also take the -memory argument and TextureCache=10 in their INI (their FAQ explains both).",
			"https://dyndolod.info/FAQ")),
		new(Sheson, ["Access violation"], new("The tool crashed (an access violation).",
			"Use its newest version. Broken plugins or assets, low memory, unstable hardware or an antivirus can cause it: clean and error-check your plugins with xEdit, then run it again.",
			Exceptions)),
		new(Sheson, ["Can not create path", "The system cannot find the path specified"], new("The tool could not write its files.",
			"Let it write past Windows' protection and your antivirus, close programs that may hold its files, keep its output path short, and run it again.",
			Exceptions)),
		new(Sheson, ["can not be mapped to file FileID"], new("The load order is not settled.",
			"Finish your load order (sort it, no missing masters), clean and error-check the plugins with xEdit, then run it again.",
			Exceptions)),
		new(Sheson, ["Error reading grass", "Unknown grass data file format"], new("A grass cache file is broken.",
			"Delete that grass cache file in Data\\Grass (or in its mod) and make the grass cache again with the grass step.",
			Exceptions)),
		new(Sheson, ["Plugin limit", "too many plugins"], new("There are more plugins than the game and the tool can load.",
			"Untick plugins you do not need, or make small ones light (ESL), until you are under the limit.",
			"https://dyndolod.info/Messages/Plugin-Limit")),
		new(["synthesis"], ["Blocking Error"], new("A patcher in Synthesis could not run.",
			"Open Synthesis, read that patcher's error, then update it or switch it off, and run the step again.",
			"https://github.com/Mutagen-Modding/Synthesis/discussions")),
		new([], ["missing master", "requires master", "masters missing", "missing masters"], new("A plugin needs a master plugin that is not loaded.",
			"Install and tick the missing master in Mod Organizer, or untick the plugin that needs it, then run the step again.",
			null)),
	];

	// what a step's failure text means (any letter case), or null when no tool page names it
	public static Explanation? Explain(string a_step, string a_text) =>
		Table.FirstOrDefault(k => (k.Steps.Length == 0 || k.Steps.Contains(a_step)) && k.Any.Any(a => a_text.Contains(a, StringComparison.OrdinalIgnoreCase)))?.Says;

	// the simple log's lines for a step that did not finish
	public static List<string> Simple(string a_name, StepState a_state, string a_note, Explanation? a_why)
	{
		if (a_state == StepState.Stopped) {
			return [$"{a_name} was stopped."];
		}
		var lines = new List<string> { $"{a_name} did not finish: {a_why?.What ?? a_note}" };
		if (a_why != null) {
			lines.Add("What to do: " + a_why.Todo);
			if (a_why.Help != null) {
				lines.Add("Help: " + a_why.Help);
			}
			lines.Add("The tool's own words: " + a_note);
		} else {
			lines.Add("The detailed log has the tool's last lines; run the step again once you have looked.");
		}
		return lines;
	}
}
