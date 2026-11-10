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
/// Shows each deck as a read-only virtual profile. Buttons are ActionButtons, which report only a short press, so a
/// button sends a press and its release at once. Sliders are Sliders bound to a writable variable of their own: Macro
/// Deck writes the slider value to it, and the write sends the CC. A profile has no half cells, so a small button takes
/// its whole cell, and the bottom one of a pair is left out.
/// </summary>
internal sealed class DeckSurface(DeckStore store, MidiRouter router)
{
	private const string ProfilePrefix = "deck-";
	private const string FolderPrefix = "folder-";
	private const string FaderPrefix = "fader-";
	private const string ShortPress = "onShortPress";

	private static readonly JsonSerializerOptions WidgetJson = new()
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	private readonly ConcurrentDictionary<string, int> _faderValues = new(StringComparer.Ordinal);

	public IReadOnlyList<VirtualProfileDescriptor> GetProfiles() =>
	[
		.. store.Snapshot.Decks.Select(deck => new VirtualProfileDescriptor(
			ProfilePrefix + deck.Id,
			deck.Name,
			ProfileLayout.Grid(deck.Rows, deck.Columns),
			[new VirtualFolderDescriptor(FolderPrefix + deck.Id, deck.Name, [.. deck.Controls.Where(Shown).Select(control => Widget(deck, control))])])),
	];

	public IReadOnlyList<VariableDefinition> Variables() =>
	[
		.. store.Snapshot.Decks.SelectMany(deck => deck.Controls
			.Where(control => control.IsSlider)
			.Select(control => VariableDefinition.Eager(VariableName(deck, control), VariableType.Numeric) with
			{
				Id = deck.FaderVariableId(control),
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
			&& deck.Controls.FirstOrDefault(control => string.Equals(control.Id, widgetId, StringComparison.Ordinal)) is { IsSlider: false } button)
		{
			switch (button.Message)
			{
				case DeckMessage.Note:
					router.SendNoteOn(button.Channel, button.Number, 127);
					router.SendNoteOff(button.Channel, button.Number);
					break;
				case DeckMessage.ControlChange:
					router.SendControlChange(button.Channel, button.Number, 127);
					router.SendControlChange(button.Channel, button.Number, 0);
					break;
				default:
					router.SendProgramChange(button.Channel, button.Number);
					break;
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

	private static bool Shown(DeckControl control) => control is not { Kind: DeckControlKind.SmallButton, Slot: 1 };

	private static VirtualWidgetDescriptor Widget(DeckLayout deck, DeckControl control) => !control.IsSlider
		? new(control.Id, "ActionButton", control.X, control.Y, control.Width, control.Height,
			JsonSerializer.Serialize(new { label = control.Label, backgroundColor = control.Color }, WidgetJson))
		: new(control.Id, "Slider", control.X, control.Y, control.Width, control.Height,
			JsonSerializer.Serialize(new
			{
				orientation = control.Kind == DeckControlKind.VerticalSlider ? "vertical" : "horizontal",
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
			.SelectMany(deck => deck.Controls.Where(control => control.IsSlider).Select(control => (deck, control)))
			.FirstOrDefault(pair => string.Equals(pair.deck.FaderVariableId(pair.control), localId, StringComparison.Ordinal))
			.control;

	private static string VariableName(DeckLayout deck, DeckControl control) =>
		VariableNames.Sanitize($"midi_router_deck_{deck.Id}_{control.Id}".Replace('-', '_'));
}
