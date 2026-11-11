using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ToolRunner.Steps;

namespace ToolRunner;

// His checklist 433-435: a modder adds their patcher with one file in their own mod, <mod>\Output Manager\<anything>.json, saying what it is, how to
// start it, what it makes and where it goes in the order. No code of theirs is loaded into this program. The player sees exactly what it will start
// and says yes once; a file that changes later is asked about again (a setting turns the question off). What the tool writes lands in overwrite, and goes
// into the add-on's own output mod, never another mod.
public sealed record AddOn(string File, string ModDir, string Name, string Author, string Page, string Program, string Arguments, string Output, int Minutes,
	string? Makes, string? Before, string? After, string Hash)
{
	public string Key => AddOns.KeyPrefix + Name;
	public string ProgramPath => Path.GetFullPath(Path.Combine(ModDir, Program));

	// what the question to the player says
	public string Says => $"{Name}, by {Author}.\n\nIt starts:  {ProgramPath}" + (Arguments == "" ? "" : $"\nWith:  {Arguments}") +
		$"\n\nWhat it writes goes into its own output mod, {Output}. It runs as any program does: this program cannot stop one that writes somewhere else, so allow only an add-on you trust." +
		(Page == "" ? "" : $"\n\nIts page: {Page}");
}

public static class AddOns
{
	public const string KeyPrefix = "addon:";
	// the folder inside a modder's own mod: named after the program, under any name it had (an add-on made for an older name still counts)
	public const string FolderInMod = Brand.Name;

	public static bool IsFolderInMod(string? a_folderName) => Brand.Is(a_folderName);
	public const string NoAskSetting = "addonsNoAsk";
	const int Schema = 1;
	const int DefaultMinutes = 60, MostMinutes = 600, LongestName = 60;
	static readonly string[] AllowedKeys = ["schema", "name", "author", "page", "program", "arguments", "output", "minutes", "makes", "before", "after"];

