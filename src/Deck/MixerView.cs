using System.Collections.Concurrent;
using System.Globalization;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Model.Resources;
using MacroDeck.Ui.Runtime;

namespace MIDIRouter.Deck;

/// <summary>The mixer's images, registered with Macro Deck at startup; without them it draws plain shapes.</summary>
internal sealed record MixerArt(UiResource Panel, UiResource CapVertical, UiResource CapHorizontal)
{
	public static string Folder { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "Mixer");
}

/// <summary>
/// The state one mixer drawing reads: the device, and for the editor's preview the selection and what a press does.
/// Without <see cref="CellPressed"/> it is the live mixer: buttons run their actions and sliders send their CCs.
/// </summary>
internal sealed class MixerModel(DeviceActions actions, MixerArt? art)
{
	public DeviceActions Actions { get; } = actions;

	public MixerArt? Art { get; } = art;

	public UiState<VirtualDevice> Device { get; } = new(VirtualDevice.Default);

	public UiState<string?> Selected { get; } = new(null);

	/// <summary>The editor's handler for a press on a cell: x, y and the half (0 top, 1 bottom).</summary>
	public Action<int, int, int>? CellPressed { get; init; }

	public bool Editing => CellPressed is not null;

	// Per element: pressed (lit) while held, and each slider's position as a 0-1 level.
	public ConcurrentDictionary<string, UiState<bool>> Lit { get; } = new(StringComparer.Ordinal);

	public ConcurrentDictionary<string, UiState<double>> Levels { get; } = new(StringComparer.Ordinal);

	public UiState<bool> LitState(DeviceElement element) => Lit.GetOrAdd(element.Id, _ => new UiState<bool>(false));

	public UiState<double> Level(DeviceElement element) =>
		Levels.GetOrAdd(element.Id, _ => new UiState<double>(Actions.SliderValue(element) / 127.0));
}

/// <summary>
/// Draws the virtual device as a mixer: brushed metal with satin key caps and fader channels, each a slot with a scale
/// and a fader cap. Cells are square; the drawing keeps the grid's aspect inside whatever it is given.
/// </summary>
internal static class MixerView
{
	private const string Panel = "#2B2D32";
	private const string PanelEdge = "#1A1B1E";
	private const string Rim = "#5E6168";
	private const string SelectedRim = "#FFB547";
	private const string Legend = "#E6E4DE";
	private const string MutedLegend = "#A4A7AE";
	private const string Transparent = "#00000000";
	private const string Channel = "#121315D9";
	private const string Slot = "#050506";
	private const string Tick = "#7A7E86";
	private const string DefaultAccent = "#5B8DEF";

	private const double CellGap = 0.018;
	private const double PanelPadding = 0.03;
	private const double KeyRadius = 0.014;
	private const double LabelSize = 0.032;
	private const double SmallLabelSize = 0.026;
	private const double LedHeight = 0.012;
	private const double SlotWidth = 0.012;
	private const double TickWidth = 0.0025;
	private const int Ticks = 11;

	// The cap's share of the slot: a fader two cells long has a cap about a fifth of it.
	private const double CapLength = 0.36;

	private static readonly UiGradient PanelFace = UiGradient.Linear(
		180,
		new UiGradientStop { Offset = 0.00, Color = "#34363C" },
		new UiGradientStop { Offset = 1.00, Color = "#1D1E22" });

	private static readonly UiGradient KeyFace = UiGradient.Linear(
		180,
		new UiGradientStop { Offset = 0.00, Color = "#4C4F55" },
		new UiGradientStop { Offset = 0.50, Color = "#303237" },
		new UiGradientStop { Offset = 1.00, Color = "#1E1F23" });

	private static readonly UiGradient KeyFaceLit = UiGradient.Linear(
		180,
		new UiGradientStop { Offset = 0.00, Color = "#6A6D75" },
		new UiGradientStop { Offset = 0.50, Color = "#45474E" },
		new UiGradientStop { Offset = 1.00, Color = "#2B2D32" });

