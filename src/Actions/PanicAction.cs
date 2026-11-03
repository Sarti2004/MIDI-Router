using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MIDIRouter.Routing;

namespace MIDIRouter.Actions;

internal sealed class PanicAction(MidiRouter router) : IActionDefinition
{
	private const string ChannelParameter = "channel";
	private const string AllChannels = "all";

	internal MidiRouter Router { get; } = router;

	public string Id => "panic";

	public LocalizedText Name => Strings.Actions.Panic.Name();

	public LocalizedText Description => Strings.Actions.Panic.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		ActionParameter.Choice(
			ChannelParameter,
			options:
			[
				new ActionParameterOption { Value = AllChannels, Label = Strings.Actions.Panic.AllChannels() },
				.. Enumerable.Range(1, 16).Select(channel => new ActionParameterOption
				{
					Value = channel.ToString(CultureInfo.InvariantCulture),
					Label = LocalizedText.FromLiteral(channel.ToString(CultureInfo.InvariantCulture)),
				}),
			],
			label: Strings.Actions.Panic.Channel(),
			defaultValue: AllChannels),
	];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.Windows | MacroDeckPlatform.MacOs;

	public IActionExecutor CreateExecutor() => new Executor(this);

	private sealed class Executor(PanicAction action) : IActionExecutor
	{
		public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			// every channel
			var channel = ActionParameters.ReadNumber(context.Parameters, ChannelParameter, 1, 16, 0);
			if (channel == 0)
			{
				action.Router.SendPanic();
			}
			else
			{
				action.Router.SendPanic(channel);
			}

			return Task.FromResult(ActionResult.Success());
		}
	}
}
