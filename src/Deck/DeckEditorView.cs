using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Config;
using MacroDeck.Ui.Config.Options;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Runtime;
using MIDIRouter.Routing;
using Serilog;

namespace MIDIRouter.Deck;

/// <summary>
/// The Decks page: edits a copy of a deck and saves it to the deck's file. Components (UiGrid, UiButton) draw nothing
/// on a plugin page and config buttons ignore RowWeight, so the grid is rows of icon-only config buttons, which the
/// host draws as equal squares. Settings of the selected element sit under the grid.
/// </summary>
internal sealed class DeckEditorView : IDisposable
{
	public const string PageId = "decks";

	// The action picker lists one integration's actions.
	private const string IntegrationId = "com.sarti2004.midi-router";
	private const string SendNoteAction = "send-note";
	private const string SendControlChangeAction = "send-cc";
	private const string SendProgramChangeAction = "send-program-change";

	private const string PlaceMode = "place";
	private const string MoveMode = "move";

	private const int FirstNote = 36;
	private const int FirstController = 20;
	private const int NewDeckColumns = 6;
	private const int NewDeckRows = 4;

	// The prompt's starting text and the name of a deck created without one: a value, so not localized.
	private const string NewDeckName = "New deck";

	private readonly DeckStore _store;
	private readonly MidiRouter _router;
	private readonly ILogger _logger;

	private readonly UiState<DeckLayout?> _draft = new(null);
	private readonly UiState<bool> _dirty = new(false);
	private readonly UiState<string?> _selected = new(null);
	private readonly UiState<string> _mode = new(PlaceMode);
	private readonly UiState<string> _placeKind = new(nameof(DeckControlKind.Button));
	private readonly UiState<int> _decksRevision = new(0);
	private readonly UiState<(LocalizedText Text, string Severity)> _status = new((Strings.Editor.Status.Hint(), "info"));

	// What the action picker sent last, before the action id: written back the same way, whatever its format.
	private string _actionPrefix = string.Empty;

	public DeckEditorView(DeckStore store, MidiRouter router, ILogger logger)
	{
		_store = store;
		_router = router;
		_logger = logger.ForContext<DeckEditorView>();
		Load(null);
		_store.Changed += OnStoreChanged;
	}

	public void Dispose() => _store.Changed -= OnStoreChanged;

	public UiConfigStack Root() => new()
	{
		Key = "decks-root",
		Children =
		[
			new UiHeading { Key = "title", Text = Strings.Editor.Title() },
			DeckRow(),
			new UiWhen
			{
				Key = "no-decks",
				Condition = () => _draft.Value is null,
				Content = () => new UiBanner { Key = "no-decks-banner", Severity = "info", Text = Strings.Editor.NoDecks() },
			},
			new UiWhen { Key = "editor", Condition = () => _draft.Value is not null, Content = Editor },
			new UiBanner { Key = "status", Severity = UiValue.From(() => _status.Value.Severity), Text = UiText.Optional(() => _status.Value.Text) },
		],
	};

	private UiConfigStack DeckRow() => new()
	{
		Key = "deck-row",
		Direction = UiComponentDirections.Horizontal,
		Children =
		[
			new UiChoiceInput
			{
				Key = "deck",
				Label = Strings.Editor.Deck(),
				RowWeight = 1,
				Options = UiValue.From<IReadOnlyList<UiOption>>(() =>
				{
					_ = _decksRevision.Value;
					return [.. _store.Snapshot.Decks.Select(deck => UiOption.Of(deck.Id, LocalizedText.FromLiteral(deck.Name)))];
				}),
				Binding = Bind.Custom(() => _draft.Value?.Id ?? string.Empty, SwitchDeck),
			},
			new UiConfigButton
			{
				Key = "new-deck",
				Label = Strings.Editor.NewDeck(),
				Icon = UiIcons.Plus,
				IconWithLabel = true,
				PromptValue = NewDeckName,
				Placeholder = Strings.Editor.NewDeckPrompt(),
				Events = [UiEventHandler.On(UiConfigEvents.Activate, data => CreateDeck(data.TryGetString(out var name) ? name : null))],
			},
			new UiWhen
			{
				Key = "delete-deck-when",
				Condition = () => _draft.Value is not null,
				Content = () => new UiConfigButton
				{
					Key = "delete-deck",
					Label = Strings.Editor.DeleteDeck(),
					Icon = UiIcons.Trash,
					Variant = UiButtonVariants.Danger,
					ConfirmMessage = UiText.Optional(() => _draft.Value is { } deck ? Strings.Editor.DeleteDeckConfirm(deck.Name) : UiText.None()),
					ConfirmDanger = true,
					Events = [UiEventHandler.On(UiConfigEvents.Activate, DeleteDeck)],
				},
			},
		],
	};

