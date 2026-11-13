namespace ToolRunner;

// His checklist 467: the first-start helper. Which of the tools a setup can run are in Mod Organizer's Executables list, which sit on the
// PC without being listed, and which are missing, with where to get each and where to put it; the ones found are added to the list for the
// player, with Mod Organizer closed (it writes its ini when it closes, over anything written meanwhile). A step exists only for a tool the
// list holds, so this comes before a setup is made. FasterNGIO and Wrye Bash are left out: the first is found in the mods without a list
// entry, the second is a guided step, off in a new setup.
public static class ToolFinder
{
	public sealed record Tool(string Step, string Name, IReadOnlyList<string> Binaries, string Arguments, string Page, string Where);

	public enum Found { Listed, NotListed, Missing }

	public sealed record Row(Tool Tool, Found State, string? Binary);

	const string OwnFolder = "unpack it into a folder of its own beside Mod Organizer - not inside Program Files, the game's folder or Mod Organizer's mods folder";
	const string AsMod = "install it with Mod Organizer, as a mod";
	const string XLodGenPage = "https://stepmodifications.org/forum/topic/13451-xlodgen-terrain-lod-beta/";

	static string PageOf(string a_step) => ToolUpdates.Known[a_step].Page;

	// in the order a run uses them; the xEdit family needs its game named on its command line
	public static readonly IReadOnlyList<Tool> Tools = [
		new("synthesis", "Synthesis", ["Synthesis.exe"], "", PageOf("synthesis"), OwnFolder),
		new("landscapefixer", "Landscape Fixer", ["LandscapeFixer.exe"], "", PageOf("landscapefixer"), AsMod),
		new("pgpatcher", "PGPatcher", ["PGPatcher.exe"], "", PageOf("pgpatcher"), AsMod),
		new("xlodgen", "xLODGen", ["xLODGenx64.exe", "xLODGen.exe"], "-sse", XLodGenPage, OwnFolder),
		new("acmos", "ACMOS Road Generator", ["ACMOS Road Generator.exe"], "", PageOf("acmos"), AsMod),
		new("treelodgen", "TreeLODGen", ["TreeLODGen.exe"], "", PageOf("treelodgen"), OwnFolder),
		new("texgen", "TexGen", ["TexGenx64.exe", "TexGen.exe"], "-sse", PageOf("texgen"), OwnFolder),
		new("dyndolod", "DynDOLOD", ["DynDOLODx64.exe", "DynDOLOD.exe"], "-sse", PageOf("dyndolod"), OwnFolder),
		new("pandora", "Pandora Behaviour Engine", ["Pandora Behaviour Engine+.exe", "Pandora Behaviour Engine.exe"], "", PageOf("pandora"), AsMod),
	];

	// where a player keeps tools: the mods, and the usual folders beside Mod Organizer
	public static List<string> Places(Mo2Instance a_mo2)
	{
		var exeDir = Path.GetDirectoryName(a_mo2.Exe)!;
		var places = new List<string> { a_mo2.ModsDir, Path.Combine(exeDir, "tools"), Path.Combine(a_mo2.BaseDir, "tools") };
		var parent = Path.GetDirectoryName(exeDir);
		if (parent != null && Path.GetDirectoryName(parent) != null) {
			try {
				places.AddRange(Directory.EnumerateDirectories(parent).Where(d => !Files.SamePath(d, exeDir)));
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				// a folder that cannot be listed holds nothing it can name
			}
		}
		var root = Path.GetPathRoot(exeDir)!;
		places.Add(Path.Combine(root, "Tools"));
		places.Add(Path.Combine(root, "Modding", "Tools"));
		return [.. places.Distinct(StringComparer.OrdinalIgnoreCase)];
	}

	// the deepest a tool sits under a place: a mod's own folder, and two more
	const int LookDeep = 3;

