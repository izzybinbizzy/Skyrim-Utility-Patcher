using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ToolRunner.App;

// Themes (his ask 2026-10-27, "a full template of all features that can be redesigned ... so i can have it create some themes for
// the runner"): a theme is a copy of "Theme template.json" with new values, kept in the data folder's "Themes"; the one picked
// in Settings is applied to the window at once (every themeable value is read live - DynamicResource) and kept for the next
// start. A key left out keeps the default; a value that does not read is named, and its default kept.
public static class Theme
{
	// the default look: white panels, blue accents (his order 2026-11-10). App.xaml holds its values; its theme file is built in
	// like the others, for the gallery's picture of it and as the start of a player's own theme
	public const string Default = "Daylight";

	// the default until 2026-11-10; a player who picked it keeps it
	public const string BlackAndGold = "Black and Gold";

	public static string Folder => Path.Combine(AppPaths.Data, "Themes");
	static string ChoiceFile => Path.Combine(AppPaths.Data, "theme.txt");
	static string TextSizeFile => Path.Combine(AppPaths.Data, "text size.txt");

	// the looks built into the program beside the default (his checklist 473): Windows' own high-contrast colors, taken from
	// Windows each time it is applied, and a palette whose good / warning / bad states stay apart for color-blind eyes
	// (blue / dark yellow / orange, from the Okabe-Ito set); every state keeps its own glyph as well
	public const string HighContrast = "High contrast", ColorBlind = "Color-blind safe";

	// the themes shipped inside the program as theme files (Themes\<name>.json), in the gallery's order
	static readonly string[] Shipped = [BlackAndGold, "Graphite", "Midnight", "Slate", "Paper", "Forest", "Ember", "Orchid", "Carbon", "Chalk"];

	static readonly string[] BuiltIns = [.. Shipped, HighContrast, ColorBlind];

	public static IReadOnlyList<string> BuiltInNames => BuiltIns;

	public static IReadOnlyList<string> ShippedNames => Shipped;

	static string Hex(Color a_c) => $"#{a_c.R:X2}{a_c.G:X2}{a_c.B:X2}";

	static JsonObject Tokens(params (string Key, string Value)[] a_values)
	{
		var o = new JsonObject();
		foreach (var (k, v) in a_values) {
			o[k] = new JsonObject { ["value"] = v };
		}
		return o;
	}

	// a shipped theme's own file, read out of the program
	public static string ShippedText(string a_name)
	{
		using var s = typeof(Theme).Assembly.GetManifestResourceStream($"Theme.{a_name}.json") ?? throw new InvalidDataException($"the theme {a_name} is missing from the program");
		using var r = new StreamReader(s);
		return r.ReadToEnd();
	}

	static JsonObject BuiltIn(string a_name)
	{
		if (Shipped.Contains(a_name) || a_name == Default) {
			return JsonNode.Parse(ShippedText(a_name)) as JsonObject ?? [];
		}
		if (a_name == ColorBlind) {
			return new JsonObject { ["colors"] = Tokens(("Good", "#0072B2"), ("Warn", "#7A5C00"), ("Bad", "#B34E00"), ("BannerBad", "#FBE6D4")) };
		}
		var win = Hex(SystemColors.WindowColor);
		var text = Hex(SystemColors.WindowTextColor);
		var high = Hex(SystemColors.HighlightColor);
		var onHigh = Hex(SystemColors.HighlightTextColor);
		var w = SystemColors.WindowColor;
		var dark = 0.2126 * w.R + 0.7152 * w.G + 0.0722 * w.B < 128;
		return new JsonObject {
			["windowsMode"] = dark ? "Dark" : "Light",
			["colors"] = Tokens(("Bg", win), ("Panel", win), ("Panel2", win), ("Line", text), ("Accent", high), ("AccentLight", text), ("AccentDim", text),
				("Text", text), ("Muted", text), ("Good", text), ("Warn", text), ("Bad", text), ("OnAccent", onHigh), ("HoverFill", win), ("BannerBad", win),
				("GuidedFill", win), ("Shade", "#99000000")),
			["highContrastPairs"] = true,
			["gradients"] = new JsonObject { ["PrimaryAndProgress"] = new JsonObject { ["stops"] = new JsonArray(
				new JsonObject { ["color"] = high, ["at"] = 0.0 }, new JsonObject { ["color"] = high, ["at"] = 1.0 }) } },
			["windowsAccent"] = new JsonObject { ["Light3"] = high, ["Light2"] = high, ["Light1"] = high, ["Base"] = high, ["Dark1"] = high, ["Dark2"] = high, ["Dark3"] = high },
		};
	}