	/// <summary>The mixer filling a folder: centred on the panel, as large as the folder allows at the grid's aspect.</summary>
	public static UiElement Folder(MixerModel model) =>
		new UiLayer
		{
			Key = "mixer",
			Fill = true,
			Children =
			[
				Background(model, "fbg"),
				new UiStack
				{
					Key = "c",
					Justify = UiComponentJustify.Center,
					Align = UiComponentAlignments.Center,
					Padding = UiSize.FromBasis(0.02),
					Children = [Fitted(model, fullWidth: false)],
				},
			],
		};

	/// <summary>The mixer as one block of a config page, as wide as the page.</summary>
	public static UiElement Preview(MixerModel model) => Fitted(model, fullWidth: true);

	// A frame with an aspect gives the components a size to draw at.
	private static UiRepeat<string> Fitted(MixerModel model, bool fullWidth) => new()
	{
		Key = "fr",
		// Rebuilt when the device changes shape; values inside follow their states.
		Items = UiValue.From<IReadOnlyList<string>>(() => [model.Device.Value.GetHashCode().ToString(CultureInfo.InvariantCulture)]),
		KeySelector = key => key,
		Template = (_, _) =>
		{
			var device = model.Device.Peek();
			var aspect = (double)device.Columns / device.Rows;
			return new UiModifier
			{
				Key = "f",
				Fill = true,
				Frame = fullWidth
					? new UiFrame { AspectRatio = aspect, Width = UiLength.OfParent(1) }
					: new UiFrame { AspectRatio = aspect },
				Radius = UiSize.FromBasis(KeyRadius * 1.5),
				BorderWidth = UiSize.FromBasis(0.003),
				BorderColor = PanelEdge,
				Clip = UiComponentClips.Bounds,
				Child = new UiLayer
				{
					Key = "p",
					Children =
					[
						Background(model, "bg"),
						new UiStack
						{
							Key = "pd",
							Padding = UiSize.FromBasis(PanelPadding),
							Children = [Grid(model, device)],
						},
					],
				},
			};
		},
	};

	// Brushed metal when the image is there, a gradient when not.
	private static UiElement Background(MixerModel model, string key) =>
		model.Art is { } art
			? new UiButton { Key = key, Fill = true, Background = Panel, Source = art.Panel, Fit = UiComponentImageFits.Cover }
			: new UiModifier { Key = key + "-style", Background = PanelFace, Child = new UiStack { Key = key, Fill = true } };

	private static UiGrid Grid(MixerModel model, VirtualDevice device)
	{
		var cells = new List<UiElement>();
		var covered = new bool[device.Columns, device.Rows];
		for (var y = 0; y < device.Rows; y++)
		{
			for (var x = 0; x < device.Columns; x++)
			{
				if (covered[x, y])
				{
					continue;
				}

				var here = device.Elements.Where(element => element.X == x && element.Y == y).ToList();
				if (here.FirstOrDefault(element => element.Kind != DeviceElementKind.SmallButton) is { } element)
				{
					for (var cx = x; cx < x + element.Width; cx++)
					{
						for (var cy = y; cy < y + element.Height; cy++)
						{
							covered[cx, cy] = true;
						}
					}

					cells.Add(element.IsSlider ? Slider(model, element) : Key(model, element, small: false));
				}
				else if (here.Count > 0)
				{
					cells.Add(SmallPair(model, x, y, here));
				}
				else
				{
					cells.Add(Empty(model, x, y, 0, CellKey(x, y)));
				}
			}
		}

		return new UiGrid
		{
			Key = "g",
			Fill = true,
			Columns = device.Columns,
			Rows = device.Rows,
			Gap = UiSize.FromBasis(CellGap),
			Children = cells,
		};
	}

	private static UiStack SmallPair(MixerModel model, int x, int y, IReadOnlyList<DeviceElement> smalls) => new()
	{
		Key = CellKey(x, y),
		Direction = UiComponentDirections.Vertical,
		Gap = UiSize.FromBasis(CellGap / 2),
		Children =
		[
			.. Enumerable.Range(0, 2).Select(slot => smalls.FirstOrDefault(small => small.Slot == slot) is { } small
				? (UiElement)Key(model, small, small: true)
				: Empty(model, x, y, slot, CellKey(x, y) + (slot == 0 ? "-top" : "-bottom"))),
		],
	};

