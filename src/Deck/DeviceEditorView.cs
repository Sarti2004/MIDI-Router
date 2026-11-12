using System.Globalization;
using System.Text.Json;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Config;
using MacroDeck.Ui.Config.Options;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Runtime;

namespace MIDIRouter.Deck;

/// <summary>
/// The Virtual Device page: the mixer as it looks on the deck, where pressing an empty cell places the chosen element
/// and pressing an element selects it; the selected element's settings and action below. Every edit is saved at once,
/// so the mixer folder follows.
/// </summary>
internal sealed class DeviceEditorView : IDisposable
{
	public const string PageId = "virtual-device";

	private const string PlaceMode = "place";
	private const string MoveMode = "move";
	private const int DefaultChannel = 16;
	private const int FirstNote = 36;
	private const int FirstController = 20;

	// A change to the device redraws the preview in one patch, which Macro Deck takes up to 64 KB.
	private const int MaxPreviewBytes = 56 * 1024;

	private readonly DeviceStore _store;
	private readonly DeviceActions _actions;
	private readonly MixerModel _mixer;
	private readonly UiState<DeviceElementKind> _placeKind = new(DeviceElementKind.Button);
	private readonly UiState<string> _mode = new(PlaceMode);
	private readonly UiState<(LocalizedText Text, string Severity)> _status = new((Strings.Editor.Status.Hint(), "info"));

	public DeviceEditorView(DeviceStore store, DeviceActions actions, MixerArt? art)
	{
		_store = store;
		_actions = actions;
		_mixer = new MixerModel(actions, art) { CellPressed = CellPressed };
		_mixer.Device.Set(store.Device);
		_store.Changed += OnStoreChanged;
	}

	public void Dispose() => _store.Changed -= OnStoreChanged;

	public UiConfigStack Root() => new()
	{
		Key = "d",
		Children =
		[
			DeviceSettings(),
			Palette(),
			MixerView.Preview(_mixer),
			new UiBanner { Key = "status", Severity = UiValue.From(() => _status.Value.Severity), Text = UiText.Optional(() => _status.Value.Text) },
			new UiWhen
			{
				Key = "duplicates-when",
				Condition = () => _mixer.Device.Value.DuplicateBindings().Count > 0,
				Content = () => new UiBanner
				{
					Key = "duplicates",
					Severity = "warning",
					Text = UiText.Optional(() => _mixer.Device.Value.DuplicateBindings() is [var first, ..]
						? Strings.Editor.Status.Duplicate(first.Element.Label, first.Twin.Label, first.Binding.ToString())
						: UiText.None()),
				},
			},
			new UiRepeat<string>
			{
				Key = "inspector-repeat",
				Items = UiValue.From<IReadOnlyList<string>>(() => _mixer.Selected.Value is { } id ? [id] : []),
				KeySelector = id => id,
				Template = (_, _) => Inspector(),
			},
			new UiConfigButton
			{
				Key = "export",
				Label = Strings.Editor.Export(),
				Icon = UiIcons.Upload,
				IconWithLabel = true,
				Variant = UiButtonVariants.Secondary,
				Events = [UiEventHandler.OnAsync(UiConfigEvents.Activate, _ => ExportAsync())],
			},
		],
	};

	private UiConfigStack DeviceSettings() => new()
	{
		Key = "settings",
		Direction = UiComponentDirections.Horizontal,
		Children =
		[
			new UiStringInput
			{
				Key = "name",
				Label = Strings.Editor.Name(),
				RowWeight = 2,
				Binding = Bind.Custom(() => _mixer.Device.Value.Name, name => Change(device => device with { Name = name })),
			},
			Number("columns", Strings.Editor.Columns(), 1, VirtualDevice.MaxGridSize, () => _mixer.Device.Value.Columns,
				columns => Change(device => device with { Columns = columns })),
			Number("rows", Strings.Editor.Rows(), 1, VirtualDevice.MaxGridSize, () => _mixer.Device.Value.Rows,
				rows => Change(device => device with { Rows = rows })),
		],
	};

