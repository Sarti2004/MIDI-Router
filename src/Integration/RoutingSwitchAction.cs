using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MIDIRouter.Routing;

namespace MIDIRouter.Integration;

internal sealed class RoutingSwitchAction(MidiRouter router, Func<bool, Task> saveEnabled) :
	IActionDefinition,
	IStateProviderActionDefinition
{
	private const string SwitchParameter = "switch";
	private const string SwitchToggle = "toggle";
	private const string SwitchOn = "on";
	private const string SwitchOff = "off";

	private static readonly IReadOnlyList<ActionStateDefinition> States =
	[
		new(SwitchOn, Strings.Actions.Routing.StateOn())
		{
			DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#2E7D32" },
		},
		new(SwitchOff, Strings.Actions.Routing.StateOff()),
	];

	internal MidiRouter Router { get; } = router;

	internal Func<bool, Task> SaveEnabled { get; } = saveEnabled;

	public string Id => "routing-on-off";

	public LocalizedText Name => Strings.Actions.Routing.Name();

	public LocalizedText Description => Strings.Actions.Routing.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		ActionParameter.Choice(
			SwitchParameter,
			options:
			[
				new ActionParameterOption { Value = SwitchToggle, Label = Strings.Actions.Routing.Switch.Toggle() },
				new ActionParameterOption { Value = SwitchOn, Label = Strings.Actions.Routing.Switch.On() },
				new ActionParameterOption { Value = SwitchOff, Label = Strings.Actions.Routing.Switch.Off() },
			],
			label: Strings.Actions.Routing.Switch.Label(),
			description: Strings.Actions.Routing.Switch.Description(),
			defaultValue: SwitchToggle),
	];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.Windows | MacroDeckPlatform.MacOs;

	public TimeSpan StatePollInterval => TimeSpan.FromSeconds(2);

	public Task<ActionStateSnapshot?> GetActionStateAsync(IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken) =>
		Task.FromResult<ActionStateSnapshot?>(new(States, Router.Enabled ? SwitchOn : SwitchOff));

	public IActionExecutor CreateExecutor() => new Executor(this);

	private sealed class Executor(RoutingSwitchAction action) : IActionExecutor
	{
		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			var requested = context.Parameters.TryGetValue(SwitchParameter, out var value) ? value?.ToString() : null;
			var enabled = requested switch
			{
				SwitchOn => true,
				SwitchOff => false,
				_ => !action.Router.Enabled,
			};

			// Opening and closing devices can take a moment; keep it off the host's invocation thread.
			await Task.Run(() => action.Router.SetEnabled(enabled), context.CancellationToken).ConfigureAwait(false);
			await action.SaveEnabled(enabled).ConfigureAwait(false);
			return ActionResult.Success(enabled ? SwitchOn : SwitchOff);
		}
	}
}