	// the text size, a share of the theme's own sizes (1 = as the theme has them)
	public static double TextScale { get; private set; } = 1;

	public static readonly double[] TextScales = [1, 1.15, 1.3, 1.5];

	public static void SetTextScale(double a_scale)
	{
		TextScale = TextScales.Contains(a_scale) ? a_scale : 1;
		try {
			Files.WriteAtomic(TextSizeFile, TextScale.ToString(InvariantCulture));
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
		ApplyScale();
	}

	// the "Size..." values as the theme set them (before the text size), taken each time a theme is applied
	static Dictionary<string, double> s_sizes = [];

	static void TakeSizes()
	{
		var res = Application.Current.Resources;
		s_sizes = res.Keys.OfType<string>().Where(k => k.StartsWith("Size", StringComparison.Ordinal) && res[k] is double).ToDictionary(k => k, k => (double)res[k]);
	}

	// every "Size..." value the theme set, times the text size
	static void ApplyScale()
	{
		var res = Application.Current.Resources;
		foreach (var (k, d) in s_sizes) {
			res[k] = Math.Round(d * TextScale, 1);
		}
	}

	// Windows' high contrast switched on or off while the window is open: a player who never picked a theme follows it
	static bool s_followHighContrast;

	// Windows' colors changed (high contrast on or off, or another high-contrast theme): once WPF has taken Windows' new colors
	// (a low-priority turn after the change), a player who never picked a theme follows high contrast, and the High contrast
	// look takes the new colors. Nothing here may escape: it runs on the window's thread.
	static void FollowHighContrast()
	{
		Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) => {
			if (e.Category is not (Microsoft.Win32.UserPreferenceCategory.Color or Microsoft.Win32.UserPreferenceCategory.Accessibility or
			    Microsoft.Win32.UserPreferenceCategory.General)) {
				return;
			}
			Application.Current?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => {
				try {
					if (s_followHighContrast && (SystemParameters.HighContrast ? Current != HighContrast : Current == HighContrast)) {
						Apply(SystemParameters.HighContrast ? HighContrast : Default, false);
					} else if (Current == HighContrast) {
						Apply(HighContrast, false);
					}
					Changed?.Invoke();
				} catch (Exception ex) when (ex is not OutOfMemoryException) {
					Reset();
				}
			});
		};
	}

	// the look changed without the player picking it (Settings shows the new one)
	public static event Action? Changed;

	// any theme applied: what a converter colored from the old one is colored again (the state colors, the message's color)
	public static event Action? Applied;

	// the template's colour names -> the window's resource keys (a brush, and the same name + "Color")
	static readonly Dictionary<string, string> ColorKeys = new(StringComparer.Ordinal) {
		["Bg"] = "Bg", ["Panel"] = "Panel", ["Panel2"] = "Panel2", ["Line"] = "Line", ["Accent"] = "Accent", ["AccentLight"] = "AccentLight",
		["AccentDim"] = "AccentDim", ["Text"] = "Text", ["Muted"] = "Muted", ["Good"] = "Good", ["Warn"] = "Warn", ["Bad"] = "Bad",
		["OnAccent"] = "OnAccent", ["HoverFill"] = "HoverFill", ["BannerBad"] = "BannerBad", ["GuidedFill"] = "GuidedFill", ["Shade"] = "Shade",
	};

	static readonly Dictionary<string, string> FontKeys = new(StringComparer.Ordinal) { ["Ui"] = "UiFont", ["Heading"] = "HeadFont", ["Mono"] = "MonoFont" };

	static readonly Dictionary<string, string> CornerKeys = new(StringComparer.Ordinal) {
		["CardCorner"] = "CardCorner", ["ButtonCorner"] = "ButtonCorner", ["CheckBoxCorner"] = "CheckBoxCorner", ["GuidedBoxCorner"] = "GuidedBoxCorner",
	};

	static readonly Dictionary<string, string> AccentKeys = new(StringComparer.Ordinal) {
		["Light3"] = "SystemAccentColorLight3", ["Light2"] = "SystemAccentColorLight2", ["Light1"] = "SystemAccentColorLight1", ["Base"] = "SystemAccentColor",
		["Dark1"] = "SystemAccentColorDark1", ["Dark2"] = "SystemAccentColorDark2", ["Dark3"] = "SystemAccentColorDark3",
	};

	static readonly Dictionary<string, string> DefaultGlyphs = new(StringComparer.Ordinal) {
		["Running"] = "▶", ["Done"] = "✔", ["Failed"] = "✖", ["Stopped"] = "■", ["Skipped"] = "–", ["IssueBlocks"] = "✖", ["IssueNote"] = "•",
	};

	static Dictionary<string, string> s_glyphs = new(DefaultGlyphs);
	static Dictionary<object, object>? s_defaults;

	public static string Current { get; private set; } = Default;

	public static string Glyph(string a_key) => s_glyphs.TryGetValue(a_key, out var g) ? g : DefaultGlyphs.GetValueOrDefault(a_key, "");

	// the built-in template, shipped inside the program (a copy is put in the Themes folder for a designer to start from)
	public static string TemplateText()
	{
		using var s = typeof(Theme).Assembly.GetManifestResourceStream("ThemeTemplate.json") ?? throw new InvalidDataException("the theme template is missing");
		using var r = new StreamReader(s);
		return r.ReadToEnd();
	}

	public static void EnsureFolder()
	{
		Directory.CreateDirectory(Folder);
		var t = Path.Combine(Folder, "Theme template.json");
		if (!File.Exists(t)) {
			Files.WriteAtomic(t, TemplateText());
		}
	}

	// the themes on offer: the built-in one first, then every theme file in the folder (by its own name)
	public static List<(string Name, string? File)> List()
	{
		var outList = new List<(string, string?)> { (Default, null) };
		outList.AddRange(BuiltIns.Select(b => (b, (string?)null)));
		if (!Directory.Exists(Folder)) {
			return outList;
		}
		foreach (var f in Directory.EnumerateFiles(Folder, "*.json").Order(StringComparer.OrdinalIgnoreCase)) {
			if (Path.GetFileName(f).Equals("Theme template.json", StringComparison.OrdinalIgnoreCase)) {
				continue;
			}
			try {
				var o = JsonNode.Parse(Files.ReadSharedText(f)) as JsonObject;
				var name = (o?["name"] as JsonObject)?["value"]?.ToString() ?? o?["name"]?.ToString();
				// a theme named like a built-in look (by its name or its file name) gets "(file)" added, so both can be picked
				var shown = string.IsNullOrWhiteSpace(name) || name == Default || BuiltIns.Contains(name) ? Path.GetFileNameWithoutExtension(f) : name;
				outList.Add((shown == Default || BuiltIns.Contains(shown) ? shown + " (file)" : shown, f));
			} catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
				outList.Add((Path.GetFileNameWithoutExtension(f) + " (does not read)", f));
			}
		}
		return outList;
	}

	// at start: the theme picked last time (gone or broken = the built-in one). It runs before the window is made, so nothing a
	// theme does may escape here - one that did left the program with no window, every start (the fourth review, H2)
	public static void ApplySaved()
	{
		try {
			if (File.Exists(TextSizeFile) && double.TryParse(File.ReadAllText(TextSizeFile).Trim(), System.Globalization.NumberStyles.Float, InvariantCulture, out var scale) &&
			    TextScales.Contains(scale)) {
				TextScale = scale;
			}
			// no theme picked yet: Windows' high contrast, when it is on, is followed (and keeps being followed while unpicked)
			s_followHighContrast = !File.Exists(ChoiceFile);
			FollowHighContrast();
			var name = File.Exists(ChoiceFile) ? File.ReadAllText(ChoiceFile).Trim() : SystemParameters.HighContrast ? HighContrast : Default;
			Apply(name, false);
		} catch (Exception e) when (e is not OutOfMemoryException) {
			Reset();
			// the built-in look, at the text size that was read (Settings shows it, and a later pick works from these sizes)
			TakeSizes();
			ApplyScale();
		}
	}

	// the theme's own icon, for the window made after the theme was applied (at start the window does not exist yet)
	public static ImageSource? Icon { get; private set; }

	// the built-in look: every resource as the program started with it, a theme's extra keys removed
	static void Reset()
	{
		var res = Application.Current.Resources;
		s_defaults ??= res.Keys.Cast<object>().ToDictionary(k => k, k => res[k]);
		foreach (var k in res.Keys.Cast<object>().Where(k => !s_defaults.ContainsKey(k)).ToList()) {
			res.Remove(k);
		}
		foreach (var (k, v) in s_defaults) {
			res[k] = v;
		}
		s_glyphs = new(DefaultGlyphs);
		Icon = null;
		Application.Current.MainWindow?.ClearValue(Window.IconProperty);
		SetMode("Light");
	}

	// ---------------------------------------------------------------- the gallery: a theme's own colors, read without applying it
	public sealed record Look(string Name, string? File, string About, bool Dark, Color Bg, Color Panel, Color Panel2, Color Line, Color Accent, Color AccentLight,
		Color Text, Color Muted, Color Good, Color Warn, Color Bad, Color OnAccent, Color BarFrom, Color BarTo, double Corner, string? Problem);

	static Color ColorOr(JsonObject? a_colors, string a_key, Color a_else)
	{
		try {
			return Value(a_colors?[a_key]) is { Length: > 0 } text ? (Color)ColorConverter.ConvertFromString(text.Trim()) : a_else;
		} catch (FormatException) {
			return a_else;
		}
	}

	// what a theme looks like, for its card in the gallery; a key it leaves out shows the default's value, as applying it would
	public static Look LookOf(string a_name, string? a_file)
	{
		var basis = JsonNode.Parse(ShippedText(Default)) as JsonObject ?? [];
		JsonObject? own = null;
		string? problem = null;
		try {
			own = a_file != null ? JsonNode.Parse(Files.ReadSharedText(a_file)) as JsonObject : a_name == Default ? basis : BuiltIn(a_name);
		} catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidDataException) {
			problem = "does not read: " + e.Message;
		}
		Color Pick(string a_key)
		{
			var fallback = ColorOr(basis["colors"] as JsonObject, a_key, Colors.Gray);
			return ColorOr(own?["colors"] as JsonObject, a_key, fallback);
		}
		(Color From, Color To) Bar(JsonObject? a_t)
		{
			if (((a_t?["gradients"] as JsonObject)?["PrimaryAndProgress"] as JsonObject)?["stops"] is JsonArray { Count: >= 2 } stops) {
				try {
					return ((Color)ColorConverter.ConvertFromString(stops[0]!["color"]!.ToString()), (Color)ColorConverter.ConvertFromString(stops[^1]!["color"]!.ToString()));
				} catch (Exception e) when (e is FormatException or NullReferenceException) {
				}
			}
			return (Pick("Accent"), Pick("Accent"));
		}
		var bar = Bar(own is { } o && o["gradients"] != null ? o : own == null || a_name is HighContrast or ColorBlind ? basis : null);
		var mode = Value(own?["windowsMode"]) ?? (a_name == ColorBlind || own == null ? "Light" : Value(basis["windowsMode"]) ?? "Light");
		var corner = ((own?["shape"] ?? basis["shape"]) as JsonObject)?["CardCorner"] is JsonValue c && c.TryGetValue<double>(out var r) ? Math.Clamp(r, 0, 30) : 8;
		return new(a_name, a_file, Value(own?["description"]) ?? (a_name switch {
			HighContrast => "Windows' own high-contrast colors.", ColorBlind => "Finished, warning and failed kept apart for color-blind eyes.", _ => "" }),
			mode == "Dark", Pick("Bg"), Pick("Panel"), Pick("Panel2"), Pick("Line"), Pick("Accent"), Pick("AccentLight"), Pick("Text"), Pick("Muted"), Pick("Good"),
			Pick("Warn"), Pick("Bad"), Pick("OnAccent"), bar.From, bar.To, corner, problem);
	}

	// a new theme started from an existing one (the gallery's "New theme from this one"): the template with that theme's values, in
	// the Themes folder under a free name. Returns the new file
	public static string CopyAs(string a_name, string? a_file)
	{
		EnsureFolder();
		var doc = JsonNode.Parse(TemplateText()) as JsonObject ?? [];
		var from = a_file != null ? JsonNode.Parse(Files.ReadSharedText(a_file)) as JsonObject ?? [] : a_name == Default ? [] : BuiltIn(a_name);
		// every value the source sets goes over the template's, group by group, so the copy keeps each "where" line
		foreach (var (key, node) in from) {
			if (node is JsonObject group && doc[key] is JsonObject target && key is "colors" or "fonts" or "sizes" or "shape" or "glyphs" or "windowsAccent") {
				foreach (var (k, v) in group) {
					if (target[k] is JsonObject slot && v is JsonObject given && given["value"] != null) {
						slot["value"] = given["value"]!.DeepClone();
					} else if (v != null) {
						target[k] = v.DeepClone();
					}
				}
			} else if (key is "gradients" && node != null) {
				doc[key] = node.DeepClone();
			} else if (key is "windowsMode" or "description" or "icon" && Value(node) is { } text && doc[key] is JsonObject one) {
				one["value"] = text;
			}
		}
		var title = a_name.Replace(" (file)", "", StringComparison.Ordinal) + " copy";
		var path = Path.Combine(Folder, title + ".json");
		for (var n = 2; File.Exists(path); ++n) {
			title = $"{a_name.Replace(" (file)", "", StringComparison.Ordinal)} copy {n}";
			path = Path.Combine(Folder, title + ".json");
		}
		if (doc["name"] is JsonObject nameSlot) {
			nameSlot["value"] = title;
		}
		if (doc["author"] is JsonObject author) {
			author["value"] = "";
		}
		Files.WriteAtomic(path, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
		return path;
	}

	// a theme file read and parsed (no window work - Settings does this off the window's thread)
	sealed record Read(JsonObject? Json, string? Dir, string? Problem);

	static Read ReadTheme(string a_name)
	{
		if (BuiltIns.Contains(a_name)) {
			return new(BuiltIn(a_name), Folder, null);
		}
		var file = List().FirstOrDefault(t => t.Name == a_name).File;
		if (file == null) {
			return new(null, null, $"no theme called \"{a_name}\" in the Themes folder - the built-in one is used");
		}
		try {
			return new(JsonNode.Parse(Files.ReadSharedText(file)) as JsonObject ?? [], Path.GetDirectoryName(file)!, null);
		} catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
			return new(null, null, $"{Path.GetFileName(file)} does not read ({e.Message}) - the built-in theme is used");
		}
	}

	// the Settings picker: the folder and the file read off the window's thread, the look applied on it
	public static async Task<List<string>> ApplyAsync(string a_name)
	{
		var read = a_name == Default ? null : await Task.Run(() => ReadTheme(a_name));
		return Apply(a_name, read, true);
	}

	// applies the theme by its name; returns what did not read (empty = all of it). a_keep: remembered for the next start
	public static List<string> Apply(string a_name, bool a_keep = true) => Apply(a_name, a_name == Default ? null : ReadTheme(a_name), a_keep);

	static List<string> Apply(string a_name, Read? a_read, bool a_keep)
	{
		Reset();
		var problems = new List<string>();
		if (a_name != Default) {
			if (a_read?.Json == null) {
				problems.Add(a_read?.Problem ?? $"no theme called \"{a_name}\" - the built-in one is used");
				a_name = Default;
			} else {
				try {
					ApplyFile(a_read.Json, a_read.Dir!, problems);
				} catch (InvalidOperationException e) {
					Reset();
					problems.Add($"\"{a_name}\" does not read ({e.Message}) - the built-in theme is used");
					a_name = Default;
				}
			}
		}
		Current = a_name;
		WindowFrame.PaintAll();
		TakeSizes();
		ApplyScale();
		Applied?.Invoke();
		if (a_keep) {
			// a theme the player picked: Windows' high contrast is no longer followed
			s_followHighContrast = false;
			try {
				Files.WriteAtomic(ChoiceFile, a_name);
			} catch (IOException) {
			}
		}
		return problems;
	}

	static string? Value(JsonNode? a_n) => a_n is JsonObject o ? o["value"]?.ToString() : a_n is JsonValue ? a_n.ToString() : null;

	static void ApplyFile(JsonObject a_t, string a_dir, List<string> a_problems)
	{
		var res = Application.Current.Resources;
		Color? ReadColor(string a_where, string? a_text)
		{
			if (string.IsNullOrWhiteSpace(a_text)) {
				return null;
			}
			try {
				return (Color)ColorConverter.ConvertFromString(a_text.Trim());
			} catch (FormatException) {
				a_problems.Add($"{a_where}: \"{a_text}\" is not a colour (#RRGGBB or #AARRGGBB)");
				return null;
			}
		}
		if (Value(a_t["windowsMode"]) is { } mode) {
			if (mode is "Dark" or "Light") {
				SetMode(mode);
			} else {
				a_problems.Add($"windowsMode: \"{mode}\" is not Dark or Light");
			}
		}
		foreach (var (name, key) in ColorKeys) {
			if (ReadColor("colors." + name, Value((a_t["colors"] as JsonObject)?[name])) is { } c) {
				res[key + "Color"] = c;
				var b = new SolidColorBrush(c);
				b.Freeze();
				res[key] = b;
			}
		}
		if (((a_t["gradients"] as JsonObject)?["PrimaryAndProgress"] as JsonObject)?["stops"] is JsonArray stops && stops.Count >= 2) {
			var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
			foreach (var s in stops.OfType<JsonObject>()) {
				if (ReadColor("gradients.PrimaryAndProgress", s["color"]?.ToString()) is { } c && s["at"] is JsonValue at && at.TryGetValue<double>(out var o)) {
					g.GradientStops.Add(new GradientStop(c, Math.Clamp(o, 0, 1)));
				}
			}
			if (g.GradientStops.Count >= 2) {
				g.Freeze();
				res["AccentBar"] = g;
			}
		}
		if (a_t["windowsAccent"] is JsonObject acc) {
			foreach (var (name, key) in AccentKeys) {
				if (ReadColor("windowsAccent." + name, acc[name]?.ToString()) is { } c) {
					res[key] = c;
				}
			}
		}
		if (a_t["fonts"] is JsonObject fonts) {
			foreach (var (name, key) in FontKeys) {
				if (Value(fonts[name]) is { Length: > 0 } f) {
					res[key] = new FontFamily(f);
				}
			}
		}
		if (a_t["sizes"] is JsonObject sizes) {
			foreach (var (name, n) in sizes) {
				if (n is JsonValue v && v.TryGetValue<double>(out var d)) {
					if (d is >= 6 and <= 72) {
						res["Size" + name] = d;
					} else {
						a_problems.Add($"sizes.{name}: {d} is outside 6 - 72");
					}
				}
			}
		}
		if (a_t["shape"] is JsonObject shape) {
			foreach (var (name, n) in shape) {
				if (n is not JsonValue v || !v.TryGetValue<double>(out var d)) {
					continue;
				}
				if (CornerKeys.TryGetValue(name, out var key) && d is >= 0 and <= 30) {
					res[key] = new CornerRadius(d);
				} else if (name == "ProgressBarHeight" && d is >= 2 and <= 30) {
					res["ProgressBarHeight"] = d;
				} else if (CornerKeys.ContainsKey(name) || name == "ProgressBarHeight") {
					a_problems.Add($"shape.{name}: {d} is out of range");
				}
			}
		}
		if (a_t["highContrastPairs"] is JsonValue hc && hc.TryGetValue<bool>(out var pairs) && pairs) {
			// a selected or hovered row and the attention banner: Windows' highlight behind its highlight text, as Windows draws them
			var hb = new SolidColorBrush(SystemColors.HighlightColor);
			var ht = new SolidColorBrush(SystemColors.HighlightTextColor);
			hb.Freeze();
			ht.Freeze();
			foreach (var k in new[] { "ListBoxItemSelectedBackgroundThemeBrush", "ListBoxItemSelectedBackgroundPointerOverThemeBrush", "ListBoxItemSelectedBackgroundPressedThemeBrush",
				         "ListBoxItemBackgroundSelectedPressedThemeBrush" }) {
				res[k] = hb;
			}
			res["ListBoxItemSelectedForegroundThemeBrush"] = ht;
		}
		if (a_t["glyphs"] is JsonObject glyphs) {
			foreach (var key in DefaultGlyphs.Keys) {
				if (glyphs[key]?.ToString() is { Length: > 0 and <= 4 } g) {
					s_glyphs[key] = g;
				}
			}
		}
		if (Value(a_t["icon"]) is { Length: > 0 } icon) {
			// only a file beside the theme: a full path, a share or ".." would read (or reach) outside its folder
			var dir = Path.GetFullPath(a_dir).TrimEnd('\\') + "\\";
			var path = Path.GetFullPath(Path.Combine(dir, icon));
			if (Path.IsPathRooted(icon) || !path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) {
				a_problems.Add($"icon: \"{icon}\" must be a file beside the theme");
			} else {
				try {
					var img = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
					img.Freeze();
					res["Logo"] = img;
					Icon = img;
					if (Application.Current.MainWindow != null) {
						Application.Current.MainWindow.Icon = img;
					}
				} catch (Exception e) when (e is IOException or FormatException or NotSupportedException or ArgumentException or
				                            InvalidOperationException or System.Runtime.InteropServices.ExternalException) {
					// a damaged picture throws FileFormatException (a FormatException) or a COM error from Windows' decoder - it is only
					// an icon, so it never stops the theme (the fourth review, H2)
					a_problems.Add($"icon: \"{icon}\" does not open ({e.Message})");
				}
			}
		}
	}

	// Dark or Light: the base Windows draws its own controls with under the theme in use
	public static string Mode { get; private set; } = "Light";

	static void SetMode(string a_mode)
	{
		if (Application.Current.ThemeMode.Value != a_mode) {
			Application.Current.ThemeMode = new ThemeMode(a_mode);
		}
		// the Fluent brushes built from the accent (Accent.xaml / AccentLight.xaml), the set made for this mode's background, LAST
		// among the merged dictionaries: WPF reads them from the last one back, so ours win over the Fluent theme's own (which
		// takes Windows' accent - blue on most PCs)
		var merged = Application.Current.Resources.MergedDictionaries;
		var want = a_mode == "Light" ? "AccentLight.xaml" : "Accent.xaml";
		Mode = a_mode;
		var ours = merged.Where(IsAccent).ToList();
		if (ours.Count == 1 && merged[^1] == ours[0] && AccentFile(ours[0]) == want) {
			return;
		}
		foreach (var d in ours) {
			merged.Remove(d);
		}
		merged.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/{Brand.Name};component/{want}", UriKind.Absolute) });
	}

	static string AccentFile(ResourceDictionary a_d) => a_d.Source is { } s ? s.OriginalString[(s.OriginalString.LastIndexOf('/') + 1)..] : "";

	static bool IsAccent(ResourceDictionary a_d) => AccentFile(a_d) is "Accent.xaml" or "AccentLight.xaml";

	// the accent set in use (the offline test reads it): the file name of the last merged dictionary that is one of ours
	public static string AccentInUse => Application.Current.Resources.MergedDictionaries.LastOrDefault(IsAccent) is { } d ? AccentFile(d) : "";
}

