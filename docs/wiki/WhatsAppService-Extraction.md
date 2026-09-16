# WhatsAppService → façades

How the compatibility client becomes **connection-only** on this side of the boundary. Status of the work, then the remaining phases. Product history stays in [Changelog](Changelog); protocol/broker leftovers stay in [Migration](Migration).

**Goal:** ViewModels and views talk only to façades (`IConnectionService`, `IMessageService`, `IChatService`, `IContactService`, `IProfileService`, `IHistoryService`, `IStatusService`, plus new contracts this plan adds). `WhatsAppService` shrinks to socket lifecycle. Facades that are ready already reach `WhatsAppSession` via `IWhatsAppSessionProvider`.

**Partials are not façades.** `WhatsAppService.Avatars.cs` is still the same class as `WhatsAppService.cs`. The compiler merges them. Facades inject `IWhatsAppService` and call methods; they do not reference a partial file. Moving work to a façade means **cutting methods out of the class** and implementing them on `ContactFacade` / `MessageFacade` / …, not “wiring the façade to the partial”.

---

## Done: phase A — `IJidResolver`

Callers that only needed “is this the same person?” no longer name the client.

- Contract: `Unison.Core/Contracts/IJidResolver.cs` (`GetCanonicalJid`, `TryGetAlias`, `Self`)
- UWP: `JidResolver` wraps the concrete `WhatsAppService` alias table (table move is still later)
- Switched: Core helpers + `ParticipantResolutionContext`, ViewModels, UI shells, `ContactFacade` / policy helpers, `ChatFacade`, `MessageFacade`, `DebugSendService`, `CommentRichService`
- `IContactService.ResolveDisplayName` for name lookup without the client
- **Removed from `IWhatsAppService`:** `GetCanonicalJid`, `JidAlias`

---

## Done: phase 0 (mechanical)

No behaviour change for users. Prep so later diffs are one cluster, not a 16k-line blob.

### Dead code

- Unreachable legacy JSON history apply: `ProcessHistorySyncBodyAsync`, `StoreConversationTcTokenAsync`, `ApplyHistoryConversationPin`, `UseHistorySqliteApplyPath`. `ProcessHistorySyncCoreAsync` only notifies SQLite-path progress (`NotifyHistorySqliteChunkApplied`).
- Leftover types that were **never in the UWP csproj**: `Services/WhatsApp/MessageService.cs`, `ContactService.cs`, `ConnectionService.cs`, `ProfileService.cs`, and the duplicate `DebugSendService.cs` beside the façades. Live debug sender is `Diagnostics/DebugSendService.cs`.
- `SettingsViewModel` no longer takes unused `IWhatsAppService` (logout is `IConnectionService`).

### Partials

`public partial class WhatsAppService` — one type, many files under `src/Unison.Uwp/Services/WhatsApp/`:

| File | Cluster |
|---|---|
| `WhatsAppService.cs` | Fields, properties, ctor/`Create`/`Attach*`, send, history SQLite notify, leftovers |
| `WhatsAppService.Connection.cs` | Connect / resume / pairing / reconnect / suspend / broker transfer |
| `WhatsAppService.Media.cs` | On-demand decrypt + cache (`Ensure*AvailableAsync`) |
| `WhatsAppService.Groups.cs` | w:g2 metadata, members, send permissions |
| `WhatsAppService.Avatars.cs` | Fetch/apply/cache, group HQ + fallbacks |
| `WhatsAppService.Identity.cs` | Canonical JID, alias LID/PN, `ResolveDisplayName`, usync |
| `WhatsAppService.AppState.cs` | App-state appliers (pin, mute, delete, names) |
| `WhatsAppService.Persistence.cs` | chats.json debounce, load persisted UI, suspend tail |
| `WhatsAppService.Receipts.cs` | Receipt nodes → send state |
| `WhatsAppService.IncomingPump.cs` | Decrypted-message queue, placeholders, offline replay summaries |

`IWhatsAppService` is unchanged as the façade-facing surface (minus the deleted history body). Do not grow it for UI; add members on the façade that owns the subject ([Coding standards](Coding-Standards) §6).

---

## Done: phase 1 — the UI frontier

No logic moved. The UI stopped naming the client; every member below is still implemented in
`WhatsAppService` and reached through a façade that forwards.

