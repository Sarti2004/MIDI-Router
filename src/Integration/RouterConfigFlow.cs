using System.Collections;
using System.Globalization;
using System.Text.Json;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using MacroDeck.Sdk.Ui;
using MIDIRouter.Routing;

namespace MIDIRouter.Integration;


internal sealed class RouterConfigFlow(MidiRouter router) : IUiConfigFlow
{
	private const string SettingsStepId = "settings";
	private const string ShortcutsStepId = "shortcuts";

	private const string KeyboardsField = "keyboards";

	private const string TypeNone = "none";
	private const string DefaultAction = "toggle";

	// The lowest key of the piano (A0)
	private const int DefaultNote = 21;

	private RouterConfig? _settings;

	private readonly RouterConfigView _view = new(router);

	private readonly Lock _previewLock = new();
	private RouterConfig? _beforePreview;
	private int _sessions;
	private bool _completed;

	public Task<ConfigFlowResult> StartAsync(IConfigFlowContext context, CancellationToken cancellationToken) =>
		Task.FromResult(Show(ConfigFlowResult.Step(BuildSettingsStep(router.Config))));

	public Task<ConfigFlowResult> SubmitAsync(
		string stepId,
		IReadOnlyDictionary<string, object?> input,
		IConfigFlowContext context,
		CancellationToken cancellationToken) =>
		Task.FromResult(Show(stepId == ShortcutsStepId ? SubmitShortcuts(input) : SubmitSettings(input)));

	public Task<IUiSession?> CreateUiSessionAsync(UiSessionRequest request, CancellationToken cancellationToken)
	{
		var session = _view.CreateSession(request, OnSessionClosed);
		if (session is not null)
		{
			lock (_previewLock)
			{
				_sessions++;
			}

			// A session reopened on the shortcuts page (after a retryable error) opens the keyboards again.
			if (_settings is { } settings)
			{
				Preview(settings);
			}
		}

		return Task.FromResult(session);
	}

	private ConfigFlowResult Show(ConfigFlowResult result)
	{
		if (result.NextStep is { } step)
		{
			_view.Show(step, result.FieldErrors, step.StepId == ShortcutsStepId ? LearnTargets() : []);
		}
		else
		{
			// Completed: the host re-initializes the integration with the stored values, so nothing is put back.
			lock (_previewLock)
			{
				_completed = true;
				_beforePreview = null;
			}

			_view.Close();
		}

		return result;
	}

	private void Preview(RouterConfig settings)
	{
		lock (_previewLock)
		{
			var current = router.Config;
			if (_completed || _sessions == 0 || current.InputDevices.SequenceEqual(settings.InputDevices, StringComparer.Ordinal))
			{
				return;
			}

			_beforePreview ??= current;
			router.Apply(current with { InputDevices = settings.InputDevices });
		}
	}

	private void OnSessionClosed()
	{
		lock (_previewLock)
		{
			if (--_sessions > 0 || _beforePreview is not { } before)
			{
				return;
			}

			_beforePreview = null;
			router.Apply(before);
		}
	}

	// Only keys from opened keyboard can be learned;
	private List<LearnTarget> LearnTargets()
	{
		var settings = _settings ?? router.Config;
		var open = router.Config.InputDevices;
		return
		[
			.. settings.InputDevices
				.Select((device, i) => (device, i))
				.Where(item => open.Contains(item.device, StringComparer.Ordinal))
				.Select(item => new LearnTarget(item.device, FieldName(item.i, "type"), FieldName(item.i, "note"), FieldName(item.i, "controller"))),
		];
	}

