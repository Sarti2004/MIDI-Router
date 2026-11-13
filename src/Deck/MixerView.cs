using System.Collections.Concurrent;
using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Model.Resources;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;

namespace MIDIRouter.Deck;

internal sealed record MixerArt(UiResource Panel)
{
	public static string Folder { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "Mixer");
}

internal sealed class MixerModel(DeviceActions actions, MixerArt? art)
{
	public DeviceActions Actions { get; } = actions;

	public MixerArt? Art { get; } = art;

	public UiState<VirtualDevice> Device { get; } = new(VirtualDevice.Default);

	public UiState<string?> Selected { get; } = new(null);

	public Action<int, int, int>? CellPressed { get; init; }

	public bool Editing => CellPressed is not null;

	public UiState<DevicePortState> Power { get; } = new(DevicePortState.Off);

	public Func<CancellationToken, Task>? PowerPressed { get; init; }

	public UiState<bool> PowerHeld { get; } = new(false);

	public ActivityLed MidiIn { get; } = new();

	public ActivityLed MidiOut { get; } = new();

	public bool Powered => Power.Value == DevicePortState.On;

	public ConcurrentDictionary<string, UiState<bool>> Lit { get; } = new(StringComparer.Ordinal);

	public ConcurrentDictionary<string, UiState<bool>> Leds { get; } = new(StringComparer.Ordinal);

	public ConcurrentDictionary<string, UiState<double>> Levels { get; } = new(StringComparer.Ordinal);

	public UiState<bool> LitState(DeviceElement element) => Lit.GetOrAdd(element.Id, _ => new UiState<bool>(false));

	public UiState<bool> Led(DeviceElement element) => Leds.GetOrAdd(element.Id, _ => new UiState<bool>(Actions.Led(element)));

	public UiState<double> Level(DeviceElement element) =>
		Levels.GetOrAdd(element.Id, _ => new UiState<double>(Actions.SliderValue(element) / 127.0));
}

// copy-paste not sure about it
internal sealed class ActivityLed
{
	private const long GlowMs = 70;

	private long _offAt;
	private int _lit;

	public UiState<bool> Lit { get; } = new(false);

	public void Pulse()
	{
		Volatile.Write(ref _offAt, Environment.TickCount64 + GlowMs);
		if (Interlocked.Exchange(ref _lit, 1) == 0)
		{
			Lit.Set(true);
			_ = GoOutAsync();
		}
	}

