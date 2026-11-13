using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ToolRunner.App;

// The theme gallery (his order 2026-11-10: "add more themes and make them easily changeable"): every theme as a card that shows
// its own colors, one click to use it - at once, no restart - and a way to start a new theme from the one in use. A card is drawn
// from the theme's file without applying it; everything around the cards takes the window's colors by reference, so the gallery
// itself follows the theme it just applied.
public static class ThemeGallery
{
	// three cards side by side in the drawer
	const double CardWidth = 162;

	static SolidColorBrush Fill(Color a_c)
	{
		var b = new SolidColorBrush(a_c);
		b.Freeze();
		return b;
	}

	// a small picture of the window in the theme's own colors: the header, a card with two lines of text, the primary button,
	// the three state colors and the progress bar
	static Border Picture(Theme.Look a_l)
	{
		var corner = Math.Min(a_l.Corner, 10);
		var bar = new LinearGradientBrush(a_l.BarFrom, a_l.BarTo, 0);
		bar.Freeze();
		var canvas = new Canvas { Width = 184, Height = 108, ClipToBounds = true };
		void Add(double a_x, double a_y, double a_w, double a_h, Brush a_fill, double a_radius = 0, Brush? a_edge = null)
		{
			var r = new Rectangle { Width = a_w, Height = a_h, Fill = a_fill, RadiusX = a_radius, RadiusY = a_radius, Stroke = a_edge, StrokeThickness = a_edge == null ? 0 : 1 };
			Canvas.SetLeft(r, a_x);
			Canvas.SetTop(r, a_y);
			canvas.Children.Add(r);
		}
		void Dot(double a_x, double a_y, double a_d, Color a_c)
		{
			var e = new Ellipse { Width = a_d, Height = a_d, Fill = Fill(a_c) };
			Canvas.SetLeft(e, a_x);
			Canvas.SetTop(e, a_y);
			canvas.Children.Add(e);
		}
		var line = Fill(a_l.Line);
		Add(0, 0, 184, 108, Fill(a_l.Bg));
		Add(0, 0, 184, 20, Fill(a_l.Panel));
		Add(0, 20, 184, 1, line);
		Dot(8, 6, 8, a_l.Accent);
		Add(22, 7, 46, 6, Fill(a_l.AccentLight), 3);
		Add(10, 30, 60, 68, Fill(a_l.Panel), corner * 0.6, line);
		foreach (var (y, color) in new[] { (38.0, a_l.Good), (52.0, a_l.Good), (66.0, a_l.Warn), (80.0, a_l.Muted) }) {
			Add(17, y + 1, 30, 4, Fill(a_l.Text), 2);
			Dot(56, y - 1, 7, color);
		}
		Add(78, 30, 96, 68, Fill(a_l.Panel), corner * 0.6, line);
		Add(86, 39, 54, 7, Fill(a_l.AccentLight), 3);
		Add(86, 52, 78, 4, Fill(a_l.Muted), 2);
		Add(86, 62, 80, 4, Fill(a_l.Panel2), 2);
		Add(86, 62, 46, 4, bar, 2);
		Add(86, 76, 36, 14, bar, Math.Min(corner, 7));
		Add(94, 81, 20, 4, Fill(a_l.OnAccent), 2);
		Add(128, 76, 30, 14, Fill(a_l.Panel2), Math.Min(corner, 7), Fill(a_l.Accent));
		Dot(162, 80, 7, a_l.Bad);
		// a border does not clip to its round corners: the picture is cut to them itself, then scaled to the card
		var clip = new RectangleGeometry(new Rect(0, 0, 184, 108), 9, 9);
		clip.Freeze();
		canvas.Clip = clip;
		var edge = new Rectangle { Width = 184, Height = 108, RadiusX = 9, RadiusY = 9, Stroke = line, StrokeThickness = 1 };
		canvas.Children.Add(edge);
		return new Border { Child = new Viewbox { Width = CardWidth, Child = canvas }, SnapsToDevicePixels = true };
	}