	private UiConfigStack Editor() => new()
	{
		Key = "editor-stack",
		Children =
		[
			DeckSettings(),
			new UiConfigStack
			{
				Key = "tools",
				Direction = UiComponentDirections.Horizontal,
				Children =
				[
					new UiChoiceInput
					{
						Key = "place-kind",
						Label = Strings.Editor.Place(),
						RowWeight = 1,
						Options = UiValue.Of<IReadOnlyList<UiOption>>([.. Enum.GetValues<DeckControlKind>().Select(kind => UiOption.Of(kind.ToString(), KindName(kind)))]),
						Binding = Bind.To(_placeKind),
					},
					new UiChoiceInput
					{
						Key = "mode",
						Label = Strings.Editor.Mode(),
						RowWeight = 1,
						Options = UiValue.Of<IReadOnlyList<UiOption>>(
						[
							UiOption.Of(PlaceMode, Strings.Editor.Modes.Place()),
							UiOption.Of(MoveMode, Strings.Editor.Modes.Move()),
						]),
						Binding = Bind.Custom(() => _mode.Value, mode =>
						{
							_mode.Set(mode);
							Status(mode == MoveMode ? Strings.Editor.Status.MoveHint() : Strings.Editor.Status.Hint(), "info");
						}),
					},
				],
			},
			// Rebuilt on every change to the deck or the selection.
			new UiRepeat<string>
			{
				Key = "grid-repeat",
				Items = UiValue.From<IReadOnlyList<string>>(() =>
				[
					string.Create(CultureInfo.InvariantCulture, $"{_draft.Value?.GetHashCode()}-{_selected.Value}"),
				]),
				KeySelector = key => key,
				Template = (_, _) => Grid(_draft.Peek()!),
			},
			// Rebuilt when another element is selected; its fields read the deck as it changes.
			new UiRepeat<string>
			{
				Key = "inspector-repeat",
				Items = UiValue.From<IReadOnlyList<string>>(() => _selected.Value is { } id ? [id] : []),
				KeySelector = id => id,
				Template = (_, _) => Inspector(),
			},
			new UiConfigStack
			{
				Key = "commands",
				Direction = UiComponentDirections.Horizontal,
				Children =
				[
					new UiConfigButton
					{
						Key = "save",
						Label = Strings.Editor.Save(),
						Icon = UiIcons.DeviceFloppy,
						IconWithLabel = true,
						Variant = UiValue.From(() => _dirty.Value ? UiButtonVariants.Primary : UiButtonVariants.Secondary),
						Events = [UiEventHandler.On(UiConfigEvents.Activate, Save)],
					},
					new UiConfigButton
					{
						Key = "revert",
						Label = Strings.Editor.Revert(),
						Icon = UiIcons.Undo,
						IconWithLabel = true,
						Variant = UiButtonVariants.Secondary,
						Events = [UiEventHandler.On(UiConfigEvents.Activate, () => Load(_draft.Peek()?.Id))],
					},
					new UiConfigButton
					{
						Key = "export",
						Label = Strings.Editor.Export(),
						Icon = UiIcons.Upload,
						IconWithLabel = true,
						Variant = UiButtonVariants.Secondary,
						Events = [UiEventHandler.On(UiConfigEvents.Activate, () => _ = ExportAsync())],
					},
				],
			},
		],
	};

	private UiConfigStack DeckSettings() => new()
	{
		Key = "deck-settings",
		Direction = UiComponentDirections.Horizontal,
		Children =
		[
			new UiStringInput
			{
				Key = "deck-name",
				Label = Strings.Editor.Name(),
				RowWeight = 2,
				Binding = Bind.Custom(() => _draft.Value?.Name ?? string.Empty, name => ChangeDeck(deck => deck with { Name = name })),
			},
			Number("columns", Strings.Editor.Columns(), 1, DeckLayout.MaxGridSize, () => _draft.Value?.Columns ?? 1,
				columns => ChangeDeck(deck => deck with { Columns = columns })),
			Number("rows", Strings.Editor.Rows(), 1, DeckLayout.MaxGridSize, () => _draft.Value?.Rows ?? 1,
				rows => ChangeDeck(deck => deck with { Rows = rows })),
			// Elements on the old default channel move with it, as they would in the file.
			Number("deck-channel", Strings.Editor.Channel(), 1, 16, () => _draft.Value?.Channel ?? DeckLayout.DefaultChannel,
				channel => ChangeDeck(deck => deck with
				{
					Channel = channel,
					Controls = [.. deck.Controls.Select(control => control.Channel == deck.Channel ? control with { Channel = channel } : control)],
				})),
		],
	};

