using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToolRunner;

// How big each output was when its step last finished, kept beside the setup (as the tools' versions are). The forecast reads a
// number instead of walking a LOD output of a hundred thousand files before every start. Keyed by the mod's own folder, so two
// Mod Organizers with an output of the same name never share a size.
public static class OutputSizes
{
	static string StoreFile(Setup a_setup) => Path.Combine(Path.GetDirectoryName(a_setup.File) ?? "", "output sizes.json");

	public static Dictionary<string, long> Load(Setup a_setup)
	{
		var known = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		var f = StoreFile(a_setup);
		try {
			if (File.Exists(f) && JsonSerializer.Deserialize<Dictionary<string, long>>(Files.ReadShared(f)) is { } read) {
				foreach (var (k, v) in read) {
					known[k] = v;
				}
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			// a store that does not read is no store: the sizes are measured again
		}
		return known;
	}

	public static void Remember(Setup a_setup, string a_modDir, long a_bytes)
	{
		var known = Load(a_setup);
		known[Path.GetFullPath(a_modDir)] = a_bytes;
		Files.WriteAtomic(StoreFile(a_setup), JsonSerializer.Serialize(known, new JsonSerializerOptions { WriteIndented = true }));
	}
}

// The room a run takes on each disk, worked out before it starts from what it is about to remake: every output is built anew
// while the old one is kept in the run's backup, so a run needs about one more copy of each output it makes - on the disk the
// tool writes to, and on the backup's disk when that is another one. The flat floor a setup carries ("minFreeGb") knew none of
// this: a run that would not fit failed an hour in, at the first tool that ran out of room.
//
// What is certain is told apart from what is only wanted. Needed: without that room a tool fails part way. Wanted: the run's own
// extras - packing the old outputs into one file at the end, the copy of the saves - each of which the run leaves out by itself
// when there is no room for it.
public sealed class DiskForecast
{
	public sealed record Part(string Step, string What, long Bytes, bool Needed);

	public sealed class Disk
	{
		public required string Key { get; init; }
		// the disk as a player names it ("D:\")
		public required string Shown { get; init; }
		// null: Windows would not say
		public long? Free { get; init; }
		public List<Part> Parts { get; } = [];
		public long Needs => Parts.Where(p => p.Needed).Sum(p => p.Bytes);
		public long Wants => Parts.Sum(p => p.Bytes);
	}

	public List<Disk> Disks { get; } = [];
	// outputs never built here: nothing to go by, the setup's floor stands in for them
	public List<string> NotKnown { get; } = [];
	// what could not be measured, in words (a folder that would not list): never a reason to stop
	public List<string> NotMeasured { get; } = [];

	// what the forecast asks the PC (the offline tests answer for it)
	public sealed record Probes(Func<string, string> Volume, Func<string, long?> Free, Func<string, long> SizeOf);

	public static Probes Real { get; set; } = new(Files.VolumeKey, Files.FreeOn, Files.SizeOf);

	// a new build is taken as a tenth bigger than the last one: load orders grow more often than they shrink
	const int GrowthPercent = 10;

	// FillsOnly: the step only fills the folder (Scratch); the mod it ends in is another step's, left alone by this run
	sealed record Output(string Step, string Mod, string? Scratch, bool FillsOnly = false);