	public static FrameworkElement Build(MainWindow a_w, MainViewModel a_vm)
	{
		var note = new TextBlock { Text = "Pick a look: it is used at once and kept for the next start.", Style = (Style)a_w.FindResource("MutedText"),
			TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
		var cards = new WrapPanel { Margin = new Thickness(-6, 0, 0, 0) };
		var marks = new Dictionary<string, (Border Frame, TextBlock InUse)>(StringComparer.Ordinal);
		void MarkCurrent()
		{
			foreach (var (name, (frame, inUse)) in marks) {
				var current = name == Theme.Current;
				frame.SetResourceReference(Border.BorderBrushProperty, current ? "Accent" : "Line");
				frame.BorderThickness = new Thickness(current ? 2 : 1);
				frame.Padding = new Thickness(current ? 9 : 10);
				inUse.Visibility = current ? Visibility.Visible : Visibility.Collapsed;
			}
		}
		async void Use(string a_name)
		{
			if (a_name == Theme.Current) {
				return;
			}
			var problems = await Theme.ApplyAsync(a_name);
			note.Text = problems.Count == 0 ? $"\"{Theme.Current}\" is in use." : "In use, with these values left as the default: " + string.Join("; ", problems.Take(6));
			MarkCurrent();
			A11y.Say(a_w, note.Text);
		}
		Button Card(Theme.Look a_l)
		{
			var name = new TextBlock { Text = a_l.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 8, 0, 0) };
			name.SetResourceReference(TextBlock.ForegroundProperty, "Text");
			var kind = new TextBlock { Text = a_l.Problem ?? (a_l.Dark ? "Dark" : "Light") + (a_l.File == null ? "" : "  ·  your file"), Style = (Style)a_w.FindResource("MutedText"),
				TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
			var inUse = new TextBlock { Text = "In use", FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed };
			inUse.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
			var foot = new Grid();
			foot.Children.Add(kind);
			foot.Children.Add(inUse);
			var stack = new StackPanel { Width = CardWidth };
			stack.Children.Add(Picture(a_l));
			stack.Children.Add(name);
			stack.Children.Add(foot);
			var frame = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10), Child = stack, BorderThickness = new Thickness(1) };
			frame.SetResourceReference(Border.BackgroundProperty, "Panel2");
			frame.SetResourceReference(Border.BorderBrushProperty, "Line");
			marks[a_l.Name] = (frame, inUse);
			// the card is its own picture: a plain button (keyboard, screen reader) with no chrome of its own
			var b = new Button { Content = frame, Margin = new Thickness(6), Padding = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = a_l.About == "" ? null : a_l.About,
				Background = Brushes.Transparent, BorderThickness = new Thickness(0),
				Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
					"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><ContentPresenter/></ControlTemplate>") };
			System.Windows.Automation.AutomationProperties.SetName(b, $"{a_l.Name} theme, {(a_l.Dark ? "dark" : "light")}");
			System.Windows.Automation.AutomationProperties.SetHelpText(b, a_l.About);
			b.GotKeyboardFocus += (_, _) => frame.SetResourceReference(Border.BorderBrushProperty, "AccentLight");
			b.LostKeyboardFocus += (_, _) => MarkCurrent();
			b.Click += (_, _) => Use(a_l.Name);
			return b;
		}
		// the themes are read off the window's thread (a slow or huge theme file never freezes it); the cards are made on it
		async void Fill2()
		{
			var (looks, error) = await Task.Run(() => {
				string? err = null;
				try {
					Theme.EnsureFolder();
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) {
					err = "The Themes folder could not be made: " + e.Message;
				}
				List<(string Name, string? File)> list;
				try {
					list = Theme.List();
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) {
					// an unreadable folder still shows every built-in look, with why
					list = [(Theme.Default, null), .. Theme.BuiltInNames.Select(b => (b, (string?)null))];
					err = "The Themes folder could not be read: " + e.Message;
				}
				return (list.Select(t => Theme.LookOf(t.Name, t.File)).ToList(), err);
			});
			cards.Children.Clear();
			marks.Clear();
			foreach (var l in looks) {
				cards.Children.Add(Card(l));
			}
			if (error != null) {
				note.Text = error;
			}
			MarkCurrent();
		}
		Fill2();
		// a look changed by Windows (high contrast followed) shows here while the gallery is open
		void OnChanged() => cards.Dispatcher.BeginInvoke(MarkCurrent);
		Theme.Changed += OnChanged;
		cards.Unloaded += (_, _) => Theme.Changed -= OnChanged;

		var copy = new Button { Content = "New theme from the one in use ...", Style = (Style)a_w.FindResource("Ghost"), Margin = new Thickness(0, 0, 8, 8),
			ToolTip = "Makes a copy of the look in use as a theme file of your own, ready to change: every color, font, size and corner, each with where it shows" };
		copy.Click += async (_, _) => {
			copy.IsEnabled = false;
			try {
				var current = Theme.Current;
				var file = await Task.Run(() => Theme.CopyAs(current, Theme.List().FirstOrDefault(t => t.Name == current).File));
				a_vm.Say($"Made: {System.IO.Path.GetFileName(file)}. Change its values in any text editor, then press Look again here.", "good");
				System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
				Fill2();
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception) {
				a_vm.Say("The new theme could not be made: " + e.Message, "bad");
			} finally {
				copy.IsEnabled = true;
			}
		};
		var open = new Button { Content = "Open the Themes folder", Style = (Style)a_w.FindResource("Ghost"), Margin = new Thickness(0, 0, 8, 8),
			ToolTip = "A theme is one file: drop one here (yours or someone else's) and press Look again" };
		open.Click += (_, _) => {
			try {
				Theme.EnsureFolder();
				System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Theme.Folder) { UseShellExecute = true });
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception) {
				a_vm.Say("The Themes folder could not be opened: " + e.Message, "bad");
			}
		};
		var again = new Button { Content = "Look again", Style = (Style)a_w.FindResource("Ghost"), Margin = new Thickness(0, 0, 8, 8), ToolTip = "Reads the Themes folder again (a theme just added or changed)" };
		again.Click += (_, _) => Fill2();
		var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
		buttons.Children.Add(copy);
		buttons.Children.Add(open);
		buttons.Children.Add(again);

		// his checklist 473: bigger text for every theme, kept for the next start
		var sizeLabel = new TextBlock { Text = "TEXT SIZE", Style = (Style)a_w.FindResource("H2"), Margin = new Thickness(0, 14, 0, 6) };
		var size = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = Theme.TextScales.Select(s => $"{s * 100:0} %").ToList(),
			SelectedIndex = Math.Max(0, Array.IndexOf(Theme.TextScales, Theme.TextScale)) };
		System.Windows.Automation.AutomationProperties.SetName(size, "Text size");
		size.SelectionChanged += (_, _) => {
			if (size.SelectedIndex >= 0) {
				Theme.SetTextScale(Theme.TextScales[size.SelectedIndex]);
			}
		};
		var about = new TextBlock { Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
			Text = "\"High contrast\" takes Windows' own high-contrast colors, and is used by itself while Windows has high contrast on and no look was picked here. " +
			       "\"Color-blind safe\" keeps finished, warning and failed apart by more than red and green. Every state also has its own mark." };

		var body = new StackPanel();
		body.Children.Add(note);
		body.Children.Add(cards);
		body.Children.Add(buttons);
		body.Children.Add(sizeLabel);
		body.Children.Add(size);
		body.Children.Add(about);
		return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = body, Focusable = false };
	}
}