	private ConfigFlowResult SubmitSettings(IReadOnlyDictionary<string, object?> input)
	{
		var current = router.Config;
		var output = Text(input, RouterConfig.Keys.OutputDevice) ?? current.OutputDevice;
		var virtualName = Text(input, RouterConfig.Keys.VirtualPortName) is { Length: > 0 } name ? name : RouterConfig.DefaultVirtualPortName;

		var candidate = current with
		{
			OutputDevice = output,
			VirtualPortName = virtualName,
			ModeSound = ModeSound.Parse(Text(input, RouterConfig.Keys.ModeSound)) ?? current.ModeSound,
			StartMode = ModeController.Parse(Text(input, RouterConfig.Keys.StartMode)) ?? current.StartMode,
		};

		// Drop a selection that is the output itself: need more thinking, still buggy;
		var devices = List(input, KeyboardsField)
			.Where(device => !InputDevices.IsRouterOutput(device, candidate.OutputName))
			.ToList();

		if (string.IsNullOrEmpty(output))
		{
			return Error(BuildSettingsStep(candidate), RouterConfig.Keys.OutputDevice, Strings.ConfigFlow.Errors.NoOutput());
		}

		_settings = candidate with { InputDevices = devices };
		Preview(_settings);
		return devices.Count > 0
			? ConfigFlowResult.Step(BuildShortcutsStep(_settings))
			: Complete(_settings with { Shortcuts = [] });
	}