	static string? Text(JsonObject a_o, string a_key) => a_o[a_key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;

	static string Hash(byte[] a_bytes) => Convert.ToHexString(SHA256.HashData(a_bytes));

	static bool RelativeInside(string a_path) =>
		!Path.IsPathRooted(a_path) && !a_path.Replace('\\', '/').Split('/').Contains("..");

	static bool GoodFolderName(string a_name) => a_name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && a_name != "." && a_name != ".." && !a_name.EndsWith('.');

	// what is wrong with an add-on file, in plain words for its author (none = it is right); a_modDir: also look for the program there
	public static List<string> Problems(string a_text, string? a_modDir = null)
	{
		JsonObject? o;
		try {
			o = JsonNode.Parse(a_text, null, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
		} catch (JsonException e) {
			return ["it is not valid JSON: " + e.Message];
		}
		if (o == null) {
			return ["it must be one JSON object, { ... }"];
		}
		var p = new List<string>();
		p.AddRange(o.Select(kv => kv.Key).Where(k => !AllowedKeys.Contains(k, StringComparer.Ordinal)).Select(k => $"\"{k}\" is not a key an add-on file has - a typing mistake?"));
		if (o["schema"] is not JsonValue sv || !sv.TryGetValue<int>(out var schema) || schema != Schema) {
			p.Add($"\"schema\" must be {Schema}");
		}
		foreach (var need in new[] { "name", "author", "program", "output" }.Where(k => string.IsNullOrEmpty(Text(o, k)))) {
			p.Add($"\"{need}\" is missing");
		}
		if (Text(o, "name") is { Length: > 0 } name && (name.Length > LongestName || !GoodFolderName(name))) {
			p.Add($"\"name\" must be at most {LongestName} letters and make a good folder name");
		}
		if (Text(o, "output") is { Length: > 0 } output && !GoodFolderName(output)) {
			p.Add("\"output\" must make a good folder name: it names the mod the tool's files go into");
		}
		if (Text(o, "program") is { Length: > 0 } program) {
			if (!RelativeInside(program) || !program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) {
				p.Add("\"program\" must be an .exe inside your mod, written from your mod's own folder (for example \"tools/MyPatcher.exe\")");
			} else if (a_modDir != null && !File.Exists(Path.Combine(a_modDir, program))) {
				p.Add($"\"program\" names {program}, which is not in the mod");
			}
		}
		if (Text(o, "makes") is { Length: > 0 } makes && !RelativeInside(makes)) {
			p.Add("\"makes\" must be a file the tool writes, from the game's Data folder (for example \"MyPatch.esp\")");
		}
		if (Text(o, "page") is { Length: > 0 } page && !page.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) {
			p.Add("\"page\" must be an https:// link");
		}
		if (o["minutes"] != null && (o["minutes"] is not JsonValue mv || !mv.TryGetValue<int>(out var minutes) || minutes < 1 || minutes > MostMinutes)) {
			p.Add($"\"minutes\" must be a number from 1 to {MostMinutes}: how long the tool may take");
		}
		foreach (var place in new[] { "before", "after" }) {
			if (Text(o, place) is { Length: > 0 } step && !StepCatalog.DefaultOrder.Contains(step, StringComparer.Ordinal)) {
				p.Add($"\"{place}\" names {step}, which is no step (they are: {string.Join(", ", StepCatalog.DefaultOrder)})");
			}
		}
		return p;
	}

	// null when the file has problems
	public static AddOn? Read(string a_file, string a_modDir)
	{
		var bytes = File.ReadAllBytes(a_file);
		var text = Files.DecodeText(bytes);
		if (Problems(text, a_modDir).Count > 0) {
			return null;
		}
		var o = (JsonObject)JsonNode.Parse(text, null, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })!;
		return new AddOn(a_file, a_modDir, Text(o, "name")!, Text(o, "author")!, Text(o, "page") ?? "", Text(o, "program")!, Text(o, "arguments") ?? "", Text(o, "output")!,
			o["minutes"]?.GetValue<int>() ?? DefaultMinutes, Text(o, "makes"), Text(o, "before"), Text(o, "after"), Hash(bytes));
	}

	public sealed record Found(string File, string Mod, AddOn? AddOn, IReadOnlyList<string> Problems);

	// every add-on file in the profile's ticked mods
	public static List<Found> Find(Mo2Instance a_mo2, Profile a_profile)
	{
		var found = new List<Found>();
		foreach (var mod in a_profile.States().Where(s => s.Value == '+').Select(s => s.Key)) {
			var modDir = a_mo2.ModDir(mod);
			foreach (var f in Brand.AllNames.Select(n => Path.Combine(modDir, n)).Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*.json"))) {
				try {
					var problems = Problems(Files.ReadSharedText(f), modDir);
					found.Add(new Found(f, mod, problems.Count == 0 ? Read(f, modDir) : null, problems));
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					found.Add(new Found(f, mod, null, ["it could not be read: " + e.Message]));
				}
			}
		}
		return found;
	}

	public static bool InSetup(Setup a_setup, AddOn a_addOn) => a_setup.Steps.Any(s => s.Key == a_addOn.Key);

	// in the setup, as its file stands now
	public static bool Allowed(Setup a_setup, AddOn a_addOn) => a_setup.Steps.Any(s => s.Key == a_addOn.Key && Setup.Str(s.Settings, "sha") == a_addOn.Hash);

	// the mod this add-on's output would land on when that is a mod of the player's: a run moves an output's old files aside and replaces them. Its own
	// output from before is no clash. Asked when the player allows it: a setup keeps the output's name from then on
	public static string? OutputTaken(Mo2Instance a_mo2, Setup a_setup, AddOn a_addOn)
	{
		var name = SetupMaker.OutputName(Setup.Str(a_setup.Root, "outputPattern") ?? "{output} - {profile}", a_addOn.Output, a_setup.Profile);
		if (a_setup.StepSettings(a_addOn.Key) is { } had && string.Equals(new StepSettings(had).OutputMod, name, StringComparison.OrdinalIgnoreCase)) {
			return null;
		}
		var dir = a_mo2.ModDir(name);
		return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(e => !Path.GetFileName(e).Equals("meta.ini", StringComparison.OrdinalIgnoreCase)) ? name : null;
	}

	// the add-on as a step of the setup, where its file says it goes (before texgen, where the LOD tools start, when it says nothing);
	// allowed: its file's fingerprint is kept, and a file changed since is asked about again. Returns what the player must hear when its two places cannot both hold
	public static string? AddToSetup(JsonObject a_root, AddOn a_addOn, string a_profile)
	{
		var steps = a_root["steps"] as JsonArray ?? [];
		var pattern = Setup.Str(a_root, "outputPattern") ?? "{output} - {profile}";
		for (var i = steps.Count - 1; i >= 0; --i) {
			if (steps[i] is JsonObject o && Setup.Str(o, "step") == a_addOn.Key) {
				steps.RemoveAt(i);
			}
		}
		int At(string? a_step) => a_step == null ? -1 : steps.Select((s, i) => (s, i)).FirstOrDefault(x => x.s is JsonObject o && Setup.Str(o, "step") == a_step, (null, -1)).Item2;
		var after = At(a_addOn.After);
		var before = At(a_addOn.Before ?? (a_addOn.After == null ? "texgen" : null));
		var last = At("playsave");
		var place = after >= 0 ? after + 1 : before >= 0 ? before : last >= 0 ? last : steps.Count;
		steps.Insert(place, new JsonObject {
			["step"] = a_addOn.Key, ["on"] = true,
			["settings"] = new JsonObject { ["file"] = a_addOn.File, ["sha"] = a_addOn.Hash, ["output_mod"] = SetupMaker.OutputName(pattern, a_addOn.Output, a_profile), ["deadline_min"] = a_addOn.Minutes },
		});
		a_root["steps"] = steps;
		return after >= 0 && before >= 0 && before <= after
			? $"{a_addOn.Name} asks to run after {StepCatalog.Name(a_addOn.After!)} and before {StepCatalog.Name(a_addOn.Before!)}, but this setup runs them the other way round. It runs right after {StepCatalog.Name(a_addOn.After!)}: check its place in the step list."
			: null;
	}
}

// One add-on's step: its program through Mod Organizer, ended by itself, what it wrote moved into its output mod
public sealed class AddOnStep(string a_key) : IStep
{
	public string Key => a_key;

