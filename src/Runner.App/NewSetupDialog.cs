using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ToolRunner.App;

// A new setup: Mod Organizer's program, its profile, a name - the steps are found from MO2's Executables list.
public static class NewSetupDialog
{
	public static string? Run(Window a_owner)
	{
		string? made = null;
		var w = new Window {
			Owner = a_owner, Title = "New setup", Width = 640, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Background = Brushes2.Get("Panel"), Foreground = Brushes2.Get("Text"), ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
		};
		var stack = new StackPanel { Margin = new Thickness(22) };
		stack.Children.Add(new TextBlock { Text = "NEW SETUP", Style = (Style)a_owner.FindResource("H1"), Margin = new Thickness(0, 0, 0, 6), FontSize = (double)a_owner.FindResource("SizeHeading1") });
		stack.Children.Add(new TextBlock { Text = "One setup = one Mod Organizer profile. The steps are found from MO2's Executables list; you can switch any off afterwards.",
			Style = (Style)a_owner.FindResource("MutedText"), Margin = new Thickness(0, 0, 0, 14) });

		var exe = new TextBox();
		var browse = new Button { Content = "Browse ...", Style = (Style)a_owner.FindResource("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
		// a portable install keeps its instance beside the program; a normal install keeps one or more in %LOCALAPPDATA%\ModOrganizer
		var instance = new ComboBox { DisplayMemberPath = "Key" };
		var profile = new ComboBox();
		var name = new TextBox();
		var pattern = new TextBox { Text = "{output} - {profile}" };
		var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

		void Row(string a_label, FrameworkElement a_edit, FrameworkElement? a_extra = null)
		{
			stack.Children.Add(new TextBlock { Text = a_label, Foreground = Brushes2.Get("Gold"), Margin = new Thickness(0, 8, 0, 4) });
			var d = new DockPanel();
			if (a_extra != null) {
				DockPanel.SetDock(a_extra, Dock.Right);
				d.Children.Add(a_extra);
			}
			d.Children.Add(a_edit);
			System.Windows.Automation.AutomationProperties.SetName(a_edit, a_label);
			stack.Children.Add(d);
		}
		Row("Mod Organizer's program (ModOrganizer.exe)", exe, browse);
		Row("Its instance", instance);
		Row("Profile", profile);
		Row("Setup name", name);
		Row("Output mod names ({output} and {profile} are filled in)", pattern);
		stack.Children.Add(note);

		// his checklist 467: a step exists only for a tool Mod Organizer's list holds, so this comes before the setup is made
		var findTools = new Button { Content = "Find my tools ...", Style = (Style)a_owner.FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0),
			ToolTip = "Says which tools Mod Organizer's list holds, which are on this PC but not listed, and where to get the rest" };
		var toolRows = new StackPanel();
		stack.Children.Add(findTools);
		stack.Children.Add(new ScrollViewer { Content = toolRows, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0) });

		// what was found belongs to the Mod Organizer it was found in: its Add button must not write to one the player has left
		void ForgetTools()
		{
			if (toolRows.Children.Count > 0) {
				toolRows.Children.Clear();
				note.Text = "";
			}
		}
		exe.TextChanged += (_, _) => ForgetTools();
		instance.SelectionChanged += (_, _) => ForgetTools();

		// the instances this program can open: beside it (portable), and the global ones (filled again only for another program,
		// so a picked instance and profile survive a click in and out of the box)
		string? loaded = null;
		void FillInstances()
		{
			loaded = exe.Text.Trim();
			var list = new List<KeyValuePair<string, string?>>();
			var dir = File.Exists(exe.Text.Trim()) ? Path.GetDirectoryName(Path.GetFullPath(exe.Text.Trim())) : null;
			if (dir != null && File.Exists(Path.Combine(dir, "ModOrganizer.ini"))) {
				list.Add(new("Portable (beside Mod Organizer)", null));
			}
			list.AddRange(Mo2Instance.GlobalInstances().Select(d => new KeyValuePair<string, string?>(Path.GetFileName(d), d)));
			instance.ItemsSource = list;
			instance.SelectedIndex = list.Count > 0 ? 0 : -1;
		}

		string? InstanceDir() => instance.SelectedItem is KeyValuePair<string, string?> kv ? kv.Value : null;

		void Fill()
		{
			profile.ItemsSource = null;
			note.Text = "";
			try {
				var mo2 = Mo2Instance.Load(exe.Text.Trim(), InstanceDir());
				var list = mo2.Profiles().ToList();
				profile.ItemsSource = list;
				profile.SelectedItem = list.Contains(mo2.SelectedProfile) ? mo2.SelectedProfile : list.FirstOrDefault();
				note.Text = $"Found {list.Count} profile(s), {mo2.Executables.Count} executable(s); game: {mo2.GameDir}";
				note.Foreground = Brushes2.Get("Muted");
			} catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) {
				note.Text = e.Message;
				note.Foreground = Brushes2.Get("Bad");
			}
		}
		exe.LostFocus += (_, _) => {
			if (exe.Text.Trim() == loaded) {
				return;
			}
			FillInstances();
			Fill();
		};
		instance.SelectionChanged += (_, _) => Fill();
		browse.Click += (_, _) => {
			var d = new OpenFileDialog { Filter = "Mod Organizer|ModOrganizer.exe", Title = "Mod Organizer's program" };
			if (d.ShowDialog(w) == true) {
				exe.Text = d.FileName;
				FillInstances();
				Fill();
			}
		};
		profile.SelectionChanged += (_, _) => {
			if (profile.SelectedItem is string p && (name.Text == "" || profile.ItemsSource is IEnumerable<string> all && all.Contains(name.Text))) {
				name.Text = p;
			}
		};
		// first guess: a Mod Organizer that runs now, else its usual install folder
		var running = Procs.ByImage("ModOrganizer.exe").Select(p => { using (p) { return Procs.ExePath(p); } }).OfType<string>();
		foreach (var guess in running.Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mod Organizer 2", "ModOrganizer.exe"))
			         .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mod Organizer", "ModOrganizer.exe"))) {
			if (File.Exists(guess)) {
				exe.Text = guess;
				FillInstances();
				Fill();
				break;
			}
		}