	private async Task GoOutAsync()
	{
		while (true)
		{
			var wait = Volatile.Read(ref _offAt) - Environment.TickCount64;
			if (wait > 0)
			{
				await Task.Delay(TimeSpan.FromMilliseconds(wait)).ConfigureAwait(false);
				continue;
			}

			Lit.Set(false);
			Volatile.Write(ref _lit, 0);

			if (Volatile.Read(ref _offAt) <= Environment.TickCount64 || Interlocked.Exchange(ref _lit, 1) != 0)
			{
				return;
			}

			Lit.Set(true);
		}
	}
}

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
	private const string DefaultAccent = "#5B8DEF";
	private const string DarkLevel = "#3A3C42";
	private const string PowerOnLed = "#46D369";
	private const string PowerStartingLed = "#FFB547";
	private const string PowerOffLed = "#5A2A2A";
	private const string MidiInLed = "#46D369";
	private const string MidiOutLed = "#FFB547";

	private const double CellGap = 0.018;
	private const double PanelPadding = 0.03;
	private const double KeyRadius = 0.014;
	private const double LabelSize = 0.032;
	private const double SmallLabelSize = 0.026;
	private const double LedHeight = 0.012;
	private const double IconSize = 0.05;

	private const double FaderThickness = 0.02;

	private const double PreviewScale = 4;

	// The live mixer's grid is finer than the device's: a cell is Sub × Sub units, under a strip one unit high holding
	// the nameplate and the power key. Only the grid's spans size things reliably inside it.
	private const int Sub = 3;

	// The folder's height the panel's grid gets: the folder's and the panel's padding taken off.
	private const double GridHeight = 1 - (2 * 0.02) - (2 * PanelPadding);


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
					Children = [Fitted(model)],
				},
			],
		};

	public static UiElement Preview(MixerModel model) => new UiRepeat<string>
	{
		Key = "fr",
		Items = UiValue.From<IReadOnlyList<string>>(() => [model.Device.Value.GetHashCode().ToString(CultureInfo.InvariantCulture)]),
		KeySelector = key => key,
		Template = (_, _) =>
		{
			var device = model.Device.Peek();

			const double s = PreviewScale;
			return new MacroDeck.Ui.Config.UiConfigStack
			{
				Key = "rows",
				Gap = MacroDeck.Ui.Config.UiStackGaps.None,
				Children =
				[
					.. Enumerable.Range(0, device.Rows).Select(y => new MacroDeck.Ui.Config.UiConfigStack
					{
						Key = string.Create(CultureInfo.InvariantCulture, $"r{y}"),
						Direction = UiComponentDirections.Horizontal,
						Gap = MacroDeck.Ui.Config.UiStackGaps.None,
						Children = [.. Enumerable.Range(0, device.Columns).Select(x => CellBlock(model, device, x, y, s))],
					}),
				],
			};
		},
	};

	private static UiModifier CellBlock(MixerModel model, VirtualDevice device, int x, int y, double s)
	{
		UiElement content = device.Elements.FirstOrDefault(element => element.Kind != DeviceElementKind.SmallButton && element.Covers(x, y)) is { } element
			? element.IsSlider ? Slider(model, element, s, 1, 1) : Key(model, element, small: false, s, 1, 1)
			: device.Elements.Where(small => small.X == x && small.Y == y).ToList() is { Count: > 0 } smalls
				? SmallPair(model, x, y, smalls, s)
				: Empty(model, x, y, 0, CellKey(x, y));

		return new UiModifier
		{
			Key = string.Create(CultureInfo.InvariantCulture, $"c{x}"),
			Fill = true,
			Frame = new UiFrame { AspectRatio = 1 },
			Child = new UiGrid { Key = "g", Columns = 1, Rows = 1, Padding = B(s, CellGap / 2), Children = [content] },
		};
	}

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

	private static UiRepeat<string> Fitted(MixerModel model) => new()
	{
		Key = "fr",
		Items = UiValue.From<IReadOnlyList<string>>(() => [model.Device.Value.GetHashCode().ToString(CultureInfo.InvariantCulture)]),
		KeySelector = key => key,
		Template = (_, _) =>
		{
			var device = model.Device.Peek();
			const double s = 1;
			return new UiModifier
			{
				Key = "f",
				Fill = true,
				Frame = new UiFrame { AspectRatio = (double)(device.Columns * Sub) / ((device.Rows * Sub) + 1) },
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

	private static UiModifier Background(MixerModel model, string key) =>
		model.Art is { } art
			? new UiModifier
			{
				Key = key + "-style",
				Radius = UiSize.FromBasis(0),
				Child = new UiButton { Key = key, Fill = true, Background = Panel, Source = art.Panel, Fit = UiComponentImageFits.Cover },
			}
			: new UiModifier { Key = key + "-style", Background = PanelFace, Child = new UiStack { Key = key, Fill = true } };

	private static UiGrid Grid(MixerModel model, VirtualDevice device, double s)
	{
		var cells = new List<UiElement>(Header(model, device, s));
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

					cells.Add(element.IsSlider
						? Slider(model, element, s, element.Width * Sub, element.Height * Sub)
						: Key(model, element, small: false, s, element.Width * Sub, element.Height * Sub));
				}
				else if (here.Count > 0)
				{
					cells.Add(SmallPair(model, x, y, here, s, Sub));
				}
				else
				{
					cells.Add(Empty(model, x, y, 0, CellKey(x, y), Sub));
				}
			}
		}

		return new UiGrid
		{
			Key = "g",
			Fill = true,
			Columns = device.Columns * Sub,
			Rows = (device.Rows * Sub) + 1,
			Gap = B(s, CellGap),
			Padding = B(s, PanelPadding),
			Children = cells,
		};
	}

	// The strip across the top: the nameplate, then the power key above the last column.
	private static IEnumerable<UiElement> Header(MixerModel model, VirtualDevice device, double s)
	{
		// A grid unit in basis. The basis is the folder's shorter side, and either side may limit the panel: a landscape
		// folder its height, a portrait one its width. The smaller of the two fits both.
		var unit = GridHeight / Math.Max((device.Rows * Sub) + 1, device.Columns * Sub);
		if (device.Columns > 1)
		{
			yield return Nameplate(model, device, (device.Columns - 1) * Sub, unit, s);
		}

		yield return PowerKey(model, Sub, unit, s);
	}

	// The device's name, silk-screened, and the MIDI activity LEDs: in for what the DAW sends, out for what the device sends.
	// Spaced apart rather than pushed by a filling spacer, which takes the whole strip and shoves the LEDs out of it.
	private static UiModifier Nameplate(MixerModel model, VirtualDevice device, int columns, double unit, double s)
	{
		var text = Math.Min(SmallLabelSize, unit * 0.5);
		return new UiModifier
		{
			Key = "plate-style",
			Radius = B(s, KeyRadius),
			BorderWidth = B(s, 0.003),
			BorderColor = PanelEdge,
			Child = new UiStack
			{
				Key = "plate",
				ColumnSpan = columns,
				Direction = UiComponentDirections.Horizontal,
				Justify = UiComponentJustify.SpaceBetween,
				Align = UiComponentAlignments.Center,

				// No room above and below the text in a strip one unit high: the padding is the inset at the ends.
				Padding = B(s, unit * 0.15),
				Background = Channel,
				Children =
				[
					new UiTextRun
					{
						Key = "name",
						Text = UiText.Of(device.Name.ToUpperInvariant()),
						Size = B(s, text),
						Weight = UiComponentTextWeights.SemiBold,
						Color = Legend,
						MaxLines = 1,
					},
					new UiStack
					{
						Key = "midi",
						Direction = UiComponentDirections.Horizontal,
						Align = UiComponentAlignments.Center,
						Gap = B(s, unit * 0.3),
						Children =
						[
							ActivityLight("in", Strings.Mixer.MidiIn(), model.MidiIn, MidiInLed, unit, text, s),
							ActivityLight("out", Strings.Mixer.MidiOut(), model.MidiOut, MidiOutLed, unit, text, s),
						],
					},
				],
			},
		};
	}

	private static UiStack ActivityLight(string key, LocalizedText label, ActivityLed led, string color, double unit, double text, double s) => new()
	{
		Key = key,
		Direction = UiComponentDirections.Horizontal,
		Align = UiComponentAlignments.Center,
		Gap = B(s, unit * 0.1),
		Children =
		[
			Lamp(UiValue.Of(color), UiValue.From(() => led.Lit.Value ? 1.0 : 0.18), unit, s),
			new UiTextRun
			{
				Key = "label",
				Text = label,
				Size = B(s, text * 0.85),
				Weight = UiComponentTextWeights.Medium,
				Color = MutedLegend,
				MaxLines = 1,
			},
		],
	};

	// A key with the power symbol and its LED: green on, amber while the ports come up, dull red off.
	private static UiModifier PowerKey(MixerModel model, int columns, double unit, double s)
	{
		var button = new UiButton
		{
			Key = "power",
			ColumnSpan = columns,
			Direction = UiComponentDirections.Horizontal,
			Justify = UiComponentJustify.Center,
			Align = UiComponentAlignments.Center,
			Gap = B(s, unit * 0.18),
			Background = Transparent,
			BorderStyle = UiComponentBorderStyles.Static,
			BorderColor = Rim,
			Events =
			[
				UiEventHandler.On(UiComponentEvents.PressStart, () => model.PowerHeld.Set(true)),
				UiEventHandler.On(UiComponentEvents.PressEnd, () => model.PowerHeld.Set(false)),
				UiEventHandler.OnAsync(UiComponentEvents.Press, cancellationToken => model.PowerPressed?.Invoke(cancellationToken) ?? Task.CompletedTask),
			],
			Children =
			[
				Lamp(
					UiValue.From(() => model.Power.Value switch
					{
						DevicePortState.On => PowerOnLed,
						DevicePortState.Starting => PowerStartingLed,
						_ => PowerOffLed,
					}),
					UiValue.Of(1.0),
					unit,
					s),
				new UiIcon
				{
					Key = "icon",
					Icon = UiIcons.Power,
					Size = B(s, Math.Min(IconSize, unit * 0.55)),
					Color = UiValue.From(() => model.Powered ? Legend : MutedLegend),
				},
			],
		};

		return new UiModifier
		{
			Key = "power-style",
			Background = UiBackgroundValue.From(() => model.PowerHeld.Value ? KeyFaceLit : KeyFace),
			Radius = B(s, KeyRadius),
			AccessibilityLabel = Strings.Mixer.Power(),
			Child = button,
		};
	}

	private static UiModifier Lamp(UiValue<string> color, UiValue<double> opacity, double unit, double s) => new()
	{
		Key = "lamp",
		Frame = new UiFrame { Width = L(s, unit * 0.22), Height = L(s, unit * 0.22) },
		Opacity = opacity,
		Child = new UiShape { Key = "dot", Shape = UiComponentShapes.Circle, Color = color },
	};

	private static UiStack SmallPair(MixerModel model, int x, int y, IReadOnlyList<DeviceElement> smalls, double s, int span = 1) => new()
	{
		Key = CellKey(x, y),
		ColumnSpan = span > 1 ? span : default,
		RowSpan = span > 1 ? span : default,
		Fill = true,
		Direction = UiComponentDirections.Vertical,
		Gap = B(s, CellGap / 2),
		Children =
		[
			// Each half fills its share: an empty half has no content of its own and would otherwise collapse.
			.. Enumerable.Range(0, 2).Select(slot => smalls.FirstOrDefault(small => small.Slot == slot) is { } small
				? (UiElement)Key(model, small, small: true, s, 1, 1)
				: Empty(model, x, y, slot, CellKey(x, y) + (slot == 0 ? "-top" : "-bottom"), fill: true)),
		],
	};

	private static UiModifier Key(MixerModel model, DeviceElement element, bool small, double s, int columns, int rows)
	{
		var lit = model.LitState(element);
		var led = model.Editing ? null : model.Led(element);
		var accent = element.Color ?? DefaultAccent;

		// A modifier adds no id of its own: what it wraps is keyed by the element.
		var button = new UiButton
		{
			Key = element.Id + "-button",
			Direction = UiComponentDirections.Vertical,
			Justify = UiComponentJustify.SpaceBetween,
			Align = UiComponentAlignments.Stretch,
			Padding = B(s, small ? 0.008 : 0.012),
			Fill = small,
			Background = Transparent,
			Events = model.Editing
				? [UiEventHandler.On(UiComponentEvents.Press, () => model.CellPressed!(element.X, element.Y, element.Slot))]
				:
				[
					// As a hardware button: the message goes out when it goes down and is released when it comes up.
					UiEventHandler.On(UiComponentEvents.PressStart, () =>
					{
						lit.Set(true);
						model.Actions.Press(element);
					}),
					UiEventHandler.On(UiComponentEvents.PressEnd, () =>
					{
						lit.Set(false);
						model.Actions.Release(element);
					}),
				],
			Children =
			[
				// The LED strip in the element's color: bright while the DAW has it on, faint when not, dark while the
				// device is off. The editor shows it at rest.
				new UiModifier
				{
					Key = "led",
					Frame = new UiFrame { Height = L(s, LedHeight) },
					Opacity = led is null ? 0.45 : UiValue.From(() => !model.Powered ? 0.08 : led.Value ? 1.0 : 0.22),
					Child = new UiShape { Key = "led-shape", Shape = UiComponentShapes.Capsule, Color = accent },
				},
				Label(element.Label, small ? SmallLabelSize : LabelSize, Legend, s),
				new UiStack { Key = "spacer" },
			],
		};

		// Backlit, as a pad on studio hardware: the whole key glows in the element's color while the DAW has it on.
		var glow = Glow(accent);
		return Styled(element.Id, element.Label, button, () => lit.Value, () => model.Selected.Value == element.Id, columns, rows, model.Editing ? 0 : s,
			model.Editing ? null : () => !model.Powered,
			led is null ? null : () => led.Value && model.Powered ? glow : null);
	}

	// The element's color, strong at the top and fading down, over the panel. A color with its own alpha is kept as is.
	private static UiGradient Glow(string color)
	{
		var opaque = color.Length == 7 && color[0] == '#';
		return UiGradient.Linear(
			180,
			new UiGradientStop { Offset = 0.00, Color = opaque ? color + "E6" : color },
			new UiGradientStop { Offset = 1.00, Color = opaque ? color + "80" : color });
	}

	// A fader channel: in the mixer the readout, the fader and the label; in the editor's small preview just an icon
	// and the label.
	private static UiModifier Slider(MixerModel model, DeviceElement element, double s, int columns, int rows)
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
			var readout = new UiTextRun
			{
				Key = "val",
				Text = UiText.From(() => Readout(element, (int)Math.Round(level.Value * 127))),
				Size = B(s, SmallLabelSize),
				Weight = UiComponentTextWeights.Medium,

				// A dark display while the device is off.
				Color = UiValue.From(() => model.Powered ? MutedLegend : Transparent),
				Align = UiComponentAlignments.Center,
				MaxLines = 1,
			};
			UiElement fader = Fader(element, vertical, level, s, model);
			if (element.Centered)
			{
				fader = new UiLayer { Key = "trk", Fill = true, Children = [CenterMark(vertical, s), fader] };
			}

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
			ColumnSpan = columns,
			RowSpan = rows,
			Radius = B(model.Editing ? 0 : s, KeyRadius),
			BorderWidth = B(s, 0.003),
			BorderColor = UiValue.From(() => model.Selected.Value == element.Id ? SelectedRim : Rim),
			Clip = UiComponentClips.Bounds,
			AccessibilityLabel = UiText.Of(element.Label),
			Disabled = model.Editing ? default : UiValue.From(() => !model.Powered),
			Child = channel,
		};
	}

	// Macro Deck's own slider, in the element's color. A centered one has no level bar, which would fill from one end:
	// its knob over the center mark shows the position, as on a pan pot.
	private static UiSlider Fader(DeviceElement element, bool vertical, UiState<double> level, double s, MixerModel model) => new()
	{
		Key = "fd",
		Fill = true,
		Level = UiValue.From(() => level.Value),
		Step = 1.0 / 127,
		Direction = vertical ? UiComponentDirections.Vertical : UiComponentDirections.Horizontal,

		// As a fader cap: a touch takes it from where it is rather than jumping it to the finger.
		Interaction = UiComponentSliderInteractions.Relative,
		LevelColor = element.Centered ? Transparent : UiValue.From(() => model.Powered ? element.Color ?? DefaultAccent : DarkLevel),
		Thickness = B(s, FaderThickness),
		Events =
		[
			UiEventHandler.OnAsync(UiComponentEvents.Adjust, (data, cancellationToken) => Slide(model, element, level, data, cancellationToken)),
			UiEventHandler.OnAsync(UiComponentEvents.Change, (data, cancellationToken) => Slide(model, element, level, data, cancellationToken)),
			.. element.Centered
				? [UiEventHandler.OnAsync(UiComponentEvents.DoublePress, cancellationToken => Move(model, element, level, DeviceActions.Center, cancellationToken))]
				: Array.Empty<UiEventHandler>(),
		],
	};

	private static Task Slide(MixerModel model, DeviceElement element, UiState<double> level, UiEventData data, CancellationToken cancellationToken) =>
		data.Raw is { ValueKind: System.Text.Json.JsonValueKind.Number } raw
			? Move(model, element, level, (int)Math.Round(Math.Clamp(raw.GetDouble(), 0, 1) * 127), cancellationToken)
			: Task.CompletedTask;

	// The knob goes where the value is: to the center when a centered slider is moved near it.
	private static Task Move(MixerModel model, DeviceElement element, UiState<double> level, int value, CancellationToken cancellationToken)
	{
		value = DeviceActions.Snap(element, value);
		level.Set(value / 127.0);
		return model.Actions.SlideAsync(element, value, cancellationToken);
	}

	// A centered slider reads as a pan: L100 to C to R100, as Cubase shows it.
	private static string Readout(DeviceElement element, int value) => !element.Centered
		? value.ToString(CultureInfo.InvariantCulture)
		: value == DeviceActions.Center ? "C"
		: value < DeviceActions.Center
			? string.Create(CultureInfo.InvariantCulture, $"L{Math.Round((DeviceActions.Center - value) * 100.0 / DeviceActions.Center)}")
			: string.Create(CultureInfo.InvariantCulture, $"R{Math.Round((value - DeviceActions.Center) * 100.0 / (127 - DeviceActions.Center))}");

	// A line across the track at its middle, behind the slider, longer than the track is thick so it shows either side.
	private static UiStack CenterMark(bool vertical, double s) => new()
	{
		Key = "mark",
		Fill = true,
		Justify = UiComponentJustify.Center,
		Align = UiComponentAlignments.Center,
		Children =
		[
			new UiModifier
			{
				Key = "tick",
				Frame = vertical
					? new UiFrame { Width = L(s, FaderThickness * 2.6), Height = L(s, 0.004) }
					: new UiFrame { Width = L(s, 0.004), Height = L(s, FaderThickness * 2.6) },
				Child = new UiShape { Key = "line", Shape = UiComponentShapes.Rectangle, Color = MutedLegend },
			},
		],
	};

	// An empty cell or half: nothing in the live mixer, a faint place target in the editor. One node each, as an
	// empty grid has many.
	private static UiElement Empty(MixerModel model, int x, int y, int slot, string key, int span = 1, bool fill = false) =>
		model.Editing
			? new UiButton
			{
				Key = key,
				Fill = fill,
				Background = "#00000040",
				BorderStyle = UiComponentBorderStyles.Static,
				BorderColor = "#5A5D6466",
				Events = [UiEventHandler.On(UiComponentEvents.Press, () => model.CellPressed!(x, y, slot))],
			}
			: new UiStack { Key = key, ColumnSpan = span > 1 ? span : default, RowSpan = span > 1 ? span : default, Fill = fill };

	private static UiModifier Styled(string key, string label, UiButton button, Func<bool> lit, Func<bool> selected, int width, int height, double cornerScale,
		Func<bool>? disabled, Func<UiGradient?>? glow = null) => new()
	{
		Key = key,
		Background = UiBackgroundValue.From(() => lit() ? KeyFaceLit : glow?.Invoke() ?? KeyFace),
		Radius = B(cornerScale, KeyRadius),
		AccessibilityLabel = UiText.Of(label),
		Disabled = disabled is null ? default : UiValue.From(disabled),
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
