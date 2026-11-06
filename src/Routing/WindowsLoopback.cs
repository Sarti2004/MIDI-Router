using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MIDIRouter.Routing;


[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsLoopback : IDisposable
{
	private const string Description = "Created by MIDI Router";

	private const string RouterSidePrefix = "Router: ";

	// WinMM port names (MIDIOUTCAPS.szPname) hold 31 characters.
	private const int WinMmNameLength = 31;

	private const uint ClsctxAll = 0x17;
	private const uint CoinitMultithreaded = 0x0;

	private static readonly Guid ClsidMidiSrvTransport = new("2BA15E4E-5417-4A66-85B8-2B2260EFBC84");

	// IMidiTransport: 3 Activate. IMidiTransportConfigurationManager: 3 Initialize, 4 UpdateConfiguration, 5 Shutdown.
	private static readonly Guid IidMidiTransport = new("EA264200-3328-49E5-8815-73649A8748BE");
	private static readonly Guid IidConfigurationManager = new("F19DD642-1809-4497-9EEE-F230B11BD6FB");

	// MidiBasicLoopbackManager::TransportId and MidiLoopbackManager::TransportId.
	private static readonly Guid BasicTransport = new("10088473-9478-4E62-850B-3D2315E135B8");
	private static readonly Guid PairTransport = new("942BF02D-93C0-4EA8-B03E-D51156CA75E1");

	private readonly Guid _transport;
	private readonly Guid _associationId;
	private bool _seen;
	private bool _disposed;

	private WindowsLoopback(Guid transport, string name, Guid associationId)
	{
		_transport = transport;
		Name = name;
		PortName = transport == PairTransport ? RouterSidePrefix + name : name;
		_associationId = associationId;
	}

	// The port the target app opens.
	public string Name { get; }


	public string PortName { get; }

	// False on Windows without MIDI Services (class not registered) or with the service unreachable.
	public static bool IsAvailable()
	{
		try
		{
			WithCom(() =>
			{
				ShutDown(OpenConfigurationManager(BasicTransport));
				return true;
			});
			return true;
		}
		catch (ComException)
		{
			return false;
		}
	}

	// Null when MIDI Services can't make loopbacks. Throws InvalidOperationException when it can but creating this one failed.
	public static WindowsLoopback? TryCreate(string name, IReadOnlyCollection<string> present)
	{
		if (!IsAvailable())
		{
			return null;
		}

		// The association id is the loopback's handle for removal.
		var associationId = AssociationIdFor(name);

		// Left by a killed run, or one the user made.
		if (FindPort(RouterSidePrefix + name, present) is not null)
		{
			return new WindowsLoopback(PairTransport, name, associationId);
		}

		if (FindPort(name, present) is not null)
		{
			return new WindowsLoopback(BasicTransport, name, associationId);
		}

		var uniqueId = UniqueIdFor(name);
		try
		{
			// A failed call with no reply is how the service answers for a transport it hasn't loaded.
			var basic = new JsonObject { ["endpoint"] = Endpoint(name, uniqueId) };
			if (Send(BasicTransport, Create(associationId, basic)) is { } basicReply)
			{
				EnsureSucceeded(basicReply);
				return new WindowsLoopback(BasicTransport, name, associationId);
			}

			var pair = new JsonObject
			{
				["endpointA"] = Endpoint(RouterSidePrefix + name, uniqueId + "A"),
				["endpointB"] = Endpoint(name, uniqueId + "B"),
			};
			if (Send(PairTransport, Create(associationId, pair)) is { } pairReply)
			{
				EnsureSucceeded(pairReply);
				return new WindowsLoopback(PairTransport, name, associationId);
			}

			return null;
		}
		catch (ComException ex)
		{
			throw new InvalidOperationException(ex.Message, ex);
		}
	}

	public static string? FindPort(string name, IEnumerable<string> present)
	{
		var shortName = name.Length > WinMmNameLength ? name[..WinMmNameLength] : name;
		return present.FirstOrDefault(port => string.Equals(port, name, StringComparison.Ordinal))
			?? present.FirstOrDefault(port => string.Equals(port, shortName, StringComparison.Ordinal));
	}

	// The router's side of this loopback, once the service has added its MIDI 1.0 port.
	public string? FindPort(IEnumerable<string> present)
	{
		var port = FindPort(PortName, present);
		_seen |= port is not null;
		return port;
	}


	public bool IsGone(IEnumerable<string> present) => _seen && FindPort(PortName, present) is null;

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		try
		{
			var remove = new JsonObject { ["remove"] = new JsonArray(GuidString(_associationId)) };
			_ = Send(_transport, remove);
		}
		catch (ComException)
		{
			// The service is gone
		}
	}

	private static JsonObject Endpoint(string name, string uniqueId) => new()
	{
		["name"] = name,
		["description"] = Description,
		["uniqueIdentifier"] = uniqueId,
		["muted"] = false,
	};

	private static JsonObject Create(Guid associationId, JsonObject association) =>
		new() { ["create"] = new JsonObject { [GuidString(associationId)] = association } };

	// Errors carry "message" on every service build; "errorCode" is only on newer ones.
	private static void EnsureSucceeded(JsonObject reply)
	{
		if (Read<bool>(reply, "success"))
		{
			return;
		}

		var message = Read<string>(reply, "message");
		var code = Read<double>(reply, "errorCode");
		throw new InvalidOperationException(
			!string.IsNullOrEmpty(message) ? message
			: code != 0 ? $"Creating the loopback failed (error 0x{(int)code:X})."
			: "Creating the loopback failed.");
	}

	private static T? Read<T>(JsonObject reply, string key)
	{
		try
		{
			return reply[key] is JsonValue value && value.TryGetValue<T>(out var result) ? result : default;
		}
		catch (InvalidOperationException)
		{
			return default;
		}
	}

	// The reply, or null when the call failed without one.
	private static JsonObject? Send(Guid transportId, JsonObject transportSection)
	{
		var payload = new JsonObject
		{
			["endpointTransportPluginSettings"] = new JsonObject { [GuidString(transportId)] = transportSection },
		}.ToJsonString();

		return WithCom(() =>
		{
			var manager = OpenConfigurationManager(transportId);
			try
			{
				var (hr, text) = UpdateConfiguration(manager, payload);

				// The service also fails the call when it still sends the reason back, so the reply is read either way.
				if (string.IsNullOrEmpty(text))
				{
					return hr < 0 ? null : new JsonObject();
				}

				try
				{
					return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
				}
				catch (JsonException)
				{
					return new JsonObject();
				}
			}
			finally
			{
				ShutDown(manager);
			}
		});
	}

	private static (int Hr, string? Reply) UpdateConfiguration(void* manager, string payload)
	{
		char* reply = null;
		int hr;
		fixed (char* json = payload)
		{
			hr = ((delegate* unmanaged<void*, char*, char**, int>)Slot(manager, 4))(manager, json, &reply);
		}

		try
		{
			return (hr, reply is null ? null : new string(reply));
		}
		finally
		{
			// Allocated by the service with CoTaskMemAlloc
			Marshal.FreeCoTaskMem((nint)reply);
		}
	}

	private static void* OpenConfigurationManager(Guid transportId)
	{
		void* transport = null;
		var clsid = ClsidMidiSrvTransport;
		var iidTransport = IidMidiTransport;
		Check(CoCreateInstance(&clsid, null, ClsctxAll, &iidTransport, &transport), "CoCreateInstance");

		void* manager = null;
		try
		{
			var iidManager = IidConfigurationManager;
			Check(((delegate* unmanaged<void*, Guid*, void**, int>)Slot(transport, 3))(transport, &iidManager, &manager), "Activate");
		}
		finally
		{
			Release(transport);
		}

		try
		{
			// Connects to the service. The device and service configuration managers are only for transports inside it.
			Check(((delegate* unmanaged<void*, Guid, void*, void*, int>)Slot(manager, 3))(manager, transportId, null, null), "Initialize");
			return manager;
		}
		catch
		{
			Release(manager);
			throw;
		}
	}

	private static void ShutDown(void* manager)
	{
		_ = ((delegate* unmanaged<void*, int>)Slot(manager, 5))(manager);
		Release(manager);
	}

	// Threads the router calls from may not have joined an apartment yet. One that is already STA keeps it.
	private static T WithCom<T>(Func<T> action)
	{
		var hr = CoInitializeEx(null, CoinitMultithreaded);
		try
		{
			return action();
		}
		finally
		{
			if (hr >= 0)
			{
				CoUninitialize();
			}
		}
	}

	// Letters and digits only
	private static string UniqueIdFor(string name)
	{
		var hash = 2166136261;
		foreach (var c in name)
		{
			hash = (hash ^ c) * 16777619;
		}

		return $"MIDIRouter{hash:X8}";
	}

	private static Guid AssociationIdFor(string name) =>
		new(SHA256.HashData(Encoding.UTF8.GetBytes("MIDIRouter loopback " + name)).AsSpan(0, 16));

	// The service writes and reads GUIDs with StringFromCLSID: braces, upper case.
	private static string GuidString(Guid value) => value.ToString("B").ToUpperInvariant();

	private static void* Slot(void* obj, int index) => (*(void***)obj)[index];

	private static void Release(void* obj)
	{
		if (obj is not null)
		{
			((delegate* unmanaged<void*, uint>)Slot(obj, 2))(obj);
		}
	}

	private static void Check(int hr, string call)
	{
		if (hr < 0)
		{
			throw new ComException(hr, call);
		}
	}

	// A failed HRESULT: class not registered on Windows without MIDI Services, or the service unreachable.
	private sealed class ComException(int hr, string call) : Exception($"Windows MIDI Services {call} failed (0x{hr:X8}).")
	{
	}

	[LibraryImport("ole32.dll")]
	private static partial int CoCreateInstance(Guid* clsid, void* outer, uint context, Guid* iid, void** instance);

	[LibraryImport("ole32.dll")]
	private static partial int CoInitializeEx(void* reserved, uint coInit);

	[LibraryImport("ole32.dll")]
	private static partial void CoUninitialize();
}
