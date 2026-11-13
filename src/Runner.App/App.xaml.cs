using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ToolRunner.App;

//   "Tool Runner.exe"                       the window
//   "Tool Runner.exe" run | check | ...     the command line (Cli.cs) - no window
//   "Tool Runner.exe" --snapshot <folder>   the window drawn off screen into <folder>\*.png (nothing is shown)
public partial class App : Application
{
	// started for the command line or the off-screen pictures (no window of the player's): nothing goes online then
	public static bool Headless { get; internal set; }

	static bool s_logging;

	// what no window can show is written down: an error on a thread of its own, and a background task that failed with nobody
	// waiting on it (a start, a check, a look at Mod Organizer) instead of vanishing without a trace
	public static void LogCrashes()
	{
		if (s_logging) {
			return;
		}
		s_logging = true;
		AppDomain.CurrentDomain.UnhandledException += (_, e) => {
			if (e.ExceptionObject is Exception ex) {
				LogCrash(ex);
			}
		};
		TaskScheduler.UnobservedTaskException += (_, e) => {
			LogCrash(e.Exception);
			e.SetObserved();
		};
	}

	// the crash notes never grow without end: past this many bytes the older ones move to "crash (older).txt"
	public const long CrashNotesMost = 512 * 1024;

	// anything unexpected: written to the data folder's "crash.txt" and said in a message, never Windows' crash box
	public static void LogCrash(Exception a_e)
	{
		try {
			Files.AppendCapped(Path.Combine(AppPaths.Data, "crash.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {a_e}\r\n\r\n", CrashNotesMost);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	protected override void OnStartup(StartupEventArgs a_e)
	{
		base.OnStartup(a_e);
		// with no window of its own (the off-screen pictures; a command never comes this far - Program.Main) an error ends the
		// program with exit code 1: a box there would sit on the player's screen with nothing behind it, and the program would
		// wait on it forever
		var headless = a_e.Args is ["--snapshot" or "--page", _];
		Headless = headless;
		DispatcherUnhandledException += (_, e) => {
			LogCrash(e.Exception);
			e.Handled = true;
			if (headless) {
				// said where the command was started too (exit 1 alone reads like a plain refusal); --snapshot / --page never
				// attached to that console, a command already did (a second attach changes nothing)
				ConsoleAttach.ToParent();
				try {
					Console.Error.WriteLine("Something went wrong: " + e.Exception.Message + " (written in crash.txt in the data folder)");
				} catch (IOException) {
				}
				Shutdown(1);
				return;
			}
			var text = "Something went wrong: " + e.Exception.Message + "\n\nIt is written in crash.txt in the data folder. A run that is going is not affected.";
			// the themed box over the window (every pop-up wears the theme); Windows' own box only when the window cannot carry one
			try {
				if (MainWindow is { IsLoaded: true } owner && Ask.Show(owner, "Something went wrong", text, "Open the data folder", "Close")) {
					System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.Data) { UseShellExecute = true });
				} else if (MainWindow is not { IsLoaded: true }) {
					MessageBox.Show(text, Brand.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
				}
			} catch (Exception e2) when (e2 is InvalidOperationException or System.ComponentModel.Win32Exception) {
				MessageBox.Show(text, Brand.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
			}
		};
		LogCrashes();
		var args = a_e.Args;
		if (args.Length == 2 && args[0] == "--snapshot") {
			Shutdown(Snapshot(args[1]));
			return;
		}
		if (args.Length == 2 && args[0] == "--page") {
			Shutdown(Page(args[1]));
			return;
		}
		// what moves in the window (the bar of a step whose share is not known) is drawn thirty times a second, not sixty: it
		// looks the same and costs half - set before the first animation exists
		System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(System.Windows.Media.Animation.Timeline), new FrameworkPropertyMetadata(30));
		// the theme picked last time, before the window is drawn
		Theme.ApplySaved();
		WindowFrame.PaintEveryWindowOnOpen();
		// his checklist 476
		var languageProblem = WindowWords.LoadPicked();
		WindowWords.OnEveryWindow();
		var w = new MainWindow();
		if (Theme.Icon is { } icon) {
			w.Icon = icon;
		}
		MainWindow = w;
		w.Show();
		if (languageProblem != "") {
			w.Say(languageProblem);
		}
		// One full collection once the window is up and has settled. What starting it took (the markup read, the theme's first
		// pass, the first look at every run record) is given back then; left alone it is held until memory runs short, which
		// for an idle window is never (measured 2026-11-10: about 6 MB less private memory for as long as the window stays open)
		_ = Task.Run(async () => {
			await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
			System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
			GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
		});
		// his checklist 468: started from Mod Organizer's Tools menu, on the profile it shows
		if (args.Length == 6 && args[0] == "--for-profile" && args[2] == "--mo2" && args[4] == "--base") {
			w.ShowProfile(args[3], args[5], args[1]);
		}
		// runs at a set time are gone (his checklist answer 451): a task an older version made is cleared away, off the
		// window's thread (a failure leaves it; it can start nothing that is not there)
		_ = Task.Run(() => {
			try {
				// said once with every name: a message line shows only its last message
				if (Schedule.RemoveLeftoversOnce(AppPaths.Data) is { Count: > 0 } left) {
					w.Dispatcher.BeginInvoke(() => w.Say("Old timed runs' tasks could not be removed - delete them in Windows' Task Scheduler: " + string.Join(", ", left)));
				}
			} catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) {
			}
		});
	}

	static void Pump(Window a_w, int a_ms)
	{
		var until = DateTime.Now.AddMilliseconds(a_ms);
		while (DateTime.Now < until) {
			a_w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
			Thread.Sleep(30);
		}
	}

	// a window's content drawn into <a_dir>\<a_name>.png (never shown on screen)
	static void Picture(Window a_win, string a_dir, string a_name)
	{
		a_win.UpdateLayout();
		var root = (FrameworkElement)a_win.Content;
		var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
		bmp.Render(root);
		var png = new PngBitmapEncoder();
		png.Frames.Add(BitmapFrame.Create(bmp));
		using var f = File.Create(Path.Combine(a_dir, a_name + ".png"));
		png.Save(f);
	}

	static Window ShowBox(MainWindow a_w, RunPreview a_preview)
	{
		var box = Ask.Make(a_w, "What this run will do", string.Join("\n", a_preview.Lines(k => MainViewModel.DemoMinutes.TryGetValue(k, out var m) ? TimeSpan.FromMinutes(m) : null)),
			"Start the run", "Not now", _ => { });
		box.ShowActivated = false;
		box.WindowStartupLocation = WindowStartupLocation.Manual;
		box.Left = -30000;
		box.Top = -30000;
		box.Show();
		Pump(box, 600);
		return box;
	}

	// the mod page's pictures, drawn off screen for the made-up setup the data folder holds: the steps, a run going (and its
	// frames, a minute apart, for a GIF), a guided step, past runs with a log, what MO2 changed, the preview
	static int Page(string a_dir)
	{
		Directory.CreateDirectory(a_dir);
		Theme.Apply(Theme.Default, false);
		var w = new MainWindow { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000 };
		w.Show();
		Pump(w, 2500);
		// the pictures show a setup's steps: with none in the data folder they would be empty
		if ((w.DataContext as MainViewModel)?.SelectedSetup == null) {
			w.Close();
			Console.Error.WriteLine("no setup in the data folder - make one (New setup), then draw the pictures again");
			return 1;
		}
		var name = (w.DataContext as MainViewModel)!.SelectedSetup!.Name;
		w.DemoReady();
		Pump(w, 600);
		Picture(w, a_dir, "1 the steps");
		for (var frame = 0; frame < 10; ++frame) {
			w.Demo(false, frame);
			Pump(w, 400);
			Picture(w, a_dir, $"run frame {frame:00}");
		}
		w.Demo(false, 2);
		Pump(w, 400);
		Picture(w, a_dir, "2 a run going");
		w.Demo(true);
		Pump(w, 600);
		Picture(w, a_dir, "3 a guided step");
		// past runs: their logs written into a scratch folder for the history view to read, removed after
		var runs = Path.Combine(Path.GetTempPath(), $"Tool Runner page runs {Guid.NewGuid():N}");
		try {
			var steps = StepCatalog.DefaultOrder.Where(k => k is not ("wryebash" or "anvillod" or "easynpc" or "upf" or "smash" or "reqtificator")).ToList();
			var full = new List<string> { "21:02:10  Tool Runner - " + name + " (profile " + name + "), " + steps.Count + " steps",
				"21:02:10  every output switched off - each back on once its tool rebuilt it" };
			var at = new DateTime(2026, 10, 27, 21, 2, 22);
			foreach (var k in steps) {
				var m = k == "shaders" ? 0 : MainViewModel.DemoMinutes.GetValueOrDefault(k, 5);
				if (k == "shaders") {
					full.Add($"{at:HH:mm:ss}  === Shader caches === skipped: the shader mods did not change");
					continue;
				}
				full.Add($"{at:HH:mm:ss}  === {StepCatalog.Name(k)} ===");
				at = at.AddMinutes(m);
				full.Add($"{at:HH:mm:ss}  === {StepCatalog.Name(k)} done in {m.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} min ===");
			}
			full.Add($"{at:HH:mm:ss}  ALL DONE: finished: {steps.Count} step(s) in 101 min");
			var rows = new List<HistoryRow>();
			foreach (var (when, took, verdict, ok, log) in new[] {
				("Tue Oct 27  21:02", "1h 41m", $"finished: {steps.Count} step(s) in 101 min", true, full),
				("Mon Oct 26  23:15", "30m", "finished: 3 step(s) in 30 min", true,
					new List<string> { "23:15:02  Tool Runner - " + name + " (profile " + name + "), from TexGen: 3 steps", "23:45:10  ALL DONE: finished: 3 step(s) in 30 min" }),
				("Sun Oct 25  22:40", "48m", "stopped at DynDOLOD - the profile is back as it was, with this run's finished outputs on; carry on from DynDOLOD", false,
					new List<string> { "22:40:11  Tool Runner - " + name + " (profile " + name + "), " + steps.Count + " steps", "23:28:30  stopped on request" }),
			}) {
				var folder = Path.Combine(runs, rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
				Directory.CreateDirectory(folder);
				File.WriteAllText(Path.Combine(folder, "log.txt"), string.Join("\n", log) + "\n");
				rows.Add(new HistoryRow(when, name, took, verdict, ok, folder));
			}
			w.DemoReady();
			w.DemoHistory(rows);
			w.ShowPanel("history", true);
			Pump(w, 800);
			Picture(w, a_dir, "4 past runs");
		} finally {
			try {
				Directory.Delete(runs, true);
			} catch (IOException) {
			}
		}
		var t = new DateTime(2026, 10, 27, 22, 43, 0);
		var run = $"the run of Oct 27 21:02 ({name})";
		w.ShowChangesDemo([
			new(t.AddHours(-5), Mo2Changes.Outside, "Wildlands Renewal", "added to Mod Organizer", []),
			new(t.AddHours(-5), Mo2Changes.Outside, "Wildlands Renewal", "added (unticked, under Other)", [name, "Second Profile"]),
			new(t.AddHours(-2), Mo2Changes.Outside, "Majestic Mountains", "moved from Landscape to Other", [name]),
			new(t.AddMinutes(-101), Mo2Changes.Outside, "Open Shaders", "unticked", ["Second Profile"]),
			new(t, run, "Grass Cache - " + name, "added to Mod Organizer", []),
			new(t, run, "Grass Cache - " + name, "added (ticked, under Generated)", [name]),
			new(t, run, "Old Grass Cache", "unticked", [name]),
		]);
		Pump(w, 600);
		Picture(w, a_dir, "5 what MO2 changed");
		w.CloseDrawerNow();
		var demo = new RunPreview();
		demo.Steps.AddRange([new("synthesis", "Synthesis", "Synthesis - " + name, false), new("xlodgen", "xLODGen", "xLODGen Output - " + name, false),
			new("texgen", "TexGen", "TexGen Output - " + name, false), new("dyndolod", "DynDOLOD", "DynDOLOD Output - " + name, false),
			new("playsave", "Save to play", null, false)]);
		demo.OffForTheRun.AddRange([("Synthesis - " + name, "made by Synthesis"), ("xLODGen Output - " + name, "made by xLODGen"),
			("TexGen Output - " + name, "made by TexGen"), ("DynDOLOD Output - " + name, "made by DynDOLOD"),
			("Some Patch", "SomePatch.esp needs Synthesis.esp from Synthesis - " + name)]);
		demo.StartsTheGame.Add("Save to play");
		var box = ShowBox(w, demo);
		Picture(box, a_dir, "6 preview before a run");
		box.Close();
		// no settings picture: its foot names the data folder, wherever this copy of the program sits
		w.Close();
		return 0;
	}

	static int Snapshot(string a_dir)
	{
		Directory.CreateDirectory(a_dir);
		// the built-in look exactly as a start with no theme picked lays it out (its accent brushes last, over the Fluent theme's)
		Theme.Apply(Theme.Default, false);
		var w = new MainWindow { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000 };
		w.Show();
		Pump(w, 2500);
		void Save(string a_name, Window? a_of = null) => Picture(a_of ?? w, a_dir, a_name);
		Save("1 dashboard");
		w.ShowPanel("history");
		Pump(w, 600);
		Save("2 history");
		// History > What MO2 changed, with made-up rows (nothing read from or written to a Mod Organizer)
		var t = new DateTime(2026, 10, 27, 22, 41, 0);
		w.ShowChangesDemo([
			new(t.AddMinutes(-90), Mo2Changes.Outside, "Wildlands Renewal", "added to Mod Organizer", []),
			new(t.AddMinutes(-90), Mo2Changes.Outside, "Wildlands Renewal", "added (unticked, under Other)", ["Bottled Shaders", "Illuminated - ENB"]),
			new(t.AddMinutes(-20), Mo2Changes.Outside, "Open Shaders", "unticked", ["RELight - Spell Addon"]),
			new(t, "the run of Oct 27 22:39 (Bottled Shaders)", "Synthesis - Bottled Shaders", "ticked", ["Bottled Shaders"]),
			new(t, "the run of Oct 27 22:39 (Bottled Shaders)", "PG Patch - Bottled Shaders", "moved from Generated to Patches", ["Bottled Shaders"]),
			new(t, "the run of Oct 27 22:39 (Bottled Shaders)", "Old Grass Cache", "removed from Mod Organizer", []),
		]);
		Pump(w, 600);
		Save("2b what MO2 changed");
		w.ShowPanel("settings");
		Pump(w, 600);
		Save("3 settings");
		// the settings further down: the run options, tool updates
		foreach (var (y, name) in new[] { (420.0, "3b settings, run options"), (900.0, "3c settings, tool updates"), (1500.0, "3c2 settings, further down"), (99999.0, "3c3 settings, the end") }) {
			w.ScrollDrawer(y);
			Pump(w, 400);
			Save(name);
		}
		// the theme gallery: every look as its own card (its themes are read off the window's thread, so it gets a moment)
		w.ShowPanel("themes");
		Pump(w, 1500);
		Save("3d themes");
		w.CloseDrawerNow();
		// the preview before a run (his checklist 270), for a made-up run
		var demo = new RunPreview();
		demo.Steps.AddRange([new("synthesis", "Synthesis", "Synthesis Output", false), new("xlodgen", "xLODGen", "xLODGen Output", false),
			new("dyndolod", "DynDOLOD", "DynDOLOD Output", false), new("playsave", "Save to play", null, false)]);
		demo.OffForTheRun.AddRange([("Synthesis Output", "made by Synthesis"), ("xLODGen Output", "made by xLODGen"), ("DynDOLOD Output", "made by DynDOLOD"),
			("Some Patch", "SomePatch.esp needs Synthesis.esp from Synthesis Output")]);
		demo.StartsTheGame.Add("Save to play");
		var box = Ask.Make(w, "What this run will do", string.Join("\n", demo.Lines(k => k == "dyndolod" ? TimeSpan.FromMinutes(24) : k == "synthesis" ? TimeSpan.FromMinutes(2) : null)),
			"Start the run", "Not now", _ => { });
		box.ShowActivated = false;
		box.WindowStartupLocation = WindowStartupLocation.Manual;
		box.Left = -30000;
		box.Top = -30000;
		box.Show();
		Pump(box, 600);
		Save("7 preview before a run", box);
		box.Close();
		// the smallest window the program allows: nothing may be cut off there
		w.Width = w.MinWidth;
		w.Height = w.MinHeight;
		Pump(w, 600);
		Save("4 smallest window");
		w.Width = 1240;
		w.Height = 800;
		w.Demo(false);
		Pump(w, 600);
		Save("5 a run going");
		w.Demo(true);
		Pump(w, 600);
		Save("6 a guided step");
		// the run waits on the player's own Mod Organizer: its band without a Done button
		w.DemoWaitsOutside();
		Pump(w, 600);
		Save("6b the run waits for you");
		// a run that ended, with its summary: finished, and stopped by a problem (that one in the smallest window too)
		w.DemoEnded(false);
		Pump(w, 600);
		Save("9 a run finished");
		w.DemoEnded(true);
		Pump(w, 600);
		Save("9b a run stopped by a problem");
		w.Width = w.MinWidth;
		w.Height = w.MinHeight;
		Pump(w, 600);
		Save("9d a run stopped, smallest window");
		// the first start, step by step, in the smallest window and at its usual size
		w.DemoFirstStart();
		Pump(w, 600);
		Save("9e first start, smallest window");
		w.Width = 1240;
		w.Height = 800;
		Pump(w, 600);
		Save("9c first start");
		// a dry run that would be refused: each reason with what to do
		var dry = new DryRunReport { SetupFile = "", SetupName = "My Profile", Preview = demo };
		dry.Refusals.Add(("", "the game is running (SkyrimSE.exe) - close it first"));
		dry.Refusals.Add(("dyndolod", "DynDOLOD is not in Expert mode (its settings file, Expert=1) - the runner needs its Expert window to start it"));
		dry.Notes.Add(("", "Mod Organizer is open - the run will close it"));
		var dryBox = Ask.Make(w, "Dry run - nothing is started", string.Join("\n", dry.Lines(k => k == "dyndolod" ? TimeSpan.FromMinutes(24) : null)), "Close", null, _ => { });
		dryBox.ShowActivated = false;
		dryBox.WindowStartupLocation = WindowStartupLocation.Manual;
		dryBox.Left = -30000;
		dryBox.Top = -30000;
		dryBox.Show();
		Pump(dryBox, 600);
		Save("7b a dry run that would be refused", dryBox);
		dryBox.Close();
		w.Demo(true);
		Pump(w, 400);
		// the same run in some of the shipped themes, a dark and a light one among them: what a theme reaches
		foreach (var theme in new[] { Theme.BlackAndGold, "Midnight", "Paper", Theme.ColorBlind }) {
			var themeProblems = Theme.Apply(theme, false);
			Pump(w, 800);
			Save("8 a run going - " + theme + (themeProblems.Count > 0 ? " - with problems" : ""));
		}
		Theme.Apply(Theme.Default, false);
		w.Close();
		return 0;
	}
}
