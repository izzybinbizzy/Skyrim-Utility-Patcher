namespace ToolRunner;

// His checklist 492: our own add-on for Root Builder setups, Root Builder Profiles (izzydoingit), offered from Settings where Root Builder
// is in the player's Mod Organizer. It is our code and uses none of Root Builder's; Root Builder itself (Kezyma) comes from its own page and
// nothing of it ships here. Mod Organizer loads the add-on the next time it starts.
public static class RootBuilderAddOn
{
	public const string Title = "Root Builder Profiles";
	const string Folder = "rootbuilderprofiles";

	// its file names are Mod Organizer's and Python's to fix; the embedded copies go by the names the csproj gives them
	static readonly (string File, string Resource)[] Parts = [("__init__.py", "RootBuilderProfiles.init"), ("core.py", "RootBuilderProfiles.core")];

	public enum State { NoRootBuilder, NotAdded, Different, Current }

	static string Plugins(Mo2Instance a_mo2) => Path.Combine(Path.GetDirectoryName(a_mo2.Exe)!, "plugins");

	static byte[] Embedded(string a_resource)
	{
		using var s = typeof(RootBuilderAddOn).Assembly.GetManifestResourceStream(a_resource) ?? throw new InvalidDataException("the add-on is missing from the program: " + a_resource);
		using var ms = new MemoryStream();
		s.CopyTo(ms);
		return ms.ToArray();
	}

	public static State StateOf(Mo2Instance a_mo2)
	{
		var plugins = Plugins(a_mo2);
		if (!Directory.Exists(Path.Combine(plugins, "rootbuilder"))) {
			return State.NoRootBuilder;
		}
		var there = Path.Combine(plugins, Folder);
		if (!Directory.Exists(there)) {
			return State.NotAdded;
		}
		return Parts.All(p => File.Exists(Path.Combine(there, p.File)) && File.ReadAllBytes(Path.Combine(there, p.File)).AsSpan().SequenceEqual(Embedded(p.Resource))) ? State.Current : State.Different;
	}

	// into Mod Organizer's plugins folder, each file whole or not at all
	public static void Add(Mo2Instance a_mo2)
	{
		var there = Path.Combine(Plugins(a_mo2), Folder);
		foreach (var (file, resource) in Parts) {
			Files.WriteAtomic(Path.Combine(there, file), Embedded(resource));
		}
	}
}
