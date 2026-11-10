using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Notifications;
using MIDIRouter.Deck;
using MIDIRouter.Routing;

namespace MIDIRouter.Actions;

internal sealed class ExportCubaseAction(MidiRouter router, DeckStore store, Func<INotificationService?> notifications) : IActionDefinition
{
	internal MidiRouter Router { get; } = router;

	internal DeckStore Store { get; } = store;

	internal Func<INotificationService?> Notifications { get; } = notifications;

	public string Id => "export-cubase";

	public LocalizedText Name => Strings.Actions.ExportCubase.Name();

	public LocalizedText Description => Strings.Actions.ExportCubase.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } = [];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.Windows | MacroDeckPlatform.MacOs;

	public IActionExecutor CreateExecutor() => new Executor(this);

	private sealed class Executor(ExportCubaseAction action) : IActionExecutor
	{
		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			var portName = action.Router.Config.OutputName;
			if (portName.Length == 0)
			{
				var noOutput = Strings.Actions.ExportCubase.NoOutput();
				await NotifyAsync(noOutput, null, NotificationSeverity.Warning).ConfigureAwait(false);
				return ActionResult.Failed("no-output", noOutput);
			}

			var snapshot = action.Store.Snapshot;
			if (snapshot.Errors.Count > 0)
			{
				var first = snapshot.Errors[0];
				await NotifyAsync(
					Strings.Actions.ExportCubase.Skipped(Count(snapshot.Errors.Count)),
					LocalizedText.FromLiteral($"{first.FileName}: {first.Message}"),
					NotificationSeverity.Warning).ConfigureAwait(false);
			}

			if (snapshot.Decks.Count == 0)
			{
				var noDecks = Strings.Actions.ExportCubase.NoDecks();
				await NotifyAsync(noDecks, Strings.Actions.ExportCubase.NoDecksDetail(DeckStore.Folder), NotificationSeverity.Warning)
					.ConfigureAwait(false);
				return ActionResult.Failed("no-decks", noDecks);
			}

			DeckExportResult result;
			try
			{
				result = await DeckExport.ExportAsync(snapshot.Decks, portName, context.CancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				var failed = Strings.Actions.ExportCubase.Failed();
				await NotifyAsync(failed, LocalizedText.FromLiteral(ex.Message), NotificationSeverity.Error).ConfigureAwait(false);
				return ActionResult.Failed("write-failed", failed);
			}

			var count = Count(result.Decks);
			await NotifyAsync(
				result.FallbackFolder is { } folder ? Strings.Actions.ExportCubase.ExportedToFolder(count) : Strings.Actions.ExportCubase.Exported(count, result.Apps),
				result.FallbackFolder is { } fallback ? Strings.Actions.ExportCubase.ExportedToFolderDetail(fallback) : Strings.Actions.ExportCubase.ExportedDetail(),
				NotificationSeverity.Success).ConfigureAwait(false);

			return ActionResult.Success();
		}

		private static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);

		private async Task NotifyAsync(LocalizedText title, LocalizedText? message, NotificationSeverity severity)
		{
			if (action.Notifications() is not { } service)
			{
				return;
			}

			try
			{
				await service.ShowAsync(new NotificationOptions { Title = title, Message = message, Severity = severity }).ConfigureAwait(false);
			}
			catch (NotificationException)
			{
				// Notifications are best effort
			}
		}
	}
}
