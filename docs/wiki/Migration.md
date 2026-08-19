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

Still the in-memory client: chat list, message caches, outbox/send **transport**, avatar apply + group fallbacks, presence, persist debounce, suspend hooks. Split into `partial` files by cluster (`WhatsAppService.Connection.cs`, `.Media.cs`, `.Groups.cs`, `.Avatars.cs`, `.Identity.cs`, `.AppState.cs`, `.Persistence.cs`, `.Receipts.cs`, `.IncomingPump.cs`) so later extractions do not edit a 16k-line blob.

`IWhatsAppService` still exposes many primitives the façades forward to. Screens should not take a dependency on new members there; add them to the façade that owns the subject.

### SocketClient

`Unison.Uwp/Client/SocketClient.cs` (and `PairingHandler.cs`) remain on disk but are **out of the UWP csproj**. The live connection is `SocketBridge`. Types in `SocketContracts.cs` stay until the last caller is converted.

### Broker handoff

The background task still hosts the **existing** raw-socket infrastructure (framing, Noise checkpoint, journal, toasts). `SocketBridge` does **not** transfer or reclaim the socket and does **not** cold-restore a broker session. Details: [Background broker](Background-Broker).

### ChatsModule

Presence / privacy / blocklist / profile helpers exist in `Unison.Socket` as `ChatsModule`. The bridge does not instantiate that module yet; some of those flows still go through the client.

### History migration gate (`history_migration`)

SQLite table in `unison.db` tracks whether a **history batch** has landed for the current `MessageStoreSyncId` epoch. Status: Pending → InProgress → Succeeded (or Failed).

- Store: `IHistoryMigrationStore` / `HistoryMigrationStore`
- **Owned by `HistoryFacade`** (`Track*` / `ResetHistorySqliteAsync` / `PersistHistorySqliteChunkAsync`)
- **Called from `MessageFacade.SyncMessageHistoryAsync`**: Person upsert → `PersistHistorySqliteChunkAsync` → `ChatMessagesChanged` for touched chats
- Reset on `OnSessionCleared` and on conversation resync wipe in `HistoryFacade`
- Live/JSON cache may still exist for leftover identity sidecars; list + timeline persist in SQLite

### History chat preview (`history_chat_preview`) — phase 1–2

List-row snapshots written **off the UI thread** inside `HistoryFacade.PersistHistorySqliteChunkAsync` (`HistoryChatPreviewBuilder` → `IHistoryChatPreviewStore`).

**Filters** (`HistorySyncContentFilter`, shared with `HistoryMessageBuilder`): only the newest *listable* message per conversation — skips protocol/revoke, pin, reaction-only, zero timestamp, and text rows with empty body (media kinds allowed with empty caption). Hydrate also refuses non-listable rows (`HistoryChatPreviewApplier.IsListable`).

| Column focus | Purpose |
|---|---|
| Jid / Lid / Pn | Identity + alias hints |
| Name, unread, last preview/kind/author/time | Enough for chat-list hydrate |
| **LastMessageMentionedJids** | Comma-separated proto JIDs for the list-strip @alias parser (schema **2**) |
| SyncId / SyncType | Ties rows to the MessageStore epoch |

- **Phase 1:** persist + `ChunkPersisted`
- **Phase 2:** `HistoryFacade` relays `ChatPreviewChunkPersisted` → `ChatListViewModel` merges into `ChatStateStore` / `VisibleChats`
- Gate + preview + message persist live on **HistoryFacade**

### History messages (`history_message`) — phase 3–4

Per-message rows written **off the UI thread** in `HistoryFacade` (same chunk as previews):

