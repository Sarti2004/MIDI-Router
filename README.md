# MIDI Router

A Macro Deck 3 plugin that routes input from MIDI keyboards and controllers to music apps (Dorico, DAWs, etc.), while allowing Macro Deck triggers to intercept specific MIDI messages.

**Only subscribed keys and controllers are intercepted in Control mode. Everything else passes through unchanged, with almost no added latency.** With no subscriptions, MIDI Router works as a simple MIDI Thru.

Built mainly for [DoriDeck](https://github.com/Sarti2004/DoriDeck), but can be used as a standalone MD controller and any other MD plugin can subscribe.

```
keyboard ──► MIDI Router ──► virtual / loopback port ──► App(Dorico, etc.)
                  │
                  └── subscribed events ──► Macro Deck triggers, other plugins
```
## Setup

### 1. Create a MIDI output port (Windows only)

On macOS, MIDI Router creates its own virtual MIDI port automatically.

On Windows, create a loopback MIDI port using either:

- [loopMIDI](https://www.tobias-erichsen.de/software/loopmidi.html)
- Windows MIDI Services (loopback endpoint)

This only needs to be done once.

### 2. Configure your music application (once)

In your music application, disable hardware MIDI keyboard.

**For Dorico:**

1. Open **Preferences → Play → MIDI Input Devices**.
2. Uncheck your hardware keyboard.

### 3. Configure the plugin

Open the plugin settings in Macro Deck:

- **Keyboards:** Select the MIDI keyboards and controllers you want to route.
- **Output port:** Select the virtual port (macOS) or loopback port (Windows).
- **Mode shortcuts (optional):** Configure keyboard keys to switch between Play and Control modes. See [Modes](#modes).

## Using MIDI Router with Macro Deck

### MIDI triggers (events)

Assign MD actions to specific MIDI notes or controllers (CCs). Use it with action buttons or automations.

In the Macro Deck **Events** block, select:

| Setting | Value |
|---|---|
| Provider | MIDI Router |
| Event | MIDI key pressed, MIDI key released, or MIDI controller (CC) |
| MIDI device | The keyboard or controller to listen to |
| Key / Controller | The MIDI note or CC number |

Attach the MD actions you want to execute when the event occurs.

**Note:** Only subscribed events are intercepted, and only in Control mode.

### Modes

The Router has two modes:

**Play (default)** — Use your keyboard normally. MIDI passes through to the app without triggering actions. "Switch the mode" shortcuts are the only exception.

**Control** — Subscribed notes and CCs trigger their assigned actions instead of reaching the app. Everything else still goes to the app.

#### Switching modes

**From Macro Deck**

Use the **MIDI Router mode** action and select *Play*, *Control* or *Toggle* (switches between the two on each press).

The button lights up when its assigned mode is active; a *Toggle* button lights up in Control mode.

**From a keyboard**

Open the **Mode shortcuts (optional)** page in the plugin settings.

Each selected keyboard can have its own shortcut, allowing different keyboards to use different keys or controllers.

Choose a shortcut type:

- **None:** No shortcut.
- **A key:** Use a MIDI note (default: A0).
- **A pedal or controller (CC):** Use a controller number (default: CC 67, soft pedal). 

Then choose its behavior:

| Behavior | Description |
|---|---|
| Toggle Play / Control | Switch between modes on each press |
| Control while held | Stay in Control mode while the key or pedal is held |
| Switch to Play | Activate Play mode |
| Switch to Control | Activate Control mode |

The router handles shortcut keys itself, in both modes. They never reach the app, Macro Deck or other plugins.

### Turning MIDI routing on or off

The **MIDI routing on/off** action releases the keyboards (closes them) so another app can use them directly.

This is particularly useful on Windows, where many MIDI drivers allow only one application to access a device at a time.

## Subscription API for other plugins

See [API documentation](API.md).

## Known Limitations

- **MIDI Router must be running to forward MIDI.** If your music app receives MIDI from the router instead of the hardware keyboard, the keyboard will be silent when Macro Deck or MIDI Router is not running. Re-enable the hardware keyboard in your music application when using it without the router.

- **Windows requires a loopback MIDI port.** MIDI Router cannot create its own virtual MIDI port on Windows. Create one using [loopMIDI](https://www.tobias-erichsen.de/software/loopmidi.html) or Windows MIDI Services.

- **Some Windows MIDI devices are single-client.** When MIDI Router has a keyboard open, another application may be unable to access it directly, and vice versa. MIDI Router reports this as *in use by another app* and automatically retries the connection.

## Credits

- [DryWetMIDI](https://github.com/melanchall/drywetmidi) — MIDI communication library (MIT).

## License

MIT License. See [LICENSE](LICENSE).