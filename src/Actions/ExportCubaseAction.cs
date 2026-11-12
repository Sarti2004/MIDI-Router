using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Notifications;
using MIDIRouter.Deck;

namespace MIDIRouter.Actions;

internal sealed class ExportCubaseAction(DeviceStore store, Func<INotificationService?> notifications) : IActionDefinition
{
	internal DeviceStore Store { get; } = store;

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
			if (action.Store.Device.Elements.Count == 0)
			{
				var empty = Strings.Actions.ExportCubase.NoElements();
				await NotifyAsync(empty, Strings.Actions.ExportCubase.NoElementsDetail(), NotificationSeverity.Warning).ConfigureAwait(false);
				return ActionResult.Failed("no-elements", empty);
			}

			DeckExportResult result;
			try
			{
				result = await DeckExport.ExportAsync(action.Store.Device, context.CancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				var failed = Strings.Actions.ExportCubase.Failed();
				await NotifyAsync(failed, LocalizedText.FromLiteral(ex.Message), NotificationSeverity.Error).ConfigureAwait(false);
				return ActionResult.Failed("write-failed", failed);
			}

			await NotifyAsync(
				result.FallbackFolder is not null ? Strings.Actions.ExportCubase.ExportedToFolder() : Strings.Actions.ExportCubase.Exported(result.Apps),
				result.FallbackFolder is { } fallback ? Strings.Actions.ExportCubase.ExportedToFolderDetail(fallback) : Strings.Actions.ExportCubase.ExportedDetail(),
				NotificationSeverity.Success).ConfigureAwait(false);

			return ActionResult.Success();
		}

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
