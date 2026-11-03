using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MIDIRouter.Routing;

namespace MIDIRouter.Actions;

internal sealed class SendProgramChangeAction(MidiRouter router) : IActionDefinition
{
	private const string ChannelParameter = "channel";
	private const string ProgramParameter = "program";

	internal MidiRouter Router { get; } = router;

	public string Id => "send-program-change";

	public LocalizedText Name => Strings.Actions.SendProgram.Name();

	public LocalizedText Description => Strings.Actions.SendProgram.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		ActionParameter.Number(
			ChannelParameter,
			label: Strings.Actions.SendProgram.Channel(),
			min: 1,
			max: 16,
			step: 1,
			defaultValue: 1),
		ActionParameter.Number(
			ProgramParameter,
			label: Strings.Actions.SendProgram.Program.Label(),
			description: Strings.Actions.SendProgram.Program.Description(),
			min: 0,
			max: 127,
			step: 1,
			defaultValue: 0),
	];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.Windows | MacroDeckPlatform.MacOs;

	public IActionExecutor CreateExecutor() => new Executor(this);

	private sealed class Executor(SendProgramChangeAction action) : IActionExecutor
	{
		public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			action.Router.SendProgramChange(
				ActionParameters.ReadNumber(context.Parameters, ChannelParameter, 1, 16, 1),
				ActionParameters.ReadNumber(context.Parameters, ProgramParameter, 0, 127, 0));
			return Task.FromResult(ActionResult.Success());
		}
	}
}
