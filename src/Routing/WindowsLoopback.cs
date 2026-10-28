using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MIDIRouter.Routing;

// A Windows MIDI Services basic loopback: one MIDI 1.0 port its output comes back as its input, so target app sees it the way it sees a loopMIDI port. 
// IIDs are from src/in-box/Inc/midi_sdk_idl_defs.h in github.com/microsoft/MIDI, vtable slots from the .idl files next to the classes in
// src/in-box/Client/WinRT/core: slot 6 is the first member after IInspectable, then declaration order.
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsLoopback : IDisposable
{
	private const string ApiClass = "Windows.Devices.Midi2.MidiApi";
	private const string Namespace = "Windows.Devices.Midi2.Transports.BasicLoopback.";
	private const string ManagerClass = Namespace + "MidiBasicLoopbackManager";
	private const string DefinitionClass = Namespace + "MidiBasicLoopbackEndpointDefinition";
	private const string CreationConfigClass = Namespace + "MidiBasicLoopbackCreationConfig";
	private const string RemovalConfigClass = Namespace + "MidiBasicLoopbackRemovalConfig";

	private const string Description = "Created by MIDI Router";

	// MidiBasicLoopbackErrorCode, basic_loopback_transport_error_codes.h.
	private const int DuplicateUniqueId = 0x141;
	private const int DuplicateEndpointName = 0x144;

	// WinMM port names (MIDIOUTCAPS.szPname) hold 31 characters.
	private const int WinMmNameLength = 31;

	private static readonly Guid IidApiStatics = new("8087b303-0519-c0de-31d1-ee0010000000");
	private static readonly Guid IidManagerStatics = new("8087b303-0519-c0de-31d1-ee00f0101000");
	private static readonly Guid IidCreationConfigFactory = new("8087b303-0519-c0de-31d1-ff00f0102000");
	private static readonly Guid IidRemovalConfigFactory = new("8087b303-0519-c0de-31d1-ff00f0103000");
	private static readonly Guid IidDefinitionFactory = new("8087b303-0519-c0de-31d1-ff00f0108000");

	private readonly string _uniqueId;
	private Guid? _created;

	private WindowsLoopback(string name, string uniqueId, Guid? created)
	{
		Name = name;
		_uniqueId = uniqueId;
		_created = created;
	}

	public string Name { get; }

	// False on Windows without MIDI Services (class not registered) or with the transport turned off.
	public static bool IsAvailable()
	{
		try
		{
			return EnsureServiceAvailable() && IsTransportAvailable();
		}
		catch (AbiException)
		{
			return false;
		}
	}

	// Null when MIDI Services can't make loopbacks here. Throws InvalidOperationException when it can
	// but creating this one failed.
	public static WindowsLoopback? TryCreate(string name)
	{
		if (!IsAvailable())
		{
			return null;
		}

		var uniqueId = UniqueIdFor(name);
		void* definition = null, config = null, response = null, entry = null;
		try
		{
			// Left by a run that was killed before removing it; it lasts until the service restarts.
			if (DoesLoopbackExist(uniqueId))
			{
				return new WindowsLoopback(name, uniqueId, null);
			}

			definition = NewDefinition(name, Description, uniqueId);
			config = NewCreationConfig(definition);
			response = CreateTransientLoopback(config);

			if (!GetBool(response, 6))
			{
				var code = GetInt32(response, 7);
				if (code is DuplicateEndpointName or DuplicateUniqueId)
				{
					// The user already has a loopback with this name: use it.
					return new WindowsLoopback(name, uniqueId, null);
				}

				var message = GetString(response, 8);
				throw new InvalidOperationException(message.Length > 0 ? message : $"Creating the loopback failed (error 0x{code:X}).");
			}

			entry = GetObject(response, 9);
			return new WindowsLoopback(name, uniqueId, GetGuid(entry, 6));
		}
		catch (AbiException ex)
		{
			throw new InvalidOperationException(ex.Message, ex);
		}
		finally
		{
			Release(entry);
			Release(response);
			Release(config);
			Release(definition);
		}
	}

	public static string? FindPort(string name, IEnumerable<string> present)
	{
		var shortName = name.Length > WinMmNameLength ? name[..WinMmNameLength] : name;
		return present.FirstOrDefault(port => string.Equals(port, name, StringComparison.Ordinal))
			?? present.FirstOrDefault(port => string.Equals(port, shortName, StringComparison.Ordinal));
	}

	// False once the service restarted or the user removed it.
	public bool Exists()
	{
		try
		{
			return DoesLoopbackExist(_uniqueId);
		}
		catch (AbiException)
		{
			return false;
		}
	}

	public void Dispose()
	{
		if (_created is not { } associationId)
		{
			return;
		}

		_created = null;
		void* config = null, response = null;
		try
		{
			var factory = GetFactory(RemovalConfigClass, IidRemovalConfigFactory);
			try
			{
				void* created;
				Check(((delegate* unmanaged<void*, Guid, void**, int>)Slot(factory, 6))(factory, associationId, &created));
				config = created;
			}
			finally
			{
				Release(factory);
			}

			response = CallManager(9, config);
		}
		catch (AbiException)
		{
			// The service is gone 
		}
		finally
		{
			Release(response);
			Release(config);
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

	private static bool EnsureServiceAvailable()
	{
		var statics = GetFactory(ApiClass, IidApiStatics);
		try
		{
			return GetBool(statics, 6);
		}
		finally
		{
			Release(statics);
		}
	}

	private static bool IsTransportAvailable()
	{
		var statics = GetFactory(ManagerClass, IidManagerStatics);
		try
		{
			return GetBool(statics, 6);
		}
		finally
		{
			Release(statics);
		}
	}

	private static bool DoesLoopbackExist(string uniqueId)
	{
		var statics = GetFactory(ManagerClass, IidManagerStatics);
		var id = CreateHString(uniqueId);
		try
		{
			byte exists;
			Check(((delegate* unmanaged<void*, nint, byte*, int>)Slot(statics, 11))(statics, id, &exists));
			return exists != 0;
		}
		finally
		{
			DeleteHString(id);
			Release(statics);
		}
	}

	private static void* CreateTransientLoopback(void* config) => CallManager(8, config);

	// CreateTransientLoopback (8) and RemoveTransientLoopback (9): one config in, one response out.
	private static void* CallManager(int slot, void* config)
	{
		var statics = GetFactory(ManagerClass, IidManagerStatics);
		try
		{
			void* response;
			Check(((delegate* unmanaged<void*, void*, void**, int>)Slot(statics, slot))(statics, config, &response));
			return response;
		}
		finally
		{
			Release(statics);
		}
	}

	// IMidiBasicLoopbackEndpointDefinitionFactory: 6 (name), 7 (name, description), 8 (name, description, uniqueId).
	private static void* NewDefinition(string name, string description, string uniqueId)
	{
		var factory = GetFactory(DefinitionClass, IidDefinitionFactory);
		var nameString = CreateHString(name);
		var descriptionString = CreateHString(description);
		var idString = CreateHString(uniqueId);
		try
		{
			void* definition;
			Check(((delegate* unmanaged<void*, nint, nint, nint, void**, int>)Slot(factory, 8))(factory, nameString, descriptionString, idString, &definition));
			return definition;
		}
		finally
		{
			DeleteHString(idString);
			DeleteHString(descriptionString);
			DeleteHString(nameString);
			Release(factory);
		}
	}

	private static void* NewCreationConfig(void* definition)
	{
		var factory = GetFactory(CreationConfigClass, IidCreationConfigFactory);
		try
		{
			void* config;
			Check(((delegate* unmanaged<void*, void*, void**, int>)Slot(factory, 6))(factory, definition, &config));
			return config;
		}
		finally
		{
			Release(factory);
		}
	}

	private static void* GetFactory(string className, Guid iid)
	{
		var name = CreateHString(className);
		try
		{
			void* factory;
			Check(RoGetActivationFactory(name, &iid, &factory));
			return factory;
		}
		finally
		{
			DeleteHString(name);
		}
	}

	private static bool GetBool(void* obj, int slot)
	{
		byte value;
		Check(((delegate* unmanaged<void*, byte*, int>)Slot(obj, slot))(obj, &value));
		return value != 0;
	}

	private static int GetInt32(void* obj, int slot)
	{
		int value;
		Check(((delegate* unmanaged<void*, int*, int>)Slot(obj, slot))(obj, &value));
		return value;
	}

	private static Guid GetGuid(void* obj, int slot)
	{
		Guid value;
		Check(((delegate* unmanaged<void*, Guid*, int>)Slot(obj, slot))(obj, &value));
		return value;
	}

	private static void* GetObject(void* obj, int slot)
	{
		void* value;
		Check(((delegate* unmanaged<void*, void**, int>)Slot(obj, slot))(obj, &value));
		return value;
	}

	private static string GetString(void* obj, int slot)
	{
		nint value;
		Check(((delegate* unmanaged<void*, nint*, int>)Slot(obj, slot))(obj, &value));
		try
		{
			uint length;
			var chars = WindowsGetStringRawBuffer(value, &length);
			return chars is null ? string.Empty : new string(chars, 0, (int)length);
		}
		finally
		{
			DeleteHString(value);
		}
	}

	private static void* Slot(void* obj, int index) => (*(void***)obj)[index];

	private static void Release(void* obj)
	{
		if (obj is not null)
		{
			((delegate* unmanaged<void*, uint>)Slot(obj, 2))(obj);
		}
	}

	private static nint CreateHString(string value)
	{
		fixed (char* chars = value)
		{
			nint hstring;
			Check(WindowsCreateString(chars, (uint)value.Length, &hstring));
			return hstring;
		}
	}

	// Only fails for a null or invalid handle, and there is nothing to do about either.
	private static void DeleteHString(nint hstring) => _ = WindowsDeleteString(hstring);

	private static void Check(int hr)
	{
		if (hr < 0)
		{
			throw new AbiException(hr);
		}
	}

	// A failed HRESULT: class not registered on Windows without MIDI Services, or a call that failed.
	private sealed class AbiException(int hr) : Exception($"Windows MIDI Services call failed (0x{hr:X8}).")
	{
	}

	[LibraryImport("combase.dll")]
	private static partial int RoGetActivationFactory(nint activatableClassId, Guid* iid, void** factory);

	[LibraryImport("combase.dll")]
	private static partial int WindowsCreateString(char* sourceString, uint length, nint* hstring);

	[LibraryImport("combase.dll")]
	private static partial int WindowsDeleteString(nint hstring);

	[LibraryImport("combase.dll")]
	private static partial char* WindowsGetStringRawBuffer(nint hstring, uint* length);
}