	private static UiModifier Key(MixerModel model, DeviceElement element, bool small)
	{
		var lit = model.LitState(element);
		var accent = element.Color ?? DefaultAccent;

		// A modifier adds no id of its own: what it wraps is keyed by the element.
		var button = new UiButton
		{
			Key = element.Id + "-button",
			Direction = UiComponentDirections.Vertical,
			Justify = UiComponentJustify.SpaceBetween,
			Align = UiComponentAlignments.Stretch,
			Padding = UiSize.FromBasis(small ? 0.008 : 0.012),
			Background = Transparent,
			Events = model.Editing
				? [UiEventHandler.On(UiComponentEvents.Press, () => model.CellPressed!(element.X, element.Y, element.Slot))]
				:
				[
					UiEventHandler.On(UiComponentEvents.PressStart, () => lit.Set(true)),
					UiEventHandler.On(UiComponentEvents.PressEnd, () => lit.Set(false)),
					UiEventHandler.OnAsync(UiComponentEvents.Press, cancellationToken => model.Actions.PressAsync(element, cancellationToken)),
				],
			Children =
			[
				// The LED strip: the element's color, bright while pressed.
				new UiModifier
				{
					Key = "led",
					Frame = new UiFrame { Height = UiLength.OfBasis(LedHeight) },
					Opacity = UiValue.From(() => lit.Value ? 1.0 : 0.45),
					Child = new UiShape { Key = "led-shape", Shape = UiComponentShapes.Capsule, Color = accent },
				},
				Label(element.Label, small ? SmallLabelSize : LabelSize, Legend),
				new UiStack { Key = "spacer" },
			],
		};

		return Styled(element.Id, element.Label, button, () => lit.Value, () => model.Selected.Value == element.Id, element.Width, element.Height);
	}

	// A fader channel: readout, the fader (slot, scale and cap under an invisible slider) and the label.
	private static UiModifier Slider(MixerModel model, DeviceElement element)
	{
		var vertical = element.Kind == DeviceElementKind.VerticalSlider;
		var level = model.Level(element);
		var fader = new UiLayer
		{
			Key = "fd",
			Fill = true,
			Children =
			[
				Scale(element, vertical),
				SlotWithLevel(element, vertical, level),
				Cap(model, element, vertical, level),
				Input(model, element, vertical, level),
			],
		};

		var readout = new UiTextRun
		{
			Key = "val",
			Text = UiText.From(() => ((int)Math.Round(level.Value * 127)).ToString(CultureInfo.InvariantCulture)),
			Size = UiSize.FromBasis(SmallLabelSize),
			Weight = UiComponentTextWeights.Medium,
			Color = MutedLegend,
			Align = UiComponentAlignments.Center,
			MaxLines = 1,
		};

		var channel = new UiStack
		{
			Key = "ch",
			Direction = vertical ? UiComponentDirections.Vertical : UiComponentDirections.Horizontal,
			Align = UiComponentAlignments.Center,
			Gap = UiSize.FromBasis(0.008),
			Padding = UiSize.FromBasis(0.012),
			Background = Channel,
			Children = vertical
				? [readout, fader, Label(element.Label, SmallLabelSize, Legend)]
				: [Label(element.Label, SmallLabelSize, Legend), fader, readout],
		};

		// In the editor the whole channel is one press target that selects it.
		UiElement content = model.Editing
			? new UiButton
			{
				Key = "sel",
				Background = Transparent,
				Events = [UiEventHandler.On(UiComponentEvents.Press, () => model.CellPressed!(element.X, element.Y, 0))],
				Children = [channel with { Fill = true }],
			}
			: channel;

		return new UiModifier
		{
			Key = element.Id,
			ColumnSpan = element.Width,
			RowSpan = element.Height,
			Radius = UiSize.FromBasis(KeyRadius),
			BorderWidth = UiSize.FromBasis(0.003),
			BorderColor = UiValue.From(() => model.Selected.Value == element.Id ? SelectedRim : Rim),
			Clip = UiComponentClips.Bounds,
			AccessibilityLabel = UiText.Of(element.Label),
			Child = content,
		};
	}

