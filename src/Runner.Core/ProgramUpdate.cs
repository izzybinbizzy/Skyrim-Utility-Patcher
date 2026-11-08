using System.Text.Json;

namespace ToolRunner;

// "A new version is out" (his checklist 471): at most once a day the program reads the newest version of ITSELF where it is
// published - its Nexus page once it has one, until then the repository's latest GitHub release - and the window says so with
// a link to the download page. Nothing is downloaded or installed: the download stays the player's. The player can turn the
// check off, and a version they set aside ("Not now") is not mentioned again; the next newer one is.
public static class ProgramUpdate
{
	// its Nexus mod number once the page is published (null until then)
	public static readonly string? NexusId;

	public const string GitHubRepo = "izzybinbizzy/Skyrim-Utility-Patcher";

	public static string Page => NexusId != null ? $"https://www.nexusmods.com/skyrimspecialedition/mods/{NexusId}?tab=files" : $"https://github.com/{GitHubRepo}/releases/latest";

	public static readonly TimeSpan Every = TimeSpan.FromDays(1);

	// what the last look found, whether the player turned the look off, and the version they set aside
	public sealed record State(DateTime At, string Newest, bool Off = false, string? SetAside = null);

	public static string File(string a_dataDir) => Path.Combine(a_dataDir, "new version.json");

	// the offline tests answer for the web
	public static Func<CancellationToken, Task<string>>? Probe { get; set; }

	public static State? Read(string a_dataDir)
	{
		try {
			return System.IO.File.Exists(File(a_dataDir)) ? JsonSerializer.Deserialize<State>(Files.ReadShared(File(a_dataDir))) : null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			return null;
		}
	}

	static void Write(string a_dataDir, State a_state)
	{
		try {
			Directory.CreateDirectory(a_dataDir);
			Files.WriteAtomic(File(a_dataDir), JsonSerializer.Serialize(a_state));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// not kept: the next start looks again
		}
	}

	public static bool IsOff(string a_dataDir) => Read(a_dataDir)?.Off == true;

	public static void SetOff(string a_dataDir, bool a_off) =>
		Write(a_dataDir, (Read(a_dataDir) ?? new State(DateTime.MinValue, "")) with { Off = a_off });

	public static void SetAside(string a_dataDir, string a_version) =>
		Write(a_dataDir, (Read(a_dataDir) ?? new State(DateTime.MinValue, a_version)) with { SetAside = a_version });

	// the newer version to tell the player about, or null (none, the look is off, it was set aside, or the web did not answer -
	// a failed look is never an error on screen and is tried again on the next start)
	public static async Task<string?> Newer(string a_dataDir, string a_current, DateTime a_now, CancellationToken a_ct)
	{
		var state = Read(a_dataDir);
		if (state?.Off == true) {
			return null;
		}
		var newest = state?.Newest ?? "";
		var due = state == null || a_now < state.At || a_now - state.At >= Every;
		if (due) {
			try {
				newest = await (Probe?.Invoke(a_ct) ?? ToolUpdates.Newest(NexusId, GitHubRepo, a_ct)).ConfigureAwait(false);
			} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException or NotSupportedException) {
				// a look that failed (offline, no release yet) counts as the day's look too: asked again tomorrow, never each start
				Write(a_dataDir, (state ?? new State(a_now, "")) with { At = a_now, Newest = "" });
				return null;
			}
			state = (state ?? new State(a_now, newest)) with { At = a_now, Newest = newest };
			Write(a_dataDir, state);
		}
		if (newest == "" || !ToolUpdates.IsNewer(a_current, newest)) {
			return null;
		}
		return state?.SetAside is { } aside && !ToolUpdates.IsNewer(aside, newest) ? null : newest;
	}
}