// Windows draws every window's title bar and border itself: each one is painted in the theme's colors (Windows 11 takes them;
// an older Windows keeps its own frame), when a window opens and again whenever the theme changes
public static partial class WindowFrame
{
	[System.Runtime.InteropServices.LibraryImport("dwmapi.dll")]
	private static partial int DwmSetWindowAttribute(nint a_hwnd, int a_attr, ref int a_value, int a_size);

	const int DarkTitle = 20;
	const int BorderColor = 34;
	const int CaptionColor = 35;
	const int TitleTextColor = 36;

	public static void PaintEveryWindowOnOpen() =>
		EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((a_s, _) => Paint((Window)a_s)));

	public static void PaintAll()
	{
		if (Application.Current == null) {
			return;
		}
		foreach (Window w in Application.Current.Windows) {
			Paint(w);
		}
	}

	public static void Paint(Window a_w)
	{
		var hwnd = new System.Windows.Interop.WindowInteropHelper(a_w).Handle;
		if (hwnd == 0) {
			return;
		}
		var back = a_w.Background as SolidColorBrush ?? Brushes2.Get("Bg") as SolidColorBrush;
		Set(hwnd, DarkTitle, Application.Current.ThemeMode.Value == "Light" ? 0 : 1);
		if (back != null) {
			Set(hwnd, CaptionColor, Ref(back.Color));
		}
		if (Brushes2.Get("Line") is SolidColorBrush line) {
			Set(hwnd, BorderColor, Ref(line.Color));
		}
		if (Brushes2.Get("Text") is SolidColorBrush text) {
			Set(hwnd, TitleTextColor, Ref(text.Color));
		}
	}

	// a Windows color is 0x00BBGGRR
	public static int Ref(Color a_c) => a_c.R | (a_c.G << 8) | (a_c.B << 16);

	static void Set(nint a_hwnd, int a_attr, int a_value) => _ = DwmSetWindowAttribute(a_hwnd, a_attr, ref a_value, sizeof(int));
}
