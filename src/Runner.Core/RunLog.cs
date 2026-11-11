using System.Text;

namespace ToolRunner;

public enum Level
{
	Info,
	Detail,
	Warn,
	Error,
}

// One run's logs. Each line is flushed at once: a run that dies leaves everything up to its death on disk. Two logs (his
// checklist 469): log.txt, the detailed one, every line; steps.txt, the simple one, step by step in plain words - each step
// started and done, a failure with what it means and what to do, the run's verdict.
//
// Writing a line never throws. Log lines stand in front of the work on every recovery path (the put-back says what it does
// before it does it): on a full disk a line that could not be written must not be what keeps the profile from being put back.
// Lines the disk refused are kept, a few hundred at most, and written when it takes them again.
public sealed class RunLog : IDisposable
{
	readonly StreamWriter? _text;
	readonly StreamWriter? _simple;
	StreamWriter? _mirror;
	readonly Lock _gate = new();
	readonly Queue<(bool Simple, string Line)> _owed = new();
	int _lost;
	const int OwedMost = 400;

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
			_text = Open(TextPath, FileMode.Append);
			_simple = Open(Path.Combine(a_folder, SimpleName), FileMode.Append);
		} catch {
			_text?.Dispose();
			throw;
		}
	}

	// the simple log's file name in a run folder
	public const string SimpleName = "steps.txt";

	static StreamWriter Open(string a_path, FileMode a_mode) =>
		new(new FileStream(a_path, a_mode, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };

	// every line also into one fixed file, emptied first (one name for an outside watch, e.g. watch.py --watch); a file that
	// cannot be opened is simply not mirrored
	public void Mirror(string a_path)
	{
		try {
			_mirror = Open(a_path, FileMode.Create);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			_mirror = null;
		}
	}

	// how many lines the disk refused and are still waiting (the offline tests read it)
	public int Owed {
		get {
			lock (_gate) {
				return _owed.Count + _lost;
			}
		}
	}

	// under the gate. What is owed goes first, in order, so the log reads straight once the disk answers again
	void Write(bool a_simple, string a_line)
	{
		if ((a_simple ? _simple : _text) == null) {
			return;
		}
		try {
			while (_owed.TryPeek(out var o)) {
				(o.Simple ? _simple : _text)!.WriteLine(o.Line);
				_owed.Dequeue();
			}
			if (_lost > 0) {
				_text?.WriteLine($"{DateTime.Now.ToString("HH:mm:ss", InvariantCulture)}  ! {_lost} more line(s) could not be written while the disk did not take them");
				_lost = 0;
			}
			(a_simple ? _simple : _text)!.WriteLine(a_line);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) {
			if (_owed.Count < OwedMost) {
				_owed.Enqueue((a_simple, a_line));
			} else {
				++_lost;
			}
		}
	}

	public void Say(string a_text, Level a_level = Level.Info)
	{
		var line = $"{DateTime.Now.ToString("HH:mm:ss", InvariantCulture)}  {(a_level == Level.Detail ? "  " : "")}{(a_level == Level.Warn ? "! " : a_level == Level.Error ? "!!! " : "")}{a_text}";
		lock (_gate) {
			Write(false, line);
			try {
				_mirror?.WriteLine(line);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) {
			}
		}
		Line?.Invoke(a_text, a_level);
	}

	// a line of the simple log (the detailed log has its own wording of the same moment)
	public void Simple(string a_text)
	{
		lock (_gate) {
			Write(true, $"{DateTime.Now.ToString("HH:mm", InvariantCulture)}  {a_text}");
		}
	}

	public void Detail(string a_text) => Say(a_text, Level.Detail);
	public void Warn(string a_text) => Say(a_text, Level.Warn);
	public void Error(string a_text) => Say(a_text, Level.Error);

	public void Dispose()
	{
		lock (_gate) {
			foreach (var w in new[] { _text, _simple, _mirror }) {
				try {
					w?.Dispose();
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					// a last flush the disk refused: nothing more to do with it
				}
			}
		}
	}
}
