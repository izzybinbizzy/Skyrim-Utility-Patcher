namespace ToolRunner;

// The program's name, and the names it had before: a save, a cache stamp, a data folder or a Windows task made under the old
// name is still its own.
public static class Brand
{
	public const string Name = "Skyrim Utility Patcher";

	public static readonly string[] OldNames = ["Patcher Unification Project", "MO2 Tool Runner"];

	public static IEnumerable<string> AllNames => OldNames.Prepend(Name);

	// its folder in ProgramData, for the one tool route that needs a path with no space in it (FasterNGIO's wrapper); the old
	// name's folder is still its own (the clean removal takes both)
	public static string WorkRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SkyrimUtilityPatcher");

	public static string OldWorkRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MO2ToolRunner");

	// "<name> - ..." under the new name or an old one
	public static bool Made(string a_text) => AllNames.Any(n => a_text.StartsWith(n + " - ", StringComparison.OrdinalIgnoreCase));
}
