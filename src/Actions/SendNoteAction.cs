using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MIDIRouter.Routing;

namespace MIDIRouter.Actions;

internal sealed class SendNoteAction(MidiRouter router) : IActionDefinition
{
	private const string ChannelParameter = "channel";
	private const string NoteParameter = "note";
	private const string TypeParameter = "type";
	private const string VelocityParameter = "velocity";
	private const string DurationParameter = "duration";

	private const string TypeTap = "tap";
	private const string TypeOn = "on";
	private const string TypeOff = "off";

	private const int DefaultNote = 60;
	private const int DefaultVelocity = 100;
	private const int DefaultDuration = 100;
	private const int MaxDuration = 10000;

	internal MidiRouter Router { get; } = router;

	public string Id => "send-note";

	public LocalizedText Name => Strings.Actions.SendNote.Name();

	public LocalizedText Description => Strings.Actions.SendNote.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		ActionParameter.Number(
			ChannelParameter,
			label: Strings.Actions.SendNote.Channel(),
			min: 1,
			max: 16,
			step: 1,
			defaultValue: 1),
		ActionParameter.Number(
			NoteParameter,
			label: Strings.Actions.SendNote.Note.Label(),
			description: Strings.Actions.SendNote.Note.Description(),
			min: 0,
			max: 127,
			step: 1,
			defaultValue: DefaultNote),
		ActionParameter.Choice(
			TypeParameter,
			options:
			[
				new ActionParameterOption { Value = TypeTap, Label = Strings.Actions.SendNote.Type.Tap() },
				new ActionParameterOption { Value = TypeOn, Label = Strings.Actions.SendNote.Type.On() },
				new ActionParameterOption { Value = TypeOff, Label = Strings.Actions.SendNote.Type.Off() },
			],
			label: Strings.Actions.SendNote.Type.Label(),
			description: Strings.Actions.SendNote.Type.Description(),
			defaultValue: TypeTap),
		ActionParameter.Number(
			VelocityParameter,
			label: Strings.Actions.SendNote.Velocity.Label(),
			description: Strings.Actions.SendNote.Velocity.Description(),
			min: 1,
			max: 127,
			step: 1,
			defaultValue: DefaultVelocity)
			.OnlyWhen(TypeParameter, TypeTap, TypeOn),
		ActionParameter.Number(
			DurationParameter,
			label: Strings.Actions.SendNote.Duration.Label(),
			description: Strings.Actions.SendNote.Duration.Description(),
			min: 1,
			max: MaxDuration,
			step: 1,
			defaultValue: DefaultDuration)
			.OnlyWhen(TypeParameter, TypeTap),
	];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.Windows | MacroDeckPlatform.MacOs;

	public IActionExecutor CreateExecutor() => new Executor(this);

	private sealed class Executor(SendNoteAction action) : IActionExecutor
	{
		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			var channel = ActionParameters.ReadNumber(context.Parameters, ChannelParameter, 1, 16, 1);
			var note = ActionParameters.ReadNumber(context.Parameters, NoteParameter, 0, 127, DefaultNote);
			var velocity = ActionParameters.ReadNumber(context.Parameters, VelocityParameter, 1, 127, DefaultVelocity);
			var type = context.Parameters.TryGetValue(TypeParameter, out var value) ? value?.ToString() : null;

			switch (type)
			{
				case TypeOn:
					action.Router.SendNoteOn(channel, note, velocity);
					break;
				case TypeOff:
					action.Router.SendNoteOff(channel, note);
					break;
				default:
					var duration = ActionParameters.ReadNumber(context.Parameters, DurationParameter, 1, MaxDuration, DefaultDuration);
					action.Router.SendNoteOn(channel, note, velocity);
					try
					{
						await Task.Delay(duration, context.CancellationToken).ConfigureAwait(false);
					}
					finally
					{
						action.Router.SendNoteOff(channel, note);
					}

					break;
			}

			return ActionResult.Success();
		}
	}
}