	static string ModDirOf(string a_file) => Path.GetDirectoryName(Path.GetDirectoryName(a_file)!)!;

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		var file = a_s.Str("file") ?? "";
		if (!File.Exists(file)) {
			yield return new Issue(Key, true, "its add-on file is gone: " + file);
			yield break;
		}
		var modDir = ModDirOf(file);
		var problems = AddOns.Problems(Files.ReadSharedText(file), modDir);
		foreach (var p in problems) {
			yield return new Issue(Key, true, "its add-on file: " + p);
		}
		if (problems.Count > 0) {
			yield break;
		}
		var addOn = AddOns.Read(file, modDir)!;
		if (addOn.Hash != a_s.Str("sha") && Setup.Bool(a_c.Setup.Root, AddOns.NoAskSetting) != true) {
			yield return new Issue(Key, true, "its add-on file changed since you allowed it: look at it again in Settings > ADD-ONS");
		}
		if (a_c.States.TryGetValue(Path.GetFileName(modDir), out var state) && state != '+') {
			yield return new Issue(Key, true, $"the mod that holds it, {Path.GetFileName(modDir)}, is switched off in this profile");
		}
		if (a_s.OutputMod == null) {
			yield return new Issue(Key, true, "no output mod is set");
		}
	}

	public void Run(RunContext a_ctx, StepSettings a_s)
	{
		var file = a_s.Req("file");
		var addOn = AddOns.Read(file, ModDirOf(file)) ?? throw new StepFailedException("its add-on file has problems - Settings > ADD-ONS names them");
		// the file as it stands now is what starts: changed since the pre-check, it is not what the player allowed
		if (addOn.Hash != a_s.Str("sha") && Setup.Bool(a_ctx.Setup.Root, AddOns.NoAskSetting) != true) {
			throw new StepFailedException("its add-on file changed since you allowed it: look at it again in Settings > ADD-ONS");
		}
		var output = a_s.OutputMod ?? throw new StepFailedException("no output mod is set");
		var image = Path.GetFileName(addOn.ProgramPath);
		var before = Runner.OverwriteFiles(a_ctx);
		StepKit.EnsureOutput(a_ctx, output);
		try {
			using var proc = a_ctx.Control.RunExecutable(addOn.ProgramPath, image, addOn.Arguments == "" ? null : addOn.Arguments, a_ctx.Cancel, null, true);
			a_ctx.Activity(Key, addOn.Name + " is working");
			Waits.Tool(a_ctx, Key, proc, a_s.Deadline(addOn.Minutes), () => proc.HasExited ? Waits.Poll.Done : Waits.Poll.Wait);
			if (proc.HasExited && proc.ExitCode != 0) {
				throw new StepFailedException($"{addOn.Name} ended with the code {proc.ExitCode}");
			}
		} finally {
			StepKit.CloseTool(a_ctx, image);
		}
		Collect(a_ctx, Key, output, addOn.Makes, before);
		a_ctx.SetAfter(output);
	}

