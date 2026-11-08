using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace ToolRunner.App;

// His checklist 476: the window built ready for translators, English first. A language is one file, Languages\<its name>.json in the data folder:
// { "the English words": "their translation" }. Whatever a file leaves out stays English, and text a binding fills (what a run is doing) is left
// alone: a translator can start small. Settings makes the file from what the window shows.
public static class WindowWords
{
	public const string English = "English";

	static Dictionary<string, string> s_words = new(StringComparer.Ordinal);
	// back from a translation to its English, so a file made with a language in use still has English keys
	static Dictionary<string, string> s_english = new(StringComparer.Ordinal);

	public static string Folder => Path.Combine(AppPaths.Data, "Languages");
	static string PickedFile => Path.Combine(AppPaths.Data, "language.txt");

	public static string Picked()
	{
		try {
			return File.Exists(PickedFile) ? File.ReadAllText(PickedFile).Trim() : English;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return English;
		}
	}

	public static List<string> Available()
	{
		var names = new List<string> { English };
		if (Directory.Exists(Folder)) {
			names.AddRange(Directory.GetFiles(Folder, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)!).Order(StringComparer.OrdinalIgnoreCase));
		}
		return names;
	}

	public static void Pick(string a_name)
	{
		Directory.CreateDirectory(AppPaths.Data);
		Files.WriteAtomic(PickedFile, a_name);
	}

	public static void Use(IReadOnlyDictionary<string, string> a_words)
	{
		s_words = a_words.Where(p => !string.IsNullOrWhiteSpace(p.Value)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
		s_english = new(StringComparer.Ordinal);
		foreach (var (english, word) in s_words) {
			s_english.TryAdd(word, english);
		}
	}

	// the words of a language file; null, with why, when it does not read
	public static Dictionary<string, string>? Read(string a_text, out string a_problem)
	{
		a_problem = "";
		try {
			return JsonSerializer.Deserialize<Dictionary<string, string>>(a_text, new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip }) ?? [];
		} catch (JsonException e) {
			a_problem = e.Message;
			return null;
		}
	}

	// at the start, before the window is drawn; returns what went wrong with the picked file ("" = nothing): the window stays in English then
	public static string LoadPicked()
	{
		var name = Picked();
		if (name == English) {
			return "";
		}
		// a name only: this file is read at every start, and what it names must stay in the languages folder
		if (!GoodName(name)) {
			return $"the language {name} is no language's name: the window stays in English";
		}
		var file = Path.Combine(Folder, name + ".json");
		try {
			if (!File.Exists(file)) {
				return $"the language {name} was not found: the window stays in English";
			}
			if (Read(Files.ReadSharedText(file), out var problem) is not { } words) {
				return $"the language file {name} does not read ({problem}): the window stays in English";
			}
			Use(words);
			return "";
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return $"the language file {name} could not be read ({e.Message}): the window stays in English";
		}
	}

	public static bool GoodName(string a_name) => a_name.Trim().Length > 0 && a_name == Path.GetFileName(a_name) && a_name is not ("." or "..");

	public static void OnEveryWindow() =>
		EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((a_s, _) => Tree((Window)a_s)));

	// every piece of text under a_root that no binding fills: each one seen is given to a_seen, and put in the language when one is in use
	static void Walk(DependencyObject a_root, Action<string>? a_seen)
	{
		// written only where a translation differs: a value set here would end a theme's resource, a style's value or a text's inlines
		void Do(string a_text, Action<string> a_set)
		{
			a_seen?.Invoke(s_english.GetValueOrDefault(a_text, a_text));
			if (s_words.TryGetValue(a_text, out var word) && word != a_text) {
				a_set(word);
			}
		}
		bool Free(DependencyProperty a_p) => !BindingOperations.IsDataBound(a_root, a_p);
		switch (a_root) {
			case TextBlock t when Free(TextBlock.TextProperty) && !string.IsNullOrWhiteSpace(t.Text):
				Do(t.Text, w => t.Text = w);
				break;
			case ContentControl c when Free(ContentControl.ContentProperty) && c.Content is string s && !string.IsNullOrWhiteSpace(s):
				Do(s, w => c.Content = w);
				break;
			case HeaderedItemsControl h when Free(HeaderedItemsControl.HeaderProperty) && h.Header is string hs && !string.IsNullOrWhiteSpace(hs):
				Do(hs, w => h.Header = w);
				break;
		}
		if (a_root is FrameworkElement fe) {
			if (Free(FrameworkElement.ToolTipProperty) && fe.ToolTip is string tip && !string.IsNullOrWhiteSpace(tip)) {
				Do(tip, w => fe.ToolTip = w);
			}
			if (Free(AutomationProperties.NameProperty) && AutomationProperties.GetName(fe) is { Length: > 0 } name) {
				Do(name, w => AutomationProperties.SetName(fe, w));
			}
			// a context menu is no logical child of what it opens on
			if (fe.ContextMenu is { } menu) {
				Walk(menu, a_seen);
			}
		}
		foreach (var child in LogicalTreeHelper.GetChildren(a_root).OfType<DependencyObject>().ToList()) {
			Walk(child, a_seen);
		}
	}

	public static void Tree(DependencyObject a_root)
	{
		if (s_words.Count > 0) {
			Walk(a_root, null);
		}
	}

	public static List<string> Seen(DependencyObject a_root)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		Walk(a_root, s => seen.Add(s));
		return [.. seen.Order(StringComparer.Ordinal)];
	}

	// a language file made from what the window shows: new words added with no translation, what a file already holds kept
	public static string WriteTemplate(DependencyObject a_root, string a_name = "My language")
	{
		Directory.CreateDirectory(Folder);
		var file = Path.Combine(Folder, a_name + ".json");
		var words = new SortedDictionary<string, string>(StringComparer.Ordinal);
		if (File.Exists(file)) {
			// a translator's work is never replaced by an empty file
			var had = Read(Files.ReadSharedText(file), out var problem) ?? throw new InvalidDataException($"{file} does not read ({problem}): fix it or move it first, so your work is kept");
			foreach (var (k, v) in had) {
				words[k] = v ?? "";
			}
		}
		foreach (var s in Seen(a_root)) {
			words.TryAdd(s, "");
		}
		// two English texts with one translation read back as one: the language in use still has both
		foreach (var k in s_words.Keys) {
			words.TryAdd(k, "");
		}
		Files.WriteAtomic(file, JsonSerializer.Serialize(words, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
		return file;
	}
}