| Focus | Notes |
|---|---|
| ChatJid + MessageId | Composite PK |
| Body / Kind | Lightweight text + `ChatPreviewKind` |
| **SendState** | INTEGER: NotApplicable / Pending / Sent / Delivered / Read / Failed |
| **Quote snapshot** | `QuotedMessageId` / `QuotedChatJid` / `QuotedParticipantJid` / `QuotedSenderName` / `QuotedBody` / `QuotedKind` on the same row |
| **Pin / revoke** | `IsPinned` + `PinnedAtUtc` / `PinExpiresAtUtc`; `IsRevoked` |
| **Local media** | `MediaLocalUri` / `MediaPosterUri` (kept across chunk upserts) |
| **Reactions** | Table `history_message_reaction`, PK `(ChatJid, MessageId, ReactorJid)` |
| **Mentions** | `MentionedJids` comma-separated proto JIDs (schema **4**; sqlite-net `ALTER` on `CreateTableAsync`) |
| **Indexes** | Schema **5**: `(ChatJid, TimestampUtc, MessageId)`, `(ChatJid, IsPinned, PinnedAtUtc)`, `(ChatJid, Kind, TimestampUtc)` |
| Cap | 250 listable msgs/conversation per chunk (`HistoryMessageBuilder`); reaction / pin / revoke envelopes are scanned without that cap |
| **ChatJids** on event args | Open-detail hydrate |

**Phase 4:** LID↔PN via `ApplyHistoryLidMappings` before SQLite writes; progress via `NotifySqliteHistoryChunkApplied`; `MessageFacade` raises `ChatMessagesChanged` per touched JID; detail / load-more / on-demand through **`IMessageService`**. `HistoryMessageMapper` restores quote, pin, revoke, reactions, media keys, local URIs, and mentioned JIDs onto `ChatMessage`. Schema version **5**. JSON chat files are not migrated — resync.

### Status (`history_status`)

Separated from the chat list. History conversations with id `status@broadcast` are **not** preview/message chat rows. Items go to `history_status` keyed by **author (participant) JID**, with `ExpiresAtUtc` = timestamp + 24h (Baileys/Socket TTL). `StatusFacade` / `StatusView` read that table for the Status list and viewer.

Still deferred: posting Status / receipts / reply; retiring leftover `MessageStore` JSON sidecars (contact names / aliases). Live messages, outbox, and the chat catalog now persist in SQLite.

## Suggested order of remaining work

These match comments in `SocketBridge`, `WhatsAppSession`, and the v6.9 notes, updated for the socket stack:

1. **Lend or recreate the transport** so `TransferToBrokerAsync` / reclaim / cold restore work on `WhatsAppSession`.
2. **Drop leftover `MessageStore` JSON sidecars** (contact names, JID aliases) once `PersonStore` / `LidMappingStore` are the only readers.
3. **Stop `TriggerBackgroundResolution` from living on the client** — Contact/Message façades only.
4. **Prefer `IPersonStore` for identity** — `PersonSource` (Unknown → Observed → DirectChat → AddressBook) never downgrades; address book overwrites **name only**. Index `Person.Phone` for agenda match. `PersonGroup` (Person↔Group, with LID/PN aliases) feeds member “groups in common”. Group bubble avatars resolve via `ChatDetailViewModel` (roster / 1:1 / Person), not the bubble.
5. **Finish ChatDetail code-behind** (message list host, scroll, MediaElement chrome) into ViewModels. Pin / play / presence / **group run layout + author-avatar resolve** / **group-member info open** already moved. Keep member info on `ChatDetailGroupMemberInfoPane` (not inside the user/group shell).
6. **Chat list `VisibleChats` mirror** — currently kept in the view for initial-sync safe mode.
7. **Instantiate `ChatsModule`** from the bridge when presence/privacy/blocklist callers are ready.
8. **Move remaining clusters off `WhatsAppService` into façades**, then delete the compatibility client once nothing needs it. Step-by-step (phase 0 done): [WhatsAppService extraction](WhatsAppService-Extraction).
9. **Drop `IWhatsAppSocket`** when nothing talks that legacy shape.

## Invariants to keep while migrating

- `Unison.Core` must not reference XAML or `Unison.Uwp`.
- `Unison.Background` must not reference Core or Socket.
- `ConnectionHandler` must not call use cases or hold domain collections.
- Reconnect policy stays in the host (`ConnectionUpdate`).
- Facades re-publish events; ViewModels do not subscribe to raw `IWhatsAppService` events.
- Auth LocalSettings key names stay stable (`WhatsAppAuth` / `auth_state`) so upgrades do not unlink devices.
- Language qualifiers stay in the **main** package (`AppxDefaultResourceQualifiers`), not resource packs.