	private UiConfigStack Palette() => new()
	{
		Key = "palette",
		Direction = UiComponentDirections.Horizontal,
		Align = UiStackAlignments.Center,
		Children =
		[
			.. Enum.GetValues<DeviceElementKind>().Select(kind => new UiConfigButton
			{
				Key = "place-" + kind.ToString().ToLowerInvariant(),
				Label = KindName(kind),
				Icon = Icon(kind),
				IconWithLabel = true,
				Variant = UiValue.From(() => _placeKind.Value == kind && _mode.Value == PlaceMode ? UiButtonVariants.Primary : UiButtonVariants.Secondary),
				Events = [UiEventHandler.On(UiConfigEvents.Activate, () =>
				{
					_placeKind.Set(kind);
					_mode.Set(PlaceMode);
					Status(Strings.Editor.Status.Hint(), "info");
				})],
			}),
			new UiConfigButton
			{
				Key = "move",
				Label = Strings.Editor.Modes.Move(),
				Icon = UiIcons.Crosshair,
				IconWithLabel = true,
				Variant = UiValue.From(() => _mode.Value == MoveMode ? UiButtonVariants.Primary : UiButtonVariants.Ghost),
				Events = [UiEventHandler.On(UiConfigEvents.Activate, () =>
				{
					_mode.Set(MoveMode);
					Status(_mixer.Selected.Peek() is null ? Strings.Editor.Status.MoveHint() : Strings.Editor.Status.MoveTargetSelected(), "info");
				})],
			},

			// Next to Move, so it is in reach without scrolling to the inspector. A config button can't be disabled: with
			// nothing selected it is plain and says what to do.
			new UiConfigButton
			{
				Key = "delete-element",
				Label = Strings.Editor.Delete(),
				Icon = UiIcons.Trash,
				IconWithLabel = true,
				Variant = UiValue.From(() => Selected() is null ? UiButtonVariants.Ghost : UiButtonVariants.Danger),
				Events = [UiEventHandler.On(UiConfigEvents.Activate, DeleteSelected)],
			},
		],
	};

