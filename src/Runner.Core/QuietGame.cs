using System.Text.RegularExpressions;
using ToolRunner.Steps;

namespace ToolRunner;

// His checklist 460, an option: the in-game steps in a small, muted window that keeps running behind other windows, so the player can go on
// using the PC. Their settings go back after each step, and after a crash through the run's put-back (StepKit.KeepFile). Windowed mode and
// the size are also set in SSE Display Tweaks' file where a ticked mod ships one, because it overrides the game's own.
public static class QuietGame
{
	const string SettingName = "quietGame";
	const int Width = 960, Height = 540;
	// SSE Display Tweaks reads its _Custom file over its own (its way to keep a player's settings through its updates, and many
	// lists ship one with the window mode and size in it): changed in the plain file alone, the game still started borderless
	// over the whole screen
	static readonly string[] TweaksFiles = ["SSEDisplayTweaks.ini", "SSEDisplayTweaks_Custom.ini"];

	public static bool On(Setup a_setup) => Setup.Bool(a_setup.Root, SettingName) == true;

	// a_key set in a_section, every other line as it was: an active line first, else one commented out, else a new line under the section
	public static string Set(string a_ini, string a_section, string a_key, string a_value)
	{
		var nl = a_ini.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
		var lines = a_ini.Split(nl).ToList();
		var head = lines.FindIndex(l => l.Trim().Equals("[" + a_section + "]", StringComparison.OrdinalIgnoreCase));
		var line = a_key + "=" + a_value;
		if (head < 0) {
			while (lines.Count > 0 && lines[^1] == "") {
				lines.RemoveAt(lines.Count - 1);
			}
			lines.AddRange(["[" + a_section + "]", line, ""]);
			return string.Join(nl, lines);
		}
		var next = lines.FindIndex(head + 1, l => l.TrimStart().StartsWith('['));
		var end = next < 0 ? lines.Count : next;
		var active = new Regex(@"^\s*" + Regex.Escape(a_key) + @"\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
		var commented = new Regex(@"^\s*[#;]\s*" + Regex.Escape(a_key) + @"\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
		var at = lines.FindIndex(head + 1, end - head - 1, active.IsMatch);
		at = at >= 0 ? at : lines.FindIndex(head + 1, end - head - 1, commented.IsMatch);
		if (at >= 0) {
			lines[at] = line;
		} else {
			lines.Insert(head + 1, line);
		}
		return string.Join(nl, lines);
	}

	public static string GamePrefs(string a_ini)
	{
		a_ini = Set(a_ini, "Display", "bFull Screen", "0");
		a_ini = Set(a_ini, "Display", "bBorderless", "0");
		a_ini = Set(a_ini, "Display", "iSize W", Width.ToString(InvariantCulture));
		a_ini = Set(a_ini, "Display", "iSize H", Height.ToString(InvariantCulture));
		a_ini = Set(a_ini, "General", "bAlwaysActive", "1");
		return Set(a_ini, "AudioMenu", "fAudioMasterVolume", "0.0000");
	}

	public static string DisplayTweaks(string a_ini)
	{
		a_ini = Set(a_ini, "Render", "Fullscreen", "false");
		a_ini = Set(a_ini, "Render", "Borderless", "false");
		a_ini = Set(a_ini, "Render", "Resolution", $"{Width}x{Height}");
		// a scale would apply over the size just set; the file's own comment form takes it out
		var scale = new Regex(@"^(\s*)ResolutionScale(\s*=.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
		return scale.Replace(a_ini, "$1#ResolutionScale$2");
	}

	public static bool IsLocalSettings(string a_line)
	{
		var parts = a_line.Split('=', 2);
		return parts.Length == 2 && parts[0].Trim().Equals("LocalSettings", StringComparison.OrdinalIgnoreCase) && parts[1].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
	}

	// GOG's own library ships in its copy of the game, wherever that was installed
	public static bool IsGog(string a_gameDir) => File.Exists(Path.Combine(a_gameDir, "Galaxy64.dll"));

	// the profile's own settings file when it keeps them, else My Games'
	static string PrefsPath(Profile a_profile)
	{
		var settings = Path.Combine(a_profile.Dir, "settings.ini");
		if (File.Exists(settings) && Files.ReadSharedText(settings).Split('\n').Any(IsLocalSettings)) {
			return Path.Combine(a_profile.Dir, "skyrimprefs.ini");
		}
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", IsGog(a_profile.Mo2.GameDir) ? "Skyrim Special Edition GOG" : "Skyrim Special Edition", "SkyrimPrefs.ini");
	}

	// the files a step changed are read back to what they were by PutBack; a game start that finds them changed already leaves them
	public static void Apply(RunContext a_ctx)
	{
		if (!On(a_ctx.Setup) || a_ctx.QuietFiles.Count > 0) {
			return;
		}
		var prefs = PrefsPath(a_ctx.Profile);
		if (!File.Exists(prefs)) {
			a_ctx.Log.Warn("the game's settings file was not found, so it starts as usual: " + prefs);
			return;
		}
		var files = new List<(string Path, Func<string, string> Change)> { (prefs, GamePrefs) };
		// overwrite outranks every mod. The game's own Data folder is never written
		files.AddRange(a_ctx.Profile.States().Where(s => s.Value == '+').Select(s => a_ctx.Mo2.ModDir(s.Key)).Append(a_ctx.Mo2.OverwriteDir)
			.SelectMany(d => TweaksFiles.Select(n => Path.Combine(d, "SKSE", "Plugins", n))).Where(File.Exists)
			.Select(f => (f, (Func<string, string>)DisplayTweaks)));
		foreach (var (path, change) in files) {
			StepKit.KeepFile(a_ctx, path);
			try {
				// written back the way it was written (the game's own files are in the PC's code page: an accented value stays one)
				var text = Files.DecodeText(Files.ReadShared(path), out var how);
				Files.WriteAtomic(path, Files.EncodeText(change(text), how));
				a_ctx.QuietFiles.Add(path);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				// a file another program holds: left as it is and said - the game then starts with what could be set, never a
				// failed step over an option
				StepKit.Forget(a_ctx, path);
				a_ctx.Log.Warn($"{Path.GetFileName(path)} could not be changed for the small window ({e.Message}) - the game may start as it usually does");
			}
		}
		// the save to play runs after the run's record says finished: a crash there must still leave something to put back
		if (a_ctx.Journal.InGameAfter) {
			a_ctx.Journal.NeedsRestore = true;
			a_ctx.Journal.Save();
		}
		a_ctx.Log.Detail($"the game starts in a small, muted window ({Width} x {Height})");
	}

	// a file that will not go back stops the run: the next step would run in the quiet window, and the run's record keeps the file for "Put my profile back"
	public static void PutBack(RunContext a_ctx)
	{
		var notBack = a_ctx.QuietFiles.SelectMany(path => StepKit.PutFilesBack(a_ctx, path)).ToList();
		a_ctx.QuietFiles.Clear();
		if (notBack.Count > 0) {
			throw new StepFailedException($"the game's settings could not be put back from the quiet window ({Path.GetFileName(notBack[0])})");
		}
	}
}
