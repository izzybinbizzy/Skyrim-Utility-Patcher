using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ToolRunner;

// "Help me report a bug" (his checklist 482): one zip holding what a bug report needs - the run's record and both its logs, the
// setup it ran, the crash notes, and the versions of the program, Windows, Mod Organizer, the game and each step's tool - with the
// player's own names masked (their Windows user name and the PC's name), ready to attach to an issue. Nothing is sent anywhere:
// the zip is the player's to post or not.
public static class BugReport
{
	public const string Issues = "https://github.com/izzybinbizzy/Skyrim-Utility-Patcher/issues/new/choose";

	// a log can be long (a DynDOLOD run): only its last part goes in
	const int MaxBytes = 8 * 1024 * 1024;

	// the names a report never carries, longest first (so "Itsth-PC" is masked before "Itsth"); too short a name is left alone,
	// since it would mask parts of ordinary words
	public static List<(string Name, string Mask)> Names()
	{
		var outList = new List<(string, string)>();
		void Add(string? a_name, string a_mask)
		{
			if (a_name is { Length: >= 3 } && !outList.Any(o => string.Equals(o.Item1, a_name, StringComparison.OrdinalIgnoreCase))) {
				outList.Add((a_name, a_mask));
			}
		}
		Add(Environment.MachineName, "<this PC>");
		Add(Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), "<you>");
		Add(Environment.UserName, "<you>");
		return outList.OrderByDescending(o => o.Item1.Length).ToList();
	}

	// every whole-word use of each name (any letter case) replaced by its mask
	public static string Mask(string a_text, IEnumerable<(string Name, string Mask)> a_names)
	{
		foreach (var (name, mask) in a_names) {
			a_text = Regex.Replace(a_text, @"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?![\p{L}\p{N}])", mask, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		}
		return a_text;
	}

	// only the last MaxBytes are read off the disk (a log of many gigabytes never comes into memory whole)
	static string ReadTail(string a_path)
	{
		using var fs = new FileStream(a_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		if (fs.Length <= MaxBytes) {
			var all = new byte[fs.Length];
			fs.ReadExactly(all);
			return Files.DecodeText(all);
		}
		fs.Seek(-MaxBytes, SeekOrigin.End);
		var bytes = new byte[MaxBytes];
		fs.ReadExactly(bytes);
		var tail = Encoding.UTF8.GetString(bytes);
		return "(only the last part of this file is here)\n" + tail[(tail.IndexOf('\n') + 1)..];
	}

	static string FileVersion(string? a_exe)
	{
		try {
			return a_exe != null && File.Exists(a_exe) ? FileVersionInfo.GetVersionInfo(a_exe) is { } v && !string.IsNullOrWhiteSpace(v.FileVersion) ? v.FileVersion! : "(no version)" : "(not found)";
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) {
			return "(not readable)";
		}
	}

	// the versions a report needs to be answered at all
	public static string About(string a_version, Setup? a_setup)
	{
		var lines = new List<string> {
			$"{Brand.Name} {a_version}",
			$"Windows: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), language {System.Globalization.CultureInfo.CurrentUICulture.Name}",
			$".NET: {RuntimeInformation.FrameworkDescription}",
			"Run as administrator: " + (Environment.IsPrivilegedProcess ? "yes" : "no"),
		};
		if (a_setup != null) {
			lines.Add($"Setup: {a_setup.Name} (profile {a_setup.Profile})");
			lines.Add("Mod Organizer: " + FileVersion(a_setup.Mo2Exe));
			try {
				var mo2 = Mo2Instance.Load(a_setup.Mo2Exe, a_setup.Mo2Instance);
				lines.Add($"Game: {FileVersion(Path.Combine(mo2.GameDir, "SkyrimSE.exe"))} ({(Mo2Control.IsSteamGame(mo2.GameDir) ? "Steam" : "not Steam - GOG or another copy")})");
				lines.Add("Steps (on / off, the tool's version):");
				foreach (var s in a_setup.Steps) {
					var (exe, version) = ToolUpdates.Yours(mo2, new StepSettings(s.Settings));
					lines.Add($"  {(s.On ? "on " : "off")} {StepCatalog.Name(s.Key)}: {(exe == null ? "(its program is not found)" : version == "" ? "(no version)" : version)}");
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) {
				lines.Add("Mod Organizer's instance could not be read: " + e.Message);
			}
		}
		return string.Join(Environment.NewLine, lines) + Environment.NewLine;
	}

	// the zip, made in a_outDir; returns its path. a_run: the run it is about (null = only the program's own notes)
	public static string Make(string a_dataDir, Journal? a_run, string a_outDir, string a_version)
	{
		var names = Names();
		Setup? setup = null;
		var setupFile = a_run?.SetupFile;
		if (setupFile != null && File.Exists(setupFile)) {
			try {
				setup = Setup.Load(setupFile);
			} catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException) {
			}
		}
		var entries = new List<(string Name, string Text)> { ("about.txt", About(a_version, setup)) };
		if (a_run != null) {
			foreach (var f in new[] { "journal.json", "log.txt", RunLog.SimpleName }) {
				var p = Path.Combine(a_run.Folder, f);
				if (File.Exists(p)) {
					entries.Add(("run\\" + f, ReadTail(p)));
				}
			}
		}
		if (setupFile != null && File.Exists(setupFile)) {
			entries.Add(("setup.json", ReadTail(setupFile)));
		}
		var crash = Path.Combine(a_dataDir, "crash.txt");
		if (File.Exists(crash)) {
			entries.Add(("crash.txt", ReadTail(crash)));
		}
		Directory.CreateDirectory(a_outDir);
		var zip = Path.Combine(a_outDir, $"{Brand.Name} bug report {DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", InvariantCulture)}.zip");
		var tmp = zip + ".tmp";
		try {
			using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
			using (var z = new ZipArchive(fs, ZipArchiveMode.Create)) {
				foreach (var (name, text) in entries) {
					using var w = new StreamWriter(z.CreateEntry(name.Replace('\\', '/'), CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
					w.Write(Mask(text, names));
				}
			}
			File.Move(tmp, zip, true);
		} catch {
			Files.TryDelete(tmp);
			throw;
		}
		return zip;
	}
}
