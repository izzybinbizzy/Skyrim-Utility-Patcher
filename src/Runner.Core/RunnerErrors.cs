namespace ToolRunner;

// One catalog of everything the runner itself refuses or fails on, each with what it means and what to do. A refusal's own text
// says what was found ("the game is running (SkyrimSE.exe)"); this says, the same way every time, what a player does about it -
// in the window under a start that was refused, in the simple log, on the command line and in its --json output. The tools' own
// errors are ToolErrors' (read off each tool's documentation); Explain.For asks both.
//
// An entry is found by words its text always holds, so the many places that word a refusal stay as they are. A test reads the
// engine's source and holds every refusal and every stopping finding of the check against this table: a new one without an
// entry fails the build's tests. The codes are for a bug report and the readme, and never change once given.
public static class RunnerErrors
{
	public sealed record Entry(string Code, string[] Any, string What, string Todo);

	static Entry E(string a_code, string a_any, string a_what, string a_todo) => new(a_code, a_any.Split('|'), a_what, a_todo);

	public static IReadOnlyList<Entry> All { get; } = [
		// ---- a start that was refused: nothing was touched
		E("TR-101", "another run is going|a run is going - learn", "Only one run, put-back or look at a tool goes at a time.",
			"Wait for the run that is going, or stop it in the window (Stop now), then start again."),
		E("TR-102", "the setup does not read", "The setup file is damaged or is not a setup.",
			"Open Settings and save the setup again, or bring back a copy from the data folder's \"Setup backups\". A file edited by hand must stay valid JSON."),
		E("TR-103", "is damaged, so it cannot be told whether that run finished", "The record of the last run cannot be read, so the program cannot tell whether your profile was left half switched.",
			"Look at your profile in Mod Organizer: are the outputs you expect ticked? Then move that run's folder out of the Runs folder and start again."),
		E("TR-104", "cannot be read right now", "Another program holds the last run's record for a moment (an antivirus, a backup tool).",
			"Try again in a moment. If it keeps happening, leave the program's data folder out of what that other program scans."),
		E("TR-105", "did not finish - put the profile back first|this run did not finish - put your profile back first", "An earlier run was cut off (a crash, a power cut, the PC switched off) and outputs may still be off.",
			"Press \"Put my profile back\" at the top of the window (command line: restore). Nothing new starts until that is done."),
		E("TR-106", "is left out: this setup is set to a new game", "The save steps are left out while the setup is set to a new game.",
			"Untick \"New game\" beside START to run the clean save or the save to play."),
		E("TR-107", "is not a step of this setup", "The step asked for is switched off in this setup or is not in it.",
			"Tick the step in the list, or name one of the steps the message lists."),
		E("TR-108", "the first step comes after the last one", "The run was asked to go from a step to one that comes before it.",
			"Name the earlier step first, or move the steps in the list."),
		E("TR-109", "the check before the run could not finish", "Something the check must read could not be read (a drive that is not there, a folder with no access).",
			"Read the reason in the message, make that drive or folder reachable, and press Check (F5) to see that it now passes."),
		E("TR-110", "Mod Organizer was not found at|No ModOrganizer.ini beside Mod Organizer", "The setup points at a Mod Organizer that is not there (moved, renamed, or on a drive that is not plugged in).",
			"Open Settings and set Mod Organizer's program and its instance folder again, or make a new setup (New setup)."),
		E("TR-111", "Mod Organizer's program is not set|the MO2 profile is not set|the backup folder is not set|must be a whole path on this PC",
			"The setup is missing a folder or names one that is not a whole path on this PC.", "Open Settings, fill it in with a whole path (like D:\\Modding\\Backups), and save."),
		E("TR-112", "no steps|every step is switched off|only the save steps are switched on", "The setup has nothing to run.",
			"Tick at least one step in the list (for a new game the two save steps are left out, so tick a tool as well)."),
		E("TR-113", "a step is listed twice|unknown step(s)|is no longer supported - Landscape Fixer replaced it", "The setup's step list holds a step twice, or one this version does not know.",
			"Make a new setup for the profile (New setup), or take the named step out of the setup file. SnowFixer and AutoBlend were replaced by Landscape Fixer."),
		E("TR-114", "is not a mod's name", "A setting that must be a mod's name holds a path or a character Windows does not allow in a folder name.",
			"Open Settings and give the step a plain mod name there (one folder name, no \\ / : or \"..\"). A setup file from someone else should be brought in with the import command, which checks this."),
		E("TR-115", "the run's log could not be opened", "The run's own folder could not be written (the disk is full, or the data folder lost its access).",
			"Free some room on the disk the program's data folder is on, or move the program to a folder you may write in, then start again."),
		E("TR-116", "command ended with exit code|command did not end within|could not start:", "A command this setup runs before or after a run failed.",
			"Open Settings > commands before and after a run, run that command yourself in a command window to see what it says, and fix or remove it."),
		E("TR-117", "stopped before anything was changed", "The run stopped before it switched anything: your mods are as they were.",
			"Read the reason after it. Nothing needs putting back - fix that and start again."),

		// ---- the check before a run: the PC, Mod Organizer, the game
		E("TR-201", "Mod Organizer has no profile", "The profile this setup was made for is no longer in Mod Organizer (renamed or deleted).",
			"Rename the profile back, or make a new setup for the profile you use now (New setup)."),
		E("TR-202", "was started from inside Mod Organizer", "The program is running as one of Mod Organizer's own executables, where it cannot close and open Mod Organizer.",
			"Close it and start it from its own file or shortcut. (Mod Organizer's Tools menu entry is fine: that one starts it on its own.)"),
		E("TR-203", "the game is running", "Skyrim (or its launcher) is open, and the tools and the in-game steps need the game closed.",
			"Close the game, then start again."),
		E("TR-204", "Mod Organizer is downloading", "Mod Organizer is in the middle of a download, and closing it would cut the download off.",
			"Let the download finish (or pause it in Mod Organizer's Downloads tab), then start again."),
		E("TR-205", "GB free on|not enough room on", "A disk the run writes to has less room than the run needs for its new outputs and for the old ones it keeps.",
			"Free room on that disk, or remove old run backups (Settings > Backups > Keep only the newest now), or point the backups or a tool's output folder at a roomier disk. " +
			"The floor is the setup's \"Free space a run needs\" in Settings; with \"Stop a run that will not fit on its disks\" switched off the forecast is only told."),
		E("TR-206", "this step is not available in this version", "The setup names a step this version of the program does not have.",
			"Update the program, or untick that step."),
		E("TR-207", "its check could not read a file|its check found a setting of the wrong kind", "A file the step's check needs is locked or missing, or one of its settings holds the wrong kind of value.",
			"Read the file or setting named after it; close the program that holds the file, or correct the setting in Settings > A step."),
		E("TR-208", "Steam is updating or checking Skyrim", "Steam is changing the game's files right now.",
			"Wait until Steam has finished, then start again. A new game version needs a matching SKSE and plugins before the in-game steps can work."),
		E("TR-209", "a Mod Organizer runs that the runner cannot read|a Mod Organizer runs that this program cannot read|started as administrator, or by another Windows user", "A Mod Organizer is open that Windows will not let this program look at or close (started as administrator, or by another Windows user).",
			"Close that Mod Organizer yourself, or start both programs the same way (both normally, or both as administrator), then start again."),
		E("TR-210", "is set to always run as administrator", "Mod Organizer is marked \"Run this program as an administrator\" and this program is not.",
			"Right-click ModOrganizer.exe > Properties > Compatibility and untick it, or start this program as administrator too."),
		E("TR-211", "another Mod Organizer is open", "A second Mod Organizer install is open, and a tool could be started in the wrong one.",
			"Close the other Mod Organizer, then start again."),
		E("TR-212", "full plugins are switched on - the game loads at most|light plugins (.esl or ESL-flagged) are switched on - the game loads at most", "More plugins are switched on than the game can load.",
			"Untick plugins you do not need, or make small ones light (ESL-flag them with xEdit where they allow it), until you are under the limit."),

		// ---- the check before a run: a step's own settings and its tool
		E("TR-301", "no output mod is set|no output folder is set|the terrain LOD folder it reads is not set|no shader cache is set", "A step has no place to put what it makes.",
			"Open Settings > A step, pick the step, and fill in its output mod (and its output folder where it has one). A new setup fills these in by itself."),
		E("TR-302", "is in (or holds)|(the folder it writes into) holds a program", "A tool's output folder is one of Mod Organizer's own folders, the game's folder, or the tool's own folder.",
			"Give the step an empty folder of its own outside those (Settings > A step > output folder)."),
		E("TR-303", "no Mod Organizer executable is set for this step|Mod Organizer has no executable called|which does not exist", "The tool is not in Mod Organizer's Executables list under the name the setup uses, or its entry points at a file that is gone.",
			"Add or correct the tool in Mod Organizer (the gears button > Executables), or let the program add it: New setup > Find my tools. Then press Check (F5)."),
		E("TR-304", "program is not found", "A tool this step starts directly is not where the setup says.",
			"Open Settings > A step and set the path of the tool's program; download the tool from its own page if you do not have it (Settings > TOOL UPDATES opens each page)."),
		E("TR-305", "this profile has no save to start the clean save from", "The clean save needs one of your saves, and this profile has none.",
			"Tick \"New game\" beside START if you will start a new character, or make a save in the game first."),
		E("TR-306", "the game route needs NGIO's PrecacheGrass.txt", "Making the grass cache with the game itself needs a file in the game folder, and this setup does not allow writing there.",
			"Use FasterNGIO for the grass step (Settings > A step > Grass cache), or allow the game-folder file in that step's settings."),
		E("TR-307", "none of the shader mods is in this profile", "The shader cache step names shader mods this profile does not have.",
			"Untick the Shader caches step for this profile, or correct its shader mods in Settings > A step."),
		E("TR-308", "\"on_crash\" is", "A step's setting holds a word it does not know.",
			"Open Settings > A step and pick one of the words the message lists."),
		E("TR-309", "DynDOLOD is not in Expert mode", "DynDOLOD starts in its wizard, and the step needs its Expert window.",
			"Open DynDOLOD_SSE.ini in DynDOLOD's own folder (Edit Scripts\\DynDOLOD), set Expert=1, save, and press Check (F5)."),
		E("TR-313", "DynDOLOD looks unpacked over an older version", "A new DynDOLOD was unpacked over the old one, which its author says not to do: TexGen then refuses to start.",
			"Unpack the new version into a new, empty folder (dyndolod.info/Updating), copy your preset and settings files over, point Mod Organizer's Executables entries at it, and press Check (F5)."),
		E("TR-314", "DynDOLOD's preset does not use Ultra tree LOD", "TreeLODGen makes models only DynDOLOD's Ultra tree LOD uses, and the preset has it off.",
			"Tick Ultra in DynDOLOD's own window and save the preset again, or untick the TreeLODGen step."),
		E("TR-315", "DynDOLOD's preset file is not found", "The step is set to change DynDOLOD's saved preset, and that file is not where the setting says.",
			"Save a preset from DynDOLOD's own window and name it in Settings > A step > DynDOLOD, or switch the step's terrain underside option off."),
		E("TR-316", "AnvilLOD needs DynDOLOD's folder", "AnvilLOD reads DynDOLOD's rule files and does not know where DynDOLOD is.",
			"Set DynDOLOD's folder in Settings > A step > AnvilLOD."),
		E("TR-310", "the PGPatcher settings set is missing|but the PGPatcher settings have PGLite Mode", "PGPatcher's saved settings do not match this profile.",
			"Learn PGPatcher's settings again from its own window (Settings > A step > PGPatcher > Learn its settings), or tick / untick PGLite in Mod Organizer to match."),
		E("TR-311", "its add-on file is gone|its add-on file:|changed since you allowed it|the mod that holds it", "An add-on step's file is missing, changed, or its mod is switched off.",
			"Tick the add-on's mod in Mod Organizer and look at the add-on again in Settings > ADD-ONS; allow it only if you trust what it starts."),
		E("TR-317", "Mod Organizer's LOOT is not there", "The LOOT sort step runs the LOOT that ships inside Mod Organizer, and it is missing from Mod Organizer's folder.",
			"Reinstall Mod Organizer over itself (it brings its loot folder back), or untick the LOOT sort step."),
		E("TR-312", "is not in this profile's mod list", "A mod this step names is not in this profile's mod list.",
			"Install the mod or correct its name: in Mod Organizer, or in Settings > A step where the step names it."),

		// ---- a run that stopped part way
		E("TR-401", "could NOT be put back|the profile is back except|put back except|could not put the profile back", "The run ended, but a file was held by another program, so part of your profile is not back yet.",
			"Close what holds the file (Mod Organizer, a tool left open, an Explorer preview), then press \"Put my profile back\". Until then that mod stays switched off, and its old output is safe in the run's backup."),
		E("TR-402", "the save to play was not made", "Every tool finished and your outputs are on; only the last save in the game was not made.",
			"Load the clean save yourself, walk back to where you were, and save - or run only the Save to play step (right-click it)."),
		E("TR-403", "Mod Organizer was not ready after|Mod Organizer did not close within|Mod Organizer's companion did not read the lists back", "Mod Organizer did not answer in time (a box of its own left open, a very large load order, a slow disk).",
			"Open Mod Organizer once by hand and answer whatever it asks, close it, then run again from this step."),
		E("TR-404", "the Mod Organizer you opened stayed open|Mod Organizer was in use for|Mod Organizer asked about downloads in progress|Mod Organizer asked to end programs that are still running",
			"You were using Mod Organizer, and the run never closes it under you.", "Finish what you were doing, close Mod Organizer, and press \"Run again from\" this step."),
		E("TR-405", "still runs from Mod Organizer's folders|is already running (process", "A program from an earlier start is still open (a tool, the game, a crash reporter), and Mod Organizer cannot start cleanly beside it.",
			"Close that program (the message names it), then run again from this step."),
		E("TR-407", "Steam is not running and its program could not be found|Steam did not start within", "A Steam copy of the game needs Steam running first.",
			"Start Steam, wait until it is signed in, then run again from this step."),
		E("TR-406", "did not start within|started and ended at once|ended as soon as it started|could not start ", "The tool or the game did not come up.",
			"Start it once yourself through Mod Organizer to see what it says (a missing requirement, a wrong path, an update box), fix that, then run again from this step. Settings > Test my tools checks every tool at once."),
		E("TR-408", "minute limit|sat idle for|closed before it finished", "The tool ran longer than its limit, sat without working, or closed without finishing.",
			"Open the run's detailed log for the tool's last lines. A tool that only needs more time: raise its limit (Settings > A step > Deadline min). One that asked a question nobody answered: answer it once in the tool's own window (Learn its settings), then run again from this step."),
		E("TR-409", "this step needs your hands in the game and nobody can answer here", "A guided step needs you, and the run was started where nobody can press Done (the command line, a queue left alone).",
			"Run that step from the window, or install DevBench so the in-game steps run by themselves."),
		E("TR-410", "is not set", "A setting the step needs is empty.",
			"Open Settings > A step, fill it in, and save."),
		E("TR-411", "file(s) - too few", "The tool finished but made almost nothing, so its old output was put back.",
			"Open the tool once by hand and look at its own log: it usually found nothing to work on (no worldspace ticked, a missing master, a wrong output path)."),
		E("TR-412", "opened on the player's screen", "A tool that runs hidden showed a window on your screen, so it was ended.",
			"Run again from this step. If it happens every time, switch \"Hidden\" off for that step in Settings > A step, so the tool runs in the open."),
		E("TR-414", "LOOT wrote no sorted list|LOOT's sorted list is empty|LOOT did not see", "LOOT could not sort this load order, so your order was left as it was.",
			"Press Sort in Mod Organizer itself to read LOOT's own message (a rule that goes in a circle, no masterlist yet while offline), put that right, then run again from this step - or untick the LOOT sort step."),
		E("TR-413", "its learned settings name a file outside their folder|saved no settings file while it was open", "The settings kept from the tool's own window are missing or damaged.",
			"Learn them again: Settings > A step > Learn its settings, change something in the tool's window, save, and close it."),

		// ---- putting back and undoing
		E("TR-501", "that run is still going|a run or another put-back is going|a run or a put-back is going", "A run or a put-back is still at work.",
			"Wait until it has ended, then press the button again."),
		E("TR-502", "nothing was undone|could not be undone whole|the undo stopped half way", "The undo could not finish (a file held by another program, Mod Organizer moved).",
			"Close what holds the file and undo the run again from History. What already moved is on record, and nothing is lost: its files are in the run's backup."),
		E("TR-503", "this run was undone already|this run built nothing to undo|only the last run can be undone", "Only the last run that changed a profile can be undone, and only once.",
			"To go further back, bring an older output back by hand from that run's backup folder (History > Open its folder names it)."),
		E("TR-504", "this run's copy of your mod and plugin lists is gone|this run's backup is gone", "The files an undo needs were removed (old backups are tidied after each run, or the folder was cleared by hand).",
			"This run can no longer be undone. Raise \"Backups kept\" in Settings to keep more of them in future."),
	];

	// the entry a refusal or failure text falls under (any letter case), or null when it is none of the runner's own
	public static Entry? Find(string a_text) =>
		All.FirstOrDefault(e => e.Any.Any(a => a_text.Contains(a, StringComparison.OrdinalIgnoreCase)));
}

// what a problem means and what to do about it, whoever's it is: the runner's own first, then the tools' documented errors
public static class Explain
{
	public sealed record Answer(string? Code, string What, string Todo, string? Help);

	public static Answer? For(string a_step, string a_text) =>
		RunnerErrors.Find(a_text) is { } own ? new Answer(own.Code, own.What, own.Todo, null) :
		ToolErrors.Explain(a_step, a_text) is { } tool ? new Answer(null, tool.What, tool.Todo, tool.Help) : null;

	// one refusal as lines for a person: what was found, then what to do (and nothing more when nobody knows better)
	public static List<string> Lines(string a_step, string a_text)
	{
		var l = new List<string> { a_text };
		if (For(a_step, a_text) is { } a) {
			l.Add("  What to do: " + a.Todo + (a.Code == null ? "" : $" [{a.Code}]"));
			if (a.Help != null) {
				l.Add("  Help: " + a.Help);
			}
		}
		return l;
	}
}
