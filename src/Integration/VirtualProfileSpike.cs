using System.Text.Json;
using MacroDeck.Plugin.Hosting.Integrations.HostApis;
using MacroDeck.Sdk.Profiles;
using MacroDeck.Sdk.Variables;
using Serilog;

namespace MIDIRouter.Integration;

/// <summary>
/// Throwaway spike: answers what a virtual profile can do before an exporter is built on it.
/// 1. Which widget types and data render (built-in ActionButton / Slider, and an unknown type).
/// 2. Which trigger types arrive on a press (press and release, or only short and long press).
/// 3. Whether CatalogChanged("virtual-profiles") refreshes the deck live ("Refresh" bumps a revision).
/// 4. Whether a Slider bound to a writable plugin variable hands its value to SetValueAsync.
/// </summary>
internal sealed class VirtualProfileSpike(IPluginCatalogNotifier catalogNotifier, ILogger logger)
{
	private const string ProfileId = "midi-router-spike";
	private const string FolderId = "midi-router-spike-home";
	private const string RefreshWidgetId = "refresh";
	private const string FaderVariableName = "midi_router_spike_fader";

	public static readonly VariableDefinition FaderVariable =
		VariableDefinition.Eager(FaderVariableName, VariableType.Numeric) with { Id = "spike-fader", Write = new VariableWriteCapability() };

	private readonly ILogger _logger = logger.ForContext<VirtualProfileSpike>();
	private int _revision;
	private double _faderValue;

	public IReadOnlyList<VirtualProfileDescriptor> GetProfiles()
	{
		var revision = Volatile.Read(ref _revision);
		_logger.Information("Spike: virtual profiles requested (revision {Revision}).", revision);

		return
		[
			new VirtualProfileDescriptor(
				ProfileId,
				"MIDI Router spike",
				ProfileLayout.Grid(rows: 3, columns: 4),
				[
					new VirtualFolderDescriptor(
						FolderId,
						"Home",
						[
							Widget("pad-plain", "ActionButton", 0, 0, data: new { label = "Pad 1" }),
							Widget("pad-colored", "ActionButton", 1, 0, data: new { label = "Pad 2", backgroundColor = "#2E7D32" }),
							Widget("pad-wide", "ActionButton", 2, 0, width: 2, data: new { label = "Wide pad" }),
							Widget("slider-h", "Slider", 0, 1, width: 2, data: new { orientation = "horizontal", label = "Fader H", valueVariable = FaderVariableName, min = 0, max = 127, step = 1, showValue = true }),
							Widget("slider-v", "Slider", 2, 1, height: 2, data: new { orientation = "vertical", label = "Fader V", valueVariable = FaderVariableName, min = 0, max = 127, step = 1, showValue = true }),
							Widget("unknown-type", "com.sarti2004.midi-router::pad", 3, 1, data: new { label = "Custom type" }),
							Widget("no-data", "ActionButton", 0, 2),
							Widget(RefreshWidgetId, "ActionButton", 1, 2, data: new { label = $"Refresh (rev {revision})" }),
						]),
				]),
		];
	}

	public Task HandleWidgetInteractionAsync(string profileId, string folderId, string widgetId, WidgetInteraction interaction)
	{
		_logger.Information(
			"Spike: interaction {TriggerType} on widget {WidgetId} (profile {ProfileId}, folder {FolderId}).",
			interaction.TriggerType, widgetId, profileId, folderId);

		if (string.Equals(widgetId, RefreshWidgetId, StringComparison.Ordinal))
		{
			Interlocked.Increment(ref _revision);
			catalogNotifier.CatalogChanged("virtual-profiles", "provider", "Spike revision bumped");
		}

		return Task.CompletedTask;
	}

	public static bool Owns(string localId) => string.Equals(localId, FaderVariable.Id, StringComparison.Ordinal);

	public VariableReading ReadFader() => VariableReading.Of(Volatile.Read(ref _faderValue), 0, 127, 1);

	public VariableWriteResult WriteFader(object? value)
	{
		_logger.Information("Spike: fader write {Value} ({ValueType}).", value, value?.GetType().Name ?? "null");
		if (double.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
		{
			Volatile.Write(ref _faderValue, number);
		}

		return VariableWriteResult.Applied();
	}

	private static VirtualWidgetDescriptor Widget(string id, string type, int x, int y, int width = 1, int height = 1, object? data = null) =>
		new(id, type, x, y, width, height, data is null ? null : JsonSerializer.Serialize(data));
}
