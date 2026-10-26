namespace MIDIRouter.Routing;


internal static class DeviceCloser
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

	public static void Dispose(IDisposable? device, Action? before = null)
	{
		if (device is null)
		{
			return;
		}

		var close = Task.Run(() =>
		{
			try
			{
				before?.Invoke();
			}
			catch (Exception)
			{
				// Stopping a device that is already gone fails; disposing below is all that is left.
			}

			try
			{
				device.Dispose();
			}
			catch (Exception)
			{
				// Same: the handle is unusable anyway.
			}
		});

		close.Wait(Timeout);
	}
}
