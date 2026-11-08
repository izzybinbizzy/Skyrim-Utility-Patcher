using System.Text;

namespace ToolRunner;

// His checklist 439: the game's load boxes in any language, by the game's own text for them and never by a list of ours. The boxes that
// ask to activate or search for missing content sit in Interface\translate_<language>.txt inside its interface archive, under fixed
// keys; "... continue loading?" is a game setting, read through DevBench. Every language's words are taken: no need to know which one
// the game runs. A game in English never comes here.
public sealed class GameWords
{
	// answered No: activating a plugin in game can crash it
	static readonly string[] NoKeys = ["$SaveGameInactiveContent", "$SaveGameMissingContentActivate", "$SaveGameMissingUnavailableSearch",
		"$SaveGameMissingUnidentifiedContent", "$SaveGameMissingUnidentifiedSearch", "$SaveGameMissingUnownedContent", "$SaveGameUnownedContinue"];

	// answered Yes
	public static readonly IReadOnlyList<string> YesSettings = ["sSaveGameContentIsMissing", "sLoadVanillaSaveWithMods"];

	// a shorter text could stand inside any box
	const int ShortestAsk = 8;

	readonly HashSet<string> _yes = [], _no = [];
	readonly List<string> _askNo = [], _askYes = [];

	static string Low(string a_s) => a_s.Trim().ToLowerInvariant();

	public bool AsksNo(string a_body) => _askNo.Exists(a => Low(a_body).Contains(a, StringComparison.Ordinal));
	public bool AsksYes(string a_body) => _askYes.Exists(a => Low(a_body).Contains(a, StringComparison.Ordinal));
	public int HasAskYes => _askYes.Count;

	// the place of the button for this answer among a box's labels (-1: none reads so)
	public int Button(bool a_yes, IReadOnlyList<string> a_labels)
	{
		var want = a_yes ? _yes : _no;
		for (var i = 0; i < a_labels.Count; ++i) {
			if (want.Contains(Low(a_labels[i]))) {
				return i;
			}
		}
		return -1;
	}

	// a_translations: the text of each translate_<language>.txt; a_askYes: what the game settings say
	public static GameWords Of(IEnumerable<string> a_translations, IEnumerable<string> a_askYes)
	{
		var w = new GameWords();
		foreach (var line in a_translations.SelectMany(t => t.Split('\n'))) {
			var tab = line.IndexOf('\t', StringComparison.Ordinal);
			if (tab <= 0) {
				continue;
			}
			var key = line[..tab].Trim();
			var value = Low(line[(tab + 1)..]);
			if (value == "") {
				continue;
			}
			if (key.Equals("$Yes", StringComparison.OrdinalIgnoreCase)) {
				w._yes.Add(value);
			} else if (key.Equals("$No", StringComparison.OrdinalIgnoreCase)) {
				w._no.Add(value);
			} else if (value.Length >= ShortestAsk && NoKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) {
				w._askNo.Add(value);
			}
		}
		w._askYes.AddRange(a_askYes.Select(Low).Where(a => a.Length >= ShortestAsk));
		return w;
	}

	// nothing readable leaves the English words alone at work, and a box they do not know stops its step as it always did
	public static GameWords Read(string a_gameDir, Func<string, string?> a_setting)
	{
		var texts = new List<string>();
		try {
			texts = TranslationsIn(Path.Combine(a_gameDir, "Data", "Skyrim - Interface.bsa"));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or OverflowException) {
		}
		return Of(texts, YesSettings.Select(a_setting).OfType<string>());
	}

	const uint ArchiveMagic = 0x00415342;
	const uint ArchiveVersion = 105;
	const uint FlagPacked = 4;
	const uint FlagEmbeddedNames = 0x100;
	const uint SizePackedBit = 0x40000000;
	const uint SizeMask = 0x3FFFFFFF;
	const int LargestText = 4 * 1024 * 1024;
	const int MostEntries = 1_000_000;

	// the translation files out of a game archive (version 105: Skyrim SE and AE). The game's own are stored unpacked; a packed one is
	// passed over
	public static List<string> TranslationsIn(string a_archive)
	{
		var outList = new List<string>();
		if (!File.Exists(a_archive)) {
			return outList;
		}
		using var f = new FileStream(a_archive, FileMode.Open, FileAccess.Read, FileShare.Read);
		using var r = new BinaryReader(f);
		if (r.ReadUInt32() != ArchiveMagic || r.ReadUInt32() != ArchiveVersion) {
			return outList;
		}
		var foldersAt = r.ReadUInt32();
		var flags = r.ReadUInt32();
		var folders = r.ReadInt32();
		var files = r.ReadInt32();
		r.ReadUInt32();
		var namesLength = r.ReadInt32();
		if (folders is < 0 or > MostEntries || files is < 0 or > MostEntries || namesLength < 0) {
			throw new InvalidDataException("not a game archive: " + a_archive);
		}
		f.Position = foldersAt;
		var counts = new int[folders];
		for (var i = 0; i < folders; ++i) {
			r.ReadUInt64();
			counts[i] = r.ReadInt32();
			r.ReadUInt32();
			r.ReadUInt64();
		}
		var items = new List<(uint Size, uint At)>(files);
		foreach (var count in counts) {
			var folderName = r.ReadByte();
			f.Position += folderName;
			for (var i = 0; i < count; ++i) {
				r.ReadUInt64();
				items.Add((r.ReadUInt32(), r.ReadUInt32()));
			}
		}
		var names = Encoding.Latin1.GetString(r.ReadBytes(namesLength)).Split('\0');
		var packed = (flags & FlagPacked) != 0;
		var embedded = (flags & FlagEmbeddedNames) != 0;
		for (var i = 0; i < items.Count && i < names.Length; ++i) {
			if (!names[i].StartsWith("translate_", StringComparison.OrdinalIgnoreCase) || !names[i].EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) {
				continue;
			}
			var (size, at) = items[i];
			if (packed != ((size & SizePackedBit) != 0)) {
				continue;
			}
			f.Position = at;
			var length = (int)(size & SizeMask);
			if (embedded) {
				var name = r.ReadByte();
				f.Position += name;
				length -= name + 1;
			}
			if (length is > 0 and <= LargestText) {
				outList.Add(Files.DecodeText(r.ReadBytes(length)));
			}
		}
		return outList;
	}
}