	// every wanted program under the places; of two copies, the newer
	static Dictionary<string, string> OnDisk(IEnumerable<string> a_places)
	{
		var want = Tools.SelectMany(t => t.Binaries).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = LookDeep, IgnoreInaccessible = true };
		var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var place in a_places.Where(Directory.Exists)) {
			try {
				foreach (var f in Directory.EnumerateFiles(place, "*.exe", options).Where(f => want.Contains(Path.GetFileName(f)))) {
					var name = Path.GetFileName(f);
					if (!found.TryGetValue(name, out var had) || File.GetLastWriteTimeUtc(f) > File.GetLastWriteTimeUtc(had)) {
						found[name] = f;
					}
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				// a folder that stops answering holds nothing it can name
			}
		}
		return found;
	}

	public static List<Row> Find(Mo2Instance a_mo2, IEnumerable<string>? a_places = null)
	{
		var found = OnDisk(a_places ?? Places(a_mo2));
		return [.. Tools.Select(t => {
			var listed = a_mo2.Executables.FirstOrDefault(e => File.Exists(e.Binary) && t.Binaries.Any(b => string.Equals(Path.GetFileName(e.Binary), b, StringComparison.OrdinalIgnoreCase)));
			if (listed != null) {
				return new Row(t, Found.Listed, listed.Binary);
			}
			var there = t.Binaries.Select(b => found.GetValueOrDefault(b)).OfType<string>().FirstOrDefault();
			return there != null ? new Row(t, Found.NotListed, there) : new Row(t, Found.Missing, null);
		})];
	}

	// A Qt ini value: backslashes and quotes escaped, quoted where Qt would read it as a list, and every character outside plain
	// ASCII written as \x and its number in hex - which every Mod Organizer reads back the same (written as it is, a path with
	// an accent was read as other letters by a Mod Organizer built on Qt 5). The character after such an escape is written
	// as one too when it is a hex digit, or Qt would read it into the number: Qt's own rule
	public static string QtText(string a_value)
	{
		var sb = new System.Text.StringBuilder(a_value.Length + 8);
		var afterHex = false;
		foreach (var c in a_value) {
			if (c is '\\' or '"') {
				sb.Append('\\').Append(c);
				afterHex = false;
			} else if (c < 0x20 || c > 0x7E || (afterHex && Uri.IsHexDigit(c))) {
				sb.Append("\\x").Append(((int)c).ToString("x", InvariantCulture));
				afterHex = true;
			} else {
				sb.Append(c);
				afterHex = false;
			}
		}
		var s = sb.ToString();
		return s.IndexOfAny([',', ';', '=']) >= 0 || s != s.Trim() ? "\"" + s + "\"" : s;
	}

	const string Section = "[customExecutables]";

	// the ini's text with one more executable at the end of its list, every other line as it was
	public static string WithExecutable(string a_ini, string a_title, string a_binary, string a_arguments)
	{
		var nl = a_ini.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
		var lines = a_ini.Split(nl).ToList();
		var head = lines.IndexOf(Section);
		if (head < 0) {
			while (lines.Count > 0 && lines[^1] == "") {
				lines.RemoveAt(lines.Count - 1);
			}
			head = lines.Count;
			lines.AddRange([Section, "size=0", ""]);
		}
		var end = lines.FindIndex(head + 1, l => l.StartsWith('['));
		end = end < 0 ? lines.Count : end;
		while (end > head + 1 && lines[end - 1] == "") {
			--end;
		}
		var sizeAt = lines.FindIndex(head + 1, end - head - 1, l => l.StartsWith("size=", StringComparison.Ordinal));
		var size = sizeAt >= 0 && int.TryParse(lines[sizeAt].AsSpan(5), System.Globalization.NumberStyles.None, InvariantCulture, out var n) ? n : 0;
		var at = size + 1;
		var binary = Path.GetFullPath(a_binary).Replace('\\', '/');
		lines.InsertRange(end, [
			$@"{at}\arguments={QtText(a_arguments)}",
			$@"{at}\binary={QtText(binary)}",
			$@"{at}\hide=false",
			$@"{at}\ownicon=false",
			$@"{at}\steamAppID=",
			$@"{at}\title={QtText(a_title)}",
			$@"{at}\toolbar=false",
			$@"{at}\workingDirectory={QtText(Path.GetDirectoryName(binary)!.Replace('\\', '/'))}",
		]);
		if (sizeAt >= 0) {
			lines[sizeAt] = "size=" + at.ToString(InvariantCulture);
		} else {
			lines.Insert(head + 1, "size=" + at.ToString(InvariantCulture));
		}
		return string.Join(nl, lines);
	}

	// a title the list does not use yet
	static string FreeTitle(string a_name, HashSet<string> a_taken)
	{
		var title = a_name;
		for (var i = 2; a_taken.Contains(title); ++i) {
			title = $"{a_name} ({i})";
		}
		return title;
	}

	public static string BackupOf(Mo2Instance a_mo2) => a_mo2.IniPath + ".before tools were added";

	// the tools found but not listed, into Mod Organizer's list; returns their titles. Its ini as it was is kept beside it
	public static List<string> AddFound(Mo2Instance a_mo2, IEnumerable<Row> a_rows)
	{
		var running = Procs.ByImage("ModOrganizer.exe").Select(p => {
			using (p) {
				return Procs.ExePath(p);
			}
		}).Any(p => p == null || Files.SamePath(p, a_mo2.Exe));
		if (running) {
			throw new Mo2BusyException("close Mod Organizer first - it writes this list when it closes (one started as administrator counts too, its path cannot be read)");
		}
		var taken = a_mo2.Executables.Select(e => e.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var text = Files.ReadSharedText(a_mo2.IniPath);
		var added = new List<string>();
		foreach (var r in a_rows.Where(r => r.State == Found.NotListed && r.Binary != null)) {
			var title = FreeTitle(r.Tool.Name, taken);
			text = WithExecutable(text, title, r.Binary!, r.Tool.Arguments);
			taken.Add(title);
			added.Add(title);
		}
		if (added.Count > 0) {
			Files.Copy(a_mo2.IniPath, BackupOf(a_mo2), true);
			Files.WriteAtomic(a_mo2.IniPath, text);
		}
		return added;
	}
}
