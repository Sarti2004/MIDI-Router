using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Config;
using MacroDeck.Ui.Config.Options;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Runtime;
using Serilog;

namespace MIDIRouter.Deck;

/// <summary>
/// Spike for the deck editor page. What earlier rounds found: components (UiGrid, UiButton) draw nothing on a plugin
/// page, whether inside the config stack or as the page root, and config buttons ignore RowWeight and size to their
/// label. This round draws the grid as icon-only config buttons, which the host draws as equal squares, and tries
/// selection: pressing an element highlights every cell it covers, pressing an empty cell places one.
/// </summary>
internal sealed class DeckEditorView : IDisposable
{
	public const string PageId = "decks";

	// Not a file: an empty 6 x 4 grid to place elements on.
	private static readonly DeckLayout BlankDeck = new("blank-6x4", "Blank 6 × 4 (not saved)", 6, 4, []);

	private const string PlaceButton = "button";
	private const string PlaceSmall = "small";
	private const string PlaceSlider = "slider";

	private readonly DeckStore _store;
	private readonly ILogger _logger;
	private readonly UiState<int> _revision = new(0);
	private readonly UiState<string> _deckId = new(BlankDeck.Id);
	private readonly UiState<string> _placeMode = new(PlaceButton);
	private readonly UiState<string?> _selected = new(null);
	private readonly UiState<string> _status = new("Press an empty cell to place, or an element to select it.");
	private readonly UiState<IReadOnlyDictionary<(int X, int Y), string>> _temporary = new(new Dictionary<(int X, int Y), string>());

	public DeckEditorView(DeckStore store, ILogger logger)
	{
		_store = store;
		_logger = logger.ForContext<DeckEditorView>();
		_store.Changed += OnStoreChanged;
	}

	public void Dispose() => _store.Changed -= OnStoreChanged;

	public UiConfigStack Root() => new()
	{
		Key = "decks-root",
		Children =
		[
			new UiHeading { Key = "title", Text = "Decks (editor spike)" },
			new UiChoiceInput
			{
				Key = "deck",
				Label = "Deck",
				Options = UiValue.From<IReadOnlyList<UiOption>>(() =>
				{
					_ = _revision.Value;
					return [.. _store.Snapshot.Decks.Prepend(BlankDeck).Select(deck => UiOption.Of(deck.Id, LocalizedText.FromLiteral(deck.Name)))];
				}),
				Binding = Bind.To(_deckId),
			},
			new UiChoiceInput
			{
				Key = "place",
				Label = "Press an empty cell to place",
				Options = UiValue.Of<IReadOnlyList<UiOption>>(
				[
					UiOption.Of(PlaceButton, LocalizedText.FromLiteral("Button")),
					UiOption.Of(PlaceSmall, LocalizedText.FromLiteral("Two small buttons")),
					UiOption.Of(PlaceSlider, LocalizedText.FromLiteral("Slider")),
				]),
				Binding = Bind.To(_placeMode),
			},
			// Keyed by everything the grid shows, so any change rebuilds it.
			new UiRepeat<string>
			{
				Key = "grid-repeat",
				Items = UiValue.From<IReadOnlyList<string>>(() =>
				[
					string.Create(CultureInfo.InvariantCulture,
						$"{_revision.Value}-{_deckId.Value}-{_selected.Value}-{string.Join('_', _temporary.Value.Select(cell => $"{cell.Key.X}x{cell.Key.Y}{cell.Value}"))}"),
				]),
				KeySelector = key => key,
				Template = (_, _) => IconGrid(CurrentDeck()),
			},
			new UiBanner { Key = "status", Severity = "info", Text = UiText.From(() => _status.Value) },
		],
	};

	// One row of icon-only config buttons per grid row. A control covering several cells shows in each of them, all
	// highlighted when it is selected.
	private UiConfigStack IconGrid(DeckLayout deck)
	{
		var temporary = _temporary.Peek();
		var selected = _selected.Peek();
		return new UiConfigStack
		{
			Key = "grid",
			Children =
			[
				.. Enumerable.Range(0, deck.Rows).Select(y => new UiConfigStack
				{
					Key = string.Create(CultureInfo.InvariantCulture, $"row-{y}"),
					Direction = UiComponentDirections.Horizontal,
					Children =
					[
						.. Enumerable.Range(0, deck.Columns).Select(x =>
						{
							var control = deck.Controls.FirstOrDefault(control =>
								x >= control.X && x < control.X + control.Width && y >= control.Y && y < control.Y + control.Height);
							var (id, label, icon) = control is not null
								? (control.Id, control.Label, control.Kind == DeckControlKind.Fader ? UiIcons.Sliders : UiIcons.ActionButtonType)
								: temporary.TryGetValue((x, y), out var kind)
									? (TemporaryId(x, y), Describe(kind), Icon(kind))
									: ((string?)null, "Empty", UiIcons.Plus);
							var variant = id is null ? UiButtonVariants.Ghost
								: id == selected ? UiButtonVariants.Primary
								: UiButtonVariants.Secondary;
							return new UiConfigButton
							{
								Key = string.Create(CultureInfo.InvariantCulture, $"cell-{x}-{y}"),
								Label = label,
								Icon = icon,
								Variant = variant,
								Events = [UiEventHandler.On(UiConfigEvents.Activate, () => Pressed(x, y, id, label))],
							};
						}),
					],
				}),
			],
		};
	}

	private void Pressed(int x, int y, string? id, string label)
	{
		_logger.Information("Editor spike: press on cell {X},{Y} ({Element}).", x, y, id ?? "empty");
		if (id is null)
		{
			var mode = _placeMode.Peek();
			_temporary.Set(new Dictionary<(int X, int Y), string>(_temporary.Peek()) { [(x, y)] = mode });
			_selected.Set(TemporaryId(x, y));
			_status.Set(string.Create(CultureInfo.InvariantCulture, $"Placed {Describe(mode)} at {x},{y} ({DateTime.Now:HH:mm:ss.fff})."));
			return;
		}

		_selected.Set(id);
		_status.Set(string.Create(CultureInfo.InvariantCulture, $"Selected {label} ({id}) from cell {x},{y} ({DateTime.Now:HH:mm:ss.fff})."));
	}

	private static string TemporaryId(int x, int y) => string.Create(CultureInfo.InvariantCulture, $"new-{x}-{y}");

	private static string Describe(string kind) => kind switch
	{
		PlaceSmall => "Two small buttons",
		PlaceSlider => "Slider",
		_ => "Button",
	};

	private static string Icon(string kind) => kind switch
	{
		PlaceSmall => UiIcons.Grid,
		PlaceSlider => UiIcons.Sliders,
		_ => UiIcons.ActionButtonType,
	};

	private DeckLayout CurrentDeck()
	{
		var id = _deckId.Peek();
		return _store.Snapshot.Decks.FirstOrDefault(deck => string.Equals(deck.Id, id, StringComparison.Ordinal)) ?? BlankDeck;
	}

	private void OnStoreChanged()
	{
		_temporary.Set(new Dictionary<(int X, int Y), string>());
		_selected.Set(null);
		_revision.Set(_revision.Peek() + 1);
	}
}
