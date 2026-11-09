using Serilog;

namespace MIDIRouter.Deck;

internal sealed record DeckFileError(string FileName, string Message);

internal sealed record DeckSnapshot(IReadOnlyList<DeckLayout> Decks, IReadOnlyList<DeckFileError> Errors)
{
	public static readonly DeckSnapshot Empty = new([], []);
}

/// <summary>Deck files in the decks folder, one virtual profile and one Cubase script each.</summary>
internal sealed class DeckStore : IDisposable
{
	private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(300);

	private const string ExampleFileName = "example.json";

	// Pads across the top and bottom, a long fader and a tall one. Notes and CCs on channel 16, away from the keyboard.
	private const string ExampleDeck = """
		{
		  // One file per deck. Edit and save: the deck updates in Macro Deck right away.
		  // Then run the "Export decks to Cubase" action and Reload Scripts in Cubase's MIDI Remote tab.
		  "name": "Example deck",
		  "columns": 5,
		  "rows": 3,
		  "channel": 16,
		  "controls": [
		    { "id": "pad1", "type": "pad", "label": "Pad 1", "x": 0, "y": 0, "note": 36 },
		    { "id": "pad2", "type": "pad", "label": "Pad 2", "x": 1, "y": 0, "note": 37 },
		    { "id": "pad3", "type": "pad", "label": "Pad 3", "x": 2, "y": 0, "note": 38, "color": "#2E7D32" },
		    { "id": "pad4", "type": "pad", "label": "Pad 4", "x": 3, "y": 0, "cc": 20, "color": "#B71C1C" },
		    { "id": "fader1", "type": "fader", "label": "Fader 1", "x": 0, "y": 1, "w": 4, "cc": 21 },
		    { "id": "fader2", "type": "fader", "label": "Fader 2", "x": 4, "y": 0, "h": 3, "cc": 22 },
		    { "id": "pad5", "type": "pad", "label": "Pad 5", "x": 0, "y": 2, "note": 40 },
		    { "id": "pad6", "type": "pad", "label": "Pad 6", "x": 1, "y": 2, "note": 41 },
		    { "id": "pad7", "type": "pad", "label": "Pad 7", "x": 2, "y": 2, "note": 42 },
		    { "id": "pad8", "type": "pad", "label": "Pad 8", "x": 3, "y": 2, "note": 43 }
		  ]
		}
		""";

	private readonly ILogger _logger;
	private readonly Lock _lock = new();
	private FileSystemWatcher? _watcher;
	private Timer? _reloadTimer;
	private volatile DeckSnapshot _snapshot = DeckSnapshot.Empty;

	public DeckStore(ILogger logger)
	{
		_logger = logger.ForContext<DeckStore>();
	}

	public event Action? Changed;

	// Not Documents on macOS: it is privacy-protected there, and the first access blocks until the user answers the
	// prompt, which the plugin's health check doesn't wait for.
	public static string Folder { get; } = OperatingSystem.IsMacOS()
		? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "MIDI Router", "Decks")
		: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MIDI Router", "Decks");

	public DeckSnapshot Snapshot => _snapshot;

	// Loads in the background and raises Changed when done, so a slow or blocked disk never holds up startup.
	public void Start()
	{
		lock (_lock)
		{
			if (_reloadTimer is not null)
			{
				return;
			}

			_reloadTimer = new Timer(_ => Reload(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
		}

		_ = Task.Run(Watch);
	}

	private void Watch()
	{
		FileSystemWatcher? watcher = null;
		try
		{
			if (!Directory.Exists(Folder))
			{
				Directory.CreateDirectory(Folder);
				File.WriteAllText(Path.Combine(Folder, ExampleFileName), ExampleDeck);
			}

			watcher = new FileSystemWatcher(Folder, "*.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
			watcher.Changed += OnFileEvent;
			watcher.Created += OnFileEvent;
			watcher.Deleted += OnFileEvent;
			watcher.Renamed += OnFileEvent;
			watcher.EnableRaisingEvents = true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_logger.Warning(ex, "The deck folder {Folder} is unavailable.", Folder);
		}

		lock (_lock)
		{
			// Disposed while the folder was being opened
			if (_reloadTimer is null)
			{
				watcher?.Dispose();
				return;
			}

			_watcher = watcher;
		}

		Reload();
	}

	public void Dispose()
	{
		lock (_lock)
		{
			_watcher?.Dispose();
			_watcher = null;
			_reloadTimer?.Dispose();
			_reloadTimer = null;
		}
	}

	// Editors save in bursts (truncate, write, rename), so wait for the folder to settle.
	private void OnFileEvent(object sender, FileSystemEventArgs e)
	{
		lock (_lock)
		{
			_reloadTimer?.Change(ReloadDelay, Timeout.InfiniteTimeSpan);
		}
	}

	private void Reload()
	{
		var decks = new List<DeckLayout>();
		var errors = new List<DeckFileError>();
		var ids = new HashSet<string>(StringComparer.Ordinal);
		var faderIds = new HashSet<string>(StringComparer.Ordinal);

		string[] files;
		try
		{
			files = Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.json") : [];
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_logger.Warning(ex, "The deck folder {Folder} could not be read.", Folder);
			files = [];
		}

		foreach (var path in files.Order(StringComparer.OrdinalIgnoreCase))
		{
			var fileName = Path.GetFileName(path);
			var id = DeckLayout.IdFromFileName(path);
			try
			{
				if (id.Length == 0 || !ids.Add(id))
				{
					throw new DeckLayoutException("Rename the file: its name is empty or clashes with another deck.");
				}

				var deck = DeckLayout.Parse(id, File.ReadAllText(path));
				var faders = deck.Controls.Where(control => control.Kind == DeckControlKind.Fader).Select(deck.FaderVariableId).ToList();
				if (faders.FirstOrDefault(faderIds.Contains) is { } clash)
				{
					throw new DeckLayoutException($"A fader of another deck already uses the variable id \"{clash}\": rename this file or the fader.");
				}

				faderIds.UnionWith(faders);
				decks.Add(deck);
			}
			catch (Exception ex) when (ex is DeckLayoutException or IOException or UnauthorizedAccessException)
			{
				errors.Add(new DeckFileError(fileName, ex.Message));
				_logger.Warning("Deck {File} skipped: {Error}", fileName, ex.Message);
			}
		}

		_snapshot = new DeckSnapshot(decks, errors);
		_logger.Information("Loaded {Decks} deck(s) from {Folder}.", decks.Count, Folder);
		Changed?.Invoke();
	}
}
