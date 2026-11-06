using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Notifications;
using MIDIRouter.Integration;
using MIDIRouter.Routing;

namespace MIDIRouter.Actions;

internal sealed class LearnCodeAction(MidiRouter router, Func<INotificationService?> notifications) : IActionDefinition
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

	internal MidiRouter Router { get; } = router;

	internal Func<INotificationService?> Notifications { get; } = notifications;

	public string Id => "learn-code";

	public LocalizedText Name => Strings.Actions.Learn.Name();

	public LocalizedText Description => Strings.Actions.Learn.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } = [];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.Windows | MacroDeckPlatform.MacOs;

	public IActionExecutor CreateExecutor() => new Executor(this);

	// "Note C4 (60)" or "CC 74"
	internal static string Describe(LearnedCode code) => code.Kind == MidiKinds.ControlChange
		? string.Create(CultureInfo.InvariantCulture, $"CC {code.Number}")
		: string.Create(CultureInfo.InvariantCulture, $"Note {RouterEvents.NoteName(code.Number)} ({code.Number})");

	private sealed class Executor(LearnCodeAction action) : IActionExecutor
	{
		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			if (action.Router.Status.OpenInputs == 0)
			{
				var noKeyboards = Strings.Actions.Learn.NoKeyboards();
				await NotifyAsync(noKeyboards, null, NotificationSeverity.Warning, context.CancellationToken).ConfigureAwait(false);
				return ActionResult.Failed("no-keyboards", noKeyboards);
			}

			var learned = new TaskCompletionSource<LearnedCode>(TaskCreationOptions.RunContinuationsAsynchronously);
			using var learn = action.Router.Learn(null, code => learned.TrySetResult(code));

			var listening = await ShowAsync(
				new NotificationOptions
				{
					Title = Strings.Actions.Learn.Listening.Title(),
					Message = Strings.Actions.Learn.Listening.Message(),
					Duration = Timeout,
				},
				context.CancellationToken).ConfigureAwait(false);

			LearnedCode code;
			try
			{
				code = await learned.Task.WaitAsync(Timeout, context.CancellationToken).ConfigureAwait(false);
			}
			catch (TimeoutException)
			{
				var nothing = Strings.Actions.Learn.Nothing();
				await FinishAsync(listening, nothing, null, NotificationSeverity.Warning).ConfigureAwait(false);
				return ActionResult.Failed("timeout", nothing);
			}
			catch (OperationCanceledException)
			{
				await DismissAsync(listening).ConfigureAwait(false);
				throw;
			}

			action.Router.LastLearned = code;
			await FinishAsync(
				listening,
				Strings.Actions.Learn.Learned(Describe(code)),
				Strings.Actions.Learn.LearnedDetail(code.Device, code.Channel.ToString(CultureInfo.InvariantCulture)),
				NotificationSeverity.Success).ConfigureAwait(false);

			return ActionResult.Success();
		}

		private async Task NotifyAsync(LocalizedText title, LocalizedText? message, NotificationSeverity severity, CancellationToken cancellationToken) =>
			await ShowAsync(new NotificationOptions { Title = title, Message = message, Severity = severity }, cancellationToken).ConfigureAwait(false);

				private async Task<INotificationHandle?> ShowAsync(NotificationOptions options, CancellationToken cancellationToken)
		{
			if (action.Notifications() is not { } service)
			{
				return null;
			}

			try
			{
				return await service.ShowAsync(options, cancellationToken).ConfigureAwait(false);
			}
			catch (NotificationException)
			{
				return null;
			}
		}

		private async Task FinishAsync(INotificationHandle? listening, LocalizedText title, LocalizedText? message, NotificationSeverity severity)
		{
			await DismissAsync(listening).ConfigureAwait(false);
			await NotifyAsync(title, message, severity, CancellationToken.None).ConfigureAwait(false);
		}

		private static async Task DismissAsync(INotificationHandle? listening)
		{
			if (listening is null)
			{
				return;
			}

			try
			{
				await listening.DismissAsync().ConfigureAwait(false);
			}
			catch (NotificationException)
			{
				// Already closed
			}
		}
	}
}
