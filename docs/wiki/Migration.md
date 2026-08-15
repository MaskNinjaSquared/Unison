# Migration

Unison is mid-migration from a monolithic `WhatsAppService` + `SocketClient` to **Unison.Socket + facades**. This page is the honest status of that work, not a roadmap promise.

## What is already the foundation

| Area | Status |
|---|---|
| Protocol / session | **`Unison.Socket`** (Baileys 7.0.0-rc14) |
| Wire owner | `ConnectionHandler` (socket-level only) |
| Session composition | `WhatsAppSession` + modules attached by `SocketBridge` |
| Use cases | ~38 covering messaging, receipts, retries, media, groups, profiles, USync, app-state, history, auth |
| UWP façades | Connection, messages, contacts, chats, profiles, history — **registered in DI** |
| ViewModels | Consume façades; login is façade-only |
| UI compatibility | `SocketBridge` implements `IWhatsAppSocket` so `WhatsAppService` did not have to move in the same step |
| LID addressing | `LidMappingStore` (replaces the old JidAlias flow for protocol) |
| History handshake | Buffered events + sequential offline nodes |
| Pairing | QR refresh/timeout, phone-number code, server logout |
| 1:1 after restart | Persisted `AuthState` + `FileKeyStore` |
| Disconnect / unpair | Session cleanup; QR no longer sticks |
| Self-chat read | Fixed on the new stack |
| Chat pin | App-state patch, synchronized with the phone |
| Outgoing audio | OGG before upload |

## Still compatibility

### WhatsAppService

Still the in-memory client: chat list, message caches, history-sync **body**, outbox/send **transport**, avatar apply + group fallbacks, presence, persist debounce, suspend hooks.

`IWhatsAppService` still exposes many primitives the façades forward to. Screens should not take a dependency on new members there; add them to the façade that owns the subject.

Leftover `MessageService` / `ContactService` / `ConnectionService` / `ProfileService` classes under `Services/WhatsApp/` are **not** in the container. Do not register them again.

### SocketClient

`Unison.Uwp/Client/SocketClient.cs` (and `PairingHandler.cs`) remain on disk but are **out of the UWP csproj**. The live connection is `SocketBridge`. Types in `SocketContracts.cs` stay until the last caller is converted.

### Broker handoff

The background task still hosts the **existing** raw-socket infrastructure (framing, Noise checkpoint, journal, toasts). `SocketBridge` does **not** transfer or reclaim the socket and does **not** cold-restore a broker session. Details: [Background broker](Background-Broker).

### ChatsModule

Presence / privacy / blocklist / profile helpers exist in `Unison.Socket` as `ChatsModule`. The bridge does not instantiate that module yet; some of those flows still go through the client.

## Suggested order of remaining work

These match comments in `SocketBridge`, `WhatsAppSession`, and the v6.9 notes, updated for the socket stack:

1. **Lend or recreate the transport** so `TransferToBrokerAsync` / reclaim / cold restore work on `WhatsAppSession`.
2. **Move history-sync body and send transport** out of `WhatsAppService` into `MessageFacade` / socket modules (the façade is already the entry).
3. **Stop `TriggerBackgroundResolution` from living on the client** — Contact/Message façades only.
4. **Prefer `IPersonStore` in UI** for names and avatars; flush `Person.AvatarUrl` in batches.
5. **Finish ChatDetail code-behind** (message list, scroll, MediaElement chrome) into ViewModels. Pin / play / presence already moved.
6. **Chat list `VisibleChats` mirror** — currently kept in the view for initial-sync safe mode.
7. **Instantiate `ChatsModule`** from the bridge when presence/privacy/blocklist callers are ready.
8. **Delete leftover `*Service` duplicates** and eventually `WhatsAppService` itself once no façade needs the god client.
9. **Drop `IWhatsAppSocket`** when nothing talks that legacy shape.

## Invariants to keep while migrating

- `Unison.Core` must not reference XAML or `Unison.Uwp`.
- `Unison.Background` must not reference Core or Socket.
- `ConnectionHandler` must not call use cases or hold domain collections.
- Reconnect policy stays in the host (`ConnectionUpdate`).
- Facades re-publish events; ViewModels do not subscribe to raw `IWhatsAppService` events.
- Auth LocalSettings key names stay stable (`WhatsAppAuth` / `auth_state`) so upgrades do not unlink devices.
- Language qualifiers stay in the **main** package (`AppxDefaultResourceQualifiers`), not resource packs.
