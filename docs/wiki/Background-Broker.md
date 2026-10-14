# Background broker

When Unison is suspended or the screen is off, WhatsApp traffic still has to arrive. Windows offers `SocketActivityTrigger`: the app **transfers ownership** of a connected `StreamSocket` to an out-of-process task. That task is `Unison.Background`.

The **session/protocol stack** now lives in `Unison.Socket`. The **raw TCP socket** does not. Framing, Noise checkpoint, journal, and toasts remain in the broker + UWP transport. The session lends that socket through `ConnectionHandler` (warm transfer and reclaim); cold restore after process termination is the remaining step.

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

On suspend, `App` asks the socket to transfer; on resume it reclaims. Any failure answers `false`, which the caller treats as “keep the socket in-process” (or, on resume, “connect fresh”).

Exception: with `LocationKeepAliveEnabled` on, `App` never asks for the transfer (entered-background or suspending), as in 1.8. The broker sends no `w:p` keep-alive, so the server drops a brokered session and the user sees “Unison desconectado”; ExtendedExecution keeps the process and its keep-alive running instead. If the app is suspended anyway, the socket is shut down and reconnects on resume.

The same hold applies while the first sync after login is still arriving (`IHistoryService.IsInitialSyncSafeMode`, from the first SQLite history chunk until the quiet finalize). Handing the socket to the broker mid-sync risks the same keep-alive drop with half the history persisted. The rule is `BrokerHandoffHold` (Core); the journal records `reason=initial-sync` on `entered-background-result` and `suspending-transfer-skipped`. Without background mode, a suspend during the first sync therefore shuts the socket down and the sync resumes after reconnect.

## Handoff on the new stack

| API | Behavior |
|---|---|
| `SocketBridge.TransferSocketToBrokerAsync` | `ConnectionHandler.TransferToBrokerAsync`: stops the keep-alive, holds the send gate, lets the transport quiesce, then saves the Signal preview snapshot (best effort) and the Noise checkpoint before `TransferOwnership`. `true` only when ownership moved |
| `SocketBridge.ReclaimSocketFromBrokerAsync` | `ConnectionHandler.ReclaimFromBrokerAsync`: the transport re-attaches and drains the journal through the receive path, then the keep-alive restarts and the checkpoint is cleared |
| `SocketBridge.IsSocketOwnedByBroker` | The transport's ownership flag |

Why the keep-alive stops: the transport keeps reporting itself connected while the broker holds the socket, so a running keep-alive would see silence, end the session and close the broker's socket.

Replay: `WaTransportAdapter` unpacks UBD3 batches into `DecodedFrames` + `PostNoiseState`; `ConnectionHandler` dispatches the frames and imports the post-state after the whole batch. Raw UBJ2 frames go through the normal Noise decode. A replay that fails throws, so the journal keeps it.

**Not wired: cold restore.** If the process was terminated while the broker held the socket, the next `StreamSocketWebSocketTransport.ConnectAsync` disposes that socket and clears the journal and checkpoints. The broker never acknowledges what it reads, so the server redelivers those messages to the new connection. Adopting the socket instead needs `ConnectionHandler` to resume from a saved Noise state without a handshake; `TryRestoreBrokerSessionAsync` exists only on the uncompiled legacy `SocketClient.cs`.

## Auth the task can see

The reconnect toast reads the same `LocalSettings` container as `AuthStore` (`WhatsAppAuth` / `auth_state`) to know whether a registered session exists. Signal preview uses `socket-broker-signal-preview-v673b.json`, which is **not** authoritative.
