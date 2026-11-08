using System.Media;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ToolRunner.App;

// a WPF window ends with OnClosed, never Dispose: the notification-area icon it owns is released there (Tray.Dispose)
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "released in OnClosed, the window's own end")]
public partial class MainWindow : Window
{
	readonly MainViewModel _vm = new();
	readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
	string _panel = "";
	Tray? _tray;

	// his version scheme is two parts (1.0, 1.1)
	public static string AppVersion => typeof(App).Assembly.GetName().Version?.ToString(2) ?? "";

	public MainWindow()
	{
		InitializeComponent();
		FitTheScreen();
		DataContext = _vm;
		_vm.Alert += Notify;
		_vm.ConfirmRun = a_text => Ask.Show(this, "What this run will do", a_text, "Start the run", "Not now");
		_vm.LogChanged += OnLog;
		_timer.Tick += async (_, _) => {
			await _vm.PollAsync();
			// the notification-area icon (there once a run started or a notification was shown) says what runs
			if (_vm.IsRunning || _tray?.Shown == true) {
				_tray?.Show("Skyrim Utility Patcher - " + _vm.NowTitle);
			}
		};
		_timer.Start();
		Footer.Text = $"Ctrl+Enter start  ·  F5 check  ·  Ctrl+H history  ·  Ctrl+M MO2 changes  ·  Ctrl+, settings  ·  Alt+Up / Down move a step   ·   version {AppVersion}";
		Footer.ToolTip = "Data folder: " + AppPaths.Data;
		PreviewKeyDown += OnKeys;
		// a mod ticked in Mod Organizer while the window was in the background: each step's last run is worked out again
		Activated += async (_, _) => await _vm.LoadLastRunsIfStale();
		if (_vm.Setups.Count == 0) {
			_vm.Say("No setup yet: press New setup and pick your Mod Organizer and profile.", "warn");
		}
		// what changes on its own is said to a screen reader (his checklist 490) - once the window is up, so the first look at the
		// last run is not read out as news
		Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _vm.PropertyChanged += Announce);
		Theme.Applied += _vm.Repaint;
	}

	// which moments of the power countdown were already said (-1 = its start)
	readonly HashSet<int> _powerSaid = [];

	void Announce(object? a_s, System.ComponentModel.PropertyChangedEventArgs a_e)
	{
		switch (a_e.PropertyName) {
		case nameof(MainViewModel.Message):
			A11y.Say(this, _vm.Message, _vm.MessageKind == "bad");
			break;
		case nameof(MainViewModel.NowTitle) when _vm.IsRunning && _vm.Waiting == "":
			A11y.Say(this, "Now: " + _vm.NowTitle);
			break;
		case nameof(MainViewModel.Waiting) when _vm.Waiting != "":
			A11y.Say(this, "Your turn in the game: " + _vm.Waiting, true);
			break;
		case nameof(MainViewModel.Verdict) when _vm.Verdict != "":
			A11y.Say(this, _vm.NowTitle + ": " + _vm.Verdict, true);
			break;
		case nameof(MainViewModel.PowerText):
			// the countdown changes every second: it is said once when it starts and at 30 and 10 seconds, never each second (a
			// screen reader would be interrupted until the Cancel button is out of reach)
			var left = System.Text.RegularExpressions.Regex.Match(_vm.PowerText, @"in (\d+) s") is { Success: true } m ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : -1;
			if (_vm.PowerText == "") {
				_powerSaid.Clear();
			} else if (_powerSaid.Count == 0 || (left is 30 or 10 && _powerSaid.Add(left))) {
				_powerSaid.Add(-1);
				A11y.Say(this, _vm.PowerText, true);
			}
			break;
		case nameof(MainViewModel.Interrupted) when _vm.Interrupted != "":
			A11y.Say(this, _vm.Interrupted, true);
			break;
		}
	}

	// a small screen or a big Windows scaling (a 1280 x 720 laptop at 150 % leaves about 850 x 480): the window's limits and size
	// shrink to the screen's usable area, so it always fits and can be moved
	void FitTheScreen()
	{
		var area = SystemParameters.WorkArea;
		if (area.Width <= 0 || area.Height <= 0) {
			return;
		}
		MinWidth = Math.Min(MinWidth, area.Width);
		MinHeight = Math.Min(MinHeight, area.Height);
		Width = Math.Min(Width, area.Width);
		Height = Math.Min(Height, area.Height);
	}

	void OnKeys(object a_s, KeyEventArgs a_e)
	{
		var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
		if (a_e.Key == Key.Escape && Drawer.Visibility == Visibility.Visible) {
			CloseDrawer();
			a_e.Handled = true;
		} else if (ctrl && a_e.Key == Key.H) {
			ShowPanel("history");
			a_e.Handled = true;
		} else if (ctrl && a_e.Key == Key.M) {
			ShowPanel("changes");
			a_e.Handled = true;
		} else if (ctrl && a_e.Key == Key.OemComma) {
			ShowPanel("settings");
			a_e.Handled = true;
		} else if (ctrl && a_e.Key == Key.F && _panel == "settings" && Drawer.Visibility == Visibility.Visible) {
			SettingsPanel.FocusFind();
			a_e.Handled = true;
		} else if (ctrl && a_e.Key == Key.N && Drawer.Visibility != Visibility.Visible) {
			NewSetup_Click(this, new RoutedEventArgs());
			a_e.Handled = true;
		}
	}

	// ---------------------------------------------------------------- alerts (his pick: notification + sound on, flash off by default)
	void Notify(string a_title, string a_text)
	{
		var s = _vm.Setup;
		var toast = s?.AlertNotification ?? true;
		var sound = s?.AlertSound ?? true;
		// Windows keeps 63 characters of a notification's title: the program's name goes only where the title still fits
		var title = (Brand.Name + " - " + a_title).Length <= 63 ? Brand.Name + " - " + a_title : a_title;
		var shown = toast && _tray != null && _tray.Notify(title, a_text.Length > 250 ? a_text[..250] + " ..." : a_text, sound);
		if (!shown && sound) {
			SystemSounds.Asterisk.Play();
		}
		if (s?.AlertFlash == true && !IsActive) {
			Native.Flash(new WindowInteropHelper(this).Handle, true);
		}
	}

	// ---------------------------------------------------------------- steps
	// the context menu and the buttons act on the selected step (a right-click selects the row under it)
	StepRow? RowOf(object a_sender) => a_sender is FrameworkElement { DataContext: StepRow r } ? r : _vm.SelectedStep;

	void StepOn_Click(object a_s, RoutedEventArgs a_e)
	{
		if (a_e.OriginalSource is CheckBox { DataContext: StepRow row } box) {
			if (_vm.IsRunning) {
				box.IsChecked = row.On = !(box.IsChecked == true);
				return;
			}
			_vm.SetOn(row, box.IsChecked == true);
		}
	}

	void RunOnly_Click(object a_s, RoutedEventArgs a_e) => StartFor(RowOf(a_s), "only");
	void RunFrom_Click(object a_s, RoutedEventArgs a_e) => StartFor(RowOf(a_s), "from");
	void RunUntil_Click(object a_s, RoutedEventArgs a_e) => StartFor(RowOf(a_s), "until");

	void StartFor(StepRow? a_row, string a_how)
	{
		if (a_row == null) {
			return;
		}
		if (!a_row.On) {
			_vm.Say($"{a_row.Name} is switched off in this setup - tick it first.", "bad");
			return;
		}
		if (a_row.LeftOut) {
			_vm.Say($"{a_row.Name} is left out for a new game - untick \"New game\" to run it.", "bad");
			return;
		}
		_ = _vm.StartWith(a_how == "only" ? a_row.Key : null, a_how == "from" ? a_row.Key : null, a_how == "until" ? a_row.Key : null);
	}

	void Up_Click(object a_s, RoutedEventArgs a_e) => Move(RowOf(a_s), -1);
	void Down_Click(object a_s, RoutedEventArgs a_e) => Move(RowOf(a_s), +1);

	void StepList_KeyDown(object a_s, KeyEventArgs a_e)
	{
		if (Keyboard.Modifiers == ModifierKeys.Alt && a_e.SystemKey is Key.Up or Key.Down) {
			Move(_vm.SelectedStep, a_e.SystemKey == Key.Up ? -1 : +1);
			a_e.Handled = true;
		} else if (a_e.Key == Key.Space && _vm.SelectedStep is { } row && !_vm.IsRunning) {
			row.On = !row.On;
			_vm.SetOn(row, row.On);
			a_e.Handled = true;
		}
	}

	// his rule: players may reorder, behind a warning window saying things could go wrong out of order
	void Move(StepRow? a_row, int a_delta)
	{
		if (a_row == null || _vm.IsRunning) {
			return;
		}
		var warn = _vm.WarningsIfMoved(a_row, a_delta);
		if (warn.Count > 0 && !Ask.Show(this, "Out of the recommended order",
			    "Moving this step breaks the order the tools' own instructions give. Things could go wrong if they run out of order:\n\n• " +
			    string.Join("\n• ", warn) + "\n\nMove it anyway?", "Move anyway", "Keep the order")) {
			return;
		}
		_vm.Move(a_row, a_delta);
		StepList.Focus();
	}

	// the log grows by appending (re-setting a long text every second re-lays the whole box); kept under 200,000 characters
	// the box's length, counted as text is added: reading LogBox.Text to measure it copies the whole box (up to 400 KB, a fresh
	// large-object allocation) every second of a run
	int _logChars;

	void OnLog(string a_add, bool a_reset)
	{
		if (a_reset) {
			LogBox.Clear();
			_logChars = 0;
		}
		if (a_add.Length > 0) {
			LogBox.AppendText(a_add);
			_logChars += a_add.Length;
			if (_logChars > 200_000) {
				LogBox.Text = LogBox.Text[^150_000..];
				_logChars = 150_000;
			}
		}
		if (Follow.IsChecked == true) {
			LogBox.ScrollToEnd();
		}
	}

	// ---------------------------------------------------------------- drawers
	void History_Click(object a_s, RoutedEventArgs a_e) => ShowPanel("history");
	void Settings_Click(object a_s, RoutedEventArgs a_e) => ShowPanel("settings");
	void CloseDrawer_Click(object a_s, RoutedEventArgs a_e) => CloseDrawer();
	void Shade_MouseDown(object a_s, MouseButtonEventArgs a_e) => CloseDrawer();

	public void CloseDrawerNow() => CloseDrawer();

	// the off-screen pictures only: the open drawer scrolled to a_y
	public void ScrollDrawer(double a_y)
	{
		// the drawer's own scroller - never the one inside a text box (Settings' search box comes first in the tree)
		static ScrollViewer? Find(DependencyObject a_d)
		{
			for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(a_d); ++i) {
				var c = System.Windows.Media.VisualTreeHelper.GetChild(a_d, i);
				if (c is System.Windows.Controls.Primitives.TextBoxBase) {
					continue;
				}
				if ((c as ScrollViewer ?? Find(c)) is { } s) {
					return s;
				}
			}
			return null;
		}
		Find(DrawerBody)?.ScrollToVerticalOffset(a_y);
	}

	void CloseDrawer()
	{
		if (!LeaveSettings()) {
			return;
		}
		Drawer.Visibility = Shade.Visibility = Visibility.Collapsed;
		_panel = "";
		// the keyboard goes back where it was before the drawer opened
		if (_beforeDrawer is UIElement { IsVisible: true, IsEnabled: true } back) {
			back.Focus();
		} else {
			StepList.Focus();
		}
		_beforeDrawer = null;
	}

	// what had the keyboard before a drawer opened (it gets it back when the drawer closes)
	IInputElement? _beforeDrawer;

	// settings typed but not saved: asked before they are thrown away
	bool LeaveSettings() =>
		_panel != "settings" || !SettingsPanel.HasChanges ||
		Ask.Show(this, "Settings not saved", "You changed settings and did not press Save. Leave without saving them?", "Leave without saving", "Keep editing");

	// the off-screen pictures only: the live view stops and a made-up run is shown (a_frame moves it on a minute a frame)
	public void Demo(bool a_waiting, int a_frame = 0)
	{
		_timer.Stop();
		_vm.Demo(a_waiting, a_frame);
	}

	// the page's pictures only: the dashboard with nothing running, and past runs of the made-up setup
	public void DemoReady()
	{
		_timer.Stop();
		_vm.DemoReady();
	}

	public void DemoHistory(IEnumerable<HistoryRow> a_rows) => _vm.DemoHistory(a_rows);

	// a_fresh: rebuild an open drawer (Save, Undo changes); otherwise pressing Settings again keeps what is typed
	public void ShowPanel(string a_which, bool a_fresh = false)
	{
		if (a_which == _panel && Drawer.Visibility == Visibility.Visible && !a_fresh) {
			return;
		}
		if (a_which != _panel && !LeaveSettings()) {
			return;
		}
		_panel = a_which;
		DrawerTitle.Text = a_which switch { "history" => "PAST RUNS", "changes" => "WHAT MO2 CHANGED", _ => "SETTINGS" };
		DrawerBody.Content = a_which switch {
			"history" => WithSwitch(HistoryPanel(), a_which),
			"changes" => WithSwitch(ChangesPanel(null), a_which),
			_ => SettingsPanel.Build(this, _vm),
		};
		var opening = Drawer.Visibility != Visibility.Visible;
		if (opening) {
			_beforeDrawer = Keyboard.FocusedElement;
		}
		System.Windows.Automation.AutomationProperties.SetName(Drawer, a_which switch { "history" => "Past runs", "changes" => "What MO2 changed", _ => "Settings" });
		Drawer.Visibility = Shade.Visibility = Visibility.Visible;
		// the keyboard moves into the drawer (Tab then stays inside it - its own cycle - until Esc or Close), and a screen reader
		// hears which one opened
		Dispatcher.BeginInvoke(DispatcherPriority.Input, () => {
			if (Drawer.IsKeyboardFocusWithin) {
				return;
			}
			// Settings: its search box; elsewhere the first thing in the drawer's own content, else its Close button
			if (a_which == "settings") {
				SettingsPanel.FocusFind();
			}
			if (!Drawer.IsKeyboardFocusWithin && !DrawerBody.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))) {
				Drawer.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
			}
		});
		A11y.Say(this, (opening ? "Opened: " : "") + System.Windows.Automation.AutomationProperties.GetName(Drawer) + ". Escape closes it.");
	}

	// the off-screen pictures only: the changes view filled with made-up rows (nothing read or written)
	public void ShowChangesDemo(IReadOnlyList<Mo2Changes.Change> a_rows)
	{
		ShowPanel("history");
		_panel = "changes";
		DrawerTitle.Text = "WHAT MO2 CHANGED";
		DrawerBody.Content = WithSwitch(ChangesPanel(a_rows), "changes");
	}

	// the history drawer's two views: the past runs and their logs, and what Mod Organizer changed
	DockPanel WithSwitch(FrameworkElement a_body, string a_which)
	{
		var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
		foreach (var (key, text, tip) in new[] { ("history", "Past runs", "Ctrl+H"), ("changes", "What MO2 changed", "Ctrl+M") }) {
			var b = new Button { Content = text, Style = (Style)FindResource(key == a_which ? "Primary" : "Ghost"), Margin = new Thickness(0, 0, 8, 0), ToolTip = tip };
			b.Click += (_, _) => ShowPanel(key);
			bar.Children.Add(b);
		}
		var dock = new DockPanel();
		DockPanel.SetDock(bar, Dock.Top);
		dock.Children.Add(bar);
		dock.Children.Add(a_body);
		return dock;
	}

	// what Mod Organizer changed (mods added, removed, ticked, unticked, moved - per profile), newest first; opening it looks at
	// Mod Organizer, unless a run is going or did not finish (its changes would read as made outside the runner)
	FrameworkElement ChangesPanel(IReadOnlyList<Mo2Changes.Change>? a_demo)
	{
		var setup = _vm.Setup;
		if (setup == null && a_demo == null) {
			return new TextBlock { Text = "Make or pick a setup first.", TextWrapping = TextWrapping.Wrap };
		}
		var note = new TextBlock { Style = (Style)FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
		var filter = new TextBox { MinWidth = 240, Margin = new Thickness(0, 0, 10, 0), VerticalContentAlignment = VerticalAlignment.Center };
		AutomationPropertiesName(filter, "Find a mod or a profile");
		var look = new Button { Content = "Look now", Style = (Style)FindResource("Ghost"), ToolTip = "Notes what changed in Mod Organizer since the last look" };
		var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
		tools.Children.Add(new TextBlock { Text = "Find", Foreground = Brushes2.Get("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
		tools.Children.Add(filter);
		tools.Children.Add(look);
		var list = new ListBox { Background = Brushes2.Get("Panel2"), BorderBrush = Brushes2.Get("Line") };
		ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
		AutomationPropertiesName(list, "What MO2 changed");
		list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
			"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Margin='2,4'>" +
			"<TextBlock TextWrapping='Wrap'><Run Text='{Binding Mod, Mode=OneWay}' FontWeight='SemiBold'/><Run Text='   '/><Run Text='{Binding What, Mode=OneWay}'/></TextBlock>" +
			"<TextBlock Text='{Binding Detail}' TextWrapping='Wrap' Opacity='0.8' FontSize='{DynamicResource SizeSmall}'/></StackPanel></DataTemplate>");
		var rows = new List<ChangeRow>();
		void Fill()
		{
			var f = filter.Text.Trim();
			list.ItemsSource = f == "" ? rows : rows.Where(r => r.Mod.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Detail.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
		}
		void Show(IEnumerable<Mo2Changes.Change> a_shown) =>
			rows = a_shown.Reverse().Select(r => new ChangeRow(r.Mod, r.What,
				$"{r.When.ToString("MMM d, HH:mm", InvariantCulture)}   ·   {(r.Profiles.Count == 0 ? "Mod Organizer's mods" : string.Join(", ", r.Profiles))}   ·   {r.Who}")).ToList();
		async void Load(bool a_look)
		{
			look.IsEnabled = false;
			note.Text = a_look ? "Looking at Mod Organizer ..." : "Reading ...";
			try {
				var (shown, since, found) = await Task.Run(() => {
					var mo2 = Mo2Instance.Load(setup!.Mo2Exe, setup.Mo2Instance);
					var dir = Mo2Changes.Dir(AppPaths.Data, mo2);
					// a run's changes in the middle of it (or of one that did not finish) are never noted as made outside the runner:
					// the look holds the run lock from its check to its end (null = a run goes or did not finish)
					var n = a_look ? Mo2Changes.RecordOutside(mo2, AppPaths.Data) : -1;
					return (Mo2Changes.Shown(Mo2Changes.Read(dir), setup.ChangesHideProfiles, setup.ChangesHideMods), Mo2Changes.WatchedSince(dir), n);
				});
				Show(shown);
				note.Text = (since == null ? "Not watched yet: the first look starts it." : $"Watched since {since.Value.ToString("MMM d, yyyy HH:mm", InvariantCulture)}.") +
					" A change made outside the runner shows with the time it was noticed - when this view opens, on Look now, or when a run starts." +
					(found > 0 ? $" {found} new." : found == 0 ? " Nothing new." : found == null ? " No new look while a run is going or did not finish." : "") +
					(rows.Count == 0 && since != null ? " No changes noted yet." : "");
				Fill();
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) {
				note.Text = "Could not read Mod Organizer: " + e.Message;
			} finally {
				look.IsEnabled = a_demo == null;
			}
		}
		look.Click += (_, _) => Load(true);
		filter.TextChanged += (_, _) => Fill();
		if (a_demo != null) {
			Show(a_demo);
			note.Text = "Watched since Oct 20, 2026 18:00. A change made outside the runner shows with the time it was noticed - when this view opens, on Look now, or when a run starts.";
			Fill();
			look.IsEnabled = false;
		} else {
			Load(true);
		}
		var dock = new DockPanel();
		DockPanel.SetDock(note, Dock.Top);
		DockPanel.SetDock(tools, Dock.Top);
		dock.Children.Add(note);
		dock.Children.Add(tools);
		dock.Children.Add(list);
		return dock;
	}

	Grid HistoryPanel()
	{
		var grid = new Grid();
		grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
		var list = new ListBox { ItemsSource = _vm.History };
		// the theme's colors as live references: a theme or Windows' high contrast changed while History is open repaints it too
		list.SetResourceReference(BackgroundProperty, "Panel2");
		list.SetResourceReference(BorderBrushProperty, "Line");
		// a long verdict wraps instead of running off the right edge
		ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
		AutomationPropertiesName(list, "Past runs");
		list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
			"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Margin='2,4'>" +
			"<TextBlock><Run Text='{Binding When, Mode=OneWay}' FontWeight='SemiBold'/><Run Text='   '/><Run Text='{Binding Setup, Mode=OneWay}'/><Run Text='   '/><Run Text='{Binding Took, Mode=OneWay}'/></TextBlock>" +
			"<TextBlock Text='{Binding Verdict}' TextWrapping='Wrap' Opacity='0.8' FontSize='{DynamicResource SizeSmall}'/></StackPanel></DataTemplate>");
		var log = new TextBox { IsReadOnly = true, FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), FontSize = (double)FindResource("SizeLog"), Background = Brushes2.Get("Panel2"),
			Foreground = Brushes2.Get("Text"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
		AutomationPropertiesName(log, "The run's log");
		var open = new Button { Content = "Open its folder", Style = (Style)FindResource("Ghost"), Margin = new Thickness(0, 0, 8, 0) };
		// his checklist 482: one zip for a bug report, names masked; made on the player's press, never sent anywhere
		var report = new Button { Content = "Make a bug report ...", Style = (Style)FindResource("Ghost"),
			ToolTip = "Puts this run's logs, its setup and the versions of everything into one zip, with your Windows name and PC name masked - for a GitHub issue" };
		report.Click += async (_, _) => {
			var row = list.SelectedItem as HistoryRow;
			report.IsEnabled = false;
			try {
				var zip = await Task.Run(() => BugReport.Make(AppPaths.Data, row == null ? null : Journal.Load(row.Folder), Path.Combine(AppPaths.Data, "Bug reports"), AppVersion));
				if (Ask.Show(this, "Bug report made", $"It is ready: {Path.GetFileName(zip)}\n\nIt holds {(row == null ? "the program's own notes" : "this run's logs and record")}, the setup it ran and the " +
				    "versions of Windows, Mod Organizer, the game and each tool. Your Windows user name and your PC's name are masked. Nothing was sent anywhere.\n\n" +
				    "To report the bug: open an issue on the program's GitHub page, say what happened and what you expected, and attach this zip.",
				    "Show the zip and open the issue page", "Close")) {
					System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{zip}\"") { UseShellExecute = true });
					System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(BugReport.Issues) { UseShellExecute = true });
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) {
				_vm.Say("The bug report could not be made: " + e.Message, "bad");
			} finally {
				report.IsEnabled = true;
			}
		};
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 10) };
		buttons.Children.Add(open);
		buttons.Children.Add(report);
		list.SelectionChanged += (_, _) => {
			if (list.SelectedItem is HistoryRow r) {
				var p = MainViewModel.LogFile(r.Folder, _vm.DetailedLog);
				try {
					log.Text = File.Exists(p) ? Tail(p, 150_000) : "(no log)";
					log.ScrollToEnd();
				} catch (IOException e) {
					log.Text = e.Message;
				}
			}
		};
		open.Click += (_, _) => {
			if (list.SelectedItem is HistoryRow r) {
				MainViewModel.OpenFolder(r.Folder);
			}
		};
		if (_vm.History.Count == 0) {
			log.Text = "No runs of this setup yet.";
		}
		// an empty list says why it is empty (a blank box reads as broken)
		var empty = new TextBlock { Text = "No runs of this setup yet - its runs show here, newest first, each with its log.", Style = (Style)FindResource("MutedText"),
			Margin = new Thickness(14), IsHitTestVisible = false, Visibility = _vm.History.Count == 0 ? Visibility.Visible : Visibility.Collapsed };
		// it follows the list: a first run that ends while History is open takes the hint away (the handler goes with the panel, so
		// panels opened again and again leave nothing hanging on the list)
		void Follow(object? a_s, System.Collections.Specialized.NotifyCollectionChangedEventArgs a_e) =>
			empty.Visibility = _vm.History.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		_vm.History.CollectionChanged += Follow;
		empty.Unloaded += (_, _) => _vm.History.CollectionChanged -= Follow;
		Grid.SetRow(list, 0);
		Grid.SetRow(empty, 0);
		Grid.SetRow(buttons, 1);
		Grid.SetRow(log, 2);
		grid.Children.Add(list);
		grid.Children.Add(empty);
		grid.Children.Add(buttons);
		grid.Children.Add(log);
		if (_vm.History.Count > 0) {
			list.SelectedIndex = 0;
		}
		return grid;
	}

	// a log's last part (a whole long log in one box freezes the window)
	static string Tail(string a_path, int a_bytes)
	{
		using var fs = new FileStream(a_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		var start = Math.Max(0, fs.Length - a_bytes);
		fs.Seek(start, SeekOrigin.Begin);
		var bytes = new byte[fs.Length - start];
		fs.ReadExactly(bytes);
		var text = System.Text.Encoding.UTF8.GetString(bytes);
		return start == 0 ? text : "(the start of this log is in its folder)\n" + text[(text.IndexOf('\n') + 1)..];
	}

	static void AutomationPropertiesName(DependencyObject a_o, string a_name) => System.Windows.Automation.AutomationProperties.SetName(a_o, a_name);

	// ---------------------------------------------------------------- a new setup
	void Queue_Click(object a_s, RoutedEventArgs a_e)
	{
		if (_vm.IsRunning) {
			_vm.Say("A run is going already.", "bad");
			return;
		}
		if (QueueDialog.Run(this, _vm.Setups.Select(s => s.File).ToList()) is { } q) {
			_ = _vm.StartQueue(q.Files, q.KeepGoing);
		}
	}

	void NewSetup_Click(object a_s, RoutedEventArgs a_e)
	{
		if (_vm.IsRunning) {
			return;
		}
		var made = NewSetupDialog.Run(this);
		if (made != null) {
			_vm.LoadSetups(made);
			_vm.Say("Setup made. Press Check (F5) to see that every tool is found, then Start.", "good");
		}
	}

	bool _stopping;
	bool _closeNow;

	// the window goes once the run has stopped and put the profile back; a run that does not stop in 10 minutes keeps the window
	async Task CloseWhenStopped(Journal a_run)
	{
		var until = DateTime.Now.AddMinutes(10);
		// a queue's next setup that started before the stop reached the last one is stopped too (a stopped run ends the queue)
		var stopped = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a_run.Folder };
		while (DateTime.Now < until && (Engine.Active() is not null || QueueRun.Read(AppPaths.Data) != null)) {
			if (Engine.Active() is { } next && stopped.Add(next.Folder)) {
				Commands.Send(next.Folder, "stop");
			}
			await Task.Delay(1000);
		}
		_stopping = false;
		if (Engine.Active() is not null || QueueRun.Read(AppPaths.Data) != null) {
			_vm.Say("The run did not stop in 10 minutes, so the window stays open. Press Stop now again.", "bad");
			return;
		}
		_closeNow = true;
		Close();
	}

	protected override void OnClosing(System.ComponentModel.CancelEventArgs a_e)
	{
		if (!LeaveSettings()) {
			a_e.Cancel = true;
		} else if (!_closeNow && Engine.Active() is { } run) {
			// a run never goes on unseen behind a closed window (it closed his Mod Organizer a minute after the window went):
			// closing stops it - the tool closed, the profile put back - or the window stays open
			if (_stopping) {
				_vm.Say("Still stopping the run - the window closes once your profile is back.", "warn");
			} else if (Ask.Show(this, "A run is going", "Closing the window stops the run: the tool it is running is closed and your profile put back, " +
				"as with Stop now. The window closes once that is done.", "Stop the run and close", "Keep the window open")) {
				Commands.Send(run.Folder, "stop");
				_vm.Say("Stopping the run - the window closes once your profile is back.", "warn");
				_stopping = true;
				_ = CloseWhenStopped(run);
			}
			a_e.Cancel = true;
		}
		base.OnClosing(a_e);
	}

	protected override void OnSourceInitialized(EventArgs a_e)
	{
		base.OnSourceInitialized(a_e);
		var helper = new WindowInteropHelper(this);
		_tray = new Tray(helper.Handle);
		// a process that ends without closing the window (a crash, Windows logging off) takes its icon with it
		AppDomain.CurrentDomain.ProcessExit += (_, _) => _tray?.Dispose();
		HwndSource.FromHwnd(helper.Handle)?.AddHook((nint _, int a_msg, nint _, nint _, ref bool _) => {
			if ((uint)a_msg == Tray.TaskbarCreated) {
				_tray?.Readd();
			}
			return 0;
		});
	}

	protected override void OnClosed(EventArgs a_e)
	{
		Theme.Applied -= _vm.Repaint;
		_timer.Stop();
		_tray?.Dispose();
		base.OnClosed(a_e);
	}
}

public sealed record ChangeRow(string Mod, string What, string Detail)
{
	public override string ToString() => $"{Mod} {What}, {Detail}";
}

// a themed yes / no window (never a plain system box)
public static class Ask
{
	public static bool Show(Window a_owner, string a_title, string a_text, string a_yes, string a_no)
	{
		var result = false;
		var w = Make(a_owner, a_title, a_text, a_yes, a_no, a_yesPressed => result = a_yesPressed);
		w.ShowDialog();
		return result;
	}

	// a notice with one button (Enter and Esc both close it)
	public static void Tell(Window a_owner, string a_title, string a_text, string a_ok) => Make(a_owner, a_title, a_text, a_ok, null, _ => { }).ShowDialog();

	// the box itself (the off-screen pictures draw it without waiting on it)
	public static Window Make(Window a_owner, string a_title, string a_text, string a_yes, string? a_no, Action<bool> a_done)
	{
		var w = new Window {
			Owner = a_owner, Title = a_title, Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Background = Brushes2.Get("Panel"), Foreground = Brushes2.Get("Text"), ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
			FontFamily = (System.Windows.Media.FontFamily)a_owner.FindResource("UiFont"), FontSize = (double)a_owner.FindResource("SizeBody"),
		};
		var yes = new Button { Content = a_yes, Style = (Style)a_owner.FindResource("Primary"), Margin = new Thickness(0, 0, a_no == null ? 0 : 10, 0) };
		// a notice has only its one button: it is the safe answer too
		var no = new Button { Content = a_no ?? "", Style = (Style)a_owner.FindResource("Ghost"), IsCancel = a_no != null, IsDefault = a_no != null };
		if (a_no == null) {
			yes.IsCancel = yes.IsDefault = true;
		}
		yes.Click += (_, _) => {
			a_done(true);
			w.Close();
		};
		no.Click += (_, _) => w.Close();
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
		buttons.Children.Add(yes);
		if (a_no != null) {
			buttons.Children.Add(no);
		}
		var body = new StackPanel { Margin = new Thickness(22) };
		body.Children.Add(new TextBlock { Text = a_title, Style = (Style)a_owner.FindResource("H1"), Margin = new Thickness(0, 0, 0, 12), FontSize = (double)a_owner.FindResource("SizeHeading1") });
		// a long text (a run's preview) scrolls inside the box instead of growing past the screen
		var scroll = new ScrollViewer { MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = true,
			Content = new TextBlock { Text = a_text, TextWrapping = TextWrapping.Wrap } };
		System.Windows.Automation.AutomationProperties.SetName(scroll, a_title);
		body.Children.Add(scroll);
		body.Children.Add(buttons);
		w.Content = new Border { Background = Brushes2.Get("Panel"), Child = body };
		// the safe answer has the keyboard; a screen reader hears the whole question, not only the button
		w.Loaded += (_, _) => {
			(a_no == null ? yes : no).Focus();
			A11y.Say(w, a_title + ". " + a_text, true);
		};
		return w;
	}
}
