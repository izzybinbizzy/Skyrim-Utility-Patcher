using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;

namespace ToolRunner.App;

// Screen readers (his checklist 490, "keyboard + screen readers"): what changes on its own - a message, the step that runs, a
// guided step waiting, the verdict - is said through UI Automation's notification event, which Narrator, NVDA and JAWS read
// out. WPF's LiveSetting alone says nothing: the program has to raise the event itself.
public static class A11y
{
	// the offline tests read what would have been said (null = say it)
	public static Action<string, bool>? Heard { get; set; }

	public static void Say(UIElement a_anchor, string? a_text, bool a_important = false)
	{
		if (string.IsNullOrWhiteSpace(a_text)) {
			return;
		}
		// a long message keeps its start and its end (what to do comes last - a list of files sits in the middle)
		var text = a_text.Length > 600 ? a_text[..400] + " ... " + a_text[^190..] : a_text;
		if (Heard != null) {
			Heard(text, a_important);
			return;
		}
		try {
			var peer = UIElementAutomationPeer.FromElement(a_anchor) ?? UIElementAutomationPeer.CreatePeerForElement(a_anchor);
			peer?.RaiseNotificationEvent(AutomationNotificationKind.Other,
				a_important ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.MostRecent,
				text, a_important ? "Tool Runner alert" : "Tool Runner status");
		} catch (Exception e) when (e is InvalidOperationException or ElementNotAvailableException or ArgumentException) {
			// nothing listens, or the element went away: nothing to say it to
		}
	}

	// a whole step in one sentence, for the step list's screen-reader status (its glyph and color say it to the eye)
	public static string StepStatus(bool a_on, bool a_leftOut, StepState? a_state, string a_elapsed, string a_usual, string a_note)
	{
		var parts = new List<string>();
		if (!a_on) {
			parts.Add("switched off");
		} else if (a_leftOut) {
			parts.Add("left out for a new game");
		}
		parts.Add(a_state switch {
			StepState.Running => "running" + (a_elapsed == "" ? "" : " for " + a_elapsed),
			StepState.Done => "done" + (a_elapsed == "" ? "" : " in " + a_elapsed),
			StepState.Failed => "failed",
			StepState.Stopped => "stopped",
			StepState.Skipped => "skipped",
			StepState.Pending => "waiting its turn",
			_ => a_usual == "" ? "" : "usually takes " + a_usual,
		});
		if (a_note != "") {
			parts.Add(a_note);
		}
		return string.Join(", ", parts.Where(p => p != ""));
	}
}
