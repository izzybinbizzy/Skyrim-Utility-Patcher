namespace ToolRunner;

// The program's name, and the names it had before: a save, a cache stamp, a data folder, a run lock, an add-on folder or a Windows task made
// under an old name is still its own. The one place a rename is made: the name here, the old one added to OldNames (and its compact form to
// OldCompact), and the window project's AssemblyName (a test holds the two equal).
public static class Brand
{
	public const string Name = "Output Manager";

	// newest first
	public static readonly string[] OldNames = ["Skyrim Utility Patcher", "Patcher Unification Project", "MO2 Tool Runner"];

	public static IEnumerable<string> AllNames => OldNames.Prepend(Name);

	// the name with no space in it, for a folder or a file whose name must have none (ProgramData, Mod Organizer's plugins folder)
	public const string Compact = "OutputManager";

	// the compact names it used before; "PatcherUnificationProject" never named a folder
	public static readonly string[] OldCompact = ["SkyrimUtilityPatcher", "MO2ToolRunner"];

	// its folder in ProgramData, for the one tool route that needs a path with no space in it (FasterNGIO's wrapper); a folder under an
	// old name is still its own (the clean removal takes them all)
	public static string WorkRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Compact);

	public static IEnumerable<string> OldWorkRoots => OldCompact.Select(n => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), n));

	// "<name> - ..." under the new name or an old one
	public static bool Made(string a_text) => AllNames.Any(n => a_text.StartsWith(n + " - ", StringComparison.OrdinalIgnoreCase));

	// exactly one of its names, any case (a folder a modder named after the program, a process)
	public static bool Is(string? a_text) => a_text != null && AllNames.Any(n => string.Equals(a_text, n, StringComparison.OrdinalIgnoreCase));
}