	// One row of icon-only config buttons per grid row. An element covering several cells shows in each of them, all
	// highlighted when it is selected. A cell holding small buttons shows one icon for both halves.
	private UiConfigStack Grid(DeckLayout deck)
	{
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
							var here = deck.Controls.Where(control => control.Covers(x, y)).ToList();
							var label = here.Count == 0 ? Strings.Editor.EmptyCell() : LocalizedText.FromLiteral(string.Join(" / ", here.Select(control => control.Label)));
							var variant = here.Count == 0 ? UiButtonVariants.Ghost
								: here.Any(control => control.Id == selected) ? UiButtonVariants.Primary
								: UiButtonVariants.Secondary;
							return new UiConfigButton
							{
								Key = string.Create(CultureInfo.InvariantCulture, $"cell-{x}-{y}"),
								Label = label,
								Icon = here.Count == 0 ? UiIcons.Plus : Icon(here[0].Kind),
								Variant = variant,
								Events = [UiEventHandler.On(UiConfigEvents.Activate, () => CellPressed(x, y))],
							};
						}),
					],
				}),
			],
		};
	}

	private UiConfigStack Inspector() => new()
	{
		Key = "inspector",
		Children =
		[
			new UiHeading
			{
				Key = "selected",
				Text = UiText.Optional(() => Selected() is { } control ? Strings.Editor.Selected(control.Label, Position(control)) : UiText.None()),
			},
			new UiConfigStack
			{
				Key = "look",
				Direction = UiComponentDirections.Horizontal,
				Children =
				[
					new UiStringInput
					{
						Key = "label",
						Label = Strings.Editor.Label(),
						RowWeight = 2,
						Binding = Bind.Custom(() => Selected()?.Label ?? string.Empty, label => ChangeSelected(control => control with { Label = label })),
					},
					new UiColorInput
					{
						Key = "color",
						Label = Strings.Editor.Color(),
						RowWeight = 1,
						Binding = Bind.Custom(() => Selected()?.Color ?? string.Empty,
							color => ChangeSelected(control => control with { Color = string.IsNullOrWhiteSpace(color) ? null : color })),
					},
				],
			},
			new UiWhen
			{
				Key = "size-when",
				Condition = () => Selected() is { Kind: not DeckControlKind.SmallButton },
				Content = () => new UiConfigStack
				{
					Key = "size",
					Direction = UiComponentDirections.Horizontal,
					Children =
					[
						Number("width", Strings.Editor.Width(), 1, DeckLayout.MaxGridSize, () => Selected()?.Width ?? 1,
							width => ChangeSelected(control => control with { Width = width })),
						Number("height", Strings.Editor.Height(), 1, DeckLayout.MaxGridSize, () => Selected()?.Height ?? 1,
							height => ChangeSelected(control => control with { Height = height })),
					],
				},
			},
			new UiWhen
			{
				Key = "slot-when",
				Condition = () => Selected() is { Kind: DeckControlKind.SmallButton },
				Content = () => new UiChoiceInput
				{
					Key = "slot",
					Label = Strings.Editor.Slot(),
					Options = UiValue.Of<IReadOnlyList<UiOption>>(
					[
						UiOption.Of("0", Strings.Editor.Slots.Top()),
						UiOption.Of("1", Strings.Editor.Slots.Bottom()),
					]),
					Binding = Bind.Custom(() => Selected()?.Slot == 1 ? "1" : "0",
						slot => ChangeSelected(control => control with { Slot = slot == "1" ? 1 : 0 })),
				},
			},
			new UiActionPickerInput
			{
				Key = "action",
				Label = Strings.Editor.Action(),
				IntegrationId = IntegrationId,
				Binding = Bind.Custom(() => Selected() is { } control ? _actionPrefix + ActionId(control.Message) : string.Empty, ChangeAction),
			},
			new UiConfigStack
			{
				Key = "binding",
				Direction = UiComponentDirections.Horizontal,
				Children =
				[
					Number("channel", Strings.Editor.ElementChannel(), 1, 16, () => Selected()?.Channel ?? DeckLayout.DefaultChannel,
						channel => ChangeSelected(control => control with { Channel = channel })),
					Number("number", UiText.Optional(() => Selected()?.Message switch
						{
							DeckMessage.ControlChange => Strings.Editor.Numbers.Controller(),
							DeckMessage.ProgramChange => Strings.Editor.Numbers.Program(),
							_ => Strings.Editor.Numbers.Note(),
						}), 0, 127, () => Selected()?.Number ?? 0,
						number => ChangeSelected(control => control with { Number = number })),
				],
			},
			new UiConfigButton
			{
				Key = "delete-element",
				Label = Strings.Editor.DeleteElement(),
				Icon = UiIcons.Trash,
				IconWithLabel = true,
				Variant = UiButtonVariants.Danger,
				Events = [UiEventHandler.On(UiConfigEvents.Activate, DeleteSelected)],
			},
		],
	};

	private static UiNumberInput Number(string key, UiText label, int min, int max, Func<int> get, Action<int> set) => new()
	{
		Key = key,
		Label = label,
		RowWeight = 1,
		Min = min,
		Max = max,
		Step = 1,
		Binding = Bind.Custom(() => (double)get(), value => set((int)Math.Clamp(Math.Round(value), min, max))),
	};

	private void CellPressed(int x, int y)
	{
		if (_draft.Peek() is not { } deck)
		{
			return;
		}

		var selected = SelectedIn(deck);
		var full = deck.Controls.FirstOrDefault(control => control.Kind != DeckControlKind.SmallButton && control.Covers(x, y));
		var smalls = deck.Controls.Where(control => control.Kind == DeckControlKind.SmallButton && control.X == x && control.Y == y)
			.OrderBy(control => control.Slot).ToList();

		if (_mode.Peek() == MoveMode)
		{
			if (selected is null)
			{
				if ((full ?? smalls.FirstOrDefault()) is { } source)
				{
					Select(source);
					Status(Strings.Editor.Status.MoveTarget(source.Label), "info");
				}

				return;
			}

			if (!selected.Covers(x, y))
			{
				Move(deck, selected, x, y);
			}

			return;
		}

		if (full is not null)
		{
			Select(full);
		}
		else if (smalls.Count > 0)
		{
			// Pressing a small button's cell again goes to the other half, or places one there.
			if (selected is { Kind: DeckControlKind.SmallButton } small && small.X == x && small.Y == y)
			{
				if (smalls.FirstOrDefault(control => control.Slot != small.Slot) is { } other)
				{
					Select(other);
				}
				else if (_placeKind.Peek() == nameof(DeckControlKind.SmallButton))
				{
					Place(deck, NewControl(deck, DeckControlKind.SmallButton, x, y, 1 - small.Slot));
				}
			}
			else
			{
				Select(smalls[0]);
			}
		}
		else
		{
			Place(deck, NewControl(deck, Enum.Parse<DeckControlKind>(_placeKind.Peek()), x, y, 0));
		}
	}

	private void Move(DeckLayout deck, DeckControl control, int x, int y)
	{
		var moved = control with { X = x, Y = y };

		// A small button takes the free half of the cell it goes to.
		if (control.Kind == DeckControlKind.SmallButton && deck.PlacementError(moved) is not null)
		{
			moved = moved with { Slot = 1 - moved.Slot };
		}

		if (Replace(deck, control, moved))
		{
			Status(Strings.Editor.Status.Moved(control.Label, Position(moved)), "success");
		}
	}

	private void Place(DeckLayout deck, DeckControl control)
	{
		var placed = deck with { Controls = [.. deck.Controls, control] };
		if (placed.PlacementError(control) is { } error)
		{
			Status(LocalizedText.FromLiteral(error), "warning");
			return;
		}

		SetDraft(placed);
		Select(control);
	}

	// A new element sends the next note (buttons) or CC (sliders) nobody on the deck's channel uses yet.
	private static DeckControl NewControl(DeckLayout deck, DeckControlKind kind, int x, int y, int slot)
	{
		var slider = kind is DeckControlKind.VerticalSlider or DeckControlKind.HorizontalSlider;
		var message = slider ? DeckMessage.ControlChange : DeckMessage.Note;
		var used = deck.Controls.Where(control => control.Channel == deck.Channel && control.Message == message)
			.Select(control => control.Number).ToHashSet();
		var number = Enumerable.Range(0, 128).Select(n => ((slider ? FirstController : FirstNote) + n) % 128).FirstOrDefault(n => !used.Contains(n));

		var name = kind switch
		{
			DeckControlKind.SmallButton => "Small",
			DeckControlKind.VerticalSlider or DeckControlKind.HorizontalSlider => "Slider",
			_ => "Button",
		};
		var count = deck.Controls.Count(control => control.IsSlider == slider && (control.Kind == DeckControlKind.SmallButton) == (kind == DeckControlKind.SmallButton)) + 1;
		var label = string.Create(CultureInfo.InvariantCulture, $"{name} {count}");
		var id = DeckLayout.ToId(label, "control");
		for (var n = 2; deck.Controls.Any(control => control.Id == id); n++)
		{
			id = string.Create(CultureInfo.InvariantCulture, $"{DeckLayout.ToId(label, "control")}-{n}");
		}

		var control = new DeckControl(id, kind, label, x, y, 1, 1, slot, deck.Channel, message, number, null);

		// Sliders start two cells long when there is room.
		var longer = kind switch
		{
			DeckControlKind.VerticalSlider => control with { Height = 2 },
			DeckControlKind.HorizontalSlider => control with { Width = 2 },
			_ => control,
		};
		return deck.PlacementError(longer) is null ? longer : control;
	}

	private void ChangeAction(string value)
	{
		if (Selected() is not { } control)
		{
			return;
		}

		// The picker may qualify the id with the integration; keep whatever comes before it.
		var separator = value.LastIndexOfAny(['/', ':']);
		var actionId = separator >= 0 ? value[(separator + 1)..] : value;
		_actionPrefix = separator >= 0 ? value[..(separator + 1)] : string.Empty;
		_logger.Debug("Action picked: {Value}.", value);

		DeckMessage? message = actionId switch
		{
			SendNoteAction => DeckMessage.Note,
			SendControlChangeAction => DeckMessage.ControlChange,
			SendProgramChangeAction => DeckMessage.ProgramChange,
			_ => null,
		};

		if (message is not { } picked)
		{
			Status(Strings.Editor.Status.UnsupportedAction(), "warning");
		}
		else if (control.IsSlider && picked != DeckMessage.ControlChange)
		{
			Status(Strings.Editor.Status.SliderNeedsCc(), "warning");
		}
		else
		{
			ChangeSelected(selected => selected with { Message = picked });
		}
	}

	private void ChangeSelected(Func<DeckControl, DeckControl> change)
	{
		if (_draft.Peek() is { } deck && SelectedIn(deck) is { } control)
		{
			Replace(deck, control, change(control));
		}
	}

	private bool Replace(DeckLayout deck, DeckControl control, DeckControl changed)
	{
		if (changed == control)
		{
			return true;
		}

		var candidate = deck with { Controls = [.. deck.Controls.Select(other => other.Id == control.Id ? changed : other)] };
		if (candidate.PlacementError(changed) is { } error)
		{
			Status(LocalizedText.FromLiteral(error), "warning");
			return false;
		}

		SetDraft(candidate);
		return true;
	}

	// Refuses a grid too small for an element on it.
	private void ChangeDeck(Func<DeckLayout, DeckLayout> change)
	{
		if (_draft.Peek() is not { } deck)
		{
			return;
		}

		var changed = change(deck);
		if (changed.Controls.Select(changed.PlacementError).FirstOrDefault(error => error is not null) is { } error)
		{
			Status(LocalizedText.FromLiteral(error), "warning");
			return;
		}

		SetDraft(changed);
	}

	private void DeleteSelected()
	{
		if (_draft.Peek() is { } deck && SelectedIn(deck) is { } control)
		{
			SetDraft(deck with { Controls = [.. deck.Controls.Where(other => other.Id != control.Id)] });
			_selected.Set(null);
		}
	}

	private void Save()
	{
		if (_draft.Peek() is not { } deck)
		{
			return;
		}

		// Saving reloads the store, which must not read as a change made elsewhere.
		_dirty.Set(false);
		try
		{
			_store.Save(deck);
			Status(Strings.Editor.Status.Saved(deck.Name), "success");
		}
		catch (Exception ex) when (ex is DeckLayoutException or IOException or UnauthorizedAccessException)
		{
			_dirty.Set(true);
			Status(Strings.Editor.Status.SaveFailed(ex.Message), "error");
		}
	}

	private async Task ExportAsync()
	{
		if (_dirty.Peek())
		{
			Status(Strings.Editor.Status.SaveFirst(), "warning");
			return;
		}

		var portName = _router.Config.OutputName;
		if (portName.Length == 0)
		{
			Status(Strings.Editor.Status.ExportNoOutput(), "warning");
			return;
		}

		try
		{
			var result = await DeckExport.ExportAsync(_store.Snapshot.Decks, portName, CancellationToken.None).ConfigureAwait(false);
			var count = result.Decks.ToString(CultureInfo.InvariantCulture);
			Status(result.FallbackFolder is { } folder
				? Strings.Editor.Status.ExportedToFolder(count, folder)
				: Strings.Editor.Status.Exported(count, result.Apps), "success");
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Status(Strings.Editor.Status.ExportFailed(ex.Message), "error");
		}
	}

	private void SwitchDeck(string deckId)
	{
		if (deckId == _draft.Peek()?.Id)
		{
			return;
		}

		if (_dirty.Peek())
		{
			Status(Strings.Editor.Status.SaveFirst(), "warning");
			return;
		}

		Load(deckId);
	}

	private void CreateDeck(string? name)
	{
		if (_dirty.Peek())
		{
			Status(Strings.Editor.Status.SaveFirst(), "warning");
			return;
		}

		name = string.IsNullOrWhiteSpace(name) ? NewDeckName : name.Trim();
		try
		{
			var deck = _store.Create(name, NewDeckColumns, NewDeckRows);
			Load(deck.Id);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Status(Strings.Editor.Status.SaveFailed(ex.Message), "error");
		}
	}

	private void DeleteDeck()
	{
		if (_draft.Peek() is not { } deck)
		{
			return;
		}

		try
		{
			_dirty.Set(false);
			_store.Delete(deck.Id);
			Load(null);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Status(Strings.Editor.Status.SaveFailed(ex.Message), "error");
		}
	}

	// The saved deck, or the first one when it is gone.
	private void Load(string? deckId)
	{
		var decks = _store.Snapshot.Decks;
		var deck = decks.FirstOrDefault(deck => deck.Id == deckId) ?? (decks.Count > 0 ? decks[0] : null);
		_draft.Set(deck);
		_dirty.Set(false);
		if (deck is null || !deck.Controls.Any(control => control.Id == _selected.Peek()))
		{
			_selected.Set(null);
		}
	}

	private void SetDraft(DeckLayout deck)
	{
		_draft.Set(deck);
		_dirty.Set(true);
	}

	private void Select(DeckControl control)
	{
		_selected.Set(control.Id);
		if (_mode.Peek() == PlaceMode)
		{
			Status(Strings.Editor.Status.Hint(), "info");
		}
	}

	private void Status(LocalizedText text, string severity) => _status.Set((text, severity));

	private DeckControl? Selected() => _draft.Value is { } deck && _selected.Value is { } id ? deck.Controls.FirstOrDefault(control => control.Id == id) : null;

	private DeckControl? SelectedIn(DeckLayout deck) => _selected.Peek() is { } id ? deck.Controls.FirstOrDefault(control => control.Id == id) : null;

	private void OnStoreChanged()
	{
		_decksRevision.Set(_decksRevision.Peek() + 1);
		if (_dirty.Peek())
		{
			Status(Strings.Editor.Status.ChangedOnDisk(), "warning");
			return;
		}

		Load(_draft.Peek()?.Id);
	}

	private static string ActionId(DeckMessage message) => message switch
	{
		DeckMessage.ControlChange => SendControlChangeAction,
		DeckMessage.ProgramChange => SendProgramChangeAction,
		_ => SendNoteAction,
	};

	private static string Position(DeckControl control) => control.Kind == DeckControlKind.SmallButton
		? string.Create(CultureInfo.InvariantCulture, $"{control.X},{control.Y} {(control.Slot == 0 ? "▲" : "▼")}")
		: string.Create(CultureInfo.InvariantCulture, $"{control.X},{control.Y} ({control.Width}×{control.Height})");

	private static LocalizedText KindName(DeckControlKind kind) => kind switch
	{
		DeckControlKind.SmallButton => Strings.Editor.Kinds.SmallButton(),
		DeckControlKind.VerticalSlider => Strings.Editor.Kinds.VerticalSlider(),
		DeckControlKind.HorizontalSlider => Strings.Editor.Kinds.HorizontalSlider(),
		_ => Strings.Editor.Kinds.Button(),
	};

	private static string Icon(DeckControlKind kind) => kind switch
	{
		DeckControlKind.SmallButton => UiIcons.Grid,
		DeckControlKind.VerticalSlider or DeckControlKind.HorizontalSlider => UiIcons.Sliders,
		_ => UiIcons.ActionButtonType,
	};
}