	// Tick marks along the fader, a longer one at the ends and the middle.
	private static UiStack Scale(DeviceElement element, bool vertical) => new()
	{
		Key = "sc",
		Fill = true,
		Direction = vertical ? UiComponentDirections.Vertical : UiComponentDirections.Horizontal,
		Justify = UiComponentJustify.SpaceBetween,
		Align = UiComponentAlignments.Center,
		Padding = UiSize.FromBasis(0.01),
		Children =
		[
			.. Enumerable.Range(0, Ticks).Select(index =>
			{
				var across = UiLength.OfParent(index % 5 == 0 ? 0.8 : 0.5);
				var along = UiLength.OfBasis(TickWidth);
				return new UiModifier
				{
					Key = string.Create(CultureInfo.InvariantCulture, $"t{index}"),
					Frame = vertical ? new UiFrame { Width = across, Height = along } : new UiFrame { Width = along, Height = across },
					Child = new UiShape { Key = string.Create(CultureInfo.InvariantCulture, $"t{index}s"), Shape = UiComponentShapes.Rectangle, Color = Tick },
				};
			}),
		],
	};

	// The slot down the middle, lit in the element's color up to the level.
	private static UiStack SlotWithLevel(DeviceElement element, bool vertical, UiState<double> level)
	{
		var accent = element.Color ?? DefaultAccent;
		return new UiStack
		{
			Key = "sl",
			Fill = true,
			Direction = vertical ? UiComponentDirections.Vertical : UiComponentDirections.Horizontal,
			Justify = UiComponentJustify.Center,
			Align = UiComponentAlignments.Center,
			Children =
			[
				new UiModifier
				{
					Key = "sf",
					Frame = vertical
						? new UiFrame { Width = UiLength.OfBasis(SlotWidth), Height = UiLength.OfParent(0.94) }
						: new UiFrame { Width = UiLength.OfParent(0.94), Height = UiLength.OfBasis(SlotWidth) },
					Radius = UiSize.FromBasis(SlotWidth / 2),
					Clip = UiComponentClips.Bounds,
					Child = new UiStack
					{
						Key = "sb",
						Direction = vertical ? UiComponentDirections.Vertical : UiComponentDirections.Horizontal,
						Justify = vertical ? UiComponentJustify.End : UiComponentJustify.Start,
						Background = Slot,
						Children =
						[
							new UiModifier
							{
								Key = "lv",
								Frame = UiValue.From(() => vertical
									? new UiFrame { Width = UiLength.OfParent(1), Height = UiLength.OfParent(level.Value) }
									: new UiFrame { Width = UiLength.OfParent(level.Value), Height = UiLength.OfParent(1) }),
								Opacity = 0.85,
								Child = new UiShape { Key = "lvs", Shape = UiComponentShapes.Rectangle, Color = accent },
							},
						],
					},
				},
			],
		};
	}

	// The cap sits at the level: a spacer before it takes the rest of the travel.
	private static UiStack Cap(MixerModel model, DeviceElement element, bool vertical, UiState<double> level)
	{
		var length = vertical ? element.Height : element.Width;
		var cap = Math.Clamp(CapLength / length, 0.12, 0.36);
		UiElement face = model.Art is { } art
			? new UiButton
			{
				Key = "ci",
				Background = Transparent,
				Source = vertical ? art.CapVertical : art.CapHorizontal,
				Fit = UiComponentImageFits.Contain,
			}
			: new UiShape { Key = "ci", Shape = UiComponentShapes.RoundedRectangle, CornerRadius = UiSize.FromBasis(0.006), Color = "#3A3C42" };

		return new UiStack
		{
			Key = "cp",
			Fill = true,
			Direction = vertical ? UiComponentDirections.Vertical : UiComponentDirections.Horizontal,
			Children =
			[
				new UiModifier
				{
					Key = "ct",
					Frame = UiValue.From(() =>
					{
						var travel = UiLength.OfParent((vertical ? 1 - level.Value : level.Value) * (1 - cap));
						return vertical ? new UiFrame { Height = travel } : new UiFrame { Width = travel };
					}),
					Child = new UiStack { Key = "cts" },
				},
				new UiModifier
				{
					Key = "cf",
					Frame = vertical ? new UiFrame { Height = UiLength.OfParent(cap) } : new UiFrame { Width = UiLength.OfParent(cap) },
					Child = face,
				},
			],
		};
	}

