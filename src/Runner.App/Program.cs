namespace ToolRunner.App;

// Where the program starts. A command (run, check, stop ...) is carried out before any window machinery exists: a run's engine
// is this same program started with "run", alive for as long as the run, and as a WPF application it held the whole theme and
// every window resource in memory for hours without ever showing a window. The window, and the off-screen pictures, start WPF.
public static class Program
{
	[STAThread]
	public static int Main(string[] a_args)
	{
		if (Cli.Handles(a_args)) {
			App.Headless = true;
			App.LogCrashes();
			// a command's words and a run's logs read the same on every PC: "finished in 17.5 min", never "17,5" on one player's
			// PC and "17.5" on another's (the window keeps the player's own way of writing numbers)
			System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
			System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
			return Cli.Execute(a_args);
		}
		Drawing.Apply();
		var app = new App();
		app.InitializeComponent();
		return app.Run();
	}
}