	// the mods a step builds, and the folder its tool writes into first when it has one. A step that only fills a folder another
	// step turns into a mod (terrain LOD, read by the road painter) counts through that other step.
	static List<Output> OutputsOf(Setup a_setup, IReadOnlyList<Setup.StepEntry> a_todo)
	{
		var builds = new List<Output>();
		foreach (var step in a_todo) {
			var s = new StepSettings(step.Settings);
			var scratch = s.Str("output_dir") ?? s.Str("reads");
			var mods = new List<string?> { s.OutputMod, s.Str("tool_writes") };
			mods.AddRange((step.Settings["caches"] as JsonArray)?.OfType<JsonObject>().Select(c => Setup.Str(c, "output_mod")) ?? []);
			var own = mods.OfType<string>().Where(Setup.PlainName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			if (own.Count == 0 && s.Str("output_dir") is { } filled) {
				// the step that reads this folder owns the mod it ends in; when that step runs too, it counts the pair once
				var reader = a_setup.Steps.FirstOrDefault(o => o.Key != step.Key && Setup.Str(o.Settings, "reads") is { } r && SamePlace(r, filled));
				if (reader != null && new StepSettings(reader.Settings).OutputMod is { } theirs && a_todo.All(t => t.Key != reader.Key)) {
					builds.Add(new Output(step.Key, theirs, filled, true));
				}
				continue;
			}
			builds.AddRange(own.Select((m, i) => new Output(step.Key, m, i == 0 ? scratch : null)));
		}
		return builds;
	}

	static bool SamePlace(string a_a, string a_b)
	{
		try {
			return Files.SamePath(a_a, a_b);
		} catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) {
			return false;
		}
	}

