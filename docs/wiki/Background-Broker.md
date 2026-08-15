# Background broker

When Unison is suspended or the screen is off, WhatsApp traffic still has to arrive. Windows offers `SocketActivityTrigger`: the app **transfers ownership** of a connected `StreamSocket` to an out-of-process task. That task is `Unison.Background`.

The **session/protocol stack** now lives in `Unison.Socket`. The **raw TCP socket** does not. Framing, Noise checkpoint, journal, and toasts remain in the broker + UWP transport. Handing that socket to the new stack is the next migration step.

## Project constraints

- Output: `winmdobj` (UWP component)
- References **only** `Unison.Baileys` — no Core, no Socket
- Entry: `Unison.Background.WhatsAppSocketActivityTask` (declared in the UWP `Package.appxmanifest`)
- Triggered for `SocketActivity`, `KeepAliveTimerExpired` (WebSocket ping), `SocketClosed`

## What the task does

```
StreamSocket (already connected)
    → RawWebSocketConnection     RFC 6455 framing
    → BackgroundNoiseDecoder     receive-only AES-GCM (session already established)
    → BrokerFrameJournal         ordered at-least-once (UBJ2 / UBD3)
    → BrokerNoiseSessionStore    atomic Noise checkpoint JSON
    → preview + toast            real contact name when possible
```

It does **not** run a handshake, does **not** send application IQs, and does **not** persist Signal ratchet advances. Preview decrypts on a **clone** of the Signal snapshot so the foreground remains the source of truth.

### Reliability pieces

| Piece | File | Role |
|---|---|---|
| Task host | `WhatsAppSocketActivityTask.cs` | Decode → journal → toast |
| Framing | `Broker/RawWebSocketConnection.cs` | Shared with UWP `StreamSocketWebSocketTransport` |
| Noise receive | `Broker/BackgroundNoiseDecoder.cs` | Established session only |
| Checkpoint | `Broker/BrokerNoiseSessionStore.cs` | `socket-broker-noise-state.json` |
| Journal | `Broker/BrokerFrameJournal.cs` | `broker-frame-*.bin`, magic `UBJ2` |
| Envelope | `Broker/BrokerDecodedFrameEnvelope.cs` | UBD3: frames + post-state Noise |
| Ownership | `Broker/BrokerOwnershipStore.cs` | Foreground / background / closed |
| Lock | `Broker/BrokerInterprocessLock.cs` | Cross-process lease |
| Preview | `Preview/BackgroundMessagePreviewEngine.cs` | Isolated Signal replay |
| Names | `Preview/BackgroundDisplayNameStore.cs` | Toast title |
| Toasts | `Notifications/BackgroundToastPresenter.cs` | One toast per real message |

## Toasts

- **Real contact toasts** when minimized or screen-off (not a generic “new activity” tile)
- **Non-message frames are filtered** — receipts, presence, and keep-alives do not toast
- **Single disconnect toast** — not one per retry
- Foreground uses the same presenter shape: circular avatar (`hint-crop="circle"`), group title = group name / body = `Author: message`, direct = contact + body
- Missing local avatar → `Assets/Toast/avatar_contact.png` or `avatar_group.png`

## Coordinator (UWP)

`SocketBrokerCoordinator` implements `ISocketBrokerService`:

- Registers `BackgroundTaskBuilder` + `SocketActivityTrigger` (`IsNetworkRequested = true`)
- Schema marker `v673b1-r1` + package version; re-registers if the schema changed
- Removes the old in-process activity task / socket id from v6.7.2
- `DisposeBrokerSocketAsync` cancels I/O and clears ownership

On suspend, `App` asks the socket to transfer. **`SocketBridge.TransferSocketToBrokerAsync` returns `false`**, which the caller already treats as “keep the socket in-process”.

## What is stubbed on the new stack

Documented in `SocketBridge`:

> Handing the raw socket to a background task needs the transport itself, which the session owns and does not lend out.

| API | Current behavior |
|---|---|
| `TransferSocketToBrokerAsync` | Always `false` |
| `ReclaimSocketFromBrokerAsync` | Always `false` |
| `IsSocketOwnedByBroker` | Always `false` |

The UWP transport **still implements** `EnableTransferOwnership`, `TransferOwnership`, `AttachExistingBrokerSocketAsync`, journal drain, and Noise checkpoint. Cold restore (`TryRestoreBrokerSessionAsync`) exists only on the **uncompiled** legacy `SocketClient.cs`. The bridge always opens a new `StreamSocketWebSocketTransport`.

Until that handoff is wired, background delivery depends on the process staying alive enough for the in-process connection, or on a future change that lets `WhatsAppSession` lend (or recreate from checkpoint) the transport.

## Auth the task can see

The reconnect toast reads the same `LocalSettings` container as `AuthStore` (`WhatsAppAuth` / `auth_state`) to know whether a registered session exists. Signal preview uses `socket-broker-signal-preview-v673b.json`, which is **not** authoritative.
