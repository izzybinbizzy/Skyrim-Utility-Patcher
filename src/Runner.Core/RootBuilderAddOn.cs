namespace ToolRunner;

// His checklist 468: the program's entry in Mod Organizer's Tools menu, one small plugin file that opens its window on the profile Mod Organizer
// shows. Offered from Settings; Mod Organizer loads it the next time it starts. A copy points at the program that added it, so a program moved
// since reads as different and is offered again.
public static class Mo2MenuEntry
{
	const string Slot = "@PROGRAM@";
	static readonly string FileName = Brand.Compact.ToLowerInvariant() + ".py";

	static string Plugins(Mo2Instance a_mo2) => Path.Combine(Path.GetDirectoryName(a_mo2.Exe)!, "plugins");

	static string There(Mo2Instance a_mo2) => Path.Combine(Plugins(a_mo2), FileName);

	// the entry an older name of the program added: left beside the new one it would show the program twice in the Tools menu and answer the
	// companion's questions twice. Only a file that is this entry (it names its author and the tool class) counts
	public static IEnumerable<string> OldOnes(Mo2Instance a_mo2) =>
		Brand.OldCompact.Select(n => Path.Combine(Plugins(a_mo2), n.ToLowerInvariant() + ".py")).Where(File.Exists)
			.Where(f => Files.ReadSharedText(f) is var text && text.Contains("Author: izzydoingit", StringComparison.Ordinal) && text.Contains("mobase.IPluginTool", StringComparison.Ordinal));

	public static string Text(string a_program)
	{
		using var s = typeof(Mo2MenuEntry).Assembly.GetManifestResourceStream("MenuEntry") ?? throw new InvalidDataException("the menu entry is missing from the program");
		using var r = new StreamReader(s);
		return r.ReadToEnd().Replace(Slot, a_program, StringComparison.Ordinal);
	}

	// current = this program's entry as it writes it now, and none of an older name beside it
	public static bool IsCurrent(Mo2Instance a_mo2, string a_program) => File.Exists(There(a_mo2)) && File.ReadAllText(There(a_mo2)) == Text(a_program) && !OldOnes(a_mo2).Any();

	public static void Add(Mo2Instance a_mo2, string a_program)
	{
		Files.WriteAtomic(There(a_mo2), Text(a_program));
		foreach (var old in OldOnes(a_mo2).ToList()) {
			Files.TryDelete(old);
		}
	}

	// the entry under the program's name now: an older name's entry cannot answer this version (its data folder is another)
	public static bool Present(Mo2Instance a_mo2) => File.Exists(There(a_mo2));
}

// His checklist 477: a run closes and starts Mod Organizer about 13 times (about 6 minutes). With this option on it stays open: the order files are written
// while it is open and read back in by the plugin above before each tool. A file for each question and each answer, in the plugin's own data folder.
public static class Companion
{
	static string Folder(Mo2Instance a_mo2) => Path.Combine(Path.GetDirectoryName(a_mo2.Exe)!, "plugins", "data", "OutputManager");

	// true = answered; false = no answer within a_wait, and nothing is left behind. The question names the instance and profile: with several Mod Organizers
	// open only the run's own may answer, or its lists would be read back into the wrong one
	public static bool Ask(Mo2Instance a_mo2, string a_profile, string a_what, TimeSpan a_wait, CancellationToken a_ct)
	{
		var folder = Folder(a_mo2);
		var id = Guid.NewGuid().ToString("N");
		var ask = Path.Combine(folder, $"ask {id}.txt");
		var done = Path.Combine(folder, $"done {id}.txt");
		Files.WriteAtomic(ask, $"{a_what}\n{a_mo2.BaseDir}\n{a_profile}");
		try {
			var sw = System.Diagnostics.Stopwatch.StartNew();
			while (sw.Elapsed < a_wait) {
				if (File.Exists(done)) {
					return true;
				}
				a_ct.WaitHandle.WaitOne(100);
				a_ct.ThrowIfCancellationRequested();
			}
			return false;
		} finally {
			Files.TryDelete(ask);
			Files.TryDelete(done);
		}
	}
}

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
