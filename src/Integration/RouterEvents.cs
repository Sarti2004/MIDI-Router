using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Events;
using MIDIRouter.Routing;

namespace MIDIRouter.Integration;

internal static class RouterEvents
{
	public const string NoteOn = "midi-note-on";
	public const string NoteOff = "midi-note-off";
	public const string ControlChange = "midi-cc";

	public const string DeviceParameter = "device";
	public const string NoteParameter = "note";
	public const string ControllerParameter = "controller";
	public const string VelocityParameter = "velocity";
	public const string ValueParameter = "value";
	public const string ChannelParameter = "channel";

	private static readonly string[] PitchClassNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

	public static IReadOnlyList<EventDefinition> Definitions { get; } =
	[
		NoteEvent(NoteOn, Strings.Events.NoteOn.Name(), Strings.Events.NoteOn.Description()),
		NoteEvent(NoteOff, Strings.Events.NoteOff.Name(), Strings.Events.NoteOff.Description()),
		new EventDefinition
		{
			Id = ControlChange,
			Name = Strings.Events.ControlChange.Name(),
			Description = Strings.Events.ControlChange.Description(),
			ConfigurationParameters =
			[
				DeviceChoice(),
				ActionParameter.Number(
					ControllerParameter,
					label: Strings.Events.Controller.Label(),
					description: Strings.Events.Controller.Description(),
					min: 0,
					max: 127,
					step: 1),
			],
			PayloadParameters =
			[
				DeviceChoice(),
				ActionParameter.Number(ControllerParameter, label: Strings.Events.Controller.Label(), min: 0, max: 127, step: 1),
				ActionParameter.Number(ValueParameter, label: Strings.Events.Value.Label(), min: 0, max: 127, step: 1),
				ActionParameter.Number(ChannelParameter, label: Strings.Events.Channel.Label(), min: 1, max: 16, step: 1),
			],
		},
	];

	public static string EventId(MidiKinds kind) => kind switch
	{
		MidiKinds.NoteOn => NoteOn,
		MidiKinds.NoteOff => NoteOff,
		_ => ControlChange,
	};

	public static Dictionary<string, object?> Payload(in ClaimedMessage message) => message.Kind switch
	{
		MidiKinds.ControlChange => new()
		{
			[DeviceParameter] = message.Device,
			[ControllerParameter] = message.Number,
			[ValueParameter] = message.Value,
			[ChannelParameter] = message.Channel,
		},
		_ => new()
		{
			[DeviceParameter] = message.Device,
			[NoteParameter] = message.Number.ToString(CultureInfo.InvariantCulture),
			[VelocityParameter] = message.Value,
			[ChannelParameter] = message.Channel,
		},
	};

	/// C4(60)
	public static string NoteName(int note) =>
		string.Create(CultureInfo.InvariantCulture, $"{PitchClassNames[note % 12]}{note / 12 - 1}");

	private static EventDefinition NoteEvent(string id, LocalizedText name, LocalizedText description) => new()
	{
		Id = id,
		Name = name,
		Description = description,
		ConfigurationParameters =
		[
			DeviceChoice(),
			ActionParameter.Choice(
				NoteParameter,
				options: NoteOptions(),
				label: Strings.Events.Note.Label(),
				description: Strings.Events.Note.Description()),
		],
		PayloadParameters =
		[
			DeviceChoice(),
			ActionParameter.Choice(NoteParameter, options: NoteOptions(), label: Strings.Events.Note.Label()),
			ActionParameter.Number(VelocityParameter, label: Strings.Events.Velocity.Label(), min: 0, max: 127, step: 1),
			ActionParameter.Number(ChannelParameter, label: Strings.Events.Channel.Label(), min: 1, max: 16, step: 1),
		],
	};

	private static ActionParameter DeviceChoice() =>
		ActionParameter.DynamicChoice(
			DeviceParameter,
			label: Strings.Events.Device.Label(),
			description: Strings.Events.Device.Description(),
			placeholder: Strings.Events.Device.Placeholder());

	public static IReadOnlyList<ActionParameterOption> NoteOptions() =>
	[
		.. Enumerable.Range(0, 128).Select(note => new ActionParameterOption
		{
			Value = note.ToString(CultureInfo.InvariantCulture),
			Label = LocalizedText.FromLiteral(string.Create(CultureInfo.InvariantCulture, $"{NoteName(note)} ({note})")),
		}),
	];
}
