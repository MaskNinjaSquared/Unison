# Socket stack

`Unison.Socket` is a **netstandard2.0** port of **Baileys 7.0.0-rc14**. It has no WinRT, no SQLite, and no NuGet packages of its own. It references only `Unison.Baileys`.

Almost every file starts with `Ports: rc14 <TypeScript path>`. That comment is the map back to upstream.

## Split: `makeSocket` vs `makeWASocket`

| Baileys | Unison |
|---|---|
| `makeSocket` (`src/Socket/socket.ts`) | `ConnectionHandler` |
| `makeWASocket` (assembly over `makeSocket`) | `WhatsAppSession` + host-attached modules |
| `makeMessagesSocket` / recv | `MessageModule` |
| media half of messages | `MediaModule` |
| `makeChatsSocket` | `AppStateModule` + `ChatsModule` |
| `makeGroupsSocket` | `GroupsModule` |
| `Defaults/index.ts` | `SocketConfig` |
| `Types/Events.ts` + `event-buffer.ts` | `WaEventKind`, `WaEventBuffer` |
| `WAUSync/*` | `USync/` |
| `lid-mapping.ts` | `LidMappingStore` |
| `message-retry-manager.ts` | `MessageRetryManager` |
| `offline-node-processor.ts` | `OfflineNodeProcessor` |

### ConnectionHandler

Owns the wire only:

- Connect the `IWaTransport`
- Noise XX handshake (`NoiseHandler` from Baileys)
- Frame / unframe binary nodes
- Correlate IQ `id` with replies (`QueryAsync`)
- Keep-alive
- Publish `ConnectionUpdate`

It **never** calls a use case, **never** stores chats, and **never** reconnects. Features register routes on `NodeDispatcher`. Reconnect is the host reacting to `WaEventKind.ConnectionUpdate`.

Node handlers run **outside** the read loop so a handler can `QueryAsync` without deadlocking (documented deviation from Node).

### WhatsAppSession

Small composition root. Constructor wires:

1. `WaEventBuffer` (or an injected bus)
2. `ConnectionHandler`
3. `PairingFlow` (QR + phone-number link code)
4. `ConnectionLifecycle` (`success` / `failure` / `stream:error`)

Public API: `ConnectAsync`, `CloseAsync`, `LogoutAsync`, `RequestPairingCodeAsync`, `Events`, `Connection`.

Message, media, app-state, groups, and offline sync are **not** constructed here. The UWP host (`SocketBridge`) attaches those modules after creating the session — the same layering Baileys uses, with the host finishing `makeWASocket`.

```
SocketBridge
  └─ WhatsAppSession
       ├─ WaEventBuffer
       ├─ ConnectionHandler  → IWaTransport, NoiseHandler, NodeDispatcher, KeepAlive
       ├─ PairingFlow
       └─ ConnectionLifecycle
  ├─ OfflineSyncCoordinator.Attach()
  ├─ MediaModule → MessageModule.Attach()
  ├─ AppStateModule.Attach()
  └─ GroupsModule
```

`ChatsModule` exists (presence, privacy, blocklist, profile picture) but the bridge does not instantiate it yet.

## Unison.Baileys

Shared leaf library. No project references.

| Folder | Responsibility |
|---|---|
| `Protocol/` | `NoiseHandler`, `NoiseSessionState`, `BinaryNode`, encoder/decoder |
| `Crypto/` | Curve25519, AES-GCM, HMAC, HKDF, XEdDSA |
| `Proto/` | Generated `WAProto.cs` |
| `Client/` | `AuthState`, `SignalHandler`, `IAuthPersistence`, `IKeyStore` |

The socket **orchestrates**; Baileys **implements primitives**. Signal ratchet encrypt/decrypt stays in `SignalHandler`. The socket talks to it through `ISignalRepository` (UWP adapter).

## Folder map (`Unison.Socket`)