| Was on `IWhatsAppService` | New owner |
|---|---|
| `Chats` | `IChatStateStore.Chats` |
| `GetCanonicalJid`, `JidAlias` | `IJidResolver` (phase A; table still on the concrete client until 3.7) |
| `ResolveDisplayName`, `PhoneContactNamesByJid`, `MarkAvatarImageLoadFailed` | `IContactService` |
| `RefreshGroupSendPermissionsAsync`, `EnsureGroupRosterLoadedFromStoreAsync`, `EnsureHighQualityGroupAvatarAsync` | new `IGroupService` / `GroupFacade` |
| `ClearUnreadForChatAsync`, `SetActiveChatJid`, `GetTotalUnreadCount`, `PersistChatListRowsPublic`, `ReconcileChatPreviewsFromSqliteAsync` | `IChatService` |
| `IsInitialSyncSafeMode`, `InitialSync*`, `IsLoadingPersistedChats`, `PreferFrugalSyncBudget` | `IHistoryService` |
| `InitializeConnectionStateAsync`, `IsRegisteredAsync`, `EnsureConnectedAsync`, `LoadPersistedUiStateAsync`, `StartDeferredStartupMaintenance`, `IsConnected` | `IConnectionService` |
| `ClearSessionAsync` (debug wipe) | `IConnectionService.ClearLocalSessionAsync` (already existed) |
| `VerboseLogging`, `SetVerboseLogging` | `IDiagnosticsConsole` → `IRuntimeDiagnostics` |

Two deviations from the original plan, both deliberate:

- Verbose logging went to `IDiagnosticsConsole`, **not** `IDebugSendService`. That service is
  registered under `#if DEBUG`, so a Release `DebugViewModel` could not have taken it. The console
  is the debug pane's surface and already exposes `IsCaptureEnabled` the same way.
- `BootView` / `MainView` no longer attach the UI dispatcher. `AttachUiDispatcher` needs a WinRT
  `CoreDispatcher`, which `IDispatcher` does not carry, so rather than widen that contract the
  attach moved to `App.ConfigureServices`, which already holds the root frame. The views stopped
  doing infrastructure wiring, which was the point.

`WhatsAppService.Instance` is gone from `ChatAvatarControl`. Note there are **two** compiled
`ChatDetailView` code-behinds (`UI/Views` and `Shell/Unison/Views`); both were converted.

**Verified:** `IWhatsAppService` appears in `src/Unison.Core` only as its own contract,
`IConnectionService.AttachWhatsAppService` (stays until phase 4) and two doc comments. Zero hits
under `src/Unison.Uwp/UI` and `src/Unison.Uwp/Shell`.

---

## Current coupling (start here next session)

### Facades still depend on the client

`ContactFacade`, `MessageFacade`, `ChatFacade`, `GroupFacade`, `HistoryFacade`, `ProfileFacade`, `ConnectionFacade` take `IWhatsAppService` (or `AttachWhatsAppService`). Policy is already on the façade; **primitives** (fetch avatar, send bytes, persist, canonical JID) still run inside the client.

Helpers under `Contacts/` (`ContactNameResolver`, `ChatAvatarPolicy`, `GroupRosterPolicy`, `AddressBookOverlay`) are the largest non-UI consumers of client members (`RunOnUiThreadAsync`, `SchedulePersistPublic`, `RaiseSyncStatus`, `FetchAndApplyAvatarAsync`, `FetchGroupMemberAvatarAsync`, `IsTransportReady`, …). Extracting avatars/names is mostly moving those primitives so the helpers stop needing the god client.

### DI still injects façades *into* the client

After `BuildServiceProvider`, `App.ConfigureServices` does:

- `AttachMessageService` / `AttachStatusService` / `AttachContactService` / `AttachConnectionService`
- `AttachPersonStore` / `AttachChatStore` / `AttachGroupRosterStore` / `AttachHistoryService`

That is the wrong direction: live ingest (status, names) **starts** in `WhatsAppService` and calls up. Phase 2 inverts it (client publishes; façade subscribes).

### What still holds the client

`App.xaml.cs` for lifecycle (`ResumeAsync`, `TransferActiveSocketToBrokerAsync`, `PrepareForSuspendAsync`, `ShutdownAsync`, `ReleaseMemoryAsync`, `IsConnected`, `AttachUiDispatcher`), the façades and their policy helpers, and `RuntimeDiagnosticsService` for health snapshots.

Raw `IWhatsAppService` **events** are façade-only; ViewModels should not subscribe there.

---

## Remaining phases

Do them in order. Phase 3.9 (list + persist) is last among the body moves because `ChatStateStore` still exposes transitional dictionaries that the client mutates on the UI thread.

### Phase 2 — Invert `Attach*`

Each `AttachFoo` is a place the flow is born in the client. Replace with events the façade already owns (or add):

- `AttachStatusService` → `StatusFacade` subscribes to decrypted `status@broadcast` (today `IngestLiveStatusAsync` in the incoming pump)
- `AttachContactService` / `AttachMessageService` → same pattern
- `AttachPersonStore` / `AttachChatStore` → writes (`PersistPersonNameAsync`, group membership persist) move to the façade that already owns the store