	private UiConfigStack Inspector() => new()
	{
		Key = "inspector",
		Children =
		[
			new UiHeading
			{
				Key = "selected",
				Text = UiText.Optional(() => Selected() is { } element ? Strings.Editor.Selected(element.Label, Position(element)) : UiText.None()),
				Badge = UiText.Optional(() => Selected() is { } element ? KindName(element.Kind) : UiText.None()),
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
						Binding = Bind.Custom(() => Selected()?.Label ?? string.Empty, label => ChangeSelected(element => element with { Label = label })),
					},
					new UiColorInput
					{
						Key = "color",
						Label = Strings.Editor.Color(),
						RowWeight = 1,
						Binding = Bind.Custom(() => Selected()?.Color ?? string.Empty,
							color => ChangeSelected(element => element with { Color = string.IsNullOrWhiteSpace(color) ? null : color })),
					},
				],
			},
			new UiWhen
			{
				Key = "size-when",
				Condition = () => Selected() is { Kind: not DeviceElementKind.SmallButton },
				Content = () => new UiConfigStack
				{
					Key = "size",
					Direction = UiComponentDirections.Horizontal,
					Children =
					[
						Number("width", Strings.Editor.Width(), 1, VirtualDevice.MaxGridSize, () => Selected()?.Width ?? 1,
							width => ChangeSelected(element => element with { Width = width })),
						Number("height", Strings.Editor.Height(), 1, VirtualDevice.MaxGridSize, () => Selected()?.Height ?? 1,
							height => ChangeSelected(element => element with { Height = height })),
					],
				},
			},
			new UiWhen
			{
				Key = "centered-when",
				Condition = () => Selected() is { IsSlider: true },
				Content = () => new UiBooleanInput
				{
					Key = "centered",
					Label = Strings.Editor.Centered.Label(),
					Description = Strings.Editor.Centered.Description(),
					Binding = Bind.Custom(() => Selected()?.Centered == true, centered => ChangeSelected(element => element with { Centered = centered })),
				},
			},
			new UiWhen
			{
				Key = "slot-when",
				Condition = () => Selected() is { Kind: DeviceElementKind.SmallButton },
				Content = () => new UiChoiceInput
				{
					Key = "slot",
					Label = Strings.Editor.Slot(),
					Segmented = true,
					Options = UiValue.Of<IReadOnlyList<UiOption>>([UiOption.Of("0", Strings.Editor.Slots.Top()), UiOption.Of("1", Strings.Editor.Slots.Bottom())]),
					Binding = Bind.Custom(() => Selected()?.Slot == 1 ? "1" : "0", slot => ChangeSelected(element => element with { Slot = slot == "1" ? 1 : 0 })),
				},
			},
			new UiChoiceInput
			{
				Key = "action",
				Label = Strings.Editor.Action(),
				Searchable = true,
				Required = true,
				Options = UiValue.From<IReadOnlyList<UiOption>>(() => Selected() is { } element
					? [.. _actions.For(element.Kind).Select(action => UiOption.Of(action.Id, action.Name))]
					: []),
				Binding = Bind.Custom(() => Selected()?.ActionId ?? string.Empty, ChangeAction),
			},
			// As on DoriDeck's MIDI keys page: the chosen action's own parameters.
			.. _actions.All.Where(action => action.Parameters.Count > 0).Select(action => new UiWhen
			{
				Key = action.Id + "-when",
				Condition = () => Selected()?.ActionId == action.Id,
				Content = () => new UiFragment
				{
					Key = action.Id + "-parameters",
					Children = [.. action.Parameters.Select(parameter => Parameter(action, parameter))],
				},
			}),
		],
	};

	private UiWhen Parameter(IActionDefinition action, ActionParameter parameter)
	{
		var key = action.Id + "-" + parameter.Name;
		var label = parameter.Label.IsEmpty ? UiText.None() : (UiText)parameter.Label;
		var description = parameter.Description.IsEmpty ? UiText.None() : (UiText)parameter.Description;
		UiElement input = parameter.Type switch
		{
			ActionParameterType.Boolean => new UiBooleanInput
			{
				Key = key,
				Label = label,
				Description = description,
				Binding = Bind.Custom(() => Value(parameter) is true or "true" or "True", value => SetParameter(parameter, value)),
			},
			ActionParameterType.Number => new UiNumberInput
			{
				Key = key,
				Label = label,
				Description = description,
				Required = parameter.Required,
				Min = parameter.Min is { } min ? min : UiValue.None<double>(),
				Max = parameter.Max is { } max ? max : UiValue.None<double>(),
				Step = parameter.Step is { } step ? step : UiValue.None<double>(),
				Binding = Bind.Custom(
					() => double.TryParse(Convert.ToString(Value(parameter), CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0,
					value => SetParameter(parameter, value)),
			},
			ActionParameterType.Choice => new UiChoiceInput
			{
				Key = key,
				Label = label,
				Description = description,
				Required = parameter.Required,
				Searchable = parameter.Searchable,
				Options = UiValue.Of<IReadOnlyList<UiOption>>([.. (parameter.Options ?? []).Select(option => UiOption.Of(option.Value, option.Label))]),
				Binding = Bind.Custom(() => Convert.ToString(Value(parameter), CultureInfo.InvariantCulture) ?? string.Empty, value => SetParameter(parameter, value)),
			},
			_ => new UiStringInput
			{
				Key = key,
				Label = label,
				Description = description,
				Required = parameter.Required,
				Binding = Bind.Custom(() => Convert.ToString(Value(parameter), CultureInfo.InvariantCulture) ?? string.Empty, value => SetParameter(parameter, value)),
			},
		};

		// Hidden for sliders (the value is the slider) and while the action's own condition says so.
		return new UiWhen
		{
			Key = key + "-visible",
			Condition = () => Selected() is { } element && DeviceActions.IsEdited(element, parameter)
				&& (parameter.VisibleWhen is not { } visibility
					|| visibility.Values.Contains(Convert.ToString(element.Parameters.GetValueOrDefault(visibility.ParameterName), CultureInfo.InvariantCulture), StringComparer.Ordinal)),
			Content = () => input,
		};
	}

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

	private void CellPressed(int x, int y, int slot)
	{
		var device = _store.Device;
		var here = device.Elements.FirstOrDefault(element => element.Covers(x, y)
			&& (element.Kind != DeviceElementKind.SmallButton || element.Slot == slot));
		var selected = Selected(device);

		if (_mode.Peek() == MoveMode)
		{
			if (selected is null || (here is not null && here.Id != selected.Id))
			{
				if (here is not null)
				{
					_mixer.Selected.Set(here.Id);
					Status(Strings.Editor.Status.MoveTarget(here.Label), "info");
				}

				return;
			}

			var moved = selected with { X = x, Y = y, Slot = selected.Kind == DeviceElementKind.SmallButton ? slot : 0 };
			if (Apply(device, moved))
			{
				Status(Strings.Editor.Status.Moved(selected.Label, Position(moved)), "success");
			}

			return;
		}

		if (here is not null)
		{
			_mixer.Selected.Set(here.Id);
			Status(Strings.Editor.Status.Hint(), "info");
			return;
		}

		var placed = NewElement(device, _placeKind.Peek(), x, y, slot);
		if (Apply(device, placed))
		{
			_mixer.Selected.Set(placed.Id);
			Status(Strings.Editor.Status.Hint(), "info");
		}
	}

	// A new element sends the next note (buttons) or CC (sliders) not yet used on its channel.
	private DeviceElement NewElement(VirtualDevice device, DeviceElementKind kind, int x, int y, int slot)
	{
		var slider = kind is DeviceElementKind.VerticalSlider or DeviceElementKind.HorizontalSlider;
		var actionId = slider ? DeviceActions.SendControlChange : DeviceActions.SendNote;
		var messageKind = slider ? MidiMessageKind.ControlChange : MidiMessageKind.Note;
		var used = device.Elements.Select(DeviceActions.Binding).OfType<MidiBinding>()
			.Where(binding => binding.Kind == messageKind && binding.Channel == DefaultChannel).Select(binding => binding.Number).ToHashSet();
		var number = Enumerable.Range(0, 128).Select(n => ((slider ? FirstController : FirstNote) + n) % 128).FirstOrDefault(n => !used.Contains(n));

		var name = kind switch
		{
			DeviceElementKind.SmallButton => "Small",
			DeviceElementKind.VerticalSlider or DeviceElementKind.HorizontalSlider => "Fader",
			_ => "Button",
		};
		var count = device.Elements.Count(element => element.IsSlider == slider && (element.Kind == DeviceElementKind.SmallButton) == (kind == DeviceElementKind.SmallButton));
		string label, id;
		do
		{
			count++;
			label = string.Create(CultureInfo.InvariantCulture, $"{name} {count}");
			id = string.Create(CultureInfo.InvariantCulture, $"{name.ToLowerInvariant()}-{count}");
		}
		while (device.Elements.Any(element => element.Id == id));

		var element = new DeviceElement(id, kind, label, x, y, 1, 1, kind == DeviceElementKind.SmallButton ? slot : 0, null,
			actionId, _actions.Defaults(actionId, DefaultChannel, number));

		// Faders start two cells long when there is room.
		var longer = kind switch
		{
			DeviceElementKind.VerticalSlider => element with { Height = 2 },
			DeviceElementKind.HorizontalSlider => element with { Width = 2 },
			_ => element,
		};
		return device.PlacementError(longer) is null ? longer : element;
	}

	private void ChangeAction(string actionId)
	{
		if (Selected() is not { } element || element.ActionId == actionId || !_actions.ById.ContainsKey(actionId))
		{
			return;
		}

		// Keep the channel and the number across actions; the rest starts at the new action's defaults.
		var binding = DeviceActions.Binding(element);
		ChangeSelected(selected => selected with
		{
			ActionId = actionId,
			Parameters = _actions.Defaults(actionId, binding?.Channel ?? DefaultChannel, binding?.Number ?? 0),
		});
	}

	private object? Value(ActionParameter parameter) =>
		Selected() is { } element && element.Parameters.TryGetValue(parameter.Name, out var value) ? value : parameter.DefaultValue;

	private void SetParameter(ActionParameter parameter, object? value) =>
		ChangeSelected(element => element with
		{
			Parameters = new Dictionary<string, object?>(element.Parameters, StringComparer.Ordinal) { [parameter.Name] = value },
		});

	private void ChangeSelected(Func<DeviceElement, DeviceElement> change)
	{
		var device = _store.Device;
		if (Selected(device) is { } element)
		{
			Apply(device, change(element));
		}
	}

	private bool Apply(VirtualDevice device, DeviceElement element)
	{
		var changed = device.With(element);
		if (changed.PlacementError(element) is { } error)
		{
			Status(LocalizedText.FromLiteral(error), "warning");
			return false;
		}

		return Store(changed);
	}

	private bool Store(VirtualDevice changed)
	{
		if (MixerView.Size(_actions, _mixer.Art, changed) > MaxPreviewBytes)
		{
			Status(Strings.Editor.Status.Full(), "warning");
			return false;
		}

		_store.Update(_ => changed);
		return true;
	}

	// Refuses a grid too small for an element on it.
	private void Change(Func<VirtualDevice, VirtualDevice> change)
	{
		var changed = change(_store.Device);
		if (changed.Elements.Select(changed.PlacementError).FirstOrDefault(error => error is not null) is { } error)
		{
			Status(LocalizedText.FromLiteral(error), "warning");
			return;
		}

		Store(changed);
	}

	private void DeleteSelected()
	{
		if (Selected(_store.Device) is not { } element)
		{
			Status(Strings.Editor.Status.DeleteHint(), "info");
			return;
		}

		_store.Update(device => device.Without(element.Id));
		_mixer.Selected.Set(null);
		Status(Strings.Editor.Status.Deleted(element.Label), "success");
	}

	private async Task ExportAsync()
	{
		try
		{
			var result = await DeckExport.ExportAsync(_store.Device, CancellationToken.None).ConfigureAwait(false);
			Status(result.FallbackFolder is { } folder
				? Strings.Editor.Status.ExportedToFolder(folder)
				: Strings.Editor.Status.Exported(result.Apps), "success");
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Status(Strings.Editor.Status.ExportFailed(ex.Message), "error");
		}
	}

	private void Status(LocalizedText text, string severity) => _status.Set((text, severity));

	private DeviceElement? Selected() => _mixer.Selected.Value is { } id ? _mixer.Device.Value.Elements.FirstOrDefault(element => element.Id == id) : null;

	private DeviceElement? Selected(VirtualDevice device) => _mixer.Selected.Peek() is { } id ? device.Elements.FirstOrDefault(element => element.Id == id) : null;

	private void OnStoreChanged()
	{
		var device = _store.Device;
		_mixer.Device.Set(device);
		if (_mixer.Selected.Peek() is { } id && device.Elements.All(element => element.Id != id))
		{
			_mixer.Selected.Set(null);
		}
	}

	private static string Position(DeviceElement element) => element.Kind == DeviceElementKind.SmallButton
		? string.Create(CultureInfo.InvariantCulture, $"{element.X + 1},{element.Y + 1} {(element.Slot == 0 ? "▲" : "▼")}")
		: string.Create(CultureInfo.InvariantCulture, $"{element.X + 1},{element.Y + 1} ({element.Width}×{element.Height})");

	private static LocalizedText KindName(DeviceElementKind kind) => kind switch
	{
		DeviceElementKind.SmallButton => Strings.Editor.Kinds.SmallButton(),
		DeviceElementKind.VerticalSlider => Strings.Editor.Kinds.VerticalSlider(),
		DeviceElementKind.HorizontalSlider => Strings.Editor.Kinds.HorizontalSlider(),
		_ => Strings.Editor.Kinds.Button(),
	};

	private static string Icon(DeviceElementKind kind) => kind switch
	{
		DeviceElementKind.SmallButton => UiIcons.Grid,
		DeviceElementKind.VerticalSlider => UiIcons.Sliders,
		DeviceElementKind.HorizontalSlider => UiIcons.AlignMiddle,
		_ => UiIcons.ActionButtonType,
	};
}