| Folder | Role |
|---|---|
| `Abstractions/` | Host seams: transport, log, LID storage, app-state store, media downloader, prekeys, message lookup |
| `Session/` | Handler, session, pairing, lifecycle, keep-alive, `SocketConfig` |
| `Events/` | Bufferable bus (`BaileysEventMap`) |
| `UseCases/` | One protocol operation per class |
| `Messages/` | Receive path, decrypt, retry, offline queue, `MessageModule` |
| `Notifications/` | Non-message stanzas: calls, presence, group, mediaretry |
| `Groups/` | Metadata parser/cache used by send |
| `Signal/` | `LidMappingStore`, `ISignalRepository`, prekey parse |
| `USync/` | Composable user × column queries |
| `AppState/` | Syncd patches, LT-hash, mute/archive/pin/read |
| `Media/` | CDN HTTPS + AES-CBC (bytes leave the socket) |
| `Sync/` | History blob download/inflate → `MessagingHistorySet` |
| `WABinary/` | JID predicates (LID vs PN) |

## Use cases (~38)

One IQ or operation, no domain state. Grouped as in the tree.

### Auth

| Class | Purpose |
|---|---|
| `LogoutUseCase` | Unlink companion on the server, then close. Deleting local keys is **not** logout. |
| `SendPassiveIqUseCase` | Active (live stream) vs passive companion |
| `UploadPreKeysUseCase` | Publish one-time prekeys (rc14 volume: **812** initial) |
| `UploadPreKeysIfRequiredUseCase` | Upload only when the server count is below `MinPreKeyCount` (**5**) |

### Messages (send, receipts, retries)

| Class | Purpose |
|---|---|
| `SendMessageUseCase` | App entry: factory → relay → local upsert |
| `RelayMessageUseCase` | Group skmsg / 1:1 per device / retry |
| `CreateParticipantNodesUseCase` | Encrypt one copy per device |
| `GetUSyncDevicesUseCase` | Device list + LID column for fan-out |
| `AssertSessionsUseCase` | Ensure Signal sessions; one `encrypt` IQ for missing |
| `SendReceiptUseCase` | Delivered / read / played |
| `SendMessageAckUseCase` | Stanza `<ack>` / nack |
| `SendRetryRequestUseCase` | Ask for re-encrypt after decrypt failure |
| `RequestPlaceholderResendUseCase` | Last resort: plaintext from the phone |

### Media

| Class | Purpose |
|---|---|
| `RefreshMediaConnUseCase` | CDN hosts + token, cached by TTL |
| `UploadMediaUseCase` | Encrypt + HTTPS PUT |
| `DownloadMediaUseCase` | Plaintext bytes; re-upload if the URL expired |
| `UpdateMediaMessageUseCase` | Ask the phone to restore a file on the CDN |

Outgoing **audio is converted to OGG** before upload (UWP media processor).

### Groups

| Class | Purpose |
|---|---|
| `FetchGroupMetadataUseCase` | Best source of LID/PN pairs |
| `FetchParticipatingGroupsUseCase` | All groups for this login |
| `CreateGroupUseCase` | Create + server description |
| `ModifyGroupParticipantsUseCase` | Add / remove / promote / demote / join requests |
| `UpdateGroupSettingsUseCase` | Subject, description, permissions, ephemeral, leave |
| `GroupInviteUseCase` | Read / revoke / inspect / accept |

### Profile, chats, contacts

| Class | Purpose |
|---|---|
| `FetchProfilePictureUrlUseCase` | Contact or group picture URL |
| `UpdateProfileUseCase` | Status + own/group photo (JPEG already square). Display name is app-state, not this IQ. |
| `SendPresenceUseCase` | Online, typing, `presenceSubscribe` |
| `PrivacySettingsUseCase` | Privacy + disappearing default |
| `BlocklistUseCase` | Fetch + block/unblock |
| `CleanDirtyBitsUseCase` | Ack stale collections |
| `OnWhatsAppUseCase` | Does this number have WhatsApp, and which JID? |
| `ResolveContactNamesUseCase` | Names via usync contact + lid |
| `FetchLidMappingsUseCase` | LID behind each PN |

