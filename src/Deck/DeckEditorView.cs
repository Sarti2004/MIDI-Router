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
/// Spike for the deck editor page. Answers before the editor is built:
/// 1. Does a page render a UiGrid with spans, and do placeholder cells keep every control at its x/y?
/// 2. Which tap events reach a grid cell (press, tap, long-press)?
/// 3. How fast does the page redraw? Pressing an empty cell places a temporary element there.
/// 4. Does a half-cell pair of small buttons (a vertical stack in one cell) render and take presses per half?
/// 5. What does a drag report? Drag a cell by exactly one cell and read drag-end's payload: it is the distance
///    moved, and the unit decides whether drag-to-move can work out the target cell.
/// </summary>
internal sealed class DeckEditorView : IDisposable
{
	public const string PageId = "decks";

	// Not a file: an empty 6 x 4 grid to place elements on.
	private static readonly DeckLayout BlankDeck = new("blank-6x4", "Blank 6 × 4 (not saved)", 6, 4, []);

	private const string PlaceButton = "button";
	private const string PlaceSmall = "small";

	private const string DefaultPadColor = "#2563EB";
	private const string FaderColor = "#374151";
	private const string EmptyColor = "#1F2937";
	private const string TemporaryColor = "#7C3AED";
	private const string SmallColor = "#0E7490";

	private readonly DeckStore _store;
	private readonly ILogger _logger;
	private readonly UiState<int> _revision = new(0);
	private readonly UiState<string> _deckId = new(BlankDeck.Id);
	private readonly UiState<string> _placeMode = new(PlaceButton);
	private readonly UiState<string> _lastEvent = new("Press a cell.");
	private readonly UiState<IReadOnlyDictionary<(int X, int Y), string>> _temporary = new(new Dictionary<(int X, int Y), string>());
	private int _dragEvents;

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
						$"{_revision.Value}-{_deckId.Value}-{string.Join('_', _temporary.Value.Select(cell => $"{cell.Key.X}x{cell.Key.Y}{cell.Value}"))}"),
				]),
				KeySelector = key => key,
				Template = (_, _) => Grid(),
			},
			new UiBanner { Key = "last-event", Severity = "info", Text = UiText.From(() => _lastEvent.Value) },
		],
	};

	private UiElement Grid()
	{
		var deck = CurrentDeck();
		var temporary = _temporary.Peek();
		var covered = new bool[deck.Columns, deck.Rows];
		var cells = new List<UiElement>();
		for (var y = 0; y < deck.Rows; y++)
		{
			for (var x = 0; x < deck.Columns; x++)
			{
				if (deck.Controls.FirstOrDefault(control => control.X == x && control.Y == y) is { } control)
				{
					for (var cx = x; cx < x + control.Width; cx++)
					{
						for (var cy = y; cy < y + control.Height; cy++)
						{
							covered[cx, cy] = true;
						}
					}

					var color = control.Kind == DeckControlKind.Fader ? FaderColor : control.Color ?? DefaultPadColor;
					cells.Add(Cell(x, y, control.Width, control.Height, color, control.Label, control.Id));
				}
				else if (covered[x, y])
				{
					continue;
				}
				else if (temporary.TryGetValue((x, y), out var kind))
				{
					cells.Add(kind == PlaceSmall ? SmallPair(x, y) : Cell(x, y, 1, 1, TemporaryColor, "New", "new"));
				}
				else
				{
					cells.Add(Cell(x, y, 1, 1, EmptyColor, "+", null));
				}
			}
		}

		return new UiGrid
		{
			Key = "grid",
			Columns = deck.Columns,
			Rows = deck.Rows,
			Gap = 8,
			MinCellSize = 64,
			Children = cells,
		};
	}

	private UiButton Cell(int x, int y, int width, int height, string color, string label, string? controlId) =>
		Button(Key(x, y), width, height, color, label, Events(x, y, controlId));

	// One grid cell holding two half-height buttons, the "small button" of the editor.
	private UiStack SmallPair(int x, int y) => new()
	{
		Key = Key(x, y),
		Direction = UiComponentDirections.Vertical,
		Gap = 4,
		Children =
		[
			Button(Key(x, y) + "-top", 1, 1, SmallColor, "S1", Events(x, y, "small-top"), fill: true),
			Button(Key(x, y) + "-bottom", 1, 1, SmallColor, "S2", Events(x, y, "small-bottom"), fill: true),
		],
	};

	private static UiButton Button(string key, int width, int height, string color, string label, IReadOnlyList<UiEventHandler> events, bool fill = false) => new()
	{
		Key = key,
		ColumnSpan = width,
		RowSpan = height,
		Fill = fill,
		Background = color,
		Corner = UiComponentButtonCorners.Tile,
		Children = [new UiTextRun { Key = key + "-label", Text = label, Align = "center", Role = "primary" }],
		Events = events,
	};

	private UiEventHandler[] Events(int x, int y, string? controlId) =>
	[
		UiEventHandler.On(UiComponentEvents.Press, () => Pressed(x, y, controlId, UiComponentEvents.Press)),
		UiEventHandler.On(UiComponentEvents.Tap, () => Pressed(x, y, controlId, UiComponentEvents.Tap)),
		UiEventHandler.On(UiComponentEvents.LongPress, () => Pressed(x, y, controlId, UiComponentEvents.LongPress)),
		UiEventHandler.On(UiComponentEvents.Drag, _ => Interlocked.Increment(ref _dragEvents)),
		UiEventHandler.On(UiComponentEvents.DragEnd, data => DragEnded(x, y, controlId, data)),
	];

	private void Pressed(int x, int y, string? controlId, string eventName)
	{
		_logger.Information("Editor spike: {Event} on cell {X},{Y} ({Control}).", eventName, x, y, controlId ?? "empty");
		_lastEvent.Set(string.Create(CultureInfo.InvariantCulture, $"{eventName} on cell {x},{y}: {controlId ?? "empty"} ({DateTime.Now:HH:mm:ss.fff})"));

		// Press an empty cell to place; long-press a placed element to remove it, so presses on it can be tested.
		var temporary = new Dictionary<(int X, int Y), string>(_temporary.Peek());
		var changed = eventName switch
		{
			UiComponentEvents.Press when controlId is null => temporary.TryAdd((x, y), _placeMode.Peek()),
			UiComponentEvents.LongPress => temporary.Remove((x, y)),
			_ => false,
		};

		if (changed)
		{
			_temporary.Set(temporary);
		}
	}

	private void DragEnded(int x, int y, string? controlId, UiEventData data)
	{
		var moves = Interlocked.Exchange(ref _dragEvents, 0);
		var payload = data.Raw?.GetRawText() ?? "null";
		_logger.Information("Editor spike: drag-end on cell {X},{Y} ({Control}) after {Moves} drag events: {Payload}.",
			x, y, controlId ?? "empty", moves, payload);
		_lastEvent.Set(string.Create(CultureInfo.InvariantCulture, $"drag-end on cell {x},{y} after {moves} drag events: {payload}"));
	}

	private DeckLayout CurrentDeck()
	{
		var id = _deckId.Peek();
		return _store.Snapshot.Decks.FirstOrDefault(deck => string.Equals(deck.Id, id, StringComparison.Ordinal)) ?? BlankDeck;
	}

	private void OnStoreChanged()
	{
		_temporary.Set(new Dictionary<(int X, int Y), string>());
		_revision.Set(_revision.Peek() + 1);
	}

	private static string Key(int x, int y) => string.Create(CultureInfo.InvariantCulture, $"cell-{x}-{y}");
}
