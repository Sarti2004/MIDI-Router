using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using MacroDeck.Sdk.Ui;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Config;
using MacroDeck.Ui.Config.Options;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Model.Events;
using MacroDeck.Ui.Model.Nodes;
using MacroDeck.Ui.Model.Patches;
using MacroDeck.Ui.Runtime;
using MIDIRouter.Routing;

namespace MIDIRouter.Integration;

internal sealed record LearnTarget(string Device, string TypeField, string NoteField, string ControllerField);

internal sealed class RouterConfigView(MidiRouter router)
{
	private static readonly TimeSpan LearnTimeout = TimeSpan.FromSeconds(30);

	private static readonly IReadOnlyDictionary<string, LocalizedText> NoErrors = new Dictionary<string, LocalizedText>();

	private readonly UiState<ConfigFlowStep?> _step = new(null);
	private readonly UiState<IReadOnlyDictionary<string, LocalizedText>> _errors = new(NoErrors);
	private readonly UiState<IReadOnlyDictionary<string, LearnTarget>> _targets = new(new Dictionary<string, LearnTarget>());

	// The field being learned, or null.
	private readonly UiState<string?> _learning = new(null);

	private readonly ConcurrentDictionary<string, UiState<string>> _text = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, UiState<double>> _numbers = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, UiState<IReadOnlyList<string>>> _lists = new(StringComparer.Ordinal);
	private readonly Lock _learnLock = new();
	private IDisposable? _learn;

	public void Show(ConfigFlowStep step, IReadOnlyDictionary<string, LocalizedText>? errors, IReadOnlyList<LearnTarget> targets)
	{
		StopLearning();
		foreach (var field in step.Fields)
		{
			switch (field.Type)
			{
				case ActionParameterType.MultiSelect:
					_lists.GetOrAdd(field.Name, _ => new UiState<IReadOnlyList<string>>(ListDefault(field.DefaultValue)));
					break;
				case ActionParameterType.Number:
					_numbers.GetOrAdd(field.Name, _ => new UiState<double>(Convert.ToDouble(field.DefaultValue ?? field.Min ?? 0, CultureInfo.InvariantCulture)));
					break;
				default:
					_text.GetOrAdd(field.Name, _ => new UiState<string>(Convert.ToString(field.DefaultValue, CultureInfo.InvariantCulture) ?? string.Empty));
					break;
			}
		}

		_targets.Set(targets.ToDictionary(target => target.TypeField, StringComparer.Ordinal));
		_errors.Set(errors ?? NoErrors);
		_step.Set(step);
	}

	public void Close()
	{
		StopLearning();
		_step.Set(null);
	}

	public IUiSession? CreateSession(UiSessionRequest request, Action onClosed) =>
		_step.Peek() is null
			? null
			: new ViewSession(new UiView(request.Surface, Root()), () =>
			{
				StopLearning();
				onClosed();
			});

	private UiConfigStack Root() => new()
	{
		Key = "root",
		Children =
		[
			new UiRepeat<ConfigFlowStep>
			{
				Key = "steps",
				Items = UiValue.From<IReadOnlyList<ConfigFlowStep>>(() => _step.Value is { } step ? [step] : []),
				KeySelector = step => step.StepId,
				Template = (step, _) => new UiFragment { Key = "fields", Children = [.. step.Fields.Select(Field)] },
			},
		],
	};

	private UiElement Field(ActionParameter field)
	{
		UiElement input;
		if (_targets.Peek().TryGetValue(field.Name, out var target))
		{
			input = new UiConfigStack
			{
				Key = field.Name + "-learnable",
				Children =
				[
					new UiHeading { Key = field.Name + "-label", Text = Text(field.Label) },
					new UiConfigStack
					{
						Key = field.Name + "-row",
						Direction = UiComponentDirections.Horizontal,
						Children = [Input(field, inRow: true), LearnButton(target)],
					},
					new UiWhen
					{
						Key = field.Name + "-listening",
						Condition = () => _learning.Value == target.TypeField,
						Content = () => new UiBanner
						{
							Key = field.Name + "-listening-banner",
							Severity = "info",
							Text = Strings.ConfigFlow.Shortcuts.Listening(target.Device),
						},
					},
				],
			};
		}
		else
		{
			input = Input(field);
		}

		if (field.VisibleWhen is { } visibility && _text.TryGetValue(visibility.ParameterName, out var controller))
		{
			var shown = input;
			input = new UiWhen { Key = field.Name + "-when", Condition = () => visibility.Values.Contains(controller.Value, StringComparer.Ordinal), Content = () => shown };
		}

		return input;
	}

	private UiConfigButton LearnButton(LearnTarget target) => new()
	{
		Key = target.TypeField + "-learn",
		// The host draws an icon-only button: the icon itself shows that it is listening, ✕ to stop.
		Icon = UiValue.From(() => _learning.Value == target.TypeField ? UiIcons.X : UiIcons.MusicNote),
		Label = Strings.ConfigFlow.Shortcuts.Learn(),
		Events = [UiEventHandler.On(UiConfigEvents.Activate, () => ToggleLearning(target))],
	};

