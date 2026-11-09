using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MacroDeck.Localization;
using MacroDeck.Sdk.Profiles;
using MacroDeck.Sdk.Variables;
using MIDIRouter.Routing;

namespace MIDIRouter.Deck;

/// <summary>
/// Shows each deck as a read-only virtual profile. Pads are ActionButtons, which report only a short press, so a
/// pad sends a press and its release at once. Faders are Sliders bound to a writable variable of their own: Macro Deck
/// writes the slider value to it, and the write sends the CC.
/// </summary>
internal sealed class DeckSurface(DeckStore store, MidiRouter router)
{
	private const string FolderPrefix = "deck:";
	private const string FaderPrefix = "fader:";
	private const string ShortPress = "onShortPress";

	private static readonly JsonSerializerOptions WidgetJson = new()
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	private readonly ConcurrentDictionary<string, int> _faderValues = new(StringComparer.Ordinal);

	public IReadOnlyList<VirtualProfileDescriptor> GetProfiles() =>
	[
		.. store.Snapshot.Decks.Select(deck => new VirtualProfileDescriptor(
			"deck-" + deck.Id,
			deck.Name,
			ProfileLayout.Grid(deck.Rows, deck.Columns),
			[new VirtualFolderDescriptor(FolderPrefix + deck.Id, deck.Name, [.. deck.Controls.Select(control => Widget(deck, control))])])),
	];

	public IReadOnlyList<VariableDefinition> Variables() =>
	[
		.. store.Snapshot.Decks.SelectMany(deck => deck.Controls
			.Where(control => control.Kind == DeckControlKind.Fader)
			.Select(control => VariableDefinition.Eager(VariableName(deck, control), VariableType.Numeric) with
			{
				Id = FaderId(deck, control),
				DisplayName = LocalizedText.FromLiteral($"{deck.Name}: {control.Label}"),
				Write = new VariableWriteCapability(),
			})),
	];

	public static bool OwnsVariable(string localId) => localId.StartsWith(FaderPrefix, StringComparison.Ordinal);

	// The host sends an empty profile id, so the folder id identifies the deck.
	public Task PressAsync(string folderId, string widgetId, WidgetInteraction interaction)
	{
		if (string.Equals(interaction.TriggerType, ShortPress, StringComparison.Ordinal)
			&& FindDeck(folderId) is { } deck
			&& deck.Controls.FirstOrDefault(control => string.Equals(control.Id, widgetId, StringComparison.Ordinal)) is { Kind: DeckControlKind.Pad } pad)
		{
			if (pad.Message == DeckMessage.Note)
			{
				router.SendNoteOn(pad.Channel, pad.Number, 127);
				router.SendNoteOff(pad.Channel, pad.Number);
			}
			else
			{
				router.SendControlChange(pad.Channel, pad.Number, 127);
				router.SendControlChange(pad.Channel, pad.Number, 0);
			}
		}

		return Task.CompletedTask;
	}

	public VariableReading Read(string localId) =>
		FindFader(localId) is null
			? VariableReading.Unavailable
			: VariableReading.Of(_faderValues.GetValueOrDefault(localId), 0, 127, 1);

	public VariableWriteResult Write(string localId, object? value)
	{
		if (FindFader(localId) is not { } fader)
		{
			return VariableWriteResult.NotFound();
		}

		if (!double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
			|| double.IsNaN(number))
		{
			return VariableWriteResult.Unavailable();
		}

		var midiValue = (int)Math.Clamp(Math.Round(number), 0, 127);
		if (_faderValues.TryGetValue(localId, out var previous) && previous == midiValue)
		{
			return VariableWriteResult.Applied();
		}

		_faderValues[localId] = midiValue;
		router.SendControlChange(fader.Channel, fader.Number, midiValue);
		return VariableWriteResult.Applied();
	}

	private static VirtualWidgetDescriptor Widget(DeckLayout deck, DeckControl control) => control.Kind == DeckControlKind.Pad
		? new(control.Id, "ActionButton", control.X, control.Y, control.Width, control.Height,
			JsonSerializer.Serialize(new { label = control.Label, backgroundColor = control.Color }, WidgetJson))
		: new(control.Id, "Slider", control.X, control.Y, control.Width, control.Height,
			JsonSerializer.Serialize(new
			{
				orientation = control.Vertical ? "vertical" : "horizontal",
				label = control.Label,
				color = control.Color,
				valueVariable = VariableName(deck, control),
				min = 0,
				max = 127,
				step = 1,
				showValue = true,
			}, WidgetJson));

	private DeckLayout? FindDeck(string folderId) =>
		folderId.StartsWith(FolderPrefix, StringComparison.Ordinal)
			? store.Snapshot.Decks.FirstOrDefault(deck => string.Equals(deck.Id, folderId[FolderPrefix.Length..], StringComparison.Ordinal))
			: null;

	private DeckControl? FindFader(string localId) =>
		store.Snapshot.Decks
			.SelectMany(deck => deck.Controls.Where(control => control.Kind == DeckControlKind.Fader).Select(control => (deck, control)))
			.FirstOrDefault(pair => string.Equals(FaderId(pair.deck, pair.control), localId, StringComparison.Ordinal))
			.control;

	private static string FaderId(DeckLayout deck, DeckControl control) => $"{FaderPrefix}{deck.Id}:{control.Id}";

	private static string VariableName(DeckLayout deck, DeckControl control) =>
		VariableNames.Sanitize($"midi_router_deck_{deck.Id}_{control.Id}");
}
