using MacroDeck.Sdk.Storage;
using Serilog;

namespace MIDIRouter.Deck;

internal sealed class DeviceStore(ILogger logger)
{
	private const string StorageKey = "VirtualDevice.json";

	private readonly ILogger _logger = logger.ForContext<DeviceStore>();
	private readonly Lock _sync = new();
	private Task _writes = Task.CompletedTask;
	private IIntegrationStorage? _storage;
	private volatile VirtualDevice _device = VirtualDevice.Default;

	public event Action? Changed;

	public VirtualDevice Device => _device;

	public async Task LoadAsync(IIntegrationStorage storage, CancellationToken cancellationToken = default)
	{
		_storage = storage;
		VirtualDevice? device = null;
		try
		{
			var result = await storage.ReadAsync(StorageKey, cancellationToken: cancellationToken).ConfigureAwait(false);
			if (result.Status == IntegrationStorageReadStatus.Found && result.Content is { } content)
			{
				device = VirtualDevice.FromJson(content);
				if (device is null)
				{
					_logger.Warning("The config is unreadable; starting new one.");
				}
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_logger.Warning(ex, "could not read config");
		}

		_device = device ?? VirtualDevice.Default;
		_logger.Information("Virtual device: {Elements} element(s) on {Columns} × {Rows}.", _device.Elements.Count, _device.Columns, _device.Rows);
		Changed?.Invoke();
	}

	public void Update(Func<VirtualDevice, VirtualDevice> change)
	{
		lock (_sync)
		{
			var changed = change(_device);
			if (ReferenceEquals(changed, _device))
			{
				return;
			}

			_device = changed;
			if (_storage is { } storage)
			{
				var json = changed.ToJson();
				_writes = _writes.ContinueWith(_ => WriteAsync(storage, json), TaskScheduler.Default).Unwrap();
			}
		}

		Changed?.Invoke();
	}

	private async Task WriteAsync(IIntegrationStorage storage, string json)
	{
		try
		{
			await storage.WriteAsync(StorageKey, json).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "The virtual device could not be saved.");
		}
	}
}
