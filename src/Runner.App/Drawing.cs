namespace ToolRunner.App;

// How the window is drawn. Without the graphics card unless the player asks for it: a page of text and a few bars gains nothing
// from one, and WPF's hardware path holds some 24 MB more for as long as the window is open and starts about a third slower
// (measured 2026-11-10 on a busy PC: 77 MB of private memory with it, 53 without). The pick is one word in the data folder's
// "drawing.txt" and applies at the next start.
public static class Drawing
{
	static string PickedFile => Path.Combine(AppPaths.Data, "drawing.txt");

	public static bool Hardware
	{
		get {
			try {
				return File.Exists(PickedFile) && File.ReadAllText(PickedFile).Trim().Equals("hardware", StringComparison.OrdinalIgnoreCase);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				return false;
			}
		}
	}

	public static void Pick(bool a_hardware)
	{
		Directory.CreateDirectory(AppPaths.Data);
		Files.WriteAtomic(PickedFile, a_hardware ? "hardware" : "software");
	}

	// before the first window exists: WPF takes the mode once, for the whole process
	public static void Apply()
	{
		if (!Hardware) {
			System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
		}
	}

	// Windows' own "show animations" switch (Settings > Accessibility > Visual effects): off means nothing in this window moves
	// by itself either
	public static bool Motion => System.Windows.SystemParameters.ClientAreaAnimation;
}