	public static DiskForecast Build(Setup a_setup, Mo2Instance a_mo2, IReadOnlyList<Setup.StepEntry> a_todo, Probes? a_probes = null)
	{
		var probes = a_probes ?? Real;
		var f = new DiskForecast();
		var disks = new Dictionary<string, Disk>(StringComparer.OrdinalIgnoreCase);
		void Add(string a_place, Part a_part)
		{
			var key = probes.Volume(a_place);
			if (!disks.TryGetValue(key, out var d)) {
				disks[key] = d = new Disk { Key = key, Shown = Path.GetPathRoot(Path.GetFullPath(a_place)) ?? a_place, Free = probes.Free(a_place) };
				f.Disks.Add(d);
			}
			d.Parts.Add(a_part);
		}
		var known = OutputSizes.Load(a_setup);
		var backup = a_setup.BackupDir;
		long oldOutputs = 0;
		foreach (var b in OutputsOf(a_setup, a_todo)) {
			var dir = a_mo2.ModDir(b.Mod);
			long size;
			try {
				size = known.TryGetValue(Path.GetFullPath(dir), out var was) ? was : probes.SizeOf(dir);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				f.NotMeasured.Add($"{b.Mod} could not be measured ({e.Message})");
				continue;
			}
			// its meta.ini alone is no output
			if (size < 4096) {
				f.NotKnown.Add(b.Mod);
				continue;
			}
			var grown = size + size * GrowthPercent / 100;
			var name = StepCatalog.Name(b.Step);
			if (b is { FillsOnly: true, Scratch: { } fills }) {
				Add(fills, new Part(b.Step, $"what {name} writes, about the size of {b.Mod}", grown, true));
				continue;
			}
			var apart = backup != "" && probes.Volume(backup) != probes.Volume(dir);
			oldOutputs += size;
			if (b.Scratch is { } scratch) {
				Add(scratch, new Part(b.Step, $"the new {b.Mod}, as {name} writes it", grown, true));
				if (apart) {
					Add(backup, new Part(b.Step, $"the old {b.Mod}, kept in the run's backup", size, true));
				} else if (probes.Volume(scratch) != probes.Volume(dir)) {
					Add(dir, new Part(b.Step, $"the new {b.Mod}, moved in beside the old one in the run's backup", grown, true));
				}
			} else if (apart) {
				Add(backup, new Part(b.Step, $"the old {b.Mod}, kept in the run's backup", size, true));
				// whether the old one has left before the new one is written depends on the tool: wanted, never what stops a start
				Add(dir, new Part(b.Step, $"the new {b.Mod}, for the moment before the old one has moved to the backup", grown, false));
			} else {
				Add(dir, new Part(b.Step, $"the new {b.Mod}, beside the old one in the run's backup", grown, true));
			}
		}
		if (backup != "" && oldOutputs > 0 && Setup.Bool(a_setup.Root, "zipBackups") != false) {
			Add(backup, new Part("", "packing the old outputs into one file at the end", oldOutputs, false));
		}
		if (backup != "" && Setup.Bool(a_setup.Root, "copySaves") != false && a_todo.Any(s => Setup.SaveSteps.Contains(s.Key))) {
			try {
				var saves = InGame.SavesDir(new Profile(a_mo2, a_setup.Profile, () => false));
				var bytes = !Directory.Exists(saves) ? 0 : new DirectoryInfo(saves).EnumerateFiles()
					.Where(x => x.Extension.Equals(".ess", StringComparison.OrdinalIgnoreCase) || x.Extension.Equals(".skse", StringComparison.OrdinalIgnoreCase)).Sum(x => x.Length);
				if (bytes > 0) {
					Add(backup, new Part("", "the copy of your saves made before the game starts", bytes, false));
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) {
				f.NotMeasured.Add($"your saves could not be measured ({e.Message})");
			}
		}
		return f;
	}

	public static string Size(long a_bytes) =>
		a_bytes >= 1L << 30 ? $"{a_bytes / (double)(1L << 30):0.0} GB" : $"{Math.Max(1, a_bytes / (1L << 20)):0} MB";

	static string Biggest(IEnumerable<Part> a_parts) =>
		string.Join("; ", a_parts.OrderByDescending(p => p.Bytes).Take(3).Select(p => $"{p.What} ({Size(p.Bytes)})"));

	// A disk that will not hold what the run needs stops the start (a_stops false: the setup asks to be told only); one that holds
	// the run but not its extras, or only just holds it, is told
	public IEnumerable<Steps.Issue> Issues(bool a_stops = true)
	{
		foreach (var d in Disks) {
			if (d.Free is not { } free) {
				continue;
			}
			var needed = d.Parts.Where(p => p.Needed).ToList();
			if (free < d.Needs) {
				yield return new Steps.Issue("", a_stops, $"not enough room on {d.Shown} for this run: it needs about {Size(d.Needs)} there and {Size(free)} is free - {Biggest(needed)}");
			} else if (free < d.Wants) {
				yield return new Steps.Issue("", false, $"{d.Shown} has room for the run itself ({Size(d.Needs)} of {Size(free)} free) but not for all of what it does besides, " +
					$"which it then leaves out: {Biggest(d.Parts.Where(p => !p.Needed))}");
			} else if (d.Needs > 0 && free < d.Needs + d.Needs / 4) {
				yield return new Steps.Issue("", false, $"{d.Shown} will be nearly full during this run: it needs about {Size(d.Needs)} of the {Size(free)} that is free");
			}
		}
		foreach (var n in NotMeasured) {
			yield return new Steps.Issue("", false, "the room this run needs is not fully known: " + n);
		}
	}

	// in plain words, for the preview and the dry run ("" lines apart: the caller lays them out)
	public List<string> Lines()
	{
		var l = new List<string>();
		foreach (var d in Disks.Where(d => d.Wants > 0)) {
			var free = d.Free is { } fr ? $" of {Size(fr)} free" : "";
			l.Add($"  - {d.Shown} about {Size(d.Needs)}{free}{(d.Needs > 0 ? ": " + Biggest(d.Parts.Where(p => p.Needed)) : "")}");
			foreach (var p in d.Parts.Where(p => !p.Needed)) {
				l.Add($"      and up to {Size(p.Bytes)} more for {p.What} (left out by itself when there is no room)");
			}
		}
		if (l.Count > 0) {
			l.Insert(0, "Room on disk this run takes while it goes (from each output's size when it was last built):");
		}
		if (NotKnown.Count > 0) {
			l.Add((l.Count == 0 ? "Room on disk: " : "  - ") + $"not known yet for {string.Join(", ", NotKnown)} (never built here) - the setup's own floor of free room stands in for {(NotKnown.Count == 1 ? "it" : "them")}.");
		}
		return l;
	}
}