**Done when:** no `(WhatsAppService)` cast in `App` for wiring; no `AttachMessageService` / `AttachContactService` / `AttachStatusService` / `AttachPersonStore` / `AttachChatStore`. `AttachWhatsAppService` on `IConnectionService` can stay until the connection façade owns the socket.

### Phase 3 — Move the clusters (the volume)

Self-contained first. List/persist last.

| Step | Partial / area | Destination |
|---|---|---|
| 3.1 | `.Avatars.cs` | `ContactFacade` / `ChatAvatarPolicy` (already owns **when**; take **how**) |
| 3.2 | `.Groups.cs` | new `IGroupService` / `GroupFacade`; prefer Socket use cases via `IWhatsAppSessionProvider`, not raw `BinaryNode` on the client |
| 3.3 | `.Media.cs` | `MessageFacade` + a UWP `MediaCacheService`. Contract already has `Ensure*AvailableAsync` |
| 3.4 | Send (main file) | `MessageFacade` over use cases; client only “send this node” |
| 3.5 | `.Receipts.cs` | `MessageFacade` / `ChatFacade` |
| 3.6 | Names / usync (`.Identity.cs`) | `ContactFacade` / `ContactDirectory`. Masked `*****` labels stay “no name” so projection can fill |
| 3.7 | Alias LID/PN + canonical | Fold session alias into existing `LidMappingStore`; `IJidResolver` already reads the client — move the table behind it. |
| 3.8 | `.AppState.cs` | Each applier → the façade of that fact (subject → groups, contact name → contacts, read/pin/flags → chats, delete message → messages). `AppStateSyncService` talks to façades, not the concrete client |
| 3.9 | `.Persistence.cs` + list sort/preview | `ChatFacade` + `ChatStateStore` + `IChatStore` / `IMessageStore`. Close the transitional public dictionaries on `ChatStateStore` |
| 3.10 | `.IncomingPump.cs` | Decode/dispatch stays with connection; apply (row, preview, unread, toast) goes to façades |

**Thread affinity:** today the client mutates `Chats` on the UI thread; VMs read on the UI thread; `ChatStateStore`’s extra dictionaries are protected by that, not only by the lock. Any code moved to a façade that runs off-thread must use `UpsertChatsAsync` / `UpsertMessagesAsync` (or `IDispatcher`). Do not split 3.9 into half-moves.

**Canonical JID:** introduce `IJidResolver` in phase 1 as a thin wrapper so 3.2 / 3.6 / 3.7 do not all rewrite aliasing at once.

### Phase 4 — What remains is connection

Rename-able to `IWhatsAppConnection` / keep `IWhatsAppService` until the last caller dies. Target surface:

- `InitializeAsync` / `ConnectAsync` / `ResumeAsync` / `EnsureConnectedAsync` / `Disconnect`
- `IsConnected` / `IsTransportReady`
- `IsRegisteredAsync` / `ClearSessionAsync` / `NotifyServerLogoutAsync`
- Suspend / broker / `ReleaseMemoryAsync` / `ShutdownAsync`
- Raw events **for façades only**

Drop `RunOnUiThreadAsync` (`IDispatcher`) and `SchedulePersistPublic` (the writer persists). `App` lifecycle calls `IConnectionService`, not `App.GetWhatsAppService()`.

**Done when:** `.Connection.cs` + a small main file; no façade needs send/history/avatar/group/list methods on the client; leftover `IWhatsAppSocket` drop is [Migration](Migration) item 9, after this.

---

## Risks (do not skip)

1. **UI-thread mutations of `Chats`.** Off-thread `ObservableCollection` writes crash the list. Use `IChatStateStore` APIs that marshal.
2. **`GetCanonicalJid` everywhere.** Wrapper first (phase 1), table move later (3.7).
3. **Do not register** resurrected `MessageService` / `ContactService` / `ConnectionService` / `ProfileService` under `Services/WhatsApp/` — deleted on purpose; live types are `*Facade`.
4. **Broker transfer** still returns false on `SocketBridge`. Unrelated to this extraction; see [Background broker](Background-Broker).

---

## After each phase

Build `Unison.Uwp` (not only Core). Update this page (tick the phase), [Changelog](Changelog), and the façade table in [Application layer](Application-Layer) if a new contract appeared (`IJidResolver`, `IGroupService`).

## Related

- [Architecture](Architecture) — client vs policy; `WhatsAppService` today
- [Application layer](Application-Layer) — which VM talks to which façade
- [Coding standards](Coding-Standards) — do not grow `IWhatsAppService` for UI
- [Migration](Migration) — SQLite history, broker, `ChatsModule`, `SocketClient` on disk
