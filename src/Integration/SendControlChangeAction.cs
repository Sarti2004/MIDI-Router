using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MIDIRouter.Routing;

namespace MIDIRouter.Integration;

internal sealed class SendControlChangeAction(MidiRouter router) : IActionDefinition
{
	private const string ChannelParameter = "channel";
	private const string ControllerParameter = "controller";
	private const string ValueParameter = "value";

	internal MidiRouter Router { get; } = router;

	public string Id => "send-cc";

	public LocalizedText Name => Strings.Actions.SendCc.Name();

	public LocalizedText Description => Strings.Actions.SendCc.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		ActionParameter.Number(
			ChannelParameter,
			label: Strings.Actions.SendCc.Channel(),
			min: 1,
			max: 16,
			step: 1,
			defaultValue: 1),
		ActionParameter.Number(
			ControllerParameter,
			label: Strings.Actions.SendCc.Controller(),
			min: 0,
			max: 127,
			step: 1,
			defaultValue: 7),
		ActionParameter.Number(
			ValueParameter,
			label: Strings.Actions.SendCc.Value(),
			min: 0,
			max: 127,
			step: 1,
			defaultValue: 127),
	];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.Windows | MacroDeckPlatform.MacOs;

	public IActionExecutor CreateExecutor() => new Executor(this);

	private static int Read<T>(IReadOnlyDictionary<string, T> parameters, string name, int min, int max, int fallback) =>
		parameters.TryGetValue(name, out var value)
			&& double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
			&& double.IsFinite(number)
				? (int)Math.Clamp(Math.Round(number), min, max)
				: fallback;

	private sealed class Executor(SendControlChangeAction action) : IActionExecutor
	{
		public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			action.Router.SendControlChange(
				Read(context.Parameters, ChannelParameter, 1, 16, 1),
				Read(context.Parameters, ControllerParameter, 0, 127, 7),
				Read(context.Parameters, ValueParameter, 0, 127, 127));
			return Task.FromResult(ActionResult.Success());
		}
	}
}
