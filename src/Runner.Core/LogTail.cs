namespace ToolRunner;

// Follows a growing log for the window: each look hands over only the new whole lines (a letter is never cut in two), a big old
// log only its last part. A file that was replaced starts over - also one that has already grown past where the last look
// stopped, told apart by its first bytes (Windows may give a re-made file the old one's creation time, so that is no sign).
public sealed class LogTail
{
	public const int Window = 150_000;
	const int HeadSize = 128;

	string? _path;
	long _pos;
	byte[] _head = [];

	// the next look at a log starts from its beginning (another kind of log asked for); no log = nothing to empty
	public void Restart() => Forget(null);

	// the new text since the last look (null = none) and whether the box must be emptied first
	public (string? Text, bool Reset) Read(string? a_path)
	{
		var reset = false;
		if (!string.Equals(a_path, _path, StringComparison.Ordinal)) {
			reset = true;
			Forget(a_path);
		}
		if (a_path == null || !File.Exists(a_path)) {
			return (null, reset);
		}
		try {
			using var fs = new FileStream(a_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			if (fs.Length < _pos || !SameHead(fs)) {
				Forget(a_path);
				reset = true;
			}
			if (fs.Length == _pos) {
				return (null, reset);
			}
			if (_head.Length == 0) {
				_head = ReadAt(fs, 0, (int)Math.Min(HeadSize, fs.Length));
			}
			// read from one byte before the window, so a window that starts right at a line's start keeps that line (the byte
			// before it is its newline)
			var skipped = fs.Length - Window > _pos;
			var readFrom = skipped ? fs.Length - Window - 1 : _pos;
			var bytes = ReadAt(fs, readFrom, (int)(fs.Length - readFrom));
			var from = skipped ? Array.IndexOf(bytes, (byte)'\n') + 1 : 0;
			// a line still being written (no newline yet) waits for the next look
			var end = Array.LastIndexOf(bytes, (byte)'\n') + 1;
			if (end == 0 || end <= from) {
				return (null, reset);
			}
			_pos = readFrom + end;
			return (System.Text.Encoding.UTF8.GetString(bytes, from, end - from), reset);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return (null, reset);
		}
	}

	void Forget(string? a_path)
	{
		_path = a_path;
		_pos = 0;
		_head = [];
	}

	bool SameHead(FileStream a_fs) =>
		_head.Length == 0 || (a_fs.Length >= _head.Length && ReadAt(a_fs, 0, _head.Length).AsSpan().SequenceEqual(_head));

	static byte[] ReadAt(FileStream a_fs, long a_at, int a_count)
	{
		a_fs.Seek(a_at, SeekOrigin.Begin);
		var bytes = new byte[a_count];
		a_fs.ReadExactly(bytes);
		return bytes;
	}
}
