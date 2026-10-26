Other plugins subscribe through Macro Deck's **plugin message channel** (`IIntegrationContext.Messages`,
manifest permission `host:messaging`). This is MD3's cross-plugin mechanism: Macro Deck brokers every
message, stamps the sender's plugin id, and needs no reference between the plugins. 


| Topic | Kind | Payload → response |
|---|---|---|
| `midi-router.subscribe` | request | `SubscribeRequest` → `SubscribeResponse` |
| `midi-router.unsubscribe` | command | `{ "key": "…" }` |
| `midi-router.state` | request | – → `RouterState` |
| `midi-router.set-mode` | request | `{ "mode": "Control" }` (`Play` or `Control`) → `{ "outcome", "state" }` |
| `midi-router.state-changed` | event (published by the router) | `RouterState`, on mode or status changes |
| *your topic* | event (published by the router) | one `DeliveredMessage` per claimed message |

```jsonc
// SubscribeRequest - replaces everything this plugin subscribed under the same key
{
  "topic": "dorideck.midi",          // where the router publishes your messages; you subscribe to it
  "key": "default",                  // optional: several independent subscription sets per plugin
  "leaseSeconds": 60,                // optional, 10-600: renew before it runs out
  "subscriptions": [
    { "type": "note", "from": 36, "to": 59 },               // note | note-on | note-off | cc
    { "type": "cc", "number": 64, "device": "Roland A-49" } // number | numbers | from/to; omit = all
  ]
}
// SubscribeResponse
{ "rules": 2, "leaseSeconds": 60, "error": null, "state": { /* RouterState */ } }

// RouterState
{ "mode": "Play", "enabled": true, "status": "OK", "devices": ["Roland A-49"] }

// DeliveredMessage (channel is 1-16)
{ "device": "Roland A-49", "type": "note-on", "channel": 1, "number": 60, "value": 100 }
```

The same rules apply as for triggers: keys are claimed only in **Control** mode, a key is claimed as a
whole, releases follow their press, and mode shortcut keys are never delivered. An empty `subscriptions` list unsubscribes. **Subscriptions are
leases.** Renew them by sending the same request again, e.g. every `leaseSeconds / 3`, so a plugin
that crashes or is uninstalled can't keep keys silenced. Delivery follows the channel's rules: at
most once, in order, about 50 messages per second (controller values are coalesced to stay within
that).

```csharp
// In InitializeAsync. Registrations through context.Messages end with the initialization.
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
await context.Messages.SubscribeAsync<JsonElement>("dorideck.midi", (message, _, _) =>
{
    var note = message.GetProperty("number").GetInt32();
    // …react to the key…
    return Task.CompletedTask;
}, json);

var request = new
{
    topic = "dorideck.midi",
    subscriptions = new[] { new { type = "note", from = 36, to = 59 } },
};
await context.Messages.RequestAsync<object, JsonElement>("midi-router.subscribe", request, json);
// …and send the same request again every ~20 s (a timer) to keep the lease.
```