	private ConfigFlowResult SubmitShortcuts(IReadOnlyDictionary<string, object?> input)
	{
		var settings = _settings ?? router.Config;
		var shortcuts = new List<ModeShortcut>();
		for (var i = 0; i < settings.InputDevices.Count; i++)
		{
			var type = Text(input, FieldName(i, "type")) ?? TypeNone;
			if (type == TypeNone)
			{
				continue;
			}

			var numberField = FieldName(i, type == RouterConfig.ShortcutDto.ControlChangeType ? "controller" : "note");
			var shortcut = double.TryParse(Text(input, numberField), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number % 1 == 0
				? new RouterConfig.ShortcutDto(settings.InputDevices[i], type, (int)number, Text(input, FieldName(i, "action")) ?? DefaultAction).ToShortcut()
				: null;

			if (shortcut is null)
			{
				return Error(BuildShortcutsStep(settings), numberField, Strings.ConfigFlow.Errors.Shortcut());
			}

			shortcuts.Add(shortcut);
		}

		return Complete(settings with { Shortcuts = shortcuts });
	}

	private static ConfigFlowResult Complete(RouterConfig config) =>
		// Completing makes the host re-initialize the integration, which reads and applies these values.
		ConfigFlowResult.Complete(
			RouterConfig.Keys.EntryTitle,
			config.ToStrings().ToDictionary(pair => pair.Key, pair => ConfigFlowValue.Plain(pair.Value)));

	private static ConfigFlowResult Error(ConfigFlowStep step, string field, LocalizedText message) =>
		ConfigFlowResult.Error(step, message, new Dictionary<string, LocalizedText> { [field] = message });

	private static ConfigFlowStep BuildSettingsStep(RouterConfig config)
	{
		var inputs = MidiRouter.GetSelectableInputs(config);
		var deviceOptions = inputs
			.Select(name => new ActionParameterOption { Value = name, Label = LocalizedText.FromLiteral(name) })
			// Keep selected keyboards that are unplugged right now, so saving does not drop them.
			.Concat(config.InputDevices
				.Where(name => !inputs.Contains(name, StringComparer.Ordinal))
				.Select(name => new ActionParameterOption { Value = name, Label = Strings.ConfigFlow.InputDevices.NotConnected(name) }))
			.ToList();

		var supportsVirtual = OutputPort.SupportsVirtual();
		var outputOptions = new List<ActionParameterOption>();
		// Keep a saved virtual choice even where it stopped working, so saving does not drop it.
		if (supportsVirtual || config.UsesVirtualOutput)
		{
			outputOptions.Add(new ActionParameterOption { Value = RouterConfig.VirtualOutput, Label = Strings.ConfigFlow.OutputDevice.Virtual() });
		}

		var outputs = MidiRouter.GetOutputNames();
		outputOptions.AddRange(outputs
			.Where(name => !IsBuiltInSynth(name) || string.Equals(name, config.OutputDevice, StringComparison.Ordinal))
			.Order(StringComparer.OrdinalIgnoreCase)
			.Select(name => new ActionParameterOption { Value = name, Label = LocalizedText.FromLiteral(name) }));
		if (!config.UsesVirtualOutput && config.OutputDevice.Length > 0 && !outputs.Contains(config.OutputDevice, StringComparer.Ordinal))
		{
			outputOptions.Add(new ActionParameterOption { Value = config.OutputDevice, Label = Strings.ConfigFlow.InputDevices.NotConnected(config.OutputDevice) });
		}

		var devicesField = WithDefault(ActionParameter.MultiSelect(
			KeyboardsField,
			options: deviceOptions,
			label: Strings.ConfigFlow.InputDevices.Label(),
			description: Strings.ConfigFlow.InputDevices.Description()),
			// First setup: preselect the first keyboard, the usual case being exactly one.
			config.InputDevices.Count > 0 ? config.InputDevices.ToArray() : inputs.Take(1).ToArray());

		var virtualNameField = ActionParameter.Text(
			RouterConfig.Keys.VirtualPortName,
			label: Strings.ConfigFlow.VirtualPortName.Label(),
			description: Strings.ConfigFlow.VirtualPortName.Description(),
			placeholder: RouterConfig.DefaultVirtualPortName,
			defaultValue: config.VirtualPortName);

		return new ConfigFlowStep
		{
			StepId = SettingsStepId,
			Title = Strings.ConfigFlow.StepTitle(),
			Description = supportsVirtual
				? Strings.ConfigFlow.StepDescriptionVirtual()
				: Strings.ConfigFlow.StepDescriptionLoopback(),
			Fields =
			[
				devicesField,
				ActionParameter.Choice(
					RouterConfig.Keys.OutputDevice,
					options: outputOptions,
					label: Strings.ConfigFlow.OutputDevice.Label(),
					description: supportsVirtual
						? Strings.ConfigFlow.OutputDevice.DescriptionVirtual()
						: Strings.ConfigFlow.OutputDevice.DescriptionLoopback(),
					defaultValue: config.OutputDevice.Length > 0 ? config.OutputDevice : supportsVirtual ? RouterConfig.VirtualOutput : null,
					required: true),
				.. supportsVirtual || config.UsesVirtualOutput
					? [virtualNameField.OnlyWhen(RouterConfig.Keys.OutputDevice, RouterConfig.VirtualOutput)]
					: Array.Empty<ActionParameter>(),
				ActionParameter.Choice(
					RouterConfig.Keys.StartMode,
					options: [.. ModeController.Names.Select(mode => new ActionParameterOption { Value = mode, Label = LocalizedText.FromLiteral(mode) })],
					label: Strings.ConfigFlow.StartMode.Label(),
					description: Strings.ConfigFlow.StartMode.Description(),
					defaultValue: ModeController.Name(config.StartMode),
					required: true),
				ActionParameter.Choice(
					RouterConfig.Keys.ModeSound,
					options:
					[
						new ActionParameterOption { Value = ModeSound.Name(ModeSoundStyle.Off), Label = Strings.ConfigFlow.ModeSound.Off() },
						new ActionParameterOption { Value = ModeSound.Name(ModeSoundStyle.Beep), Label = Strings.ConfigFlow.ModeSound.Beep() },
						new ActionParameterOption { Value = ModeSound.Name(ModeSoundStyle.Announce), Label = Strings.ConfigFlow.ModeSound.Announce() },
					],
					label: Strings.ConfigFlow.ModeSound.Label(),
					description: Strings.ConfigFlow.ModeSound.Description(),
					defaultValue: ModeSound.Name(config.ModeSound),
					required: true),
			],
		};
	}

	private static ConfigFlowStep BuildShortcutsStep(RouterConfig config)
	{
		static ActionParameterOption Option(string value, LocalizedText label) => new() { Value = value, Label = label };

		var fields = new List<ActionParameter>();
		for (var i = 0; i < config.InputDevices.Count; i++)
		{
			var device = config.InputDevices[i];
			var current = config.Shortcuts.FirstOrDefault(shortcut => string.Equals(shortcut.Device, device, StringComparison.Ordinal));
			var stored = current is null ? null : RouterConfig.ShortcutDto.From(current);
			var typeField = FieldName(i, "type");
			var isNote = stored?.Type == RouterConfig.ShortcutDto.NoteType;
			var isControl = stored?.Type == RouterConfig.ShortcutDto.ControlChangeType;

			fields.Add(ActionParameter.Choice(
				typeField,
				options:
				[
					Option(TypeNone, Strings.ConfigFlow.Shortcuts.TypeNone()),
					Option(RouterConfig.ShortcutDto.NoteType, Strings.ConfigFlow.Shortcuts.TypeNote()),
					Option(RouterConfig.ShortcutDto.ControlChangeType, Strings.ConfigFlow.Shortcuts.TypeControlChange()),
				],
				label: LocalizedText.FromLiteral(device),
				description: Strings.ConfigFlow.Shortcuts.TypeDescription(),
				defaultValue: stored?.Type ?? TypeNone));
			fields.Add(ActionParameter.Choice(
					FieldName(i, "note"),
					options: RouterEvents.NoteOptions(),
					label: Strings.ConfigFlow.Shortcuts.Note(),
					description: Strings.ConfigFlow.Shortcuts.NoteDescription(),
					defaultValue: (isNote ? stored!.Number : DefaultNote).ToString(CultureInfo.InvariantCulture))
				.AsSearchable()
				.OnlyWhen(typeField, RouterConfig.ShortcutDto.NoteType));
			fields.Add(ActionParameter.Number(
					FieldName(i, "controller"),
					label: Strings.ConfigFlow.Shortcuts.Controller(),
					description: Strings.ConfigFlow.Shortcuts.ControllerDescription(),
					min: 0,
					max: 127,
					step: 1,
					defaultValue: isControl ? stored!.Number : HeldState.SoftPedal)
				.OnlyWhen(typeField, RouterConfig.ShortcutDto.ControlChangeType));
			fields.Add(ActionParameter.Choice(
					FieldName(i, "action"),
					options:
					[
						Option("toggle", Strings.ConfigFlow.Shortcuts.ActionToggle()),
						Option("hold", Strings.ConfigFlow.Shortcuts.ActionHold()),
						Option("play", Strings.ConfigFlow.Shortcuts.ActionPlay()),
						Option("control", Strings.ConfigFlow.Shortcuts.ActionControl()),
					],
					label: Strings.ConfigFlow.Shortcuts.Action(),
					defaultValue: stored?.Action ?? DefaultAction)
				.OnlyWhen(typeField, RouterConfig.ShortcutDto.NoteType, RouterConfig.ShortcutDto.ControlChangeType));
		}

		return new ConfigFlowStep
		{
			StepId = ShortcutsStepId,
			Title = Strings.ConfigFlow.Shortcuts.StepTitle(),
			Description = Strings.ConfigFlow.Shortcuts.StepDescription(),
			Fields = fields,
		};
	}

	// Fields are shown by the keyboard's position on the first page; a device name is not a safe (can be changed)).
	private static string FieldName(int keyboard, string field) =>
		string.Create(CultureInfo.InvariantCulture, $"shortcut-{keyboard}-{field}");

	private static bool IsBuiltInSynth(string name) =>
		name.StartsWith("Microsoft GS Wavetable", StringComparison.OrdinalIgnoreCase);

	private static ActionParameter WithDefault(ActionParameter parameter, object value)
	{
		typeof(ActionParameter).GetProperty(nameof(ActionParameter.DefaultValue))?.SetValue(parameter, value);
		return parameter;
	}

	private static string? Text(IReadOnlyDictionary<string, object?> input, string key) =>
		input.TryGetValue(key, out var value) ? Unwrap(value)?.ToString()?.Trim() : null;

	private static List<string> List(IReadOnlyDictionary<string, object?> input, string key)
	{
		if (!input.TryGetValue(key, out var value) || value is null)
		{
			return [];
		}

		IEnumerable<string?> items = value switch
		{
			JsonElement { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().Select(item => Unwrap(item)?.ToString()),
			JsonElement { ValueKind: JsonValueKind.String } text => RouterConfig.ParseList(text.GetString()),
			string text when text.TrimStart().StartsWith('[') => RouterConfig.ParseList(text),
			string text => text.Split(',', StringSplitOptions.TrimEntries),
			IEnumerable enumerable => enumerable.Cast<object?>().Select(item => Unwrap(item)?.ToString()),
			_ => [value.ToString()],
		};

		return [.. items.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!).Distinct(StringComparer.Ordinal)];
	}

	private static object? Unwrap(object? value) => value switch
	{
		JsonElement { ValueKind: JsonValueKind.String } text => text.GetString(),
		JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
		JsonElement element => element.GetRawText(),
		_ => value,
	};
}
