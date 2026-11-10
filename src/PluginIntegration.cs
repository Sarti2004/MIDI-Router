using System.Globalization;
using System.Text.Json;
using MacroDeck.Localization;
using MacroDeck.Plugin.Hosting.Integrations.HostApis;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using MacroDeck.Sdk.Events;
using MacroDeck.Sdk.Issues;
using MacroDeck.Sdk.FolderViews;
using MacroDeck.Sdk.Ui;
using MacroDeck.Sdk.Variables;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;
using MIDIRouter.Actions;
using MIDIRouter.Deck;
using MIDIRouter.Integration;
using MIDIRouter.Routing;
using Serilog;

namespace MIDIRouter;

internal sealed class PluginIntegration :
	IPluginIntegration,
	IEventProvider,
	IDynamicEventOptionsProvider,
	IVariableProvider,
	IUiConfigFlowProvider,
	IIntegrationIssueProvider,
	IFolderViewProvider,
	IUiProvider,
	IDisposable
{
	private const string EnabledStorageKey = "routing-enabled";

	private static readonly TimeSpan VariableFallbackRefresh = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan StateDebounce = TimeSpan.FromMilliseconds(30);

	private static readonly (string Name, string Id, VariableType Type, Func<MidiRouter, object?> Read)[] VariableSpecs =
	[
		("midi_router_mode", "mode", VariableType.Text, router => router.Modes.CurrentName),
		("midi_router_enabled", "enabled", VariableType.Boolean, router => router.Status.Enabled),
		("midi_router_status", "status", VariableType.Text, router => router.Status.Text),
		("midi_router_learned_device", "learned-device", VariableType.Text, router => router.LastLearned?.Device),
		("midi_router_learned_type", "learned-type", VariableType.Text, router => router.LastLearned is { } code
			? code.Kind == MidiKinds.ControlChange ? "cc" : "note"
			: null),
		("midi_router_learned_number", "learned-number", VariableType.Numeric, router => router.LastLearned?.Number),
		("midi_router_learned_channel", "learned-channel", VariableType.Numeric, router => router.LastLearned?.Channel),
	];

	private static readonly IReadOnlyCollection<string> VariableIds = [.. VariableSpecs.Select(spec => spec.Id)];

	private static readonly IReadOnlyList<VariableDefinition> RouterVariables =
		[.. VariableSpecs.Select(spec => VariableDefinition.Eager(spec.Name, spec.Type, refreshInterval: VariableFallbackRefresh) with { Id = spec.Id })];

	private readonly MidiRouter _router;
	private readonly PluginApi _pluginApi;
	private readonly ClaimSink _sink;
	private readonly IPluginCatalogNotifier _catalogNotifier;
	private readonly DeviceStore _device;
	private readonly DeviceActions _deviceActions;
	private readonly Lock _sliderRefreshLock = new();
	private readonly HashSet<string> _movedSliders = new(StringComparer.Ordinal);
	private readonly ILogger _logger;
	private readonly HashSet<string> _warnedOperators = new(StringComparer.Ordinal);
	private readonly Lock _refreshLock = new();

	private volatile IIntegrationContext? _context;
	private CancellationTokenSource? _lifetime;
	private bool _refreshScheduled;
	private volatile IReadOnlyList<VariableDefinition> _variables = RouterVariables;

	public PluginIntegration(MidiRouter router, IPluginCatalogNotifier catalogNotifier, ILogger logger)
	{
		_router = router;
		_catalogNotifier = catalogNotifier;
		_logger = logger.ForContext<PluginIntegration>();
		_pluginApi = new PluginApi(router, logger);
		_sink = new ClaimSink(this);
		_device = new DeviceStore(logger);

		Actions =
		[
			new SetModeAction(router),
			new RoutingSwitchAction(router, SaveEnabledAsync),
			new SendControlChangeAction(router),
			new SendNoteAction(router),
			new SendProgramChangeAction(router),
			new PanicAction(router),
			new LearnCodeAction(router, () => _context?.NotificationService),
			new ExportCubaseAction(router, _device, () => _context?.NotificationService),
		];
		_deviceActions = new DeviceActions(Actions, logger);

		_router.Modes.Changed += OnStateChanged;
		_router.StatusChanged += OnStatusChanged;
		_router.Learned += OnStateChanged;
		_device.Changed += OnDeviceChanged;
		_deviceActions.SliderMoved += OnSliderMoved;
	}

	public IReadOnlyList<IActionDefinition> Actions { get; }

	public string ProviderName => "MIDI Router";

	public IReadOnlyList<EventDefinition> EventDefinitions => RouterEvents.Definitions;

	// The router's own variables, then one writable variable per slider of the virtual device.
	public IReadOnlyList<VariableDefinition> Variables => _variables;

	public IReadOnlyList<VariableDefinition> DeclaredVariables => Variables;

	public bool SupportsCatalog => false;

	public bool SupportsSearch => false;

	public bool SupportsPush => false;

	public bool AllowsMultipleConfigurations => false;

	public bool RequiresConfiguration => false;

	public bool ServesConfigUiTree => true;

	public async Task InitializeAsync(IIntegrationContext context)
	{
		_lifetime?.Cancel();
		_lifetime = new CancellationTokenSource();
		_context = context;

		context.Events.BindingsChanged -= ApplyBindings;
		context.Events.BindingsChanged += ApplyBindings;

		var enabled = await context.Storage.ReadAsync(EnabledStorageKey).ConfigureAwait(false);
		await _device.LoadAsync(context.Storage).ConfigureAwait(false);
		_mixerArt ??= await RegisterMixerArtAsync(context).ConfigureAwait(false);
		var config = await ReadConfigAsync(context).ConfigureAwait(false);

		// Opening devices can take some time
		await Task.Run(() =>
		{
			_router.SetEnabled(!bool.TryParse(enabled.Content, out var on) || on);
			_router.Apply(config);
		}).ConfigureAwait(false);

		ApplyBindings();
		_router.Sink = _sink;

		try
		{
			await _pluginApi.RegisterAsync(context.Messages, _lifetime.Token).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "The plugin subscription API is unavailable.");
		}

		_logger.Information(
			"Initialized: {Inputs} keyboard(s) selected, output {Output}, mode {Mode}.",
			config.InputDevices.Count,
			config.OutputName.Length > 0 ? config.OutputName : "(none)",
			_router.Modes.CurrentName);
		_ = NotifyDeclarationsAsync(context);
		_ = RefreshHostState(issuesChanged: true);
	}

	public Task ShutdownAsync()
	{
		if (_context is { } context)
		{
			context.Events.BindingsChanged -= ApplyBindings;
		}

		_router.Sink = null;
		_lifetime?.Cancel();
		_context = null;
		return Task.CompletedTask;
	}

	public void Dispose()
	{
		_router.Modes.Changed -= OnStateChanged;
		_router.StatusChanged -= OnStatusChanged;
		_router.Learned -= OnStateChanged;
		_device.Changed -= OnDeviceChanged;
		_deviceActions.SliderMoved -= OnSliderMoved;
		_pluginApi.Dispose();
		_lifetime?.Dispose();
	}

	public IConfigFlow CreateConfigFlow() => new RouterConfigFlow(_router);

	private static FolderViewDescriptor MixerFolderView { get; } = new(
		"mixer",
		Strings.FolderViews.Mixer.Name(),
		Strings.FolderViews.Mixer.Description());

	private string? _qualifiedMixerViewId;
	private volatile MixerArt? _mixerArt;

	public IReadOnlyList<FolderViewDescriptor> GetFolderViews() => [MixerFolderView];

	public async Task InitializeAsync(IFolderViewProviderContext context, CancellationToken cancellationToken = default)
	{
		var registration = await context.RegisterFolderViewAsync(MixerFolderView, cancellationToken).ConfigureAwait(false);
		_qualifiedMixerViewId = registration.FolderViewId;
	}

	public IReadOnlyList<UiSurfaceDeclaration> Surfaces { get; } =
	[
		new UiSurfaceDeclaration { Kind = UiSurfaceKinds.Page, SessionMode = UiSessionModes.Exclusive },
		new UiSurfaceDeclaration { Kind = UiSurfaceKinds.Folder, SessionMode = UiSessionModes.Shared },
	];

	public IReadOnlyList<UiPageDeclaration> Pages { get; } =
		[new UiPageDeclaration { Id = DeviceEditorView.PageId, Title = Strings.Pages.VirtualDevice(), IconName = "sliders" }];

	public Task<IUiSession?> CreateSessionAsync(UiSessionRequest request, CancellationToken cancellationToken)
	{
		var surface = request.Surface;
		if (surface.Kind == UiSurfaceKinds.Page && Attribute(surface, UiPageSurfaceAttributes.PageId) == DeviceEditorView.PageId)
		{
			var editor = new DeviceEditorView(_device, _deviceActions, _router, _mixerArt);
			return Task.FromResult<IUiSession?>(new ViewSession(new UiView(surface, editor.Root()), editor.Dispose));
		}

		if (surface.Kind == UiSurfaceKinds.Folder
			&& Attribute(surface, UiFolderSurfaceAttributes.ViewId) is { } viewId
			&& (viewId == MixerFolderView.Id || viewId == _qualifiedMixerViewId))
		{
			var mixer = new MixerModel(_deviceActions, _mixerArt);
			mixer.Device.Set(_device.Device);
			void OnChanged() => mixer.Device.Set(_device.Device);
			void OnSlider(string variableId, int value)
			{
				if (mixer.Levels.TryGetValue(variableId[DeviceActions.SliderPrefix.Length..], out var level))
				{
					level.Set(value / 127.0);
				}
			}

			_device.Changed += OnChanged;
			_deviceActions.SliderMoved += OnSlider;
			return Task.FromResult<IUiSession?>(new ViewSession(new UiView(surface, MixerView.Folder(mixer)), () =>
			{
				_device.Changed -= OnChanged;
				_deviceActions.SliderMoved -= OnSlider;
			}));
		}

		return Task.FromResult<IUiSession?>(null);
	}

	private static string? Attribute(MacroDeck.Ui.Model.Surfaces.UiSurface surface, string name) =>
		surface.Attributes.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	public Task<DynamicOptionsResult> GetEventOptionsAsync(EventOptionsContext context, CancellationToken cancellationToken)
	{
		var learned = _router.LastLearned;
		IReadOnlyList<ActionParameterOption> options = context.ParameterName switch
		{
			RouterEvents.DeviceParameter => LastLearnedFirst(
				[.. _router.Config.InputDevices.Select(name => new ActionParameterOption { Value = name, Label = LocalizedText.FromLiteral(name) })],
				learned?.Device),
			RouterEvents.NoteParameter => LastLearnedFirst(
				RouterEvents.NoteOptions(),
				learned is { Kind: not MidiKinds.ControlChange } ? learned.Number.ToString(CultureInfo.InvariantCulture) : null),
			_ => [],
		};

		return Task.FromResult(new DynamicOptionsResult { Options = options });
	}

	private static IReadOnlyList<ActionParameterOption> LastLearnedFirst(IReadOnlyList<ActionParameterOption> options, string? learned)
	{
		if (options.FirstOrDefault(option => string.Equals(option.Value, learned, StringComparison.Ordinal)) is not { } match)
		{
			return options;
		}

		return
		[
			new ActionParameterOption { Value = match.Value, Label = Strings.Events.LastLearned(match.Label), Metadata = match.Metadata },
			.. options.Where(option => !ReferenceEquals(option, match)),
		];
	}

	public ValueTask<VariableReading> ReadAsync(string localId, CancellationToken cancellationToken = default)
	{
		if (DeviceActions.OwnsVariable(localId))
		{
			return ValueTask.FromResult(_deviceActions.Read(_device.Device, localId));
		}

		var spec = VariableSpecs.FirstOrDefault(spec => string.Equals(spec.Id, localId, StringComparison.Ordinal));
		return ValueTask.FromResult(spec.Read?.Invoke(_router) is { } value ? VariableReading.Of(value) : VariableReading.Unavailable);
	}

	public ValueTask<VariableWriteResult> SetValueAsync(string localId, object? value, CancellationToken cancellationToken = default) =>
		DeviceActions.OwnsVariable(localId)
			? _deviceActions.WriteAsync(_device.Device, localId, value, cancellationToken)
			: ValueTask.FromResult(VariableWriteResult.NotWritable());

	public Task<IReadOnlyList<IntegrationIssue>> GetIssuesAsync(CancellationToken cancellationToken = default)
	{
		IReadOnlyList<IntegrationIssue> issues =
		[
			.. _router.Status.Problems
				// Output setup problems are shown on the configuration page, not as notifications.
				.Where(problem => problem.Kind is not (ProblemKind.InputPartialMatch
					or ProblemKind.OutputNotConfigured or ProblemKind.VirtualOutputUnsupported or ProblemKind.OutputMissing))
				.Select(problem => new IntegrationIssue
				{
					Id = problem.Id,
					Title = IssueText.Title(problem),
					Description = IssueText.Description(problem),
					Severity = problem.Severity switch
					{
						ProblemSeverity.Error => IntegrationIssueSeverity.Error,
						ProblemSeverity.Warning => IntegrationIssueSeverity.Warning,
						_ => IntegrationIssueSeverity.Info,
					},
					ActionLabel = Strings.Issues.Configure(),
				}),
		];

		return Task.FromResult(issues);
	}

	public Task<IssueResolution> ResolveIssueAsync(string issueId, CancellationToken cancellationToken = default) =>
		Task.FromResult(IssueResolution.Ok(followUp: IssueResolutionFollowUp.StartConfigFlow));

	private static async Task<RouterConfig> ReadConfigAsync(IIntegrationContext context)
	{
		var entries = await context.Config.GetEntriesAsync().ConfigureAwait(false);
		var entry = entries.FirstOrDefault(entry => string.Equals(entry.Title, RouterConfig.Keys.EntryTitle, StringComparison.Ordinal))
			?? (entries.Count > 0 ? entries[0] : null);
		if (entry is null)
		{
			return RouterConfig.Default;
		}

		var values = new Dictionary<string, string?>(StringComparer.Ordinal);
		foreach (var key in RouterConfig.Keys.All)
		{
			values[key] = await context.Config.GetStringAsync(entry.Id, key).ConfigureAwait(false);
		}

		return RouterConfig.FromStrings(key => values.GetValueOrDefault(key));
	}

	private Task SaveEnabledAsync(bool enabled) =>
		_context?.Storage.WriteAsync(EnabledStorageKey, enabled.ToString(CultureInfo.InvariantCulture)) ?? Task.CompletedTask;

	private void ApplyBindings()
	{
		if (_context is not { } context)
		{
			return;
		}

		var subscriber = BindingReader.Read(context.Events.GetBindings(), op =>
		{
			lock (_warnedOperators)
			{
				if (_warnedOperators.Add(op))
				{
					_logger.Warning("Unrecognized operator '{Operator}'.", op);
				}
			}
		});

		_router.SetSubscriber(subscriber);
	}

	private void OnStateChanged() => ScheduleRefresh();

	private async Task<MixerArt?> RegisterMixerArtAsync(IIntegrationContext context)
	{
		try
		{
			async Task<MacroDeck.Ui.Model.Resources.UiResource> Register(string file, string mediaType) =>
				await context.UiResources.RegisterAsync(
					"mixer-" + Path.GetFileNameWithoutExtension(file),
					await File.ReadAllBytesAsync(Path.Combine(MixerArt.Folder, file)).ConfigureAwait(false),
					mediaType).ConfigureAwait(false);

			return new MixerArt(
				await Register("brushed-metal.jpg", "image/jpeg").ConfigureAwait(false),
				await Register("fader-cap-vertical.png", "image/png").ConfigureAwait(false),
				await Register("fader-cap-horizontal.png", "image/png").ConfigureAwait(false),
				await Register("fader-track-vertical.png", "image/png").ConfigureAwait(false),
				await Register("fader-track-horizontal.png", "image/png").ConfigureAwait(false));
		}
		catch (Exception ex) when (ex is UiResourceException or IOException or UnauthorizedAccessException)
		{
			_logger.Warning(ex, "Mixer images unavailable; the mixer draws plain shapes.");
			return null;
		}
	}

	private void OnDeviceChanged()
	{
		_variables = [.. RouterVariables, .. DeviceActions.Variables(_device.Device)];
		if (_context is { } context)
		{
			_ = NotifyDeclarationsAsync(context);
		}
	}

	private async Task NotifyDeclarationsAsync(IIntegrationContext context)
	{
		try
		{
			await context.Variables.NotifyDeclarationsChangedAsync().ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.Debug(ex, "The slider variables failed to update.");
		}
	}

	// A slider moved: have the host read its variable again, once per burst of moves.
	private void OnSliderMoved(string variableId, int value)
	{
		lock (_sliderRefreshLock)
		{
			if (!_movedSliders.Add(variableId) || _movedSliders.Count > 1)
			{
				return;
			}
		}

		_ = Task.Run(async () =>
		{
			await Task.Delay(StateDebounce).ConfigureAwait(false);
			string[] moved;
			lock (_sliderRefreshLock)
			{
				moved = [.. _movedSliders];
				_movedSliders.Clear();
			}

			if (_context is { } context)
			{
				try
				{
					await context.Variables.RequestDefinitionRefreshAsync(moved).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					_logger.Debug(ex, "The slider variables failed to refresh.");
				}
			}
		});
	}

	private void OnStatusChanged()
	{
		_catalogNotifier.CatalogChanged("issues", reason: "Router status changed");
		ScheduleRefresh();
	}

	private void ScheduleRefresh()
	{
		lock (_refreshLock)
		{
			if (_refreshScheduled)
			{
				return;
			}

			_refreshScheduled = true;
		}

		_ = Task.Run(async () =>
		{
			await Task.Delay(StateDebounce).ConfigureAwait(false);
			lock (_refreshLock)
			{
				_refreshScheduled = false;
			}

			await RefreshHostState(issuesChanged: false).ConfigureAwait(false);
		});
	}

	private async Task RefreshHostState(bool issuesChanged)
	{
		if (_context is not { } context)
		{
			return;
		}

		try
		{
			if (issuesChanged)
			{
				_catalogNotifier.CatalogChanged("issues", reason: "Router initialized");
			}

			try
			{
				await context.Variables.RequestDefinitionRefreshAsync(VariableIds).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				await context.Variables.RequestRefreshAsync().ConfigureAwait(false);
			}

			await context.Messages.PublishAsync(
				PluginApi.StateChangedTopic,
				JsonSerializer.SerializeToElement(RouterState.From(_router), PluginApi.Json)).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.Debug(ex, "The router state failed.");
		}
	}

	private sealed class ClaimSink(PluginIntegration owner) : IClaimSink
	{
		public async ValueTask DeliverAsync(ClaimedMessage message, IReadOnlyList<Subscriber> recipients)
		{
			if (owner._context is not { } context)
			{
				return;
			}

			JsonElement? delivered = null;
			HashSet<string>? topics = null;
			foreach (var recipient in recipients)
			{
				if (recipient.Topic is null)
				{
					context.Events.Publish(RouterEvents.EventId(message.Kind), RouterEvents.Payload(message));
					continue;
				}

				if (!(topics ??= new HashSet<string>(StringComparer.Ordinal)).Add(recipient.Topic))
				{
					continue;
				}

				delivered ??= JsonSerializer.SerializeToElement(
					new DeliveredMessage(
						message.Device,
						message.Kind switch
						{
							MidiKinds.NoteOn => "note-on",
							MidiKinds.NoteOff => "note-off",
							_ => "cc",
						},
						message.Channel,
						message.Number,
						message.Value),
					PluginApi.Json);

				try
				{
					await context.Messages.PublishAsync(recipient.Topic, delivered).ConfigureAwait(false);
				}
				catch (Exception)
				{
					// Rate-limited or disconnected
				}
			}
		}
	}
}