	// what the tool wrote into overwrite, each file at its own place in the output mod; a tool that wrote nothing, or not the file it promised, failed.
	// Returns whether the output mod now holds what the tool made
	public static bool Collect(RunContext a_ctx, string a_step, string a_mod, string? a_makes, IReadOnlyDictionary<string, DateTime> a_before, bool a_nothingIsFine = false)
	{
		var ow = a_ctx.Mo2.OverwriteDir;
		// overwrite\Root is Root Builder's, whatever ran
		var root = Path.Combine(ow, "Root");
		var fresh = Runner.OverwriteFiles(a_ctx).Where(n => (!a_before.TryGetValue(n.Key, out var t) || t != n.Value) && !Files.IsUnder(n.Key, root)).Select(n => n.Key).ToList();
		if (fresh.Count == 0) {
			// a tool told to write into the output mod itself: Mod Organizer's virtual folder passes that through
			if (a_makes != null && a_ctx.Journal.Step(a_step)?.Started is { } since && Path.Combine(a_ctx.Mo2.ModDir(a_mod), a_makes) is var inMod && File.Exists(inMod) && File.GetLastWriteTime(inMod) >= since) {
				a_ctx.Log.Detail($"{StepCatalog.Name(a_step)} wrote {a_makes} straight into {a_mod}");
				return true;
			}
			if (a_nothingIsFine) {
				var said = $"{StepCatalog.Name(a_step)} wrote nothing through Mod Organizer, so what it made is where you told it to go. {a_mod} stays as it was.";
				a_ctx.Log.Warn(said);
				a_ctx.Log.Simple("Note: " + said);
				return false;
			}
			throw new StepFailedException($"{StepCatalog.Name(a_step)} wrote nothing");
		}
		if (a_makes != null && !fresh.Any(f => Files.SamePath(Path.GetRelativePath(ow, f), a_makes) || string.Equals(Path.GetRelativePath(ow, f).Replace('\\', '/'), a_makes.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))) {
			throw new StepFailedException($"{StepCatalog.Name(a_step)} did not write {a_makes}, which its add-on file promises");
		}
		StepKit.ParkMod(a_ctx, a_step, a_mod);
		foreach (var f in fresh) {
			var dst = Path.Combine(a_ctx.Mo2.ModDir(a_mod), Path.GetRelativePath(ow, f));
			Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
			File.Move(f, dst, true);
		}
		a_ctx.Log.Detail($"{fresh.Count} file(s) moved from overwrite into {a_mod}");
		return true;
	}
}
