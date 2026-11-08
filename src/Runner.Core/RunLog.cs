using System.Text;
using System.Text.Json;

namespace ToolRunner;

public enum Level
{
	Info,
	Detail,
	Warn,
	Error,
}

// One run's logs and its events file (for the window). Each line is flushed at once: a run that dies leaves everything up to its
// death on disk. Two logs (his checklist 469): log.txt, the detailed one, every line; steps.txt, the simple one, step by step in
// plain words - each step started and done, a failure with what it means and what to do, the run's verdict.
public sealed class RunLog : IDisposable
{
	readonly StreamWriter? _text;
	readonly StreamWriter? _simple;
	readonly StreamWriter? _events;
	StreamWriter? _mirror;
	readonly Lock _gate = new();

	public string? TextPath { get; }
	public event Action<string, Level>? Line;

	public static RunLog Null { get; } = new();

	RunLog() { }

	public RunLog(string a_folder)
	{
		Directory.CreateDirectory(a_folder);
		TextPath = Path.Combine(a_folder, "log.txt");
		// a writer that opened is released at once when a later one cannot open (no handle left for the finalizer)
		try {
			_text = Open(TextPath);
			_simple = Open(Path.Combine(a_folder, SimpleName));
			_events = Open(Path.Combine(a_folder, "events.jsonl"));
		} catch {
			_simple?.Dispose();
			_text?.Dispose();
			throw;
		}
	}

	// the simple log's file name in a run folder
	public const string SimpleName = "steps.txt";

	static StreamWriter Open(string a_path) =>
		new(new FileStream(a_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };

	// every line also into one fixed file, emptied first (one name for an outside watch, e.g. watch.py --watch)
	public void Mirror(string a_path)
	{
		try {
			_mirror = new StreamWriter(new FileStream(a_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
		} catch (IOException) {
			_mirror = null;
		}
	}

	public void Say(string a_text, Level a_level = Level.Info)
	{
		var line = $"{DateTime.Now:HH:mm:ss}  {(a_level == Level.Detail ? "  " : "")}{(a_level == Level.Warn ? "! " : a_level == Level.Error ? "!!! " : "")}{a_text}";
		lock (_gate) {
			_text?.WriteLine(line);
			_mirror?.WriteLine(line);
		}
		Line?.Invoke(a_text, a_level);
	}

	// a line of the simple log (the detailed log has its own wording of the same moment)
	public void Simple(string a_text)
	{
		lock (_gate) {
			_simple?.WriteLine($"{DateTime.Now:HH:mm}  {a_text}");
		}
	}

	public void Detail(string a_text) => Say(a_text, Level.Detail);
	public void Warn(string a_text) => Say(a_text, Level.Warn);
	public void Error(string a_text) => Say(a_text, Level.Error);

	// a machine-readable event: {"t": "...", "kind": "...", ...}
	public void Event(string a_kind, object? a_data = null)
	{
		var obj = new Dictionary<string, object?> { ["t"] = DateTime.Now.ToString("O"), ["kind"] = a_kind };
		if (a_data != null) {
			foreach (var p in a_data.GetType().GetProperties()) {
				obj[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(a_data);
			}
		}
		var json = JsonSerializer.Serialize(obj);
		lock (_gate) {
			_events?.WriteLine(json);
		}
	}

	public void Dispose()
	{
		_text?.Dispose();
		_simple?.Dispose();
		_events?.Dispose();
		_mirror?.Dispose();
	}
}