### App-state, USync, history, peer

| Class | Purpose |
|---|---|
| `ResyncAppStateUseCase` | Align collections with the phone; bad MAC → snapshot |
| `SendAppPatchUseCase` | Mute / archive / pin / read (applied locally; server does not echo) |
| `FetchAppStateSyncKeyUseCase` | Ask the phone for a missing sync key |
| `ExecuteUSyncQueryUseCase` | Send a composed USync query |
| `FetchMessageHistoryUseCase` | Older messages from the phone |
| `SendPeerDataOperationMessageUseCase` | `category=peer` to the phone (placeholder, history, keys) |

## Events

`IWaEventBus` is the only Socket → host channel. During initial history sync, `WaEventBuffer` **buffers and merges** bursts (`MessagingHistorySet`, chats/contacts/messages upserts, receipts, group updates), then flushes. Timeout 30s auto-flush; nested flush debounce 100 ms.

QR travels on `ConnectionUpdate.Qr` (no separate QR event), matching rc14.

Offline nodes are processed **sequentially** (`OfflineNodeProcessor` + `OfflineSyncCoordinator`: preview → batches of 100 → “done” releases the buffer, with a 20s safety).

## Addressing: LID, not JidAlias

rc14 replaced the old PN/LID alias table with `LidMappingStore`:

- 3-day cache, coalesced lookups, USync fallback
- Storage is opaque (`ILidMappingStorage`); UWP uses SQLite
- Device part is reattached on read

Documented deviation: Unison omits `:0` on JIDs (rc14 writes `user:0@s.whatsapp.net`).

## Retries

`MessageRetryManager` tracks sent messages with an LRU cache and **retry reasons 0–13** (WhatsApp Web codes). `MaxMsgRetryCount` default is 5.

## Pairing

`PairingFlow` handles:

- **QR** — `pair-device` / `pair-success`; payload `https://wa.me/settings/linked_devices#…`
- **Phone-number pairing** — 8-character link code via `RequestPairingCodeAsync`

QR refreshes after `SocketConfig.QrTimeout` (60s). After disconnect, QR state is cleared so the login surface does not stick.

Logout notifies the **server** (`LogoutUseCase`) before wiping local state.

## SocketConfig defaults (rc14 wins over the old client)

| Setting | Value |
|---|---|
| WebSocket | `wss://web.whatsapp.com/ws/chat` |
| Connect timeout | 20s |
| Keep-alive | **30s** (legacy pinged 20s) |
| QR timeout | 60s |
| Initial prekeys | **812** (legacy uploaded 30) |
| Min prekeys | **5** (legacy refilled below 30) |
| Browser string | `{ "Mac OS", "Chrome", "14.4.1" }` |
| `SyncFullHistory` | `true` |

## Documented deviations from rc14

1. Event emit is awaitable; handlers run in registration order.
2. LID `:0` omitted.
3. Prekeys are raw 32-byte keys (no Signal `0x05` prefix).
4. WhatsApp padding lives in `ISignalRepository` (existing `SignalHandler`), not in the socket.
5. Dispatch off the read loop (avoid `QueryAsync` deadlock).
6. Keep-alive death = silence since last frame, not a missing pong (legacy needed an IQ within 12s).
7. `MessageEnvelope` instead of generated `WebMessageInfo` (LID fields missing in the current proto).
8. Group history and call log are **off** in `HistorySyncConfig`.
9. `ChatsModule` is split out of `makeChatsSocket`; app-state is its own module.

## Transport seam

`IWaTransport` is bytes only: connect, send, close, plus `TransferToBrokerAsync` / `ReclaimFromBrokerAsync`. It is **not** the Core `IWhatsAppTransport`. `WaTransportAdapter` wraps the existing UWP `StreamSocketWebSocketTransport` so transports did not have to be rewritten in the same PR.

See [Background broker](Background-Broker) for why transfer still returns false.
