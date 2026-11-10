using System.Collections.Concurrent;
using System.Globalization;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Model.Resources;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;

namespace MIDIRouter.Deck;

/// <summary>The mixer's images, registered with Macro Deck at startup; without them it draws plain shapes.</summary>
internal sealed record MixerArt(UiResource Panel, UiResource CapVertical, UiResource CapHorizontal, UiResource TrackVertical, UiResource TrackHorizontal)
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
/// Draws the virtual device as a mixer: brushed metal with satin key caps and fader channels, each a slotted track with
/// a scale and a fader cap. Cells are square; the drawing keeps the grid's aspect inside whatever it is given. The tree
/// is kept flat and small: Macro Deck sends at most 32 levels and 192 KB, and a change to the device redraws the mixer
/// in one patch of at most 64 KB.
/// </summary>
/// <remarks>
/// Sizes are in basis units. A folder's basis is about the folder's height, so the numbers below are fractions of it;
/// a config page's basis is a fixed 120 px, so the editor's preview is given a height in basis units and every size is
/// scaled by it to keep the same proportions.
/// </remarks>
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
	private const string SlotColor = "#050506";
	private const string DefaultAccent = "#5B8DEF";

	private const double CellGap = 0.018;
	private const double PanelPadding = 0.03;
	private const double KeyRadius = 0.014;
	private const double LabelSize = 0.032;
	private const double SmallLabelSize = 0.026;
	private const double LedHeight = 0.012;
	private const double SlotWidth = 0.008;
	private const double IconSize = 0.05;

	// The track image's scale runs from 40 px to 720 px of its 760: the share of its length before the first tick.
	private const double TrackMargin = 40.0 / 760;

	// The cap's share of the fader: a fader two cells long has a cap about a fifth of it.
	private const double CapLength = 0.36;

	// The editor's preview: this many basis units high (120 px each on a page), at most this many wide.
	private const double PreviewHeight = 3.5;
	private const double PreviewMaxWidth = 7.0;

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
			Key = "m",
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
					Children = [Fitted(model, 1)],
				},
			],
		};

	/// <summary>The mixer as one block of a config page.</summary>
	public static UiElement Preview(MixerModel model) => Fitted(model, 0);

	/// <summary>
	/// The larger of the editor's and the mixer's drawing of <paramref name="device"/>, in bytes as Macro Deck receives
	/// it: a change to the device is sent as one patch of either.
	/// </summary>
	public static int Size(DeviceActions actions, MixerArt? art, VirtualDevice device)
	{
		var editor = new MixerModel(actions, art) { CellPressed = (_, _, _) => { } };
		var live = new MixerModel(actions, art);
		editor.Device.Set(device);
		live.Device.Set(device);
		var surface = new UiSurface { Kind = UiSurfaceKinds.Page, SessionMode = UiSessionModes.Exclusive };
		using var editorView = new UiView(surface, new UiFragment { Key = "size", Children = [Preview(editor)] });
		using var liveView = new UiView(surface, new UiFragment { Key = "size", Children = [Folder(live)] });
		return Math.Max(
			MacroDeck.Ui.Model.Serialization.UiCanonicalJson.SerializeToUtf8Bytes(editorView.Tree).Length,
			MacroDeck.Ui.Model.Serialization.UiCanonicalJson.SerializeToUtf8Bytes(liveView.Tree).Length);
	}

	// A frame with an aspect gives the components a size to draw at. scale is 0 for the editor's preview: its height
	// then sets the scale.
	private static UiRepeat<string> Fitted(MixerModel model, double scale) => new()
	{
		Key = "fr",
		// Rebuilt when the device changes shape; values inside follow their states.
		Items = UiValue.From<IReadOnlyList<string>>(() => [model.Device.Value.GetHashCode().ToString(CultureInfo.InvariantCulture)]),
		KeySelector = key => key,
		Template = (_, _) =>
		{
			var device = model.Device.Peek();
			var aspect = (double)device.Columns / device.Rows;
			var height = Math.Min(PreviewHeight, PreviewMaxWidth / aspect);
			var s = scale > 0 ? scale : height;
			return new UiModifier
			{
				Key = "f",
				Fill = true,
				Frame = scale > 0
					? new UiFrame { AspectRatio = aspect }
					: new UiFrame { AspectRatio = aspect, Height = UiLength.OfBasis(height) },
				Radius = B(s, KeyRadius * 1.5),
				BorderWidth = B(s, 0.003),
				BorderColor = PanelEdge,
				Clip = UiComponentClips.Bounds,
				Child = new UiLayer
				{
					Key = "p",
					Children = [Background(model, "bg"), Grid(model, device, s)],
				},
			};
		},
	};

	// Brushed metal when the image is there, a gradient when not.
	private static UiElement Background(MixerModel model, string key) =>
		model.Art is { } art
			? new UiButton { Key = key, Fill = true, Background = Panel, Source = art.Panel, Fit = UiComponentImageFits.Cover }
			: new UiModifier { Key = key + "-style", Background = PanelFace, Child = new UiStack { Key = key, Fill = true } };

	private static UiGrid Grid(MixerModel model, VirtualDevice device, double s)
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

					cells.Add(element.IsSlider ? Slider(model, element, s) : Key(model, element, small: false, s));
				}
				else if (here.Count > 0)
				{
					cells.Add(SmallPair(model, x, y, here, s));
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
			Gap = B(s, CellGap),
			Padding = B(s, PanelPadding),
			Children = cells,
		};
	}

	private static UiStack SmallPair(MixerModel model, int x, int y, IReadOnlyList<DeviceElement> smalls, double s) => new()
	{
		Key = CellKey(x, y),
		Direction = UiComponentDirections.Vertical,
		Gap = B(s, CellGap / 2),
		Children =
		[
			.. Enumerable.Range(0, 2).Select(slot => smalls.FirstOrDefault(small => small.Slot == slot) is { } small
				? (UiElement)Key(model, small, small: true, s)
				: Empty(model, x, y, slot, CellKey(x, y) + (slot == 0 ? "-top" : "-bottom"))),
		],
	};

	private static UiModifier Key(MixerModel model, DeviceElement element, bool small, double s)
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
			Padding = B(s, small ? 0.008 : 0.012),
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
					Frame = new UiFrame { Height = L(s, LedHeight) },
					Opacity = UiValue.From(() => lit.Value ? 1.0 : 0.45),
					Child = new UiShape { Key = "led-shape", Shape = UiComponentShapes.Capsule, Color = accent },
				},
				Label(element.Label, small ? SmallLabelSize : LabelSize, Legend, s),
				new UiStack { Key = "spacer" },
			],
		};

		return Styled(element.Id, element.Label, button, () => lit.Value, () => model.Selected.Value == element.Id, element.Width, element.Height, s);
	}

	// A fader channel: in the mixer the readout, the fader and the label; in the editor's small preview just an icon
	// and the label.
	private static UiModifier Slider(MixerModel model, DeviceElement element, double s)
	{
		var vertical = element.Kind == DeviceElementKind.VerticalSlider;
		var direction = vertical ? UiComponentDirections.Vertical : UiComponentDirections.Horizontal;
		var label = Label(element.Label, SmallLabelSize, Legend, s);

		UiElement channel;
		if (model.Editing)
		{
			channel = new UiButton
			{
				Key = "ch",
				Direction = UiComponentDirections.Vertical,
				Justify = UiComponentJustify.Center,
				Align = UiComponentAlignments.Center,
				Gap = B(s, 0.008),
				Background = Channel,
				Events = [UiEventHandler.On(UiComponentEvents.Press, () => model.CellPressed!(element.X, element.Y, 0))],
				Children =
				[
					new UiIcon { Key = "icon", Icon = vertical ? UiIcons.Sliders : UiIcons.AlignMiddle, Size = B(s, IconSize), Color = element.Color ?? MutedLegend },
					label,
				],
			};
		}
		else
		{
			var level = model.Level(element);
			var cap = Math.Clamp(CapLength / (vertical ? element.Height : element.Width), 0.12, 0.36);
			var readout = new UiTextRun
			{
				Key = "val",
				Text = UiText.From(() => ((int)Math.Round(level.Value * 127)).ToString(CultureInfo.InvariantCulture)),
				Size = B(s, SmallLabelSize),
				Weight = UiComponentTextWeights.Medium,
				Color = MutedLegend,
				Align = UiComponentAlignments.Center,
				MaxLines = 1,
			};
			var fader = new UiLayer
			{
				Key = "fd",
				Fill = true,
				Children =
				[
					Track(model, vertical, cap, s),
					LevelFill(element, vertical, cap, level, s),
					Cap(model, vertical, cap, level),
					Input(model, element, vertical, level),
				],
			};

			channel = new UiStack
			{
				Key = "ch",
				Direction = direction,
				Align = UiComponentAlignments.Center,
				Gap = B(s, 0.008),
				Padding = B(s, 0.012),
				Background = Channel,
				Children = vertical ? [readout, fader, label] : [label, fader, readout],
			};
		}

		return new UiModifier
		{
			Key = element.Id,
			ColumnSpan = element.Width,
			RowSpan = element.Height,
			Radius = B(s, KeyRadius),
			BorderWidth = B(s, 0.003),
			BorderColor = UiValue.From(() => model.Selected.Value == element.Id ? SelectedRim : Rim),
			Clip = UiComponentClips.Bounds,
			AccessibilityLabel = UiText.Of(element.Label),
			Child = channel,
		};
	}

	// The fader is three layers along its length, each a stack of fractions of it: the cap's centre travels from cap/2
	// to 1 - cap/2, and the track image is placed so its first and last ticks sit at those two points.
	private static UiStack Along(bool vertical, string key, IReadOnlyList<UiElement> children, UiValue<string>? align = null) => new()
	{
		Key = key,
		Fill = true,
		Direction = vertical ? UiComponentDirections.Vertical : UiComponentDirections.Horizontal,
		Align = align ?? UiComponentAlignments.Stretch,
		Children = children,
	};

	private static UiStack Gap(string key, Func<double> fraction) =>
		new() { Key = key, MainSize = UiSize.From(() => UiLength.OfParent(Math.Max(0, fraction()))) };

	private static UiStack Track(MixerModel model, bool vertical, double cap, double s)
	{
		var length = (1 - cap) / (1 - (2 * TrackMargin));
		var before = (cap / 2) - (TrackMargin * length);
		UiElement track = model.Art is { } art
			? new UiButton
			{
				Key = "tr",
				MainSize = UiSize.FromParent(length),
				Background = Transparent,
				Source = vertical ? art.TrackVertical : art.TrackHorizontal,
				Fit = UiComponentImageFits.Contain,
			}
			: new UiModifier
			{
				Key = "trf",
				MainSize = UiSize.FromParent(length),
				Frame = vertical ? new UiFrame { Width = L(s, SlotWidth) } : new UiFrame { Height = L(s, SlotWidth) },
				Child = new UiShape { Key = "tr", Shape = UiComponentShapes.Capsule, Color = SlotColor },
			};

		// Stretched across: the image is fitted into the full width, a plain slot keeps its own.
		return Along(vertical, "tk", [Gap("tb", () => before), track], model.Art is null ? UiComponentAlignments.Center : UiComponentAlignments.Stretch);
	}

	// The slot lit in the element's color from the bottom (left) tick to the cap.
	private static UiStack LevelFill(DeviceElement element, bool vertical, double cap, UiState<double> level, double s)
	{
		var fill = new UiModifier
		{
			Key = "lvf",
			MainSize = UiSize.From(() => UiLength.OfParent(level.Value * (1 - cap))),
			Frame = vertical ? new UiFrame { Width = L(s, SlotWidth) } : new UiFrame { Height = L(s, SlotWidth) },
			Opacity = 0.85,
			Child = new UiShape { Key = "lv", Shape = UiComponentShapes.Rectangle, Color = element.Color ?? DefaultAccent },
		};

		return vertical
			? Along(true, "lf", [Gap("lb", () => (cap / 2) + ((1 - level.Value) * (1 - cap))), fill], UiComponentAlignments.Center)
			: Along(false, "lf", [Gap("lb", () => cap / 2), fill], UiComponentAlignments.Center);
	}

	private static UiStack Cap(MixerModel model, bool vertical, double cap, UiState<double> level)
	{
		UiElement face = model.Art is { } art
			? new UiButton
			{
				Key = "ci",
				MainSize = UiSize.FromParent(cap),
				Background = Transparent,
				Source = vertical ? art.CapVertical : art.CapHorizontal,
				Fit = UiComponentImageFits.Contain,
			}
			: new UiShape { Key = "ci", MainSize = UiSize.FromParent(cap), Shape = UiComponentShapes.RoundedRectangle, Color = "#3A3C42" };

		return Along(vertical, "cp", [Gap("cb", () => (vertical ? 1 - level.Value : level.Value) * (1 - cap)), face]);
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
		Events =
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

	// An empty cell or half: nothing in the live mixer, a faint place target in the editor. One node each, as an
	// empty grid has many.
	private static UiElement Empty(MixerModel model, int x, int y, int slot, string key) =>
		model.Editing
			? new UiButton
			{
				Key = key,
				Background = "#00000040",
				BorderStyle = UiComponentBorderStyles.Static,
				BorderColor = "#5A5D6466",
				Events = [UiEventHandler.On(UiComponentEvents.Press, () => model.CellPressed!(x, y, slot))],
			}
			: new UiStack { Key = key };

	// Background and radius style the button itself, so the modifier emits no node: the grid spans go on the button.
	private static UiModifier Styled(string key, string label, UiButton button, Func<bool> lit, Func<bool> selected, int width, int height, double s) => new()
	{
		Key = key,
		Background = UiBackgroundValue.From(() => lit() ? KeyFaceLit : KeyFace),
		Radius = B(s, KeyRadius),
		AccessibilityLabel = UiText.Of(label),
		Child = button with
		{
			ColumnSpan = width,
			RowSpan = height,
			BorderStyle = UiComponentBorderStyles.Static,
			BorderColor = UiValue.From(() => selected() ? SelectedRim : Rim),
		},
	};

	private static UiTextRun Label(string text, double size, string color, double s) => new()
	{
		Key = "label",
		Text = UiText.Of(text),
		Size = B(s, size),
		Weight = UiComponentTextWeights.SemiBold,
		Color = color,
		Align = UiComponentAlignments.Center,
		MaxLines = 2,
	};

	private static UiSize B(double scale, double basis) => UiSize.FromBasis(basis * scale);

	private static UiLength L(double scale, double basis) => UiLength.OfBasis(basis * scale);

	private static string CellKey(int x, int y) => string.Create(CultureInfo.InvariantCulture, $"cell-{x}-{y}");
}
