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
			return Cli.Execute(a_args);
		}
		var app = new App();
		app.InitializeComponent();
		return app.Run();
	}
}