	// On top and invisible (no track, no thumb): it takes the touches. Relative, so a touch doesn't jump the cap.
	private static UiSlider Input(MixerModel model, DeviceElement element, bool vertical, UiState<double> level) => new()
	{
		Key = "in",
		Fill = true,
		Level = UiValue.From(() => level.Value),
		Step = 1.0 / 127,
		Direction = vertical ? UiComponentDirections.Vertical : UiComponentDirections.Horizontal,
		Interaction = UiComponentSliderInteractions.Relative,
		LevelColor = Transparent,
		Thickness = UiSize.FromBasis(0.0001),
		Events = model.Editing
			? []
			:
			[
				UiEventHandler.OnAsync(UiComponentEvents.Adjust, (data, cancellationToken) => Slide(model, element, level, data, cancellationToken)),
				UiEventHandler.OnAsync(UiComponentEvents.Change, (data, cancellationToken) => Slide(model, element, level, data, cancellationToken)),
			],
	};

	private static async Task Slide(MixerModel model, DeviceElement element, UiState<double> level, UiEventData data, CancellationToken cancellationToken)
	{
		if (data.Raw is { ValueKind: System.Text.Json.JsonValueKind.Number } raw)
		{
			var value = Math.Clamp(raw.GetDouble(), 0, 1);
			level.Set(value);
			await model.Actions.SlideAsync(element, (int)Math.Round(value * 127), cancellationToken).ConfigureAwait(false);
		}
	}

	// An empty cell or half: nothing in the live mixer, a faint place target in the editor.
	private static UiElement Empty(MixerModel model, int x, int y, int slot, string key) =>
		model.Editing
			? new UiModifier
			{
				Key = key,
				Radius = UiSize.FromBasis(KeyRadius),
				BorderWidth = UiSize.FromBasis(0.002),
				BorderColor = "#5A5D6466",
				Child = new UiButton
				{
					Key = key + "-place",
					Justify = UiComponentJustify.Center,
					Align = UiComponentAlignments.Center,
					Background = "#00000040",
					Events = [UiEventHandler.On(UiComponentEvents.Press, () => model.CellPressed!(x, y, slot))],
					Children = [new UiIcon { Key = key + "-plus", Icon = UiIcons.Plus, Size = UiSize.FromBasis(0.03), Color = "#8A8D95" }],
				},
			}
			: new UiStack { Key = key };

	// Background and radius style the button itself, so the modifier emits no node: the grid spans go on the button.
	private static UiModifier Styled(string key, string label, UiButton button, Func<bool> lit, Func<bool> selected, int width, int height) => new()
	{
		Key = key,
		Background = UiBackgroundValue.From(() => lit() ? KeyFaceLit : KeyFace),
		Radius = UiSize.FromBasis(KeyRadius),
		AccessibilityLabel = UiText.Of(label),
		Child = button with
		{
			ColumnSpan = width,
			RowSpan = height,
			BorderStyle = UiComponentBorderStyles.Static,
			BorderColor = UiValue.From(() => selected() ? SelectedRim : Rim),
		},
	};

	private static UiTextRun Label(string text, double size, string color) => new()
	{
		Key = "label",
		Text = UiText.Of(text),
		Size = UiSize.FromBasis(size),
		Weight = UiComponentTextWeights.SemiBold,
		Color = color,
		Align = UiComponentAlignments.Center,
		MaxLines = 2,
	};

	private static string CellKey(int x, int y) => string.Create(CultureInfo.InvariantCulture, $"cell-{x}-{y}");
}