		findTools.Click += async (_, _) => {
			findTools.IsEnabled = false;
			toolRows.Children.Clear();
			note.Text = "Looking through your mods and the folders beside Mod Organizer ...";
			note.Foreground = Brushes2.Get("Muted");
			try {
				var (exeText, inst) = (exe.Text.Trim(), InstanceDir());
				var mo2 = Mo2Instance.Load(exeText, inst);
				var rows = await Task.Run(() => ToolFinder.Find(mo2));
				if (exe.Text.Trim() != exeText || InstanceDir() != inst) {
					note.Text = "";
					return;
				}
				foreach (var r in rows) {
					var line = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
					if (r.State == ToolFinder.Found.Missing) {
						var page = new Button { Content = "Its page", Style = (Style)a_owner.FindResource("Ghost"), Margin = new Thickness(8, 0, 0, 0), ToolTip = r.Tool.Page };
						page.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.Tool.Page) { UseShellExecute = true });
						DockPanel.SetDock(page, Dock.Right);
						line.Children.Add(page);
					}
					line.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes2.Get(r.State == ToolFinder.Found.Missing ? "Muted" : "Text"), Text = r.State switch {
						ToolFinder.Found.Listed => $"{r.Tool.Name}: in Mod Organizer's list",
						ToolFinder.Found.NotListed => $"{r.Tool.Name}: found, not in the list yet - {r.Binary}",
						_ => $"{r.Tool.Name}: not found - {r.Tool.Where}",
					} });
					toolRows.Children.Add(line);
				}
				var missing = rows.Count(r => r.State == ToolFinder.Found.Missing);
				note.Text = missing == 0 ? "Every tool was found." : $"{missing} tool(s) not found. A run only needs the ones you use: a step is made for each tool the list holds.";
				var found = rows.Count(r => r.State == ToolFinder.Found.NotListed);
				if (found > 0) {
					var add = new Button { Content = $"Add the {found} found tool(s) to Mod Organizer's list", Style = (Style)a_owner.FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
					add.Click += (_, _) => {
						try {
							var added = ToolFinder.AddFound(Mo2Instance.Load(exeText, inst), rows);
							add.IsEnabled = false;
							Fill();
							note.Text = $"Added to Mod Organizer's list: {string.Join(", ", added)}.";
						} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or Mo2BusyException) {
							note.Text = "Not added: " + e.Message;
							note.Foreground = Brushes2.Get("Bad");
						}
					};
					toolRows.Children.Add(add);
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) {
				note.Text = e.Message;
				note.Foreground = Brushes2.Get("Bad");
			} finally {
				findTools.IsEnabled = true;
			}
		};

		var create = new Button { Content = "_Make the setup", Style = (Style)a_owner.FindResource("Primary"), Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
		var cancel = new Button { Content = "Cancel", Style = (Style)a_owner.FindResource("Ghost"), IsCancel = true };
		create.Click += async (_, _) => {
			if (profile.SelectedItem is not string p || name.Text.Trim() == "") {
				note.Text = "Pick Mod Organizer, a profile and a name.";
				note.Foreground = Brushes2.Get("Bad");
				return;
			}
			var file = Path.Combine(AppPaths.Setups, Safe(name.Text.Trim()) + ".json");
			if (File.Exists(file) && !Ask.Show(w, "Replace that setup?", $"A setup called {name.Text.Trim()} exists. Replace it?", "Replace", "Keep it")) {
				return;
			}
			// it looks through every mod (thousands on a big list): off the window's thread, the window says so
			var (exeText, inst, setupName, pat) = (exe.Text.Trim(), InstanceDir(), Safe(name.Text.Trim()), pattern.Text.Trim() == "" ? "{output} - {profile}" : pattern.Text.Trim());
			create.IsEnabled = false;
			note.Text = "Looking through your mods for the tools ...";
			note.Foreground = Brushes2.Get("Muted");
			try {
				var s = await Task.Run(() => SetupMaker.Make(exeText, inst, p, setupName, AppPaths.Data, pat));
				s.Save(file);
				made = file;
				w.Close();
			} catch (Exception e) {
				note.Text = "Not made: " + e.Message;
				note.Foreground = Brushes2.Get("Bad");
			} finally {
				create.IsEnabled = true;
			}
		};
		var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
		bar.Children.Add(create);
		bar.Children.Add(cancel);
		stack.Children.Add(bar);
		w.Content = stack;
		w.ShowDialog();
		return made;
	}

	static string Safe(string a_name) => string.Concat(a_name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
}
