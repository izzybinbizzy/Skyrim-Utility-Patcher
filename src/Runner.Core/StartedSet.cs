using System.Collections;

namespace ToolRunner;

// The processes a run started, each with the time it started. Thread-safe: Stop reads it on the command watcher's thread while
// a step adds to it (CodeRabbit on PR #24 - a HashSet copied mid-add throws); enumerating gives a copy. A PID Windows gave to
// another process since is told apart by its start time (SameProcess), so Stop never ends a stranger that got a reused PID.
public sealed class StartedSet : IEnumerable<int>
{
	readonly Lock _gate = new();
	readonly Dictionary<int, DateTime?> _pids = [];

	public void Add(int a_pid)
	{
		var at = Procs.StartTime(a_pid);
		lock (_gate) {
			_pids[a_pid] = at;
		}
	}

	public bool Contains(int a_pid)
	{
		lock (_gate) {
			return _pids.ContainsKey(a_pid);
		}
	}

	public int Count
	{
		get {
			lock (_gate) {
				return _pids.Count;
			}
		}
	}

	// still the very process that was recorded (same PID, same start time); one whose start time could not be read is not
	public bool SameProcess(int a_pid)
	{
		DateTime? at;
		lock (_gate) {
			if (!_pids.TryGetValue(a_pid, out at)) {
				return false;
			}
		}
		return at != null && Procs.StartTime(a_pid) == at;
	}

	// every recorded process that is still that very process - what "the run's own" means for closing a tool or telling the
	// player's programs under Mod Organizer from the run's
	public List<int> Live()
	{
		KeyValuePair<int, DateTime?>[] copy;
		lock (_gate) {
			copy = [.. _pids];
		}
		return copy.Where(p => p.Value != null && Procs.StartTime(p.Key) == p.Value).Select(p => p.Key).ToList();
	}

	public IEnumerator<int> GetEnumerator()
	{
		List<int> copy;
		lock (_gate) {
			copy = [.. _pids.Keys];
		}
		return copy.GetEnumerator();
	}

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
