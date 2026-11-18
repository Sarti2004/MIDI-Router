using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MIDIRouter.Routing;

/// <summary>A channel message as it came in: status byte with the channel, and its data bytes (the second is 0 for program change).</summary>
internal readonly record struct ShortMessage(byte Status, byte Data1, byte Data2);

/// <summary>
/// A MIDI source and destination under one name, as a hardware device shows up: apps read what <see cref="Send"/> puts
/// out, and what they send to the destination arrives at the callback. DryWetMidi's virtual device is a loopback cable
/// instead, which would hand an app's own feedback straight back to it.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe partial class CoreMidiPorts : IDisposable
{
	private const string CoreMidi = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";
	private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

	// The read callback finds its ports by number: one arriving after Dispose finds nothing instead of a freed handle.
	private static readonly ConcurrentDictionary<nint, CoreMidiPorts> Open = new();
	private static long _nextId;

	private readonly nint _id;
	private readonly Action<ShortMessage> _received;
	private readonly Lock _parse = new();
	private uint _client;
	private uint _source;
	private uint _destination;
	private int _disposed;

	// Running status: the last channel status byte, and the first data byte while waiting for the second.
	private byte _status;
	private byte _first;
	private bool _hasFirst;
	private bool _inSysEx;

	private CoreMidiPorts(Action<ShortMessage> received)
	{
		_id = (nint)Interlocked.Increment(ref _nextId);
		_received = received;
	}

	/// <exception cref="InvalidOperationException">CoreMIDI refused to create a port.</exception>
	public static CoreMidiPorts Create(string name, Action<ShortMessage> received)
	{
		var ports = new CoreMidiPorts(received);
		Open[ports._id] = ports;
		try
		{
			var cfName = CreateString(name);
			try
			{
				uint client, source, destination;
				Check(MIDIClientCreate(cfName, 0, 0, &client), "MIDIClientCreate");
				ports._client = client;
				Check(MIDISourceCreate(client, cfName, &source), "MIDISourceCreate");
				ports._source = source;
				Check(MIDIDestinationCreate(client, cfName, (nint)(delegate* unmanaged<void*, void*, void*, void>)&Read, ports._id, &destination), "MIDIDestinationCreate");
				ports._destination = destination;
			}
			finally
			{
				CFRelease(cfName);
			}

			// The same ids every time, so apps that remember ports by id find the device again after it was off.
			var id = StableId(name);
			SetUniqueId(ports._source, id);
			SetUniqueId(ports._destination, id ^ 1);
			return ports;
		}
		catch
		{
			ports.Dispose();
			throw;
		}
	}

	/// <summary>Puts one message out of the source; dropped once disposed.</summary>
	public void Send(ReadOnlySpan<byte> message)
	{
		if (Volatile.Read(ref _disposed) != 0 || message.Length is 0 or > 3)
		{
			return;
		}

		// A MIDIPacketList holding one packet, packed to 4 bytes: count, then time stamp (0 = now), length and data.
		var list = stackalloc byte[24];
		*(uint*)list = 1;
		*(ulong*)(list + 4) = 0;
		*(ushort*)(list + 12) = (ushort)message.Length;
		message.CopyTo(new Span<byte>(list + 14, message.Length));
		_ = MIDIReceived(_source, list);
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}

		Open.TryRemove(_id, out _);
		if (_destination != 0)
		{
			_ = MIDIEndpointDispose(_destination);
		}

		if (_source != 0)
		{
			_ = MIDIEndpointDispose(_source);
		}

		if (_client != 0)
		{
			_ = MIDIClientDispose(_client);
		}
	}

	[UnmanagedCallersOnly]
	private static void Read(void* packetList, void* readRefCon, void* sourceRefCon)
	{
		try
		{
			if (Open.TryGetValue((nint)readRefCon, out var ports))
			{
				ports.Parse((byte*)packetList);
			}
		}
		catch (Exception)
		{
			// Nothing may unwind into CoreMIDI's thread.
		}
	}

	private void Parse(byte* list)
	{
		var count = *(uint*)list;
		var packet = list + 4;
		lock (_parse)
		{
			for (var i = 0; i < count; i++)
			{
				var length = *(ushort*)(packet + 8);
				var data = packet + 10;
				for (var b = 0; b < length; b++)
				{
					Take(data[b]);
				}

				// MIDIPacketNext: packets are 4-byte aligned on ARM.
				packet = data + length;
				if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
				{
					packet = (byte*)(((nuint)packet + 3) & ~(nuint)3);
				}
			}
		}
	}

	private void Take(byte value)
	{
		switch (value)
		{
			// Real-time bytes may come between any two others.
			case >= 0xF8:
				return;

			// SysEx and system common messages are not for the device; F7 ends a SysEx.
			case >= 0xF0:
				_inSysEx = value == 0xF0;
				_status = 0;
				_hasFirst = false;
				return;

			case >= 0x80:
				_inSysEx = false;
				_status = value;
				_hasFirst = false;
				return;
		}

		if (_inSysEx || _status == 0)
		{
			return;
		}

		// Program change and channel pressure have one data byte.
		if ((_status & 0xF0) is 0xC0 or 0xD0)
		{
			_received(new ShortMessage(_status, value, 0));
		}
		else if (!_hasFirst)
		{
			_first = value;
			_hasFirst = true;
		}
		else
		{
			_hasFirst = false;
			_received(new ShortMessage(_status, _first, value));
		}
	}

	private static nint CreateString(string text)
	{
		fixed (char* chars = text)
		{
			var result = CFStringCreateWithCharacters(0, chars, text.Length);
			return result != 0 ? result : throw new InvalidOperationException("CoreFoundation could not make the port name.");
		}
	}

	private static void SetUniqueId(uint endpoint, int id)
	{
		// Another endpoint may hold the id already; CoreMIDI's own random one is kept then.
		if (NativeLibrary.TryLoad(CoreMidi, out var library) && NativeLibrary.TryGetExport(library, "kMIDIPropertyUniqueID", out var key))
		{
			_ = MIDIObjectSetIntegerProperty(endpoint, *(nint*)key, id);
		}
	}

	private static int StableId(string name)
	{
		var hash = 2166136261;
		foreach (var c in "MIDIRouter device " + name)
		{
			hash = (hash ^ c) * 16777619;
		}

		// Never 0, which CoreMIDI reads as "no id".
		return (int)(hash | 0x10);
	}

	private static void Check(int status, string call)
	{
		if (status != 0)
		{
			throw new InvalidOperationException($"CoreMIDI {call} failed ({status}).");
		}
	}

	[LibraryImport(CoreMidi)]
	private static partial int MIDIClientCreate(nint name, nint notifyProc, nint notifyRefCon, uint* client);

	[LibraryImport(CoreMidi)]
	private static partial int MIDIClientDispose(uint client);

	[LibraryImport(CoreMidi)]
	private static partial int MIDISourceCreate(uint client, nint name, uint* source);

	// Deprecated for MIDIDestinationCreateWithProtocol, which takes a block; this one still delivers MIDI 1.0 packets.
	[LibraryImport(CoreMidi)]
	private static partial int MIDIDestinationCreate(uint client, nint name, nint readProc, nint refCon, uint* destination);

	[LibraryImport(CoreMidi)]
	private static partial int MIDIReceived(uint source, void* packetList);

	[LibraryImport(CoreMidi)]
	private static partial int MIDIEndpointDispose(uint endpoint);

	[LibraryImport(CoreMidi)]
	private static partial int MIDIObjectSetIntegerProperty(uint obj, nint propertyId, int value);

	[LibraryImport(CoreFoundation)]
	private static partial nint CFStringCreateWithCharacters(nint allocator, char* chars, nint length);

	[LibraryImport(CoreFoundation)]
	private static partial void CFRelease(nint cf);
}
