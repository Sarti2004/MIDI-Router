<a href="https://macro-deck.app/store/com.sarti2004.midi-router"><img alt="Open in the Macro Deck Store" src="https://assets.macro-deck.app/branding/Open%20in%20the%20Macro%20Deck%20Store%20Badge%20Dark.png" align="right" height="56"></a>

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

### 1. Create a MIDI output port (only on Windows without Windows MIDI Services)

On macOS, MIDI Router creates its own virtual MIDI port automatically.

On Windows with Windows MIDI Services, MIDI Router does the same: it creates a basic loopback port
while it runs and removes it when it stops.

On older Windows, create a loopback MIDI port once using either:

- [loopMIDI](https://www.tobias-erichsen.de/software/loopmidi.html)
- Windows MIDI Services (loopback endpoint)

### 2. Configure your music application (once)

In your music application, disable hardware MIDI keyboard.

**For Dorico:**

1. Open **Preferences → Play → MIDI Input Devices**.
2. Uncheck your hardware keyboard.

### 3. Configure the plugin

Open the plugin settings in Macro Deck:

- **Keyboards:** Select the MIDI keyboards and controllers you want to route.
- **Output port:** Select the router's virtual port, or your loopback port on Windows without Windows MIDI Services.
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

### Basic Actions
- Send MIDI note
- Send MIDI CC
- MIDI panic
- Learn MIDI code
- Export decks to Cubase

## Decks and Cubase MIDI Remote (beta)

A deck is a Macro Deck profile of pads and faders that send MIDI through the router. The plugin can also export it as a Cubase / Nuendo MIDI Remote script, with the same layout and bindings, so Cubase shows the deck as a controller you can map in the Mapping Assistant.

Each JSON file in the decks folder is one deck, and an example deck is created on first start. The folder is `~/Library/Application Support/MIDI Router/Decks` on macOS (in Finder: **Go → Go to Folder…**) and `Documents\MIDI Router\Decks` on Windows. Saving a file updates the deck in Macro Deck right away. Decks are read-only in the Macro Deck editor: change them in the file.

```jsonc
{
  "name": "Example deck",
  "columns": 5, "rows": 3,
  "channel": 16,              // default for all controls; keep it away from your keyboard's channel
  "controls": [
    { "id": "pad1", "type": "pad", "label": "Pad 1", "x": 0, "y": 0, "note": 36 },
    { "id": "pad4", "type": "pad", "label": "Pad 4", "x": 3, "y": 0, "cc": 20, "color": "#B71C1C" },
    { "id": "fader1", "type": "fader", "label": "Fader 1", "x": 0, "y": 1, "w": 4, "cc": 21 },
    { "id": "fader2", "type": "fader", "label": "Fader 2", "x": 4, "y": 0, "h": 3, "cc": 22 }
  ]
}
```

| Setting | Description |
|---|---|
| `type` | `pad` or `fader` |
| `x`, `y`, `w`, `h` | Grid cell and size (`w` and `h` default to 1) |
| `note` / `cc` | What a pad sends: a note (on, then off) or a CC (127, then 0). A fader always sends a CC (0-127) |
| `channel` | 1-16, overrides the deck's channel |
| `color`, `label` | Pad color and the label shown in Macro Deck and Cubase |
| `orientation` | Fader `horizontal` or `vertical`; by default the longer side |

To use a deck in Cubase:

1. Run the **Export decks to Cubase** action. It writes a script per deck to `Documents/Steinberg/<Cubase or Nuendo>/MIDI Remote/Driver Scripts/Local/MIDIRouter/`.
   On macOS, Macro Deck asks for access to the Documents folder the first time.
2. In Cubase, open the **MIDI Remote** tab and click **Reload Scripts**. The deck is detected on the router's output port.
3. Map the controls in the Mapping Assistant. Export again after changing the deck.

Pads send a short press only: a pad can't be held.

## Subscription API for other plugins

See [API documentation](API.md).

## Known Limitations

- **MIDI Router must be running to forward MIDI.** If your music app receives MIDI from the router instead of the hardware keyboard, the keyboard will be silent when Macro Deck or MIDI Router is not running. Re-enable the hardware keyboard in your music application when using it without the router.

- **Older Windows requires a loopback MIDI port.** MIDI Router creates its own port only through Windows MIDI Services. Without it, create one using [loopMIDI](https://www.tobias-erichsen.de/software/loopmidi.html).

- **Some Windows MIDI devices are single-client.** When MIDI Router has a keyboard open, another application may be unable to access it directly, and vice versa. MIDI Router reports this as *in use by another app* and automatically retries the connection.

## Credits

- [DryWetMIDI](https://github.com/melanchall/drywetmidi) — MIDI communication library (MIT).

## License

MIT License. See [LICENSE](LICENSE).
