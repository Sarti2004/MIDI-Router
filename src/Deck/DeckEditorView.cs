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
/// 3. How fast does the page redraw? Tapping an empty cell adds a temporary pad there, tapping it again removes it.
/// </summary>
internal sealed class DeckEditorView : IDisposable
{
	public const string PageId = "decks";

	private const string DefaultPadColor = "#2563EB";
	private const string FaderColor = "#374151";
	private const string EmptyColor = "#1F2937";
	private const string TemporaryColor = "#7C3AED";

	private readonly DeckStore _store;
	private readonly ILogger _logger;
	private readonly UiState<int> _revision = new(0);
	private readonly UiState<string> _deckId = new(string.Empty);
	private readonly UiState<string> _lastTap = new("Tap a cell.");
	private readonly UiState<IReadOnlySet<(int X, int Y)>> _temporary = new(new HashSet<(int X, int Y)>());

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
					return [.. _store.Snapshot.Decks.Select(deck => UiOption.Of(deck.Id, LocalizedText.FromLiteral(deck.Name)))];
				}),
				Binding = Bind.To(_deckId),
			},
			// Keyed by everything the grid shows, so any change rebuilds it.
			new UiRepeat<string>
			{
				Key = "grid-repeat",
				Items = UiValue.From<IReadOnlyList<string>>(() =>
				[
					string.Create(CultureInfo.InvariantCulture,
						$"{_revision.Value}-{_deckId.Value}-{string.Join('_', _temporary.Value.Select(cell => $"{cell.X}x{cell.Y}"))}"),
				]),
				KeySelector = key => key,
				Template = (_, _) => Grid(),
			},
			new UiBanner { Key = "last-tap", Severity = "info", Text = UiText.From(() => _lastTap.Value) },
		],
	};

	private UiElement Grid()
	{
		if (CurrentDeck() is not { } deck)
		{
			return new UiBanner { Key = "no-decks", Severity = "warning", Text = $"No decks in {DeckStore.Folder}." };
		}

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
				else if (!covered[x, y])
				{
					cells.Add(temporary.Contains((x, y))
						? Cell(x, y, 1, 1, TemporaryColor, "New", null)
						: Cell(x, y, 1, 1, EmptyColor, "+", null));
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

	private UiButton Cell(int x, int y, int width, int height, string color, string label, string? controlId) => new()
	{
		Key = string.Create(CultureInfo.InvariantCulture, $"cell-{x}-{y}"),
		ColumnSpan = width,
		RowSpan = height,
		Background = color,
		Corner = UiComponentButtonCorners.Tile,
		Children = [new UiTextRun { Key = string.Create(CultureInfo.InvariantCulture, $"cell-{x}-{y}-label"), Text = label, Align = "center", Role = "primary" }],
		Events =
		[
			UiEventHandler.On(UiComponentEvents.Press, () => Tapped(x, y, controlId, UiComponentEvents.Press)),
			UiEventHandler.On(UiComponentEvents.Tap, () => Tapped(x, y, controlId, UiComponentEvents.Tap)),
			UiEventHandler.On(UiComponentEvents.LongPress, () => Tapped(x, y, controlId, UiComponentEvents.LongPress)),
		],
	};

	private void Tapped(int x, int y, string? controlId, string eventName)
	{
		_logger.Information("Editor spike: {Event} on cell {X},{Y} ({Control}).", eventName, x, y, controlId ?? "empty");
		_lastTap.Set(string.Create(CultureInfo.InvariantCulture, $"{eventName} on cell {x},{y}: {controlId ?? "empty"} ({DateTime.Now:HH:mm:ss.fff})"));

		if (controlId is null && eventName == UiComponentEvents.Press)
		{
			var temporary = new HashSet<(int X, int Y)>(_temporary.Peek());
			if (!temporary.Remove((x, y)))
			{
				temporary.Add((x, y));
			}

			_temporary.Set(temporary);
		}
	}

	private DeckLayout? CurrentDeck()
	{
		var decks = _store.Snapshot.Decks;
		var id = _deckId.Peek();
		return decks.FirstOrDefault(deck => string.Equals(deck.Id, id, StringComparison.Ordinal)) ?? (decks.Count > 0 ? decks[0] : null);
	}

	private void OnStoreChanged()
	{
		_temporary.Set(new HashSet<(int X, int Y)>());
		_revision.Set(_revision.Peek() + 1);
	}
}
