namespace MIDIRouter.Routing;

[Flags]
internal enum MidiKinds
{
	None = 0,
	NoteOn = 1,
	NoteOff = 2,
	ControlChange = 4,
	Note = NoteOn | NoteOff,
}

internal sealed record SubscriptionRule(
	Func<string, bool> Device,
	MidiKinds Kinds,
	Func<int, bool> Number)
{
	public static readonly Func<string, bool> AnyName = static _ => true;
	public static readonly Func<int, bool> AnyNumber = static _ => true;
}

internal sealed record Subscriber(string Key, string? Topic, IReadOnlyList<SubscriptionRule> Rules)
{
	public const string MacroDeckKey = "macrodeck";
}

internal enum ShortcutAction
{
	None,
	Toggle,
	Hold,
	Play,
	Control,
}

internal sealed record ModeShortcut(string Device, MidiKinds Kind, int Number, ShortcutAction Action);
