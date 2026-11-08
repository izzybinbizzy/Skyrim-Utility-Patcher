using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace ToolRunner.App;

// The settings drawer: the setup's own settings and each step's, in plain names; edited on a copy, checked, saved with a
// backup (MainViewModel.SaveSetup) - never a hand-edited file.
public static class SettingsPanel
{
	static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal) {
		["executable"] = "Its entry in MO2's Executables list",
		["image"] = "Its program file name",
		["output_mod"] = "Output mod",
		["then_move_to_mod"] = "Output mod",
		["output_dir"] = "The tool's own output folder",
		["deadline_min"] = "Give up after (minutes)",
		["enable_during"] = "Mods switched ON while it runs (one per line)",
		["disable_during"] = "Mods switched OFF while it runs (one per line)",
		["replaces"] = "Mods it replaces - kept off after (one per line)",
		["worldspaces"] = "Worldspaces (all, or one per line)",
		["args"] = "Extra arguments",
		["settings"] = "PGPatcher settings set (a file beside the setup)",
		["pglite_check"] = "Stop when PGLite's tick and PGLite Mode disagree",
		["launch_executable"] = "Game launcher (its MO2 entry)",
		["interior_cell"] = "Cell the clean save is made in (QASmoke)",
		["dyndolod_wait_s"] = "Wait after DynDOLOD goes off (seconds)",
		["dyndolod_off"] = "Switch DynDOLOD off in game first",
		["save"] = "Save to start from (empty = the newest)",
		["start_min"] = "Game start limit (minutes)",
		["wait_s"] = "Wait after a save loads (seconds)",
		["by"] = "How (fasterngio or game)",
		["fasterngio_exe"] = "FasterNGIO's program",
		["fasterngio_args"] = "FasterNGIO's arguments",
		["exe"] = "Program",
		["dyndolod"] = "DynDOLOD's folder",
		["preset"] = "DynDOLOD preset file",
		["no_terrain_underside"] = "Never make a terrain underside",
		["reads"] = "The terrain LOD folder it paints",
		["tool_dir"] = "The tool's folder",
		["tool_writes"] = "The folder the tool writes into",
		["min_files"] = "Fewest files that counts as a result",
		["caches"] = "Shader caches (each: its shader mod, its cache mod)",
		["settle_s"] = "Wait before closing (seconds)",
		["quiet_min"] = "Done when quiet for (minutes)",
		["max_restarts"] = "Restarts allowed",
		["build_min"] = "Patcher build limit (minutes)",
		["extra_args"] = "Extra arguments (one per line)",
	};

	// an edit not saved yet (the window asks before the drawer closes over it)
	public static bool HasChanges { get; private set; }

	static void Changed() => HasChanges = true;

	// settings a step reads that its file may not list yet (shown empty, with the default the step uses)
	static readonly Dictionary<string, (string Key, JsonValueKind Kind, string Default)[]> Known = new(StringComparer.Ordinal) {
		["shaders"] = [("quiet_min", JsonValueKind.Number, "5"), ("min_files", JsonValueKind.Number, "50"), ("max_restarts", JsonValueKind.Number, "3")],
		["grass"] = [("stall_min", JsonValueKind.Number, "10"), ("min_files", JsonValueKind.Number, "100")],
		["pgpatcher"] = [("pglite_check", JsonValueKind.False, "off")],
		["dyndolod"] = [("no_terrain_underside", JsonValueKind.False, "off"), ("preset", JsonValueKind.String, "none")],
		["landscapefixer"] = [("min_files", JsonValueKind.Number, "3")],
		["synthesis"] = [("build_min", JsonValueKind.Number, "15")],
	};

	// the mods a LOD step builds from and the LOD tool's own help, each a page to open (the runner never guesses their values)
	static void LodBlock(MainWindow a_w, Setup a_setup, string a_step, Panel a_host)
	{
		a_host.Children.Add(new TextBlock { Text = "Before you set it up: LOD can come out too bright, too dark or missing when its settings do not match the mods it is " +
			"built from. Each of those mods' own pages (and the LOD tool's help) says what it needs - the runner never guesses another author's values.",
			Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) });
		static Button Link(MainWindow a_win, string a_text, string a_url)
		{
			var b = new Button { Content = a_text, Style = (Style)a_win.FindResource("Ghost"), Margin = new Thickness(10, 0, 0, 0), ToolTip = a_url };
			b.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(a_url) { UseShellExecute = true });
			return b;
		}
		var docs = new WrapPanel { Margin = new Thickness(-10, 0, 0, 6) };
		a_host.Children.Add(docs);
		var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), Foreground = Brushes2.Get("Warn") };
		a_host.Children.Add(note);
		// the setup's own pages (a Community Shaders setup has one more) - read off the profile, so not on the window's thread
		_ = Task.Run(() => {
			try {
				var mo2 = Mo2Instance.Load(a_setup.Mo2Exe, a_setup.Mo2Instance);
				return (LodGuide.DocsFor(a_setup, mo2, a_step), LodGuide.SetupNote(a_setup, mo2, a_step));
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) {
				return (LodGuide.Docs.GetValueOrDefault(a_step, []).ToList(), (string?)null);
			}
		}).ContinueWith(t => {
			// a read that failed some other way leaves the block without its pages (never an error box over the settings)
			if (!t.IsCompletedSuccessfully) {
				return;
			}
			foreach (var (title, url) in t.Result.Item1) {
				docs.Children.Add(Link(a_w, title, url));
			}
			note.Text = t.Result.Item2 ?? "";
			note.Visibility = t.Result.Item2 == null ? Visibility.Collapsed : Visibility.Visible;
		}, TaskScheduler.FromCurrentSynchronizationContext());
		var rows = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
		var find = new Button { Content = "Find the mods it is built from", Style = (Style)a_w.FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Left };
		find.Click += async (_, _) => {
			find.IsEnabled = false;
			rows.Children.Clear();
			rows.Children.Add(new TextBlock { Text = "Reading the profile's plugins ...", Style = (Style)a_w.FindResource("MutedText") });
			try {
				var hits = await Task.Run(() => LodGuide.Find(a_setup, Mo2Instance.Load(a_setup.Mo2Exe, a_setup.Mo2Instance), a_step));
				rows.Children.Clear();
				rows.Children.Add(new TextBlock { Text = hits.Count == 0 ? "No ticked mod changes what it builds from." :
					$"{hits.Count} ticked mod(s) change what it builds from - check each one's page for its LOD settings:", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 4) });
				foreach (var h in hits) {
					var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
					if (h.Page != null) {
						var open = Link(a_w, "Open its page", h.Page);
						DockPanel.SetDock(open, Dock.Right);
						row.Children.Add(open);
					}
					row.Children.Add(new TextBlock { Text = $"{h.Mod} ({h.What}){(h.Page == null ? " - no Nexus page recorded" : "")}", TextWrapping = TextWrapping.Wrap,
						VerticalAlignment = VerticalAlignment.Center });
					rows.Children.Add(row);
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) {
				rows.Children.Clear();
				rows.Children.Add(new TextBlock { Text = "Could not read the profile: " + e.Message, TextWrapping = TextWrapping.Wrap });
			} finally {
				find.IsEnabled = true;
			}
		};
		a_host.Children.Add(find);
		a_host.Children.Add(rows);
	}

	// his checklist 478: how much room this setup's run backups take, and a clean-up to the newest few without waiting for a run
	static void BackupsBlock(MainWindow a_w, MainViewModel a_vm, Panel a_host)
	{
		var setup = a_vm.Setup!;
		var say = new TextBlock { Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
			Text = "Measuring this setup's run backups ..." };
		var tidy = new Button { Content = "Keep only the newest now", Style = (Style)a_w.FindResource("Ghost"), IsEnabled = false };
		var open = new Button { Content = "Open the backups folder", Style = (Style)a_w.FindResource("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
		open.Click += (_, _) => MainViewModel.OpenFolder(setup.BackupDir);
		static string Gb(long a_b) => a_b >= 1L << 30 ? $"{a_b / (double)(1L << 30):0.0} GB" : $"{a_b / (double)(1L << 20):0} MB";
		async void Measure()
		{
			var all = await Task.Run(() => {
				try {
					return Runner.BackupsOf(setup);
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					return [];
				}
			});
			var keep = Setup.Int(setup.Root, "keepBackups") ?? 5;
			say.Text = all.Count == 0 ? "This setup has no run backups yet." :
				$"This setup's run backups: {all.Count} run(s), {Gb(all.Sum(b => b.Bytes))} in all - " +
				(keep > 0 ? $"the newest {keep} are kept after each run." : "every one is kept (Backups kept is 0).");
			tidy.IsEnabled = keep > 0 && all.Count > keep;
			tidy.ToolTip = $"Removes all but the newest {keep} now (the number saved above); a run's own backup and one an unfinished run needs are never touched";
		}
		tidy.Click += async (_, _) => {
			var keep = Setup.Int(setup.Root, "keepBackups") ?? 5;
			if (!Ask.Show(a_w, "Remove old backups", $"All but the newest {keep} of this setup's run backups are deleted for good (they are copies of outputs and settings " +
			        "from runs that finished long ago). A backup an unfinished run still needs is kept.", "Remove them", "Keep them")) {
				return;
			}
			tidy.IsEnabled = false;
			try {
				var removed = await Task.Run(() => {
					// one at a time with runs and put-backs (the run lock, taken and given back on this thread)
					using var hold = Runner.HoldIfFree();
					return hold == null ? null : Runner.PruneBackupsOf(setup, AppPaths.Runs, null, keep, RunLog.Null);
				});
				a_vm.Say(removed == null ? "Not removed: a run or a put-back is going - try again when it is done." : $"{removed.Count} old backup(s) removed.", removed == null ? "bad" : "good");
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				// a backup folder that went away or locked part way: said, and the button can be pressed again
				a_vm.Say("The old backups could not all be looked at: " + e.Message, "bad");
			} finally {
				tidy.IsEnabled = true;
			}
			Measure();
		};
		var row = new WrapPanel { Margin = new Thickness(230, -4, 0, 12) };
		row.Children.Add(say);
		row.Children.Add(tidy);
		row.Children.Add(open);
		a_host.Children.Add(row);
		Measure();
	}

	// the page that explains a step's tool: its download page (the author's description and instructions), or for the save steps
	// DynDOLOD's own help on the clean save
	public static (string Url, string What)? HelpPage(string a_step) =>
		a_step is "cleansave" or "playsave" ? ("https://dyndolod.info/Help/Clean-Save", "DynDOLOD's help on the clean save") :
		ToolUpdates.Known.TryGetValue(a_step, out var src) ? (src.Page.Replace("?tab=files", "", StringComparison.Ordinal), "the tool's own page") : null;

	static string Label(string a_key) => Labels.TryGetValue(a_key, out var l) ? l : char.ToUpperInvariant(a_key[0]) + a_key[1..].Replace('_', ' ');

	// his checklist 471: the program's own new versions - kept for the whole program, saved the moment it is ticked
	static void NewVersions(MainWindow a_w, StackPanel a_stack)
	{
		a_stack.Children.Add(new TextBlock { Text = "NEW VERSIONS OF THIS PROGRAM", Style = (Style)a_w.FindResource("H2"), Margin = new Thickness(0, 18, 0, 8), Tag = HeadTag });
		var newVersions = new CheckBox { Content = "Tell me when a new version is out", IsChecked = !ProgramUpdate.IsOff(AppPaths.Data), Margin = new Thickness(0, 0, 0, 4) };
		newVersions.Click += (_, _) => ProgramUpdate.SetOff(AppPaths.Data, newVersions.IsChecked != true);
		a_stack.Children.Add(newVersions);
		a_stack.Children.Add(new TextBlock { Text = "At most once a day, when the window opens, it asks its own download page which version is newest - nothing else is sent, " +
			"and nothing is downloaded: the page opens in your browser when you choose.", Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap });
	}

	public static FrameworkElement Build(MainWindow a_w, MainViewModel a_vm)
	{
		if (a_vm.Setup == null) {
			// the program's own new-version look happens with no setup too, so its switch is here as well
			var none = new StackPanel();
			none.Children.Add(new TextBlock { Text = "Make or pick a setup first.", TextWrapping = TextWrapping.Wrap });
			NewVersions(a_w, none);
			return none;
		}
		var work = (JsonObject)a_vm.Setup.Root.DeepClone();
		HasChanges = false;
		var stack = new StackPanel();
		var errors = new TextBlock { Foreground = Brushes2.Get("Bad"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
		var apply = new List<Func<string?>>();

		void Head(string a_t) => stack.Children.Add(new TextBlock { Text = a_t, Style = (Style)a_w.FindResource("H2"), Margin = new Thickness(0, 18, 0, 8), Tag = HeadTag });

		// his checklist 475: a search over every setting (Ctrl+F while Settings is open)
		var find = new TextBox { Margin = new Thickness(0, 0, 0, 4), ToolTip = "Ctrl+F" };
		System.Windows.Automation.AutomationProperties.SetName(find, "Find a setting");
		System.Windows.Automation.AutomationProperties.SetAcceleratorKey(find, "Ctrl+F");
		var findHint = new TextBlock { Text = "Find a setting - type a word (Ctrl+F)", Style = (Style)a_w.FindResource("MutedText"), Margin = new Thickness(0, 0, 0, 6) };
		find.TextChanged += (_, _) => {
			var hits = Filter(stack, find.Text.Trim());
			findHint.Text = find.Text.Trim() == "" ? "Find a setting - type a word (Ctrl+F)" : hits == 0 ? "No setting has that word." : $"{hits} setting(s) found.";
		};
		s_find = find;
		var top = new StackPanel();
		top.Children.Add(find);
		top.Children.Add(findHint);

		Head("THIS SETUP");
		// his checklist 466: a copy another player can import - its steps and options, nothing of this PC
		var share = new Button { Content = "Share this setup ...", Style = (Style)a_w.FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8),
			ToolTip = "Writes a copy of this setup (as last saved) for another player: every path of this PC and the commands it runs before and after a run are taken out" };
		share.Click += async (_, _) => {
			if (a_vm.Setup is not { } current || !share.IsEnabled) {
				return;
			}
			// the setup as it is this moment, written off the window's thread; one share at a time
			share.IsEnabled = false;
			var text = System.Text.Encoding.UTF8.GetBytes(current.Root.ToJsonString());
			var source = current.File;
			try {
				var (file, gone) = await Task.Run(() => SetupShare.Write(Setup.Parse(text, source), Path.Combine(AppPaths.Data, "Shared setups"), MainWindow.AppVersion))
					.ConfigureAwait(true);
				if (Ask.Show(a_w, "Shared copy made", $"It is ready: {Path.GetFileName(file)}\n\nWhat the other player sets themselves:\n" +
				    (gone.Count == 0 ? "nothing" : string.Join("\n", gone.Select(g => "- " + g))) + "\n\nThey bring it in with the import command (see the readme).",
				    "Show the file", "Close")) {
					System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or InvalidDataException) {
				a_vm.Say("The shared copy could not be made: " + e.Message, "bad");
			} finally {
				share.IsEnabled = true;
			}
		};
		stack.Children.Add(share);
		var mo2 = work["mo2"] as JsonObject ?? (JsonObject)(work["mo2"] = new JsonObject());
		Field(stack, apply, "Mod Organizer's program", mo2, "exe");
		Field(stack, apply, "Its instance folder", mo2, "instance");
		Field(stack, apply, "MO2 profile", mo2, "profile");
		Field(stack, apply, "Backups folder", work, "backups");
		Field(stack, apply, "Scratch folder (emptied after a whole run)", work, "scratch");
		Field(stack, apply, "SKSE log folder (empty = My Games)", work, "skseLogs");
		// his checklist 457: the character whose saves the in-game steps load (the names are read off the saves, off the window's thread)
		Field(stack, apply, "Character for the in-game steps (empty = the newest save)", work, "character");
		var whoHint = new TextBlock { Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(230, -6, 0, 10) };
		stack.Children.Add(whoHint);
		var forSaves = a_vm.Setup!;
		_ = Task.Run(() => {
			try {
				var mo2 = Mo2Instance.Load(forSaves.Mo2Exe, forSaves.Mo2Instance);
				return SaveHeader.Characters(InGame.SavesDir(new Profile(mo2, forSaves.Profile, () => false)));
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) {
				return new List<string>();
			}
		}).ContinueWith(t => whoHint.Dispatcher.BeginInvoke(() => whoHint.Text = t.Result.Count == 0 ? "No saves found in this profile yet." :
			"Characters with saves here, most recently played first: " + string.Join(", ", t.Result.Take(12))), TaskScheduler.Default);
		Field(stack, apply, "A tool idle this long is looked at (minutes)", work, "idleMinutes", JsonValueKind.Number);
		Field(stack, apply, "Free space a run needs (GB)", work, "minFreeGb", JsonValueKind.Number);
		Choice(stack, apply, "Rebuild the shader caches", work, "shaderRebuild", [("whenChanged", "only when the shader mod changed"), ("always", "every run"), ("never", "never")]);
		Field(stack, apply, "Allow writing into the game folder (NGIO's grass route)", work, "gameFolderWrites", JsonValueKind.True);
		var alerts = work["alerts"] as JsonObject ?? (JsonObject)(work["alerts"] = new JsonObject { ["notification"] = true, ["sound"] = true, ["flash"] = false });
		Field(stack, apply, "Alert: Windows notification", alerts, "notification", JsonValueKind.True);
		Field(stack, apply, "Alert: sound", alerts, "sound", JsonValueKind.True);
		Field(stack, apply, "Alert: flash the taskbar button", alerts, "flash", JsonValueKind.True);
		// his checklist 461 / 463: both optional, off unless set
		Choice(stack, apply, "After the run, the PC", work, "afterRunPower", [("none", "stays on"), ("sleep", "goes to sleep - or hibernates, as Windows is set (a minute's notice, cancel in this window)"),
			("shutdown", "shuts down (a minute's notice, cancel in this window)")]);
		work["afterRunPowerOnlyFinished"] ??= true;
		Field(stack, apply, "Only after a run that finished (a failed run keeps the PC on, so you see why)", work, "afterRunPowerOnlyFinished", JsonValueKind.True);
		work["guidedReminderMin"] ??= 0;
		Field(stack, apply, "Remind me while a step waits for me in the game, every (minutes; 0 = no reminders)", work, "guidedReminderMin", JsonValueKind.Number);
		// his checklist 270 ("all 3."): the preview before a run, and only the last few runs' backups kept
		work["previewBeforeRun"] ??= true;
		work["keepBackups"] ??= 5;
		Field(stack, apply, "Show what a run will do before it starts", work, "previewBeforeRun", JsonValueKind.True);
		Field(stack, apply, "Backups kept: the last runs' (0 = all; older are deleted)", work, "keepBackups", JsonValueKind.Number);
		BackupsBlock(a_w, a_vm, stack);
		// his checklist 479: the tools at a lower priority, so the PC stays smooth while they work (a run may take a little longer)
		work["lowerPriority"] ??= false;
		Field(stack, apply, "Run the tools at a lower priority (the PC stays smoother; a run may take a little longer)", work, "lowerPriority", JsonValueKind.True);
		Field(stack, apply, "Commands before a run (one per line, each must succeed)", work, "beforeRun", JsonValueKind.Array);
		Field(stack, apply, "Commands after a run (one per line)", work, "afterRun", JsonValueKind.Array);
		// History > What MO2 changed: a profile only tests use, or test mods, can be left out of the view (nothing is lost)
		var changes = work["changes"] as JsonObject ?? (JsonObject)(work["changes"] = new JsonObject());
		Field(stack, apply, "What MO2 changed - profiles left out of the view (one per line)", changes, "hideProfiles", JsonValueKind.Array);
		Field(stack, apply, "What MO2 changed - mods left out whose name starts with (one per line)", changes, "hideModsStartingWith", JsonValueKind.Array);

		// the window's look for every setup (his ask 2026-10-27: themes his design agent makes from "Theme template.json")
		Head("THE WINDOW'S LOOK");
		var themeNote = new TextBlock { Text = "A theme is a copy of \"Theme template.json\" (in the Themes folder) with new values. It applies at once.",
			Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
		var themeRow = new StackPanel { Orientation = Orientation.Horizontal };
		var themes = new ComboBox { MinWidth = 220, Margin = new Thickness(0, 0, 10, 0) };
		System.Windows.Automation.AutomationProperties.SetName(themes, "Theme");
		// the Themes folder is read off the window's thread (many or slow files never freeze it)
		var filling = false;
		async void FillThemes()
		{
			var (list, error) = await Task.Run(() => {
				string? err = null;
				try {
					Theme.EnsureFolder();
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) {
					err = "The Themes folder could not be made: " + e.Message;
				}
				// an unreadable folder still shows the built-in look, with why (the fourth review, L8)
				try {
					return (Theme.List(), err);
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) {
					// the theme in use stays listed beside the built-in one, so the picker shows what is on screen and the built-in
					// look can still be picked (CodeRabbit on PR #4)
					var keep = new List<(string Name, string? File)> { (Theme.Default, null) };
					keep.AddRange(Theme.BuiltInNames.Select(b => (b, (string?)null)));
					if (Theme.Current != Theme.Default && !Theme.BuiltInNames.Contains(Theme.Current)) {
						keep.Add((Theme.Current, null));
					}
					return (keep, "The Themes folder could not be read: " + e.Message);
				}
			});
			if (error != null) {
				themeNote.Text = error;
			}
			filling = true;
			themes.ItemsSource = list.Select(t => t.Name).ToList();
			themes.SelectedItem = list.Any(t => t.Name == Theme.Current) ? Theme.Current : Theme.Default;
			filling = false;
		}
		FillThemes();
		// a look changed by Windows (high contrast followed) shows in the picker while Settings is open
		void OnChanged() => themes.Dispatcher.BeginInvoke(() => {
			filling = true;
			themes.SelectedItem = Theme.Current;
			filling = false;
		});
		Theme.Changed += OnChanged;
		themes.Unloaded += (_, _) => Theme.Changed -= OnChanged;
		themes.SelectionChanged += async (_, _) => {
			if (filling || themes.SelectedItem is not string name || name == Theme.Current) {
				return;
			}
			var problems = await Theme.ApplyAsync(name);
			themeNote.Text = problems.Count == 0 ? $"\"{Theme.Current}\" in use." : "In use, with these values left as the default: " + string.Join("; ", problems.Take(6));
		};
		var openThemes = new Button { Content = "Open the Themes folder", Style = (Style)a_w.FindResource("Ghost"), Margin = new Thickness(0, 0, 8, 0) };
		openThemes.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Theme.Folder) { UseShellExecute = true });
		var reload = new Button { Content = "Look again", Style = (Style)a_w.FindResource("Ghost"), ToolTip = "Reads the Themes folder again (a theme just added)" };
		reload.Click += (_, _) => FillThemes();
		themeRow.Children.Add(themes);
		themeRow.Children.Add(openThemes);
		themeRow.Children.Add(reload);
		stack.Children.Add(themeNote);
		stack.Children.Add(themeRow);
		// his checklist 473: bigger text for every theme, kept for the next start
		stack.Children.Add(new TextBlock { Text = "Text size", Foreground = Brushes2.Get("Gold"), Margin = new Thickness(0, 12, 0, 4) });
		var size = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = Theme.TextScales.Select(s => $"{s * 100:0} %").ToList(),
			SelectedIndex = Math.Max(0, Array.IndexOf(Theme.TextScales, Theme.TextScale)) };
		System.Windows.Automation.AutomationProperties.SetName(size, "Text size");
		size.SelectionChanged += (_, _) => {
			if (size.SelectedIndex >= 0) {
				Theme.SetTextScale(Theme.TextScales[size.SelectedIndex]);
			}
		};
		stack.Children.Add(size);
		stack.Children.Add(new TextBlock { Text = "The \"High contrast\" theme takes Windows' own high-contrast colors and is used by itself while Windows has high contrast on " +
			"and no theme was picked here. \"Color-blind safe\" keeps finished, warning and failed apart by more than red and green. Every state also has its own mark.",
			Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });

		NewVersions(a_w, stack);

		// his ask 2026-10-27: aware of a tool's updates, the download from the runner (its page, in the player's browser)
		Head("TOOL UPDATES");
		var updNote = new TextBlock { Text = "Asks each tool's own page (Nexus, GitHub) for its newest version - only when you press this.",
			Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
		var updRows = new StackPanel();
		var updCheck = new Button { Content = "Check its tools for updates", Style = (Style)a_w.FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Left };
		updCheck.Click += async (_, _) => {
			updCheck.IsEnabled = false;
			updRows.Children.Clear();
			updNote.Text = "Asking the tools' pages ...";
			try {
				var setup = a_vm.Setup!;
				var res = await Task.Run(async () => await ToolUpdates.Check(setup, Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance), CancellationToken.None));
				foreach (var r in res) {
					var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
					if (r.Newer) {
						var get = new Button { Content = "Open its download page", Style = (Style)a_w.FindResource("Ghost"), Margin = new Thickness(10, 0, 0, 0) };
						var page = r.Page;
						get.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page) { UseShellExecute = true });
						DockPanel.SetDock(get, Dock.Right);
						row.Children.Add(get);
					}
					var said = r.Problem ?? (r.Newer ? $"a NEWER version is out: {r.Newest} (yours {r.Yours})" : $"up to date ({r.Yours})");
					row.Children.Add(new TextBlock { Text = $"{r.Tool}: {said}", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
						Foreground = Brushes2.Get(r.Newer ? "Warn" : r.Problem != null ? "Muted" : "Text") });
					updRows.Children.Add(row);
				}
				updNote.Text = ToolUpdates.Summary(res);
			} catch (Exception e) {
				updNote.Text = "Could not check: " + e.Message;
			} finally {
				updCheck.IsEnabled = true;
			}
		};
		stack.Children.Add(updNote);
		stack.Children.Add(updCheck);
		stack.Children.Add(updRows);

		Head("A STEP");
		var steps = (work["steps"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
		var pick = new ComboBox { ItemsSource = steps.Select(s => StepCatalog.Name(Setup.Str(s, "step") ?? "")).ToList(), Margin = new Thickness(0, 0, 0, 8) };
		System.Windows.Automation.AutomationProperties.SetName(pick, "Step to edit");
		var stepHost = new StackPanel();
		var stepApply = new List<Func<string?>>();
		var shown = -1;
		pick.SelectionChanged += (_, _) => {
			// the step on screen keeps its fields until every value reads (a wrong one stays where it can be fixed)
			var wrong = stepApply.Select(f => f()).OfType<string>().ToList();
			if (wrong.Count > 0) {
				errors.Text = "Fix this step first:\n" + string.Join("\n", wrong);
				pick.SelectedIndex = shown;
				return;
			}
			if (pick.SelectedIndex == shown) {
				return;
			}
			shown = pick.SelectedIndex;
			// a search in force is applied again once the new step's fields are built (else they stay hidden under the old filter)
			pick.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => {
				if (find.Text.Trim() is { Length: > 0 } q) {
					var hits = Filter(stack, q);
					findHint.Text = hits == 0 ? "No setting has that word." : $"{hits} setting(s) found.";
				}
			});
			stepHost.Children.Clear();
			stepApply.Clear();
			if (pick.SelectedIndex < 0) {
				return;
			}
			var o = steps[pick.SelectedIndex]["settings"] as JsonObject ?? (JsonObject)(steps[pick.SelectedIndex]["settings"] = new JsonObject());
			var aboutKey = Setup.Str(steps[pick.SelectedIndex], "step") ?? "";
			var aboutRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
			// his checklist 475: help on every step - the tool's own page (its author's instructions), opened in the browser
			if (HelpPage(aboutKey) is { } help) {
				var page = new Button { Content = "Its own page", Style = (Style)a_w.FindResource("Ghost"), Margin = new Thickness(10, 0, 0, 0), ToolTip = help.Url,
					VerticalAlignment = VerticalAlignment.Top };
				System.Windows.Automation.AutomationProperties.SetHelpText(page, help.What);
				page.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(help.Url) { UseShellExecute = true });
				DockPanel.SetDock(page, Dock.Right);
				aboutRow.Children.Add(page);
			}
			aboutRow.Children.Add(new TextBlock { Text = StepCatalog.Info(aboutKey).About, Style = (Style)a_w.FindResource("MutedText") });
			stepHost.Children.Add(aboutRow);
			foreach (var (key, val) in o.ToList()) {
				if (key.StartsWith('_')) {
					stepHost.Children.Add(new TextBlock { Text = val?.ToString() ?? "", Style = (Style)a_w.FindResource("MutedText"), Margin = new Thickness(0, 0, 0, 8) });
					continue;
				}
				Field(stepHost, stepApply, Label(key), o, key, val?.GetValueKind() ?? JsonValueKind.String);
			}
			var stepKey = Setup.Str(steps[pick.SelectedIndex], "step") ?? "";
			// his ask 2026-10-27: the tool's settings from its OWN window, kept for every run (never a copy of its window here)
			if (ToolSettings.CanLearn(new StepSettings(o)) && a_vm.Setup is { } setupNow) {
				var learned = ToolSettings.Load(setupNow, stepKey);
				var name = StepCatalog.Name(stepKey);
				var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)a_w.FindResource("MutedText"), Margin = new Thickness(0, 4, 0, 6),
					Text = learned == null ? "Its settings are not learned yet: it runs with whatever its own window last saved." :
						$"Its settings were learned {learned.At.ToLocalTime().ToString("MMM d HH:mm", InvariantCulture)} ({learned.Files.Count} file(s)) - every run uses them." };
				var learn = new Button { Content = "Learn its settings (opens its own window)", Style = (Style)a_w.FindResource("Ghost"),
					HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
				learn.Click += async (_, _) => {
					if (!Ask.Show(a_w, "Learn its settings", $"{name} opens through Mod Organizer (Mod Organizer is closed first if it is open). Set it up the way every run " +
					        "should use it, save, and close it - its settings are then kept with this setup. To change them later, do this again.", "Open it", "Not now")) {
						return;
					}
					learn.IsEnabled = false;
					status.Text = $"{name} is open - set it up, save, and close it ...";
					try {
						var l = await Task.Run(() => ToolSettings.Learn(setupNow, stepKey, RunLog.Null, CancellationToken.None));
						status.Text = $"Learned: {l.Files.Count} settings file(s) ({string.Join(", ", l.Files.Select(f => Path.GetFileName(f.Live)))}) - every run uses them.";
					} catch (Exception e) {
						status.Text = "Not learned: " + e.Message;
					} finally {
						learn.IsEnabled = true;
					}
				};
				stepHost.Children.Add(status);
				stepHost.Children.Add(learn);
			}
			// his lesson 2026-10-27 (checklist 271): LOD settings depend on the mods it is built from - their own pages say which
			if (LodGuide.Applies(stepKey)) {
				LodBlock(a_w, a_vm.Setup!, stepKey, stepHost);
			}
			foreach (var (key, kind, def) in Known.GetValueOrDefault(stepKey, []).Append((Key: "deadline_min", Kind: JsonValueKind.Number, Default: "the step's own"))
				         .Where(k => !o.ContainsKey(k.Key))) {
				Field(stepHost, stepApply, $"{Label(key)} (default {def})", o, key, kind);
			}
		};
		stack.Children.Add(pick);
		stack.Children.Add(stepHost);
		if (a_vm.SelectedStep != null) {
			pick.SelectedIndex = steps.FindIndex(s => Setup.Str(s, "step") == a_vm.SelectedStep.Key);
		}

		var save = new Button { Content = "_Save", Style = (Style)a_w.FindResource("Primary"), Margin = new Thickness(0, 0, 10, 0) };
		var undo = new Button { Content = "_Undo changes", Style = (Style)a_w.FindResource("Ghost") };
		var folders = new Button { Content = "Open the data folder", Style = (Style)a_w.FindResource("Ghost"), Margin = new Thickness(10, 0, 0, 0) };
		save.Click += (_, _) => {
			if (a_vm.SetupInUse) {
				errors.Text = "Not saved: a run is using this setup right now - save when it is done (what you typed stays here).";
				return;
			}
			var problems = apply.Concat(stepApply).Select(f => f()).OfType<string>().ToList();
			if (problems.Count > 0) {
				errors.Text = "Not saved:\n" + string.Join("\n", problems);
				return;
			}
			var trial = Setup.FromJson((JsonObject)work.DeepClone(), a_vm.Setup!.File);
			var p = trial.Problems();
			if (p.Count > 0) {
				errors.Text = "Not saved: " + string.Join("; ", p);
				return;
			}
			foreach (var k in a_vm.Setup.Root.Select(x => x.Key).ToList()) {
				a_vm.Setup.Root.Remove(k);
			}
			foreach (var k in work.Select(x => x.Key).ToList()) {
				a_vm.Setup.Root[k] = work[k]?.DeepClone();
			}
			if (!a_vm.SaveSetup()) {
				// memory back to what is on disk; the drawer keeps what was typed for another try
				a_vm.LoadSteps();
				errors.Text = "Not saved: " + a_vm.Message;
				return;
			}
			a_vm.LoadSteps();
			errors.Text = "";
			a_w.ShowPanel("settings", true);
		};
		undo.Click += (_, _) => a_w.ShowPanel("settings", true);
		folders.Click += (_, _) => MainViewModel.OpenFolder(AppPaths.Data);
		// his checklist 484: a clean removal of everything the program keeps (to the Recycle Bin, never erased)
		var remove = new Button { Content = "Remove its data ...", Style = (Style)a_w.FindResource("Danger"), Margin = new Thickness(10, 0, 0, 0),
			ToolTip = "Sends the program's own data and its run backups to the Recycle Bin - the mods the tools made stay in Mod Organizer" };
		remove.Click += async (_, _) => {
			remove.IsEnabled = false;
			try {
				await RemoveData(a_w, errors);
			} finally {
				remove.IsEnabled = true;
			}
		};
		var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
		bar.Children.Add(save);
		bar.Children.Add(undo);
		bar.Children.Add(folders);
		bar.Children.Add(remove);
		var where = new TextBlock { Text = "Data folder (setups, runs, backups): " + AppPaths.Data, Style = (Style)a_w.FindResource("MutedText"),
			TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
		var dock = new DockPanel();
		DockPanel.SetDock(where, Dock.Bottom);
		DockPanel.SetDock(bar, Dock.Bottom);
		dock.Children.Add(where);
		DockPanel.SetDock(errors, Dock.Bottom);
		dock.Children.Add(bar);
		dock.Children.Add(errors);
		DockPanel.SetDock(top, Dock.Top);
		dock.Children.Add(top);
		dock.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) });
		return dock;
	}

	const string HeadTag = "head";
	static TextBox? s_find;

	// the window's Ctrl+F while Settings is open
	public static void FocusFind()
	{
		if (s_find is { IsVisible: true } f) {
			f.Focus();
			f.SelectAll();
		}
	}

	// every row whose words hold the query stays, the others fold away; a section's heading stays while it has a row left (or
	// matches itself, then its whole section stays). Returns how many rows are shown
	static int Filter(Panel a_stack, string a_q)
	{
		bool Match(string? a_t) => (a_t ?? "").Contains(a_q, StringComparison.OrdinalIgnoreCase);
		var shown = 0;
		TextBlock? head = null;
		var headHit = false;
		var rowsSince = 0;
		void CloseSection()
		{
			if (head != null) {
				head.Visibility = a_q == "" || headHit || rowsSince > 0 ? Visibility.Visible : Visibility.Collapsed;
			}
		}
		foreach (var e in a_stack.Children.OfType<FrameworkElement>()) {
			if (e is TextBlock { Tag: HeadTag } h) {
				CloseSection();
				head = h;
				headHit = a_q != "" && Match(h.Text);
				rowsSince = 0;
				continue;
			}
			var hit = a_q == "" || headHit || Words(e).Any(Match);
			e.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
			if (hit) {
				++rowsSince;
				++shown;
			}
		}
		CloseSection();
		return shown;
	}

	// the words a row shows: its labels, notes, button texts, a list's items, its tip
	static IEnumerable<string> Words(DependencyObject a_e)
	{
		if (a_e is FrameworkElement { ToolTip: string tip }) {
			yield return tip;
		}
		switch (a_e) {
		case TextBlock t:
			yield return t.Text;
			break;
		case ContentControl { Content: string c }:
			yield return c;
			break;
		case ItemsControl items:
			foreach (var i in items.Items.OfType<object>()) {
				yield return i.ToString() ?? "";
			}
			break;
		}
		if (a_e is Panel p) {
			foreach (var c in p.Children.OfType<DependencyObject>()) {
				foreach (var w in Words(c)) {
					yield return w;
				}
			}
		}
	}

	// everything the program keeps to the Recycle Bin, after a plain list of what goes and what stays; the window closes after,
	// since the folder it writes into is gone. Refused while a run goes or one still needs its profile put back.
	// the slow parts (listing every backup folder, recycling) run off the window's thread; the button stays off meanwhile
	static async Task RemoveData(MainWindow a_w, TextBlock a_errors)
	{
		if (HasChanges && !Ask.Show(a_w, "Settings not saved", "You changed settings and did not press Save. Go on without saving them?", "Go on", "Keep editing")) {
			return;
		}
		var keepSetups = Ask.Show(a_w, "Keep your setups?", "Your setups (which steps run, in which order, with which settings) can stay, so a new copy of the program " +
			"picks them up again. Everything else the program keeps goes.", "Keep my setups", "Remove them too");
		var plan = await Task.Run(() => DataRemoval.Make(AppPaths.Data, keepSetups));
		if (plan.Refused != null) {
			a_errors.Text = "Not removed: " + plan.Refused + ".";
			return;
		}
		var lines = plan.Remove.Select(p => "• " + p).ToList();
		var text = "These go to the Recycle Bin (you can bring them back from there):\n" + string.Join("\n", lines) +
			(plan.Kept.Count > 0 ? "\n\nThese stay:\n" + string.Join("\n", plan.Kept.Select(p => "• " + p)) : "") +
			"\n\nThe mods the tools made (the outputs) stay in Mod Organizer - they are part of your load order; remove them there if you want." +
			"\n\nThe window closes afterwards. The program file itself stays where it is - delete it yourself when you are done.";
		if (!Ask.Show(a_w, "Remove the program's data", text, "Send them to the Recycle Bin", "Keep everything")) {
			return;
		}
		List<string> failed;
		try {
			failed = await Task.Run(() => DataRemoval.Carry(plan));
		} catch (InvalidOperationException e) {
			a_errors.Text = "Not removed: " + e.Message + ".";
			return;
		}
		if (failed.Count > 0) {
			a_errors.Text = "Some of it could not go to the Recycle Bin:\n" + string.Join("\n", failed);
			return;
		}
		HasChanges = false;
		Ask.Tell(a_w, "Removed", "The program's data is in the Recycle Bin. The window closes now.", "Close");
		Application.Current.Shutdown();
	}

	static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	// one setting: its editor by its kind; the returned function writes it back (or says what is wrong)
	static void Field(Panel a_host, List<Func<string?>> a_apply, string a_label, JsonObject a_obj, string a_key, JsonValueKind a_kind = JsonValueKind.String)
	{
		var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		var label = new TextBlock { Text = a_label, Foreground = Brushes2.Get("Gold"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
		row.Children.Add(label);
		FrameworkElement edit;
		var cur = a_obj[a_key];
		var kind = cur?.GetValueKind() ?? a_kind;
		if (kind is JsonValueKind.True or JsonValueKind.False) {
			var box = new CheckBox { IsChecked = cur?.GetValue<bool>() ?? false, VerticalAlignment = VerticalAlignment.Center };
			box.Click += (_, _) => Changed();
			a_apply.Add(() => {
				a_obj[a_key] = box.IsChecked == true;
				return null;
			});
			edit = box;
		} else if (kind == JsonValueKind.Number) {
			var t = new TextBox { Text = cur?.ToString() ?? "" };
			a_apply.Add(() => {
				if (t.Text.Trim() == "") {
					a_obj.Remove(a_key);
					return null;
				}
				if (!double.TryParse(t.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, InvariantCulture, out var d) || d < 0) {
					return $"{a_label}: needs a number, 0 or more";
				}
				if (d < 1 && a_key.EndsWith("_min", StringComparison.Ordinal)) {
					return $"{a_label}: needs 1 minute or more";
				}
				a_obj[a_key] = d == Math.Floor(d) ? JsonValue.Create((long)d) : JsonValue.Create(d);
				return null;
			});
			t.TextChanged += (_, _) => Changed();
			edit = t;
		} else if (kind == JsonValueKind.Array && (cur == null || (cur as JsonArray)!.All(n => n is JsonValue))) {
			var t = new TextBox { Text = string.Join("\n", (cur as JsonArray ?? []).Select(n => n?.ToString())), AcceptsReturn = true, MinHeight = 54, TextWrapping = TextWrapping.NoWrap };
			a_apply.Add(() => {
				a_obj[a_key] = new JsonArray(t.Text.Replace("\r", "").Split('\n').Select(s => s.Trim()).Where(s => s != "").Select(s => (JsonNode)JsonValue.Create(s)).ToArray());
				return null;
			});
			t.TextChanged += (_, _) => Changed();
			edit = t;
		} else if (kind is JsonValueKind.Object or JsonValueKind.Array) {
			var t = new TextBox { Text = cur?.ToJsonString(Pretty) ?? "", AcceptsReturn = true, MinHeight = 80, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = (double)Application.Current.FindResource("SizeLog") };
			a_apply.Add(() => {
				try {
					a_obj[a_key] = JsonNode.Parse(t.Text);
					return null;
				} catch (JsonException e) {
					return $"{a_label}: not valid ({e.Message})";
				}
			});
			t.TextChanged += (_, _) => Changed();
			edit = t;
		} else {
			var t = new TextBox { Text = cur?.ToString() ?? "" };
			a_apply.Add(() => {
				if (a_key is "output_mod" or "then_move_to_mod" or "executable" && t.Text.Trim() == "") {
					return $"{a_label}: cannot be empty";
				}
				if (t.Text == "" && cur == null) {
					return null;
				}
				a_obj[a_key] = t.Text;
				return null;
			});
			t.TextChanged += (_, _) => Changed();
			edit = t;
		}
		System.Windows.Automation.AutomationProperties.SetName(edit, a_label);
		Grid.SetColumn(edit, 1);
		row.Children.Add(edit);
		a_host.Children.Add(row);
	}

	static void Choice(Panel a_host, List<Func<string?>> a_apply, string a_label, JsonObject a_obj, string a_key, (string Value, string Text)[] a_options)
	{
		var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		row.Children.Add(new TextBlock { Text = a_label, Foreground = Brushes2.Get("Gold"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
		var box = new ComboBox { ItemsSource = a_options.Select(o => o.Text).ToList() };
		var cur = Setup.Str(a_obj, a_key);
		box.SelectedIndex = Math.Max(0, Array.FindIndex(a_options, o => string.Equals(o.Value, cur, StringComparison.OrdinalIgnoreCase)));
		box.SelectionChanged += (_, _) => Changed();
		System.Windows.Automation.AutomationProperties.SetName(box, a_label);
		a_apply.Add(() => {
			a_obj[a_key] = a_options[Math.Max(0, box.SelectedIndex)].Value;
			return null;
		});
		Grid.SetColumn(box, 1);
		row.Children.Add(box);
		a_host.Children.Add(row);
	}
}
