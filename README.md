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
- Export to Cubase

## Virtual Device (beta)

The Virtual Device is a mixer of buttons and faders on your Macro Deck that works like a hardware controller: switched on, it has its own MIDI input and output named after it, which your DAW opens as it would a USB controller. It can also be exported to Cubase / Nuendo as a MIDI Remote script with the same layout and MIDI, so Cubase shows it as a controller you can map in the Mapping Assistant.

**Build it** on the **Virtual Device** tab of MIDI Router's page in Macro Deck:

1. Choose an element: **Button**, **Small button** (half a cell, two per cell), **Vertical slider** or **Horizontal slider**.
2. Press an empty cell to place it, or press an element to select it. **Move** moves the selected element to the next cell you press.
3. Set the element's label, color and size, and its action: **Send MIDI note**, **Send MIDI CC** or **Send program change**, with that action's settings. A slider sends a CC, with its position as the value. Tick **Centered (pan)** on a slider to make it a pan control: it rests in the middle (64), snaps there when moved near it, reads L100 to C to R100, and returns to the middle on a double-press.

Every change is saved at once.

**Use it** on a deck: create a folder and set its view to **MIDI Router mixer**, and press the power key at its top right.

- **On** (green LED): the device's MIDI ports exist. Buttons work like hardware buttons: a note button sends its note on when pressed and its note off when let go (the note's duration setting isn't used), and a CC button sends its value when pressed and 0 when let go. Sliders send their CC as they move and take a touch from where they are, like a fader cap, rather than jumping to the finger. All of it goes out on the device's own output. What the DAW sends back to its input lights the button LEDs sending that note or CC (a program change lights the button for that program) and moves the sliders sending that CC, so the mixer shows the DAW's state. The **IN** and **OUT** LEDs on the nameplate flash with MIDI traffic.
- **Off** (red LED): the ports are gone and the controls are dark and don't respond.

The device stays on across Macro Deck restarts. Its ports are created by CoreMIDI on macOS and by [Windows MIDI Services](https://aka.ms/midi) on Windows, as a loopback pair: the DAW opens the port named after the device; the one starting with `Router:` is the plugin's side. Without Windows MIDI Services the device can't be switched on. Its name must differ from the router's own virtual port.

Each slider also has a variable, `midi_router_<element>` (0-127), so other widgets and automations can show its position, or set it to move the slider and send the CC while the device is on.

**Export it to Cubase**:

1. Press **Export to Cubase** on the Virtual Device tab, or run the **Export to Cubase** action. The script is written to `Documents/Steinberg/<Cubase or Nuendo>/MIDI Remote/Driver Scripts/Local/MIDIRouter/virtual-device/`. On macOS, Macro Deck asks for access to the Documents folder the first time.
2. Switch the device on. In Cubase, open the **MIDI Remote** tab and click **Reload Scripts**. Cubase detects the device by its ports and sends each control's value back to it, so its LEDs and faders follow Cubase.
3. Map the controls in the Mapping Assistant. Export again after changing the device.
4. For the device to follow Cubase, set two options on the mappings in the Mapping Assistant:
   - Faders: takeover mode **Jump**. The Mapping Assistant makes fader mappings **Scaled**, which suits faders that don't move by themselves, and then Cubase doesn't send their position back.
   - Buttons for on/off functions such as mute, solo or record: **Toggle**. Otherwise a press turns the function on only while the note is held.

Notes:
- Give every element its own note or CC: the tab warns when two send the same, as Cubase would see them as one control. Keeping the device on its own channel (new elements use 16) keeps it apart from your keyboard.
- Program changes aren't exported: a MIDI Remote script can't bind them.

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
