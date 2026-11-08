using System.Text;

namespace ToolRunner;

// Which character a save belongs to (his checklist 457: pick the character for the in-game steps). Only the start of a save's
// header is read - the layout the UESP wiki documents for Skyrim Special Edition saves: "TESV_SAVEGAME", the header's size, its
// version, the save number, then the player's name (a 16-bit length and its characters). Nothing else of the save is touched.
public static class SaveHeader
{
	const string Magic = "TESV_SAVEGAME";

	// the player's name in a save, or null when the file is no save this reads (locked, cut short, another game's)
	public static string? PlayerName(string a_file)
	{
		try {
			using var fs = new FileStream(a_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using var r = new BinaryReader(fs, Encoding.Latin1);
			if (fs.Length < Magic.Length + 14 || Encoding.ASCII.GetString(r.ReadBytes(Magic.Length)) != Magic) {
				return null;
			}
			r.ReadUInt32();
			r.ReadUInt32();
			r.ReadUInt32();
			var len = r.ReadUInt16();
			if (len == 0 || len > 256 || fs.Position + len > fs.Length) {
				return null;
			}
			// the game writes its names in the Windows Western code page (1252); Latin-1 reads the same letters
			var name = Encoding.Latin1.GetString(r.ReadBytes(len)).Trim();
			return name == "" ? null : name;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException) {
			return null;
		}
	}

	// every character with a save in a folder, the most recently played first
	public static List<string> Characters(string a_savesDir)
	{
		if (!Directory.Exists(a_savesDir)) {
			return [];
		}
		try {
			return new DirectoryInfo(a_savesDir).EnumerateFiles("*.ess").OrderByDescending(f => f.LastWriteTimeUtc)
				.Select(f => PlayerName(f.FullName)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return [];
		}
	}
}
