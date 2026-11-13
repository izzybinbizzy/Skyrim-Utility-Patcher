namespace ToolRunner.App;

// The runs folder watched for the files the window follows - a run's record, the queue, the notice before the PC sleeps, a
// refused start - so a change wakes the window's look at once and its timer can be slow while nothing runs. Where Windows cannot
// watch (a network drive, a folder it may not read) there is no watcher and the timer alone looks: nothing depends on an event.
public sealed class RunsWatch : IDisposable
{
	public static readonly string[] Followed = ["journal.json", "queue.json", "last queue.txt", "power.json", "last refusal.txt"];

	readonly FileSystemWatcher? _watcher;

	// a_changed is called on a pool thread, once per change Windows reports (the caller folds a burst into one look)
	public RunsWatch(string a_runsDir, Action a_changed)
	{
		FileSystemWatcher? w = null;
		try {
			Directory.CreateDirectory(a_runsDir);
			w = new FileSystemWatcher(a_runsDir) {
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
			};
			foreach (var f in Followed) {
				w.Filters.Add(f);
			}
			w.Changed += (_, _) => a_changed();
			w.Created += (_, _) => a_changed();
			w.Deleted += (_, _) => a_changed();
			w.Renamed += (_, _) => a_changed();
			// more changes than its buffer held: what was lost is not known, so look
			w.Error += (_, _) => a_changed();
			w.EnableRaisingEvents = true;
			_watcher = w;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException) {
			w?.Dispose();
		}
	}

	public bool Watching => _watcher != null;

	public void Dispose() => _watcher?.Dispose();
}