	private UiElement Input(ActionParameter field, bool inRow = false)
	{
		var invalid = UiValue.From(() => _errors.Value.ContainsKey(field.Name));
		var message = UiText.Optional(() => _errors.Value.TryGetValue(field.Name, out var error) ? error : UiText.None());
		return field.Type switch
		{
			ActionParameterType.MultiSelect => new UiMultiSelectInput
			{
				Key = field.Name,
				Label = Text(field.Label),
				HideLabel = inRow,
				RowWeight = inRow ? 1 : UiValue.None<double>(),
				Description = Text(field.Description),
				Required = field.Required,
				Options = Options(field),
				Binding = Bind.To(_lists[field.Name]),
				Invalid = invalid,
				ValidationMessage = message,
			},
			ActionParameterType.Choice => new UiChoiceInput
			{
				Key = field.Name,
				Label = Text(field.Label),
				HideLabel = inRow,
				RowWeight = inRow ? 1 : UiValue.None<double>(),
				Description = Text(field.Description),
				Required = field.Required,
				Searchable = field.Searchable,
				Options = Options(field),
				Binding = Bind.To(_text[field.Name]),
				Invalid = invalid,
				ValidationMessage = message,
			},
			ActionParameterType.Number => new UiNumberInput
			{
				Key = field.Name,
				Label = Text(field.Label),
				HideLabel = inRow,
				RowWeight = inRow ? 1 : UiValue.None<double>(),
				Description = Text(field.Description),
				Required = field.Required,
				Min = field.Min is { } min ? min : UiValue.None<double>(),
				Max = field.Max is { } max ? max : UiValue.None<double>(),
				Step = field.Step is { } step ? step : UiValue.None<double>(),
				Binding = Bind.To(_numbers[field.Name]),
				Invalid = invalid,
				ValidationMessage = message,
			},
			_ => new UiStringInput
			{
				Key = field.Name,
				Label = Text(field.Label),
				HideLabel = inRow,
				RowWeight = inRow ? 1 : UiValue.None<double>(),
				Description = Text(field.Description),
				Placeholder = Text(field.Placeholder),
				Required = field.Required,
				Binding = Bind.To(_text[field.Name]),
				Invalid = invalid,
				ValidationMessage = message,
			},
		};
	}

	private void ToggleLearning(LearnTarget target)
	{
		lock (_learnLock)
		{
			var wasLearning = _learning.Peek() == target.TypeField;
			StopLearningLocked();
			if (wasLearning)
			{
				return;
			}

			_learning.Set(target.TypeField);
			var learn = router.Learn(target.Device, (kind, number) => Learned(target, kind, number));
			_learn = learn;
			_ = Task.Delay(LearnTimeout).ContinueWith(_ => StopLearning(learn), TaskScheduler.Default);
		}
	}

	private void StopLearning(IDisposable learn)
	{
		lock (_learnLock)
		{
			// Only if this is still the same Learn: the button may have been pressed again since.
			if (ReferenceEquals(_learn, learn))
			{
				StopLearningLocked();
			}
		}
	}

	private void Learned(LearnTarget target, MidiKinds kind, int number)
	{
		lock (_learnLock)
		{
			// Cancelled, timed out or moved to another keyboard while the press was on its way.
			if (_learning.Peek() != target.TypeField)
			{
				return;
			}

			StopLearningLocked();
			if (kind == MidiKinds.ControlChange)
			{
				_numbers[target.ControllerField].Set(number);
				_text[target.TypeField].Set(RouterConfig.ShortcutDto.ControlChangeType);
			}
			else
			{
				_text[target.NoteField].Set(number.ToString(CultureInfo.InvariantCulture));
				_text[target.TypeField].Set(RouterConfig.ShortcutDto.NoteType);
			}
		}
	}

	private void StopLearning()
	{
		lock (_learnLock)
		{
			StopLearningLocked();
		}
	}

	private void StopLearningLocked()
	{
		_learn?.Dispose();
		_learn = null;
		_learning.Set(null);
	}

	private static UiValue<IReadOnlyList<UiOption>> Options(ActionParameter field) =>
		UiValue.Of<IReadOnlyList<UiOption>>([.. (field.Options ?? []).Select(option => UiOption.Of(option.Value, option.Label))]);

	private static UiText Text(LocalizedText text) => text.IsEmpty ? UiText.None() : text;

	private static IReadOnlyList<string> ListDefault(object? value) => value switch
	{
		IEnumerable<string> items => [.. items],
		JsonElement { ValueKind: JsonValueKind.Array } array => [.. array.EnumerateArray().Select(item => item.ToString())],
		_ => [],
	};

	/// <summary>The adapter from a view to the host's session, as in the Macro Deck UI guide.</summary>
	private sealed class ViewSession : IUiSession
	{
		private readonly UiView _view;
		private readonly Action _onDisposed;

		public ViewSession(UiView view, Action onDisposed)
		{
			_view = view;
			_onDisposed = onDisposed;
			_view.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
			_view.HandlerFaulted += (_, fault) => Faulted?.Invoke(this, new UiSessionFaultedEventArgs(fault.Exception.Message, fault.Exception));
		}

		public event EventHandler? Changed;

		public event EventHandler<UiSessionFaultedEventArgs>? Faulted;

		public UiTree BuildTree() => _view.Tree;

		public IReadOnlyList<UiPatch> DrainPatches() => _view.DrainPatches();

		public void Dispatch(UiEvent uiEvent) => _view.Dispatch(uiEvent);

		public ValueTask DisposeAsync()
		{
			_onDisposed();
			_view.Dispose();
			return ValueTask.CompletedTask;
		}
	}
}
