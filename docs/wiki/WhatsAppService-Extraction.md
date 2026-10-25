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
| `WhatsAppService.cs` | Fields, properties, ctor/`Create`/`Func<>` façades, pin-in-chat send, history SQLite notify, leftovers |
| `WhatsAppService.Connection.cs` | Connect / resume / pairing / reconnect / suspend / broker transfer |
| `WhatsAppService.Media.cs` | On-demand decrypt + cache (`Ensure*AvailableAsync`) |
| `WhatsAppService.StartupPhases.cs` | Quiet-wait / memory-headroom host wiring (`StartupQuietGate`) |
| `WhatsAppService.Identity.cs` | Canonical JID, alias LID/PN, `ResolveDisplayName`, usync |
| ~~`WhatsAppService.AppState.cs`~~ | Removed — `ForgetChatStorageHost` + `AppStateLocalHost` |
| `WhatsAppService.Persistence.cs` | chats.json debounce, load persisted UI, suspend tail |
| `WhatsAppService.Receipts.cs` | Receipt nodes → send state; receipt-path group metadata apply via `IGroupService` |
| `WhatsAppService.IncomingPump.cs` | Decrypted-message queue, placeholders, offline replay summaries |
| `WhatsAppService.OutgoingHost.cs` | Explicit `IOutgoingMessageHost`: the caches and socket the send flows on `MessageFacade` reach (3.4) |

`WhatsAppService.Groups.cs` and `WhatsAppService.Avatars.cs` are gone: group apply lives on `GroupFacade` / `GroupMetadataApply`; avatar fetch/apply moved earlier in phase 3.

`IWhatsAppService` is unchanged as the façade-facing surface (minus the deleted history body). Do not grow it for UI; add members on the façade that owns the subject ([Coding standards](Coding-Standards) §6).

---

## Done: phase 1 — the UI frontier

No logic moved. The UI stopped naming the client; every member below is still implemented in
`WhatsAppService` and reached through a façade that forwards.

| Was on `IWhatsAppService` | New owner |
|---|---|
| `Chats` | `IChatStateStore.Chats` |
| `GetCanonicalJid`, `JidAlias` | `IJidResolver` (phase A). Table is `JidAliasTable` since 3.7a |
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

**Verified:** `IWhatsAppService` appears in `src/Unison.Core` only as its own contract
and doc comments. `ConnectionFacade` uses `IConnectionEventSource` /
`IConnectionSessionHost`. Zero hits under `src/Unison.Uwp/UI` and `src/Unison.Uwp/Shell`.

---

## Done: phase 2 — invert `Attach*` (the reportable half)

Each `AttachFoo` is a place the flow is born in the client. Where the call was a **report** —
fire-and-forget, no return value, no ordering the client depends on — it is now an event the client
raises and the façade subscribes to in its own constructor.

| Client event | Subscriber | Replaced |
|---|---|---|
| `OnStreamError` | `ConnectionFacade` | `AttachConnectionService` (gone) |
| `OnInvalidSessionSuspected` | `ConnectionFacade` | same |
| `OnLiveStatusReceived` | `StatusFacade` | `AttachStatusService` (gone) |
| `OnBackgroundHistorySyncBounced` | `HistoryFacade` | `NotifyBackgroundHistorySyncBounced` (host calls) |
| `OnAvatarCached` | `ContactFacade` | `NotifyAvatarCached` (host calls) |
| `OnJidAliasResolved` | `ContactFacade` | `NotifyJidAliasResolved` (host calls) |
| `OnAvatarRetryDue` | `ContactFacade` | gone — alias follow-up calls `RequestAvatarRefresh` itself |
| `OnHistorySyncReceived` | `HistoryFacade` | `NotifyHistorySyncReceived` (host calls) |
| `OnGroupListingDue` | `GroupFacade` | `NotifyGroupListingDue` (host calls) |

That let the push-in doors close on the contracts too. `NotifyStreamError`,
`NotifySuspectedInvalidSession`, `TryIngestLiveAsync`, `NoteBackgroundHistorySyncBounce` and
`ClearAvatarAttempted` are private to their façades now, because the client was their only caller.
`ConnectionFacade` takes connection host ports (`IConnectionEventSource`, session /
ensure / resume / broker / lifecycle) — not an aggregating client contract.
`ProfileFacade` uses `IConnectionService` only (`SessionEstablished` / `EnsureConnectedAsync`).
Contact / Group / History / Message / Chat / Status façades no longer subscribe to moved subjects.

`ApplyGroupMetadataFromResponseAsync` lost its `hydrateAvatars` parameter: both call sites passed
`false`, so the `HydrateGroupMemberAvatarsAsync` branch behind it had never run.

### What phase 2 could not do

The original plan also listed `AttachMessageService`, `AttachPersonStore` and `AttachChatStore`
here. They do not invert, because those calls are not reports:

- `AttachMessageService` — two of the three call sites are **queries** the incoming pump branches
  on: `TryHandleReaction`, which answers through an `out` parameter, and `GetChatMessage`, which
  returns the mapped message. An event cannot return a value. The third blocks history apply until
  it completes. Inverting these means moving the pump's mapping out of the client, which is
  3.4 / 3.10.
- `AttachPersonStore` / `AttachChatStore` / `AttachGroupRosterStore` — the writes are interleaved
  with client state; roster replace and group membership persist both sit inside the group-metadata
  apply. Moving them *is* 3.2 / 3.8 / 3.9.

`AttachContactService` survives too. A second pass took out what was still a report:

- The alias follow-up used to call `RequestAvatarRefresh(chat, force: true)` per row through a
  client forward. It now collects the rows still without an avatar on the UI thread and
  `ContactFacade` forces the refresh itself. Nothing after it in the follow-up waits on the fetch
  (dedup and `NotifyDisplayNamesChanged` only read rows).
- `FetchProfilePicturesAsync` / `RetrieveContactPicturesCoreAsync` are gone. They were the
  "no façade attached" fallback of the background pass, and they only forwarded back to
  `_contactService?.RetrieveContactPicturesAsync`, so with no façade they did nothing.

What is left on `_contactService` is awaited and ordered by the client, so it is orchestration:

- `HydrateCachedAvatarUrisAsync` — awaited three times (deferred startup early and late, persisted
  UI load); the faces must be bound before the dedup / preview reconcile that follows.
- `RefreshPhoneContactOverlayAsync(force: true)` in deferred startup — awaited, then
  `ApplyResolvedNamesToChatsAsync` and `SchedulePersist` read what it filled.
- `ResolveMissingNamesAsync` — awaited in the post-message enrichment and the background pass
  before the next step. The third caller (deferred startup) is fire-and-forget, but an event for it
  alone would leave the same forward in place for the other two.
- `RefreshContactNamesAsync` — awaited in the post-message enrichment before
  `TriggerBackgroundResolution`.
- `RetrieveContactPicturesAsync(token)` in `TriggerBackgroundResolution` — awaited between the
  names step and `NotifyGroupListingDue` so the listing does not compete with the picture batch for
  the socket, and `token` is `_resolutionCts`, which `ShutdownAsync` cancels (phase 4). Firing the
  listing earlier would let it overlap the picture batch; the pass and its defer checks
  (`ShouldDeferProfilePictureFetch`, `CancelDeferredProfilePictureResolution`) read private
  client state. It moves with the pass, not before it.

---

## Current coupling (start here next session)

### Phase B (inbound ownership) — in progress on `docs/netstandard-whatsapp-client`

Post-session chat envelopes route on `MessageFacade` (`RouteIncomingChatEnvelopeAsync`
incl. alias hints, controls, content, placeholder note/resend, full peer-data
response, placeholder ack, socket missing-message, receipts). Missing-message
ledger + `PlaceholderResendDispatch` also live on `MessageFacade`; the host only
implements `IPlaceholderResendSocket`. Outgoing accept / local preview / chat-changed
notify are façade callbacks (not `IOutgoingMessageHost`). Media and avatar cache
notify go to `IMessageService` / `IContactService` directly. Socket wire acks,
outgoing status, and receipts use `NotifyInboundWireAck` /
`NotifyOutgoingStatusAsync` / `NotifyReceiptAsync`. Receipt group-metadata apply is
on `MessageFacade` (host: IQ + recipient-count cache). Offline release /
post-replay list repair / deferred-startup list repair / background resolution+listing
are façade `Notify*` (host keeps quiet/memory/catch-up gates). Stanza `notify`
push-name is `ContactFacade.NotifyInboundMessageNode`. Pump keeps queue lanes and
session-only App State. Persistence debounce timer / Connection lifecycle stay on the host.
`ForgetChatStorage` orchestration is `ForgetChatStorageDispatch` (`ForgetChatStorageHost` I/O).
Full catalogue persist, preview-slice persist, and suspend-tail orchestration are
`ChatCatalogFullPersistDispatch` via `ChatCatalogDiskPersistence`
(`IChatCatalogFullPersistHost`); startup catalogue hydrate is
`LoadPersistedChatCatalogDispatch` on the same owner
(`ILoadPersistedChatCatalogHost`). Debounce timer / `IChatCatalogPersistHost`
stay on the client. Heavy-history enrichment release is
`ReleaseEnrichmentAfterHeavyHistoryCoordinator`; self push-name / phone / avatar
auth sync is `SelfProfileAuthSync`.

### Facades still depend on the client

`ConnectionFacade` takes `IConnectionEventSource` + `IConnectionSessionHost` (+ ensure /
resume / broker / lifecycle hosts) — **not** `IWhatsAppService`. **`ProfileFacade`,
`ChatFacade`, `MessageFacade`, `ContactFacade`, `GroupFacade`, `HistoryFacade`, and
`StatusFacade` likewise** do not take the client for event wiring. Policy is already
on the façades; **primitives** (fetch a chat avatar, send bytes, persist, canonical
JID) still run inside the client for some hosts. Group send-permission refresh and
roster restore run in Core. The full-size group picture and member pictures are
fetched by `GroupFacade`.

Helpers under `Contacts/` (`ContactNameResolver`, `ChatAvatarPolicy`, `GroupRosterPolicy`,
`AddressBookOverlay`) no longer take the god client — they use `SyncStatusChannel`,
`IWhatsAppSessionProvider.IsTransportReady`, `IChatAvatarFetch`, and stores. Remaining
Phase B host ownership: post-replay / post-message enrichment / offline-replay
flush extracted as `PostReplayMaintenanceDispatch`, `PostMessageEnrichmentDispatch`
`OfflineReplayFlushDispatch`, and `IncomingPumpDrainDispatch` (CAS/CTS/timer /
typed queue item is now Core `IncomingDecryptedNotifyRequest` (was UWP
`DecryptedMessageEventArgs`); queue algebra lives in Core `IncomingMessageQueue<T>`;
wire→queue map is `IncomingDecryptedNotifyMap`). Soft-reconnect schedule / post-loop /
loop gates are `ConnectionSoftReconnectGates` (pure; Task.Run + locks stay on host).
Connect transport-ready / session-died refuse / reconnect-toast clear are
`ConnectionTransportReady` / `SessionDiedConnectRefusal` /
`ConnectionStatusNotifyPolicy`. `BuildSocketBridge` is thin (`SocketBridgeRemoteHooks`);
`WireSocketEventHandlers` body is `SocketEventHandlerWiring` (UWP; still
`IWhatsAppSocket`). Broker reconnect consume gate is
`BrokerReconnectConsumePolicy` (IPC / dispose stay on host). Startup alias-row
restore decision is `PersistedAliasRestore` (Core; bulk file + self seed stay).
Linked-account / IsRegistered shape is `LinkedAccountGate`. Soft-reconnect
pairing abort / can-schedule / loop-error raise live on
`ConnectionSoftReconnectGates`. On-demand healthy/probe/teardown is
`ConnectionHealthPolicy.DecideOnDemand` (`OnDemandSocketHealthStep`).
History hydration batch loop is `HistoryHydrationBackfillDispatch`
(`IHistoryHydrationBackfillHost` I/O on the client; lock/active owned by
`HistoryHydrationBackfill`). Freshness reconnect
schedule/abort gates are `FreshnessReconnectFallbackGates`. SQLite quiet
finalize body is `SqliteHistoryQuietFinalizeDispatch`
(`ISqliteHistoryQuietFinalizeHost` I/O; delay/generation owned by
`SqliteHistoryQuietFinalize`).
Catch-up continue soft-reconnect gates are `CatchUpContinueReconnectGates`.
SQLite chunk type/progress numbers are `HistorySqliteChunkClassify`.
Post-offline freshness orchestration is `HistoryPostOfflineFreshnessDispatch`
(`IHistoryPostOfflineFreshnessHost`). Reconnect FULL_HISTORY floor is
`ReconnectHistoryCatchUpFloorGates`. Catch-up finish next-step is
`HistoryCatchUpFinishDecision`. First-sync list unlock gate is
`FirstSyncListReleaseGates`. Deferred startup body is
`DeferredStartupMaintenanceDispatch` (+ `DeferredStartupFrugalGate`).
Hydration day-retry arm is `HydrationDayRetryGates`.
User-resync FULL_HISTORY connect/retry/wait timeouts are
`UserResyncFullHistoryGates`; the retry orchestration is
`UserResyncFullHistoryDispatch` (`IUserResyncFullHistoryHost`).
Catch-up finish follow-up (list release vs enrichment) is
`HistoryCatchUpFinishDispatch` (`IHistoryCatchUpFinishHost`).

Primitives that already left the contract (façade / Netstandard Phase A):

- **UI thread.** `AttachedUiDispatcher` (UWP, `IDispatcher`) is a singleton the client, `ChatFacade`, `ContactFacade` and the contact helpers take in their constructors. `App` attaches the root frame's `CoreDispatcher` to it; `AttachUiDispatcher` and `IWhatsAppService.RunOnUiThreadAsync` are gone.
- **Catalog save.** `IChatCatalogPersistence` (`Schedule`, `PersistRows`) replaces `SchedulePersistPublic` / `PersistChatListRowsPublic`. Debounce orchestration is `ChatCatalogPersistDispatch` over `IChatCatalogPersistHost`. Full persist / suspend-tail orchestration is `ChatCatalogFullPersistDispatch`; startup hydrate is `LoadPersistedChatCatalogDispatch`. The debounce timer field and SQLite/UI snapshot I/O stay on the client because `ShutdownAsync` resets the timer.
- **Network reachability.** `INetworkAccess` / `WinRtNetworkAccess` — `ConnectionFacade.HasInternetAccess` no longer calls `NetworkInformation` directly.
- **Foreground wait.** `ContactFacade` refreshes the People overlay after `IAppLifecycle.WaitUntilForegroundAsync` instead of hooking `Window.Activated`.

### DI still injects façades *into* the client

The stores (`IPersonStore`, `IChatStore`, `IGroupRosterStore`) are constructor parameters of the client now. After `BuildServiceProvider`, façade `Attach*` into the client is gone for Message/Chat/Contact/Group:
`WhatsAppService.Create` takes `Func<IMessageService>` / `Func<IChatService>` / `Func<IContactService>` / `Func<IGroupService>` (lazy, breaks the DI cycle). One shared `GroupMetadataApply` is registered in `App` and injected into `GroupFacade`; the receipt path reaches it through `IGroupService.ApplyGroupMetadataAsync`. The pump and history sync still *call* those façades; moving that ownership is the remaining Attach* inversion work.

`AttachContactService` body work: background names/pictures on `IContactService.RunBackgroundResolutionAsync`; deferred-startup overlay/hydrate/relabel on `RunDeferredStartupContactPassAsync`. Quiet-wait / memory-headroom rules are `StartupQuietGate` (WhatsAppClient). Alias follow-up (queue + `CoalescedDebounceScheduler` + persist / avatar retry / dedupe) runs on `ContactFacade`; the client only implements `IJidAliasPersistence` and cancels via `IContactService.CancelAliasFollowUp` on shutdown. Blank-avatar row selection is `AliasFollowUpAvatarRows`. Unread badge + shortcut + catalogue slice after a conversation unread change is `ChatUnreadSideEffects` (shared by app-state read and `ChatFacade.ClearUnreadForChatAsync`). Name-entry prep before the UI walk is `ResolvedNameBatchPrep`; app-state subject/contact lists are `AppStateResolvedNameEntries`; post-delete tip refresh across alias rows is `AppStateDeleteMessagePreview`. Quoted author naming is `QuotedSenderNaming`; push-name SQLite gate is `PersonNamePersistGate`; local-send strip update is `LocalSendChatPreview`; freshness log stamps are `FreshnessTimestampFormat`; placeholder drain lines use `MissingMessageCandidate.FormatForLog`. Group title cache gate is `GroupMeaningfulLabel`; list-strip outgoing ticks are `ListPreviewOutgoingSendState`. Localized strip labels (`You` / `Yesterday`) are `LocalizedChatStripLabels` in UWP. Incoming-pump preview / offline-tip wrappers are gone — pump calls `ChatFacade.RefreshChatPreviewAsync` / `RecordOfflineReplayTip` directly. SocketBridge chat settings go through `ChatFacade.ApplyRemoteChatSettingsAsync` (local flags/read on the façade); group subjects / self push-name / contact table slices go through `IContactService.ApplyRemote*` over `IResolvedNameApplyHost` + `IJidAliasPersistence.RegisterAliasMappings` (`AccountContactUpdate` is the Core DTO at the bridge). Bridge fallbacks that duplicated façade paths are gone. Receipt orchestration (`HandleReceiptAsync` + `GroupReceiptTally`) is on `MessageFacade` over `IOutgoingReceiptHost`; timeline status apply stays on the client. List dedupe / local-send tip / legacy deleted-preview repair are on `IChatService` (`DeduplicateChatListAsync`, `ShowLocalSendPreviewAsync`, `RepairLegacyDeletedPreviewsAsync`); `IOfflineReplayHost.NotifyHistorySurfaceRefresh` covers the post-repair cache nudge. `RememberPersonName` lives on `ContactFacade` (push map + `PersonNamePersistGate` + person store); `CaptureSelfPushName` entry points go through `IContactService` into `IResolvedNameApplyHost` (auth / sidebar stay on the client). Resolved-name batch apply and persisted-title normalize live on `ContactFacade` (`ApplyResolvedNameBatchAsync` / `NormalizePersistedChatNamesAsync`); catch-up / self-name list relabel is `ApplyResolvedNamesToChatListAsync` over `ChatNameRefresh` (façade owns `Applied`). Self push-name capture policy lives on `ContactFacade.CaptureSelfPushName`; `IResolvedNameApplyHost` is auth/sidebar write + identity reads. `StartupPhaseTrace` is in WhatsAppClient (takes `IRuntimeDiagnostics`). Client tip reconcile after replay / quiet history / persisted UI goes through `IChatService.ReconcileChatPreviewsFromSqliteAsync` (no client-held `ChatPreviewReconciler` / `ChatNameRefresh`). Client no longer holds `ChatListDeduper` or duplicate dedupe/repair methods. Alias id fast-path is `AliasLinkedMessageFind.HasId`. The client still owns handshake / catch-up gates and calls `IGroupService.NotifyGroupListingDue` after background resolution.

`AttachDebugSendService` is gone (phase 4): `App` starts `IDebugSendService` once in Debug builds.
The watcher already skipped requests while the transport was not ready, so the five start/stop
calls in `.Connection.cs` were redundant. In Debug it now polls the request file while disconnected
too; requests still wait for the transport.

Alias avatar retry runs inside `ContactFacade` follow-up (`RequestAvatarRefresh`); there is no
client `OnAvatarRetryDue` event.

`AttachGroupService` is gone: its only use was the last step of the background resolution pass.
The client calls `NotifyGroupListingDue` there, and `GroupFacade` shows the Groups status, runs the
listing and clears the status itself. Quiet-wait / handshake gates for the enrichment pass
and post-message enrichment still sit on the client (`ShutdownAsync` cancels the CTS).

That is the wrong direction: live ingest (status, names) **starts** in `WhatsAppService` and calls up. Phase 2 inverted the reportable half of it (client publishes; façade subscribes); the rest is blocked on phase 3 and is described below.

### What still holds the client

The façades and their policy helpers, and `RuntimeDiagnosticsService` for health snapshots. `App.xaml.cs` no longer does: its lifecycle calls (`ResumeAsync`, `TransferActiveSocketToBrokerAsync`, `PrepareForSuspendAsync`, `ShutdownAsync`, `ReleaseMemoryAsync`, `IsConnected`) go through `IConnectionService` (phase 4, first step).

Raw `IWhatsAppService` **events** are façade-only; ViewModels should not subscribe there.

---

## Remaining phases

Do them in order. Phase 3.9 (list + persist) is last among the body moves because `ChatStateStore` still exposes transitional dictionaries that the client mutates on the UI thread. The `Attach*` calls phase 2 could not invert are folded into the steps that own them (3.2 / 3.4 / 3.8 / 3.9 / 3.10).

### Phase 3 — Move the clusters (the volume)

Self-contained first. List/persist last.

| Step | Partial / area | Destination |
|---|---|---|
| 3.1a | Avatar cache (done) | `IAvatarCache` / `AvatarCacheService` |
| 3.1b | Avatar fetch (done) | `AvatarFetcher`, behind `IUsyncGate` |
| 3.1c | Avatar apply to the row (done) | `ChatAvatarOutcome` (Core); fallback + fetch stay in the client |
| 3.2a | Group protocol reading (done) | `GroupMetadataReader` |
| 3.2b | Group apply + roster persist | **Done.** Listing, unresolved-metadata fallback, metadata apply, roster restore, the full-size group picture and member pictures run from `GroupFacade` |
| 3.3a | Media cache + file naming (done) | `IMediaCache` / `MediaCacheService`, `MediaFileExtensions` |
| 3.3b | Derived renditions — transcode / poster / WebP (done) | `MediaDerivationService` |
| 3.3c | Download reading and fetch (done) | `MediaDownloadPlan` (Core); orchestration and HTTP fetch on `MessageFacade`. Quiet save is `QuietMediaMessagePersist` over `IOutgoingMessageHost.PersistLiveAsync` (not the compatibility client) |
| 3.4 | Send (main file) | **Text, image and audio orchestration on `MessageFacade` (done)** via `OutgoingTextSend` / `OutgoingMediaSend` (Core) over `IOutgoingMessageHost`. **`AcceptOutgoing` / `LoadLiveOverlay` forward to `IMessageService`.** Socket write / invalidate / health counters stay on the host |
| 3.5a | Receipt reading (done) | `ReceiptReader` |
| 3.5b | Receipt aggregation state (done) | `GroupReceiptTally` (Core). **Outgoing status apply on `MessageFacade.ApplyOutgoingStatusAsync`** over shared `PendingMessageState` DI; host keeps group recipient count + self-link |
| 3.6 | Names / usync (`.Identity.cs`) | `ContactFacade` / `ContactUsyncResolver`. **Rules and usync orchestration out — see below.** Push name, avatar check and number match on the façade; table filing is `UsyncAliasFiling`, chat merge is `ChatListAliasMerge`; the client keeps only `TryFileUsyncAlias` (socket + identity heal) |
| 3.7a | Alias LID/PN + canonical (done) | `JidAliasTable` (Core), read through `IJidResolver` |
| 3.7b | Converge onto `LidMappingStore` | **Done.** Shared DI store; sync peek with device/hosted re-attach; dual-write + bulk restore; warm from SQLite on init; shared `TryFromAliasPair`. Table remains sync owner (self-poison / lid-like). Retiring the table is a later phase |
| 3.8 | `.AppState.cs` | **Rewritten — the premise was stale.** See below |
| 3.9a | List display order (done) | `ChatDisplayOrder` (Core) |
| 3.9b | `.Persistence.cs` + preview reconcile + the appliers from 3.8 | `ChatFacade` + `ChatStateStore` + `IChatStore` / `IMessageStore`. Close the transitional public dictionaries on `ChatStateStore`. **Persistence rules out; appliers remain — see below.** **Name maps closed (`DisplayNameTable`). `MessagesByChat` is `ChatMessageCache` (conversation set locked) and owns every edit to the per-chat lists — merge, key canonicalization, insert, trim, add-missing (with a limit), insert-if-absent, window rebuild, upsert, remove, id index. `NotifyChangedExternally` closed. Public `WhatsAppService.MessagesByChat` alias removed — client hosts / diagnostics read `_chatState` (`GetMessages` / `GetMessageCount` where that fits); façades already used the store.** **Repeated list-strip assembly is `ChatListStrip`. Phone-digit lookup is `KnownJidByPhoneDigits`. Delete/persist I/O stays** |
| 3.10 | `.IncomingPump.cs` | Decode/dispatch stays with connection. **Rules closed.** **Notify + timeline accept + persist enqueue + offline-duplicate enrich + reaction + alias finalize + cached-id probe + revoke + pin-in-chat on MessageFacade** (shared `PendingMessageQueue` DI). **Envelope push-name accept on ContactFacade.** **Live + offline list apply + tip refresh/reconcile on ChatFacade.** **Resend row transitions on `MissingMessageLedger`.** **Queue lanes, generation and stage on `IncomingMessageQueue<T>`.** **Offline replay tips on ChatFacade.** **Timeline accept (alias move / duplicate / insert) on `IncomingTimeline`.** **Resend wait / send / answer window on `PlaceholderResendDispatch`.** **`IncomingMessagePump` owns the queue + drain + handle host** (client only enqueues / health-reset / suspend-drain). **`OfflineReplayFlush` owns flush lock / idle timer / claim-drain** (client only `IOfflineReplayFlushIoHost` SQLite / catalogue) |

**3.1a is done.** The avatar half of `MediaCache` is `IAvatarCache` / `AvatarCacheService`: `TryGet`,
`SaveAsync`, `DeleteIfCached`. It took `BuildSafeAvatarFileName`, `TryGetCachedAvatarUri`,
`DownloadAndCacheAvatarAsync`, the static `AvatarHttpClient` and the file delete that was inline in
`MarkAvatarImageLoadFailed`. Callers now pass an `AvatarVariant` instead of the `"_high"` suffix.
`ProfileFacade` takes the cache directly, which let `CacheRemoteAvatarAsync` leave `IWhatsAppService`.

**The usync gate came out first.** The client held one `SemaphoreSlim` over every directory-style IQ,
shared by two clusters that are moving to different owners: profile-picture lookups (3.1) and usync
contact resolution (3.6). Moving avatar fetch out while names stayed behind would either leave the
lock in the client and force a half-move, or give avatars their own lock — which lets two IQ streams
run concurrently against the surface the lock exists to rate-limit. It is now `IUsyncGate` /
`UsyncGate`, handing out a disposable lease instead of the `WaitAsync` / `lockTaken` / `Release`
triple that was repeated at all five sites.

**3.1b is done: the fetch left, the apply stayed.** `AvatarFetcher` owns wire-and-disk — candidate
sweep, the gate, high-resolution, download — reaching the socket through
`IWhatsAppSessionProvider` so it needs no client reference. It touches no chat state, which is why it
can be called from a background batch, a row scrolling into view, or the group info pivot without any
of them agreeing about threads. `FetchBestProfilePictureResultAsync`, `GetProfilePictureAsync` and the
high-resolution loop are gone from the client; `GetProfilePictureUrlAsync` is a one-line forward.

Candidates are passed in rather than resolved inside. The table itself moved in 3.7a, but injecting
`IJidResolver` here would still close a DI cycle (`IJidResolver` eagerly resolves `IWhatsAppService`,
which takes the fetcher). Callers build the list with `ChatPinStoreKeys`.

What is left of avatars in the client is the **apply**, and it is deliberately parked: it writes
`ChatItem` rows on the UI thread and calls `SchedulePersist` (3.9), and the group-avatar fallback is
group-metadata protocol (3.2). Also staying is `ShouldDeferAvatarFetch` — it reads history backfill
and on-demand state, so it is history gating wearing an avatar name, and belongs with 3.9/3.10.

**3.2a is done: the parser left, the apply stayed.** `GroupMetadataReader` turns a `w:g2` response
into plain objects — subject, announce-only, member count, participant drafts, my role — plus the
list algebra the roster merge needs (`RosterJidSetsEqual`, `MergeInPlace`) and the placeholder-name
test. It holds no socket, no chat state and no dispatcher, so it is the first piece of the group
cluster that can be checked without watching rows change. `GroupListingEntry` and `GroupMemberDraft`
moved with it; 3.2b replaced both with `GroupMetadataSnapshot` in Core and took the list algebra
along.

It takes **two functions** rather than `IJidResolver`: canonical JID, and "is this the logged-in
account". The second one reads the PN/LID alias table. Reimplementing that in the reader would have been
a silent behaviour change in the role the composer trusts for announce-only groups. 3.7a collapsed
both functions into `IJidResolver`, which the reader takes now.

The rest of `.Groups.cs` is apply and orchestration, and it is blocked on three other steps, not on
itself: it walks `Chats` on the UI thread and calls `SchedulePersist` (3.9), writes `ContactNames`
and calls `ResolveDisplayName` (3.6), and canonicalises through the client's alias table (3.7). Move
it when those land, not before. 3.2b did it without waiting, by reaching the 3.6 / 3.9b primitives
through a port rather than moving them — see its section below.

**3.3a is done: storage left, downloading stayed.** `IMediaCache` / `MediaCacheService` owns
`LocalFolder/MediaCache/{Images,Audio,Documents,Video,VideoPosters}`; `MediaFileExtensions` (Core,
pure) owns MIME to extension. Callers name a `MediaCacheKind` and a file base and get back an
`ms-appdata:` URI, instead of writing the "open MediaCache, then open the subfolder, then build the
URI by concatenation" sequence by hand at every save and lookup — 217 lines out of `.Media.cs`.

`SaveAsync` takes `reuseExisting` because the old call sites disagreed on purpose and the difference
is worth keeping: an image or a video keyed by message id is byte-identical on redownload, so the
write is skipped, while documents, posters and transcodes are things we produced and may reproduce.

Two follow-ups, both real:

- The same folder boilerplate is still duplicated in `OggOpusToWavConverter` and
  `OggOpusHandlerService`. They should take `IMediaCache`. Left out of 3.3a deliberately — the audio
  path is the fragile one on Mobile and deserves its own change.
- `HistoryThumbnailMaterializer` reads the folder path and the URI from `MediaCacheService`
  (`LocalFolderPath`, `UriFor`) but keeps writing with `System.IO`. It does **not** take
  `IMediaCache`: it writes a whole history lot of thumbs on a worker thread while the sync awaits it,
  and `SaveAsync` costs several StorageFile round trips per file on Mobile.
- ~~`TryTranscodeOggOpusToM4aAsync` still opens the Audio folder itself.~~ Resolved in 3.3b.

**3.3b is done: the second renditions left.** `MediaDerivationService` owns the four things that
exist only because Windows 10 Mobile ships a narrower set of codecs than the desktop, and WhatsApp
sends for the desktop: the PNG sibling of a WebP, the platform PNG re-encode, the Ogg/Opus to M4A
transcode, and the first-frame video poster. 216 lines out of `.Media.cs`. The original payload
always stays on disk; every one of these is an addition next to it.

It takes `MediaCacheService` rather than `IMediaCache`, which is the point 3.3a could not reach:
`MediaTranscoder` encodes into a `StorageFile` it is handed, and a Core interface cannot produce one.
Both sides are UWP, so there was never anything to abstract — the only reason it looked like a
problem in 3.3a was that the holder was the client, which talks to Core contracts. The container now
registers the concrete type and maps `IMediaCache` onto it.

**3.3c took the reading, not the orchestration.** The four `Ensure*AvailableAsync` each opened with
the same block spelled out by hand: decode the key, decode the expected hash, pick a cache file base,
pick a default mime. Four copies of one rule, and they had drifted — audio named its cached file after
the message id while the other three used the content hash, so the same voice note forwarded to five
chats was five downloads and five copies on disk. `MediaDownloadPlan` (Core, pure, 13 tests) is that
reading in one place.

The orchestration deliberately stayed: what a missing key means (a sticker marks itself failed and
returns quietly, a video raises), what to do with the bytes, and when to persist the row. Those are
genuinely different per kind, and flattening them would be inventing a rule rather than moving one.

Extracting it also surfaced a defect the four copies were hiding: `EnsureVideoAvailableAsync` checks
the cache before taking the download lock and again after waiting on it, and only the first built the
video poster. Two taps on the same uncached video, and the one that waited returned a row with no
thumbnail. Both paths now go through `EnsureVideoPosterAsync`.

What is still in the client is the call that writes the repaired URI back onto the message and
persists it. Building the PNG sibling and the playable M4A/WAV now runs in `MediaDerivationService`
(`RepairWebPForDisplayAsync`, `EnsurePlayableAsync`). `WebPDisplayName` keeps a repair from nesting
`_display` on a file that is already the sibling. The four on-demand downloads themselves now run
from `MessageFacade`, and they share `MediaDownloadGate` with the live image and sticker hydrate
so a phone still fetches one file at a time. The same gate covers an image or a sticker that arrives
on its own: that download left the incoming pump and runs from `MessageFacade` too.

The blob fetch no longer goes through the client either. `MediaAvailability` takes
`IEncryptedMediaDownloader` (plain HTTP against the CDN, registered once in the container).
`IMediaDownloadHost` is `QuietMediaMessagePersist` (WhatsAppClient) over
`IOutgoingMessageHost.PersistLiveAsync` — the compatibility client no longer implements it.
The durable write still uses the host's live upsert until the persist queue leaves entirely.
Image/sticker hydrate asks `IWhatsAppSessionProvider.IsTransportReady` instead of a host socket flag.
Preparing a voice note the user just sent for playback moved from the client's
`IOutgoingMessageHost.PrepareLocalPlayback` to the façade, which calls `MediaAvailability`
after the send returns; the client's duplicate playable-audio path is gone.

**3.7a is done, and it did not fold into `LidMappingStore`.** The plan above said to merge the
session alias map into `LidMappingStore`, whose own header says it exists to replace exactly that.
The endpoint is right; one step is not how to get there, for two reasons.

`LidMappingStore` is async throughout (`GetLidForPnAsync`, `GetPnForLidAsync`). Canonicalization is
synchronous, sits on the incoming-message path, and is reached from ViewModels and XAML code-behind
— roughly ninety call sites inside the client and sixty outside, with `IJidResolver.GetCanonicalJid`
synchronous in the Core contract. Converting that means making all of it async, or blocking on async
on the UI thread, or keeping a synchronous cache in front — and the third is what the alias map
already was. Separately, the two disagree on keys: `LidMappingStore` keys by user part and
re-attaches the device suffix on read, while the alias map holds whole normalized JIDs and carries
special handling for LID-shaped `@s.whatsapp.net` identifiers plus the self-poisoning guards.
Merging without reconciling that changes canonicalization, which is what decides chat identity and
duplicate chats.

So 3.7a extracted instead: `JidAliasTable` in `Unison.Core/State` owns the map *and* the rules that
read it — `GetCanonicalJid`, `IsSelfLinked`, `IsSelfJid`, `IsLidLike`, `GetCanonicalSelfPnJid`. Both
moved verbatim. The client keeps its private methods as one-line forwards, so no call site changed.
`JidResolver` now reads the table directly rather than wrapping the client, and `IJidResolver` gained
`IsSelfLinked` — which let `GroupMetadataReader` drop the two delegates 3.2a gave it and take the
resolver, closing that note. `TryGetAlias` normalizes before the lookup, matching `GetCanonicalJid`,
so a device suffix cannot miss a pair. History, avatars and contact phone lookup expand keys through
`ChatPinStoreKeys` instead of three private copies of the same list.

The table learns the logged-in account through `BindSelf`, two functions rather than two strings.
The account's own LID arrives after pairing and is written in place on the existing auth state, so a
snapshot taken at construction would be a snapshot of null and the self-poisoning guards would
quietly stop firing.

3.7b is the storage swap behind this seam: reconcile the key shapes, then let the table read
`LidMappingStore` through its memory cache. Nothing above it has to change again.

**3.7b is done: storage swap behind the seam, table still sync.** Case rule:
`JidHelper.Normalize` lowercases `@g.us`. One DI `LidMappingStore` is shared by `SocketBridge`,
`ContactFacade`, and `JidAliasTable` via `ILidMappingMemory` / `LidMappingMemoryAdapter`. Sync
peek goes through `TryGetCachedPairedJid` (user-part keys + device/hosted re-attach, same as
async resolve). Misses promote into the table; indexer/`Add` dual-write via `RememberPair` /
`RememberPairs`; persistence restore uses `BeginBulkImport`. Init warms the store memory from
durable storage (`GetAllAsync` / `WarmCacheFromStorageAsync`) before alias restore. Bridge and
adapter share `LidMapping.TryFromAliasPair` (including hosted). The table keeps self-poisoning
and LID-like `@s.whatsapp.net` rules — retiring it / making canonicalization async is not 3.7b.

**3.5a is done: reading left, the tally stayed.** `ReceiptReader` turns a `<receipt>` node into
`ReceiptFacts` — status, message ids, chat, participant, whether it is a group — and counts how many
distinct other people a group message has to reach. It takes `IJidResolver`, which 3.7a made a real
implementation rather than a wrapper.

`RegisterGroupReceipt` and the recipient-count cache stayed in the client: both live under
`_messageStateLock` alongside the rest of message state, and moving them means moving that lock,
which is 3.9.

Two traps worth recording, because both fail silently rather than loudly:

- Recipient counting is **not** `GroupMetadataReader.CountMembers`. That one counts the roster,
  trusts the `size` attribute and includes the account itself. Reusing it would set a target no set
  of receipts can meet, and group messages would never show as read.
- `CountRecipients` returns `int?`, not `int`. A response with no group node and a group that counts
  nobody are different facts: the original cached the second for thirty minutes and retried the
  first. Collapsing them to `0` would have re-queried group metadata on every incoming receipt.

**3.8's premise no longer holds.** The step was written as "each applier goes to the façade of that
fact, and `AppStateSyncService` talks to façades rather than the concrete client". The second half
cannot be done: `AppStateSyncService.cs` is **not in the csproj**, exactly like `SocketClient.cs`. It
takes a `SocketClient` in its constructor, nothing builds it, and the only reference left to it is a
comment. Repointing it would be a day spent on a file the compiler never sees.

The live path replaced it and is already inside the client — `SocketBridge` calls back in:

```
bridge.SelfPushNameChanged = name => ApplyAppStateSelfPushNameAsync(name);
bridge.GroupSubjectChanged = (jid, subject) => ApplyGroupSubjectAsync(jid, subject);
bridge.ChatDeleted        = jid => ApplyAppStateDeleteChatAsync(jid);
bridge.MessageDeleted     = key => ApplyAppStateDeleteMessageAsync(key.RemoteJid, key.Id);
```

So there is no external caller left to repoint, and the first half — moving the appliers themselves —
is not a separate step either. `ApplyAppStateReadStateAsync`, `ApplyAppStateDeleteChatAsync`,
`ApplyAppStateChatFlagsAsync` and `ForgetChatInStorageAsync` all mutate `Chats`, unread counts and
the message stores. Moving them *is* moving chat state, which is 3.9.

**3.8 is therefore folded into 3.9** rather than sequenced before it. Nothing is lost: the appliers
were always going to follow the state they write.

**3.9a is done: the order left, the state did not.** `ChatDisplayOrder` in `Unison.Core/Helpers`
owns `Compare`, `Reposition` and `SortInPlace` — sibling to `ChatMessageOrder`, which already did the
same job one level down for messages. Ordering is a rule about `ChatItem`, and `ChatStateStore`,
which owns the collection, is in Core too; only the client was in the way.

Moved verbatim, including the part that is easy to mistake for an optimisation worth rewriting:
`SortInPlace` scans for the first out-of-place row before touching anything, because the list is
usually already ordered and every `Move` is a collection-changed notification the `ListView` has to
act on. On a phone with a few hundred chats that check is the difference between a sync that scrolls
and one that stutters.

This was picked as the first slice of 3.9 because it is the half the compiler can vouch for. The
rest — persistence, preview reconciliation, and the app-state appliers folded in from 3.8 — all
write chat state under the client's locks, and that is 3.9b.

**Before starting 3.9b, read this.** Phases 3.1a through 3.9a were all chosen on one criterion:
the compiler could vouch for them. A moved pure function that loses a caller does not build. That
criterion is exhausted — everything left (3.9b, 3.6, 3.10) writes mutable state under the client's
locks, where a mistake builds cleanly and shows up as a chat that stopped saving.

Two misses in the extraction bear this out: the constructor-ordering crash in 3.7a, which only
appeared at runtime, and `CountRecipients` collapsing "no group node" into "zero recipients" in 3.5a,
caught by re-reading the diff *after* a green build.

So `tests/Unison.Core.Tests` now exists (net9.0, xUnit, `dotnet test`). It pins the order rule, the
name blacklist, the extension mapping, `ToUtc` and the `JidAliasTable` canonicalization rules.

Writing the alias tests turned up something 3.9b and 3.7b both need to know. The self-poisoning guard
in `GetCanonicalJid` does not cover the case its comment claims: when a contact's LID is aliased
straight to our id, the guard's own `IsSelfLinked` call consults that alias, returns true, and
disables the guard. The pair was refused upstream by `TryRecordAliasMapping` on the live path, but
the startup restore in `.Connection.cs` wrote persisted aliases into the table directly, without that
check — so anything that reached disk came back on every launch. The restore now drops such a pair
through `IsSelfPoisoningAliasPair`, which works precisely because it asks *before* inserting; the
in-table guard asks afterwards, when the entry is its own alibi, and is left as it is. Both
behaviours are pinned by tests marked as recorded rather than endorsed. 3.9b re-keys persisted rows
by canonical address, so it inherits whatever this table says. That is the regression net for 3.9b: the order
tests fail if the move changes what the user sees in the list. It is not full cover — persistence
and the appliers are in the UWP head and out of its reach — so 3.9b still wants a device pass. It is
the difference between a silent reorder and a red test.

**3.9b has started: the pending message queue is out.** `PendingMessageQueue` (`Unison.Core/State`)
owns what is queued for SQLite and the rule for when to write it, replacing seven fields and a lock
that were spread across three files. The dedupe rule that was written twice — queue and requeue — is
now in one method.

The seam is the return value: `Add` answers with a `PendingFlushAction` and the host runs the timer
or the flush. That keeps the decision atomic inside the queue's lock while the platform work happens
outside it, which is the general shape for the rest of 3.9b — the rule moves to Core, the I/O and the
UI thread stay in the host.

Taking this one first was deliberate. It is the message persistence path, where a mistake compiles
cleanly and surfaces as messages that never land, and it is only takeable now because the queue is
deterministic given a clock reading and therefore testable. Thirty tests cover it. What is still in
the client is genuinely platform: the `Timer`, the SQLite write, the diagnostics.

**The persist debounce followed, and closed a race.** `PersistScheduler` owns whether the catalogue
owes a save and whether startup is still warming up. Those two flags used to disagree about their
lock: `SchedulePersist` read the suppression flag inside `_persistLock`, while `EnableScheduledPersist`
read *and wrote* it outside, beside a `_persistPending` read that was inside. Two callers lifting
suppression together could both decide they owed the deferred save. Lifting is now one atomic step,
and the three outcomes are a return value rather than a flag the caller has to re-derive.

Both slices leave the same residue in the client, and it is the right residue: a `System.Threading.Timer`
and a database call. The rule about *whether* to act is in Core with tests; the platform work that
follows is in the host.

**Then the two name rules, and the preview tip.** `ChatNameReplacement` and `BackgroundDisplayNameTable`
took the label rules; `ChatPreviewTip` took the three decisions inside `ReconcileChatPreviewsFromSqliteAsync`
— which message the list line should show, whether the row already shows it, and whether a row from
the old schema should have its missing `LastMessageId` stamped from the store. What is left in that
method is the alias key expansion and the SQLite read, both of which need the client.

That is the same split as the two slices above, and it is worth naming because it is the reason these
were takeable at all: the reconcile pass looks untestable, and most of it is, but the part that decides
what the user sees is a handful of predicates over `ChatItem` and `ChatMessage`.

**What is left in `.Persistence.cs` should stay there.** After four slices the file is orchestration:
snapshot the live collections on the UI thread, await a store, write a diagnostic. There is no rule
left to lift that would not amount to moving I/O into Core.

**The appliers from 3.8 have started.** `AppStateChatMutation` took the unread count when the account
marks a conversation read or unread (`ResolveUnreadCount` + `StampUnread` onto every alias row), and
the archive / pin / mute flags. Note where the difficulty actually lives — not in the flags themselves
but in the fact that one mutation lands on every row sharing a canonical identity. "Mark as unread"
carries no number, so it reads the highest count across the alias rows before falling back to 1; an
unpin writes `0` rather than null, so a row that has not received the mutation yet cannot resurrect
the pin through dedupe. Both are now tests rather than comments. App-state badge totals call
`ChatUnreadTally` directly (no client `GetTotalUnreadCount` wrapper).

**3.9b is closed.** The delete appliers gave up two rules — `ChatPreviewTip.PickLatest` and `.Clear`,
the "what does the row show now" half — and kept the rest. `ApplyAppStateDeleteChatAsync` and
`ApplyAppStateDeleteMessageAsync` still write tombstones and delete from SQLite, and that is where they
belong: the decision and the I/O genuinely are the same statement there.

Pulling `PickLatest` out found a live bug worth recording, because it is the third sighting of the same
shape. The replacement preview after a delete was chosen with `OrderByDescending` on the raw `DateTime`,
while every other ordering path in the app normalises through `ToComparableUtc` first. Rows read back
from SQLite arrive `Unspecified`. **Treat an un-normalised `DateTime` comparison as a defect on sight**
— `ChatDisplayOrder`, `ChatPreviewTip` and now this have all had it.

**3.5b and 3.1c followed, both unblocked by 3.9b closing.** `GroupReceiptTally` owns the per-message
tally of who received and who read; `ChatAvatarOutcome` records how an avatar lookup ended. The second
is small but load-bearing: *no photo* is an answer and stamps the fetch time so the row stops being
asked, while *could not reach it* must not, and must leave any existing image alone. Those two were
four separate field-writes scattered through one method, which is exactly how they would have drifted.

**What is left is no longer shaped like these.** 3.4 (send), 3.6 (names / usync) and 3.10
(`IncomingPump`) are not extractions of a rule from a method — they are moves of whole responsibilities
onto façades, with the socket and the UI thread on both sides. 3.3c is a
façade move too. The test suite does not reach any of them, because they are all in the UWP head.

**None of this has been run.** Every slice from the pending message queue onward is compile-checked
and test-covered and has not been on a device. That debt keeps growing and is the next thing to spend
time on, ahead of any further extraction.

### 3.9b continued — the timeline leaves the client in steps

What still keeps phase 4 waiting is that the client edits each conversation's message list in place.
`ChatMessageCache` takes those edits over one kind at a time; the client keeps the id index, the
missing-message ledger and the UI thread until the timeline has a single owner.

*Alias merge:* folding an alias timeline into its conversation was written four times (transient
DM merge, LID→PN duplicate merge, duplicate-row dedupe, cache-key normalisation). It is
`ChatMessageCache.MergeInto` now, behind the client's `MergeTimelineInto`. The copies had drifted;
two differences were fixed rather than kept. Only the cache-key pass sorted after merging, so the
other three left alias messages at the end out of time order while later inserts binary-search a list
they assume is sorted. And two copies appended id-less messages unconditionally, duplicating any the
target already held. Only the transient merge forgets the alias's missing-message rows, as before.

*Id index:* `MessageIdIndex` moved from a client field onto `ChatMessageCache.Ids`. Every place the
client removed or cleared a timeline already dropped its ids by hand, and replacing one rebuilt
them; `Remove`, `Clear`, `Set` and `MergeInto` do that themselves now, and `KnownIds` builds the set
lazily from the timeline. Direct list edits (insert, trim, page load) still register or rebuild.

*Initial load window — fifth sighting, fixed:* `LoadInitialMessagesAsync` still carried its own copy of
the window rule, with both defects `ActiveChatMessageWindow` had already had fixed: raw `DateTime`
ordering (live `Utc` rows against SQLite `Unspecified` ones, off by the local offset) and id-less
messages grouped on a fresh `Guid`. It sorts through `ChatMessageOrder` and trims through
`ActiveChatMessageWindow.TrimInPlace` now.

*Insert and trim:* `ChatMessageCache.Insert` (sorted insert, trim, register — the order the client
used, so an old message trimmed at once still counts as seen) and `.Trim` (window plus id rebuild).
The outgoing accept and the pinned-target insert call `Insert`; `TrimInMemoryMessageWindow` forwards
to `Trim`, which `IncomingTimeline` still reaches through its callback.

*Add missing:* `ChatMessageCache.AddMissing` appends what the timeline lacks (by id, by reference when
id-less), sorts, rebuilds the index and returns what entered. It dedupes against the list, not the
index, as both copies did. `SeedChatMessagesInMemoryAsync` and the insert loop of
`LoadMoreMessagesAsync` call it (the load-more filter and capacity `Take` stay in the client). The
seed copy appended id-less rows every time — now the same reference rule as `MergeInto`.
`RegisterMessageId` is gone. The client no longer edits a timeline list directly; the reaction and
pin paths only read it.

*Preview reconcile — the tip choice left:* `ChatPreviewTip.PickStoredTip` walks a stored page down
to its newest row that may go on the strip (the walk that was inline, plus the client's
`MapPreviewTipCandidate`), and the client compares it with the memory tip through `PickNewer`
directly.

*Preview reconcile — the pass left:* `ChatPreviewReconciler` (UWP, DI singleton over
`IHistoryMessageStore`, `JidAliasTable`, `ChatStateStore`, `ChatRowIndex` and the dispatcher) owns
the SQLite read, the memory lookup and the per-row apply, on the UI thread in slices of 24 chats
with one sort at the end, and returns how many rows changed. The client's
`ReconcileChatPreviewsFromSqliteAsync` is a forward that notes catch-up progress and schedules the
catalog save when something changed; `ChatFacade` still reaches it through the client for that
reason.

### 3.4 — send moved to `MessageFacade`

The flow left the client whole, and it lives in Core, so the suite reaches it now. `OutgoingTextSend`
owns validation, the canonical chat, the id, the optimistic bubble, the bounded retry
(`OutgoingTextSendRetry`), settlement (`OutgoingSendSettlement` + `SelfChatStatusPolicy`) and the
order of every side effect. `OutgoingMediaSend` does the same for image and audio, including the
`_out` cache write through `IMediaCache`. `OutgoingChatMessage` is the one place that says what an
outgoing bubble looks like.

What they reach through is `IOutgoingMessageHost`, a narrow Core port the client implements
**explicitly** (`WhatsAppService.OutgoingHost.cs`), registered in `App.ConfigureServices` as a cast of
the client the way `BridgeSessionProvider` already is. It is not on `IWhatsAppService`, which lost
`SendTextMessageAsync`, `SendImageAsync` and `SendAudioMessageAsync`. `DebugSendService` sends
through `IMessageService` like the chat page does.

The host keeps what the client still owns: the in-memory timeline (pending receipts, ordered insert,
trim, id index), the local-send chat-row preview, the outbox and batched flush, the socket and its
invalidation, the send counters for the health snapshot, and `OnError`. A transport attempt hands
back the socket it used, so dropping it after a failure cannot kill a newer connection.

Behaviour is kept as it was, two quirks included and pinned by tests: an image is filed under the
**normalized** JID while text and audio use the canonical one, and plain audio previews as a voice
note. Two deliberate differences: image and audio now apply any receipt that arrived before the bubble
existed (text always did), and an empty image is rejected before reconnecting rather than after.

Later, media stopped throwing on a failed send. `OutgoingMediaSend` picks the id up front (the
same generator as text) and passes it down through `IOutgoingMessageHost` and
`IWhatsAppSocket.SendImageMessageAsync` / `SendAudioMessageAsync` (`explicitMessageId`), so the
cached `_out` file and a pending bubble exist before the upload. A refused upload or a failed
reconnect settles the bubble as failed and calls `OnError`; there is still no retry. The bridge's
failed status now carries that id, so the list preview turns red too.

### 3.2b — group apply and roster out; the query passes stay

**Rules.** `GroupRosterRules` (Core) holds the decisions a roster goes through: the detached copy
written to the store, rebuilding rows from the store with a name for members persisted before one was
known, putting a restored roster on a row (never over one that is already there — a live reply that
landed during the read is fresher), matching a wire participant to the member it already was across
LID / PN / bare phone, the person keys groups-in-common is written under, and the same-JID-set test
plus in-place merge that keep the Members list from relaying out (moved from `GroupMetadataReader`).

**Apply.** `GroupMetadataApply` (Core) puts a reply on the rows: the subject into the name cache
under the placeholder and blacklist rules, the synced label onto the row, announce-only, the
account's role, the member count, and the roster replace-or-merge followed by the roster and
membership saves and `NotifyGroupRosterChanged`. A listing is still one UI pass for the whole
account, then one `SchedulePersist`. The reader now hands it `GroupMetadataSnapshot` /
`GroupMemberDraft` (Core models) — `ReadListing` for the listing, `ReadMetadata` for one group.

**Restore.** `GroupRosterRestore` (Core) reads `IGroupRosterStore`, finds the canonical row in
`IChatStateStore.Chats` and fills it if it is still empty.

**Names.** Both flows label members and rows through `IGroupNaming` (formerly `IGroupHost`), which was once implemented by
the client and is now `GroupNaming` (Core) over `ContactDisplayNames`, `ISelfMarkerNaming` and
`IStringResources`. The UI hop, the saves, the row and avatar lookups and the subject cache left
the port one at a time; see phase 4 below.

**Owners.** `GroupMetadataApply` is stateless. `App` registers one instance; `GroupFacade` holds it for the listing, the send-permission refresh and the unresolved pass, plus `GroupQueryWindow` so those callers share the two minutes. The receipt path applies a metadata reply it already fetched through `IGroupService.ApplyGroupMetadataAsync` (same applier — no second construction on the client). The full-size group picture is fetched by the façade.

**What stays:** the usync gate. Member pictures ask through it the same way a 1:1 picture does, and `GroupFacade` takes that lease itself. A miss is still remembered and a transient failure still returns the member to the queue (`GroupMemberAvatarAttempt`).

### 3.6 — the name rules are out; usync is not

Two pieces moved, and they are the two that decide what the user reads.

`ContactLabelSanitizer` answers whether an offered name is a name at all. Four sources feed names in
and they did not agree on what one is: a push name the contact chose, an address-book entry the user
typed, and a label WhatsApp masked before storing. It rejects masked numbers, phone echoes and
self-marker spoofs, and strips marker suffixes. **The self-marker rule is spoof prevention** — a
contact can set their push name to "(You)" and, without it, their messages render as the user's own.
That had no test.

`DisplayNameResolution` is the precedence between the survivors: the device address book (the
overlay, or a stored person whose `PersonSource` is `AddressBook`), then what the app learned, then
the contact's own push name, then the number. The ranking is the `NameSource` enum, and
`NameSourceRules.CanBeReplacedBy` is the single rule for both reading and writing:
`Person.CanWriteName` converts both `PersonSource`s with `ToNameSource()` and asks it, so the store
cannot keep a name the display would outrank. `PersonSource` stays the persisted value (INTEGER in
SQLite; `DirectChat` says nothing about the name, so it maps to `Learned`). Only push names get the
`~`, and not in groups or above message bubbles.

Both take the shape this extraction keeps arriving at. The marker is localized, so recognising it
stays in the UWP head behind `ISelfMarkerNaming`; the lookups stay too, because they warm a SQLite
cache as a side effect. What moved is the decision.

`MergeTransientDirectChatIntoCanonicalAsync` gave up its decision to `TransientChatMerge`, then the
orchestration itself moved to `ChatFacade.CollapseTransientDirectChatAsync` over
`ChatListAliasMerge.CollapseTransientAsync` (timeline + list + name maps). Durable follow-up uses
`IHistoryMessageStore` / `IMessageStore` / `IChatIdentityPersistence`; placeholder-ledger cleanup is
`IMissingMessageChatHost` on the client. The pump only queues the façade call.

The alias follow-up pass itself now lives on `ContactFacade` (`AliasFollowUpQueue` + debounce +
`RunAliasFollowUpAsync`). What remains in `.Identity.cs` that is not this shape is what
`ResolveContactsViaSocketAsync` does with an answer (alias, identity heal, duplicate-chat merge,
avatar fetch) beyond the usync path already on the façade — still 3.6's remaining move, not a rule
to lift out of a method.

**The apply decision left the method.** `ResolvedContactApplyPlan` answers, for one usync
contact, whether to file the alias, merge the two chats (only the first time that LID is seen),
store the push name, and fetch an avatar (only when a listed chat has none). Writing the alias
and healing identity stay on the client; chat merge goes through `ChatListAliasMerge` /
`ChatFacade`. The picture round-trip stays on the contact helpers.

**The usync orchestration moved to the façade.** `ContactUsyncResolver` (in `Contacts/`, built by
`ContactFacade`) now owns everything that decides: which numbers to ask about (`ContactLookupNumber`
against `IJidResolver`), `IUsyncGate`, the session (`ContactDirectory.GetReadySession`), the
`ResolveContactNamesUseCase` call and the per-contact `ResolvedContactApplyPlan`. `ContactNameResolver`
calls it instead of the client, and the number-search fallback (`SearchContactAsync`) now lives on
the façade. `ResolveContactsAsync` and `SearchContactAsync` are gone from `IWhatsAppService`.

What the client keeps is one narrow, façade-only primitive over what it still owns:
`TryFileUsyncAlias` — after `UsyncAliasFiling` accepts and writes both directions, register
with the socket and heal our identity in the auth state. Chat-row surgery is
`ChatListAliasMerge` on the chat façade / usync resolver (see phase 4).
`FetchAvatarForResolvedContactAsync` was a second until the resolver took it: it fetches
through the shared `AvatarFetcher` and stamps the row
`UsyncContactCaches.FindListedChatWithoutAvatar` picks, the same row the fetch decision looked at.

**Three primitives left once their tables were Core types.** The alias table (`JidAliasTable`) and
the push names (`DisplayNameTable`) are DI singletons shared with the client, so `ContactFacade`
takes them directly, plus `ISelfMarkerNaming` (now registered). `StoreUsyncPushName` and
`HasListedChatWithoutAvatar` are `UsyncContactCaches` (Core, 9 tests), called by
`ContactUsyncResolver` against the push-name table and `IChatStateStore.Chats`; the number-search
fallback calls `KnownJidByPhoneDigits` over the two tables itself. All three are gone from
`IWhatsAppService`. The push name is still written with `Set` and raises no names-changed event of its
own; the pass raises it once through `ApplyResolvedDisplayNamesToChatsAsync`, as before. One
difference: a rejected usync name is now logged as one line with its rejection reason, instead of the
client's per-reason wording, which stays on the client's other sanitize calls.

**The hand-built usync query is gone.** `ResolveContactsAsync` had two bodies: the live one over
`ResolveContactNamesUseCase`, and ~300 lines that built the `<usync>` node by hand. The second ran only
when the bridge reported a completed handshake on a disconnected session — and it sent through that
same connection, so it could only fail. It is deleted, with `allowBatchFallback` (its retry flag) and
`IWhatsAppSocket.QueryUsyncAsync` (its only caller). The live path now picks numbers through
`ContactLookupNumber`, which gained `UnmappedLid`: a LID with no known phone pair used to be read as
digits and queried as a number that belongs to nobody.

**Two more rules left before the façade move.** `MeaningfulChatLabel` (with `GroupIdPlaceholder`)
is the single "is this label recognisable?" answer — UWP's `IsMeaningfulChatLabel` and history's
weaker stand-in had drifted on masked phones and phone echoes. `SelfIdentityHealingDecision` is
heal vs purge when usync touches `Me.Id` / `Me.Lid`; the modern contact path and the legacy IQ
parser both call it. Persist / alias removal stay with the caller.

**One more rule came out of the usync half, and it was carrying a bug.** `ContactLookupNumber` (Core,
20 tests) answers "what number do we ask the server about", derived from a JID, a canonical address or
digits a person typed. It was written twice — inside `ResolveContactsAsync`'s filter loop and again in
`SearchContactAsync` — and both copies stripped `'+'`, spaces and hyphens and nothing else, so a number
typed as `(11) 99999-9999` was queried as `+(11)999999999` and came back as "not on WhatsApp".
`PhoneNumberHelper` had read numbers correctly all along; the two just never met. The helper also
returns *why* an address was skipped (`SelfAccount`, `NotDirectChat`, `NoDigits`) so the filter loop's
three `continue`s stop being three silently identical outcomes.

This is the same shape as `MediaDownloadPlan` in 3.3c and worth naming as the pattern for what is left:
the façade moves are too big to verify, but each one has a pure rule buried in it that can leave early,
under test, and the bug tends to be in the rule rather than in the orchestration around it.

**One regression, and it was not in the moved code.** `MediaDerivationService` was registered with
`AddSingleton<MediaDerivationService>()` in 3.3b while its constructor is `internal`, so the container
had no public constructor to call and threw on the first resolution. It is registered by hand now.
The lesson for the rest of phase 3 is about *when* it surfaced, not what it was: `BuildServiceProvider`
runs without `validateOnBuild`, so a broken type-activated registration is invisible until launch —
one launch later than the change that caused it. Anything added to the container by hand is exempt,
which is most of what this extraction adds.

**Thread affinity:** today the client mutates `Chats` on the UI thread; VMs read on the UI thread; `ChatStateStore`’s extra dictionaries are protected by that, not only by the lock. Any code moved to a façade that runs off-thread must use `UpsertChatsAsync` / `UpsertMessagesAsync` (or `IDispatcher`). Do not split 3.9 into half-moves.

**Canonical JID:** introduce `IJidResolver` in phase 1 as a thin wrapper so 3.2 / 3.6 / 3.7 do not all rewrite aliasing at once.

### Pinned / muted from history sync — found on device, fixed

The earlier fix stopped local pin/mute from being *deleted* by whole-row writes. Separately, pin and
mute that arrive on the history-sync `Conversation` itself were never applied: `HistoryChatPreviewBuilder`
ignored `Pinned` and `MuteEndTime`, the preview store skipped chunks whose tip had not moved (so a
pinned chat already on the list never got a notify), and `ApplyTo` from `ChatStore` ran after any
in-memory flag would have been set. Both flags now ride on the preview, re-apply after `ApplyTo`,
and persist per-field.

### Pinned / muted did not survive the sync — found on device, fixed

The pin and mute icons were missing from the list after a sync. The two obvious suspects are both
innocent and worth recording as such: `history_chat_preview` has no pin or mute columns at all, and
`HistoryChatPreviewApplier` never writes those fields, so the preview was not overwriting anything.
The durable home is a separate table, `ChatRow` / `ChatStore`, and app-state did reach it.

**The row was the unit of writing, and that was the bug.** Every caller wrote all four fields —
status, tile pin, chat pin, mute — so each one needed a current copy of the three it did not care
about. `ChatListViewModel` called `ApplyTo(chat)` first and had them; `ChatDetailViewModel`,
`ChatDetailInfoViewModel.SetNotificationsEnabledAsync` and the info-panel tile toggle did not. Muting
a conversation from the info panel therefore stored `IsChatPinned = false` over a stored `true`. The
pin was not hidden, it was deleted — and the next read faithfully restored the absence.

Writes are per-field now (`SetChatPinnedAsync`, `SetWidgetPinnedAsync`, `SetMutedUntilAsync`,
`SetStatusAsync`), each going through the existing `WriteAsync`, which was already a read-modify-write
under the write lock. `ApplyAppStateChatFlagsAsync` writes only the fields the mutation named, which
is the rule `AppStateChatMutation` already followed in memory and the disk side did not.

**`WarmAsync` cleared the cache before refilling it.** `ApplyTo` reads the cache only, so every chat
hydrated inside that window was told nothing was stored. Two callers warm the store
(`WhatsAppService.WarmChatStoreAsync` and `ChatListViewModel.EnsureLocalChatPinsAppliedAsync`), so the
window opened more than once per launch, and during a sync it lands on a list being rebuilt. The cache
is now filled first and stale keys dropped afterwards, so it is never empty.

The read rule moved to `ChatLocalStateApply` (Core, 10 tests), which pins the part that is easy to
get wrong on sight: nothing stored means *say nothing* about pin and mute, because app-state can
apply either before a row exists — but it does mean "no tile", since a tile is on the Start screen or
it is not. Assigning from the store is only correct because writes are per-field now; the two
decisions hold each other up, which is why they are described together here.

Left alone deliberately: `EnsureLocalChatPinsAppliedAsync` and `TryApplyCanonicalPinFallback` are
both patches over this area and are candidates to disappear in 3.9b, once the ownership question is
settled rather than worked around.

### 3.10 — first rule out, and it was a live bug

`HandleDecryptedMessageAsync` is ~760 lines and the pump as a whole is 2,868, so the same approach as
3.3c and 3.6 applies: take the rules out one at a time before attempting the move.

The first one out is `IncomingAttention` (Core, 11 tests): whether an arriving message counts as
unread, and whether its toast would be telling the user something already in front of them. Those
were two expressions a few dozen lines apart, and they disagreed. The unread side tested
`IsActiveChatJid(jid)`; the toast side tested `App.IsWindowVisible && IsActiveChatJid(jid)`. Nothing
calls `SetActiveChatJid(null)` when the window hides — only the detail view unloading does — so a
message arriving while the app was minimised over an open chat was announced by a toast and never
counted. The badge stayed put and the tile count sent with the notification was the stale one.

Two further copies were in the offline replay path (`RecordOfflineReplayChatSummary` and
`ApplyOfflineReplayChatSummariesAsync`), with the same omission and a worse exposure: a replay runs
on reconnect, which is when the app is most likely to be backgrounded with a chat still open.

The type returns both answers together so they cannot drift apart again, and the test that carries
the weight is the invariant rather than the four cases: unless the message is already on screen, it
is both counted and announced. `IsActiveChatJid` on its own is left alone everywhere it asks about
the *view* (hydrate this image, refresh that timeline) rather than about the user's attention.

**`MessageIdIndex` took the duplicate guard with it.** Which ids a conversation already holds is what
stops the same message being shown twice when it arrives from more than one direction — live
delivery, offline replay, history sync, and the merge of two addresses belonging to one contact. It
was a raw `Dictionary<string, HashSet<string>>` the client reached into from eleven places across
five partials, plus four hand-written copies of the same "collect the ids of these messages" loop.
Three of those four read `m.Id` without checking `m` first.

It is a type now (Core, 19 tests) and the dictionary is gone from the client. Two things the tests
pin are worth stating because they pull in opposite directions: the conversation key is
case-insensitive and normalised on the way in, since a caller that forgot to normalise got a second,
empty index for a conversation that already had one — which reads as "this message is new" for every
message in it — while the ids themselves are compared exactly, because they are server-assigned and
folding their case would merge two real messages. `GetOrBuild` hands out the live set rather than a
copy, which the transient-chat merge depends on; that is now stated in the type instead of being a
property of where the field happened to live.

**`MessageRenderReader` next, and it moved whole.** Reading an envelope into "what goes on screen" —
preview line, media kind, the sub-message the download path needs — was 134 lines of cascade in the
client. It needed nothing from the client: `MediaPreviewTag`, `ChatPreviewKind` and
`HistorySyncContentFilter` were already in Core, and Core already references `Unison.Baileys`, so
`Proto` was in reach. `MessageRenderInfo` went with it.

The cascade's value is in the order of its arms, which is why it was worth pinning with tests rather
than moving quietly. A sticker is checked before an image because an envelope assembled field by
field can carry both, and in that case the image is the sticker's thumbnail — check image first and
a stray picture renders instead of the sticker. Returning null likewise means something specific:
not "nothing to show" but "this is not a row", which is how a reaction and a revocation reach the
message they modify instead of becoming bubbles of their own. Sixteen tests, and the only shape
change was the diagnostics: the reader takes an optional log sink instead of calling the client's
`Log` and `Debug.WriteLine` directly.

**`QuotedContext`, and the move is what found the bug.** `ApplyContextInfoExtras` handed back seven
`out` parameters. Six of them — the quote, its id, its kind, its text, the mentions and the forwarded
flag — are decided by the envelope and nothing else. Only the seventh, the quoted author's *name*,
needs the account, the alias table and the directory, so that one stayed behind as
`ResolveQuotedSender` and the other six became a reading with sixteen tests.

Standing the two readings of a quote next to each other is what showed they disagreed, twice.

The live path normalized with `ChatPreviewNormalizer.Normalize`, whose own summary says not to use it
on text that gets persisted — and this text is persisted, as `HistoryMessage.QuotedBody`. It caps at
fifty characters and collapses to one line, which changed nothing on screen, because the strip
normalizes again when it draws. All it did was store a shortened copy of what history sync stored
whole.

The other direction was worse. History sync read quotes through
`HistorySyncContentFilter.ExtractContent`, which knows plain text and four media kinds. A quoted
poll, contact, location or call log fell through as empty text of kind `Text` and the strip drew
nothing — while live, the same quote showed its label. Both now read through `QuotedContext`, so
what a quote says no longer depends on whether the app was open when it arrived.

**The pump scan closed the next four rules.** A pass over `HandleDecryptedMessageAsync` against the
offline replay path found the author strip wiped on reconnect (fixed), then three more that were the
same shape: the offline duplicate fast-path threw away participant/sender upgrades the full path
kept (`DuplicateArrivalEnrichment`); empty/unrecognised content left the missing-message ledger
hanging; and `GetTotalUnreadCount` walked `Chats` off the UI thread so the toast badge could fall
back to 0. Two pure decisions followed out of the same method: `IncomingSenderResolver` (who wrote
it / is it us) and `PushNameAcceptDecision` (whether the envelope's name may enter the map). The
apply pass for offline unread also stopped re-asking `IncomingAttention` — the count was already
decided at record time, and re-asking dropped the badge when the open chat changed mid-drain. Image
hydration during offline replay now matches live (no longer gated on the chat being open).

**`LiveChatPreviewApplier` took the strip write.** Every live, offline-replay, revoke and reconcile
path funnelled through `WhatsAppService.ApplyChatPreviewIfNewer` — staleness via
`ChatPreviewStaleness`, then body / author / kind / send-state / message id / formatted clock.
That method is now a one-line forward; the rule lives in Core next to `HistoryChatPreviewApplier`
(9 tests), including `ResolveKind` (render hint vs domain message). The pump still owns when to
call it; only the write left.

**`IncomingMediaMetadata` took the media key write.** Five private statics on `.Media.cs` copied
proto image/sticker/video/audio/document fields onto `ChatMessage` for the download path. The pump
called them one by one from `MessageRenderInfo` flags; hydrate called the same five. They are one
Core type now (6 tests). The pump calls `Apply(chatMessage, renderInfo)` once. Live toasts left the
client: `IMessageService.NotifyLiveIncoming` on `MessageFacade` (with `INotificationService` + mute
via `IChatStore`). `AttachNotificationService` on the client is gone.

**`OfflineReplaySummaryMerge` took the offline tip fold.** Record and the failed-apply rollback
each had a hand-written copy of "when does this tip win, and which fields travel with it". The model
moved to Core with the merge (6 tests). Rollback now also restores `AuthorPrefix` with the tip —
the old catch left it behind. The client still owns the lock, timer, attention decision and UI apply.

**`IncomingChatMessageSnapshot` closed the GetChatMessage escape hatch.** The pump used to build
`ChatMessage` by hand when MessageFacade was unset — without media flags. Facade is always
attached from `App`; a missing attach now drops the message with a diagnostics write. Snapshot
construction is `IncomingChatMessageSnapshot.FromRender` (4 tests). Separately,
`MissingMessagePriority` owns which placeholder resends are preferred (peer/self vs group).

**`IncomingMediaHydrationPlan` aligned offline and live downloads.** Both paths used to spell the
same sticker/image ifs twice (different order). One plan from `MessageRenderInfo` feeds
`ObserveMediaHydration` so a closed chat during replay still tries the same arms as a live arrival.

**`IncomingPushNameTarget` named who owns the push name.** From-me binds to the account; otherwise
participant, then from — beside `PushNameAcceptDecision`.

**`IncomingEnvelopeAliasHints` collects PN/LID pairs** from sender lid, peer recipient, and group
participant alt before usync. The pump still registers; Core only decides which pairs are formed.

**`IncomingEnvelopeDisposition` named the early exits.** Session control (peer-data response, app-
state-only, placeholder), status broadcast address, and chat controls (revoke, pin) are classified
before the pump queues the matching work — so that order cannot drift when the method is split again.

**`IncomingEmptyContentSkip` / `IncomingLiveStatusAuthor` / `IncomingSelfChatCollapseGate`.** Empty
render clears the missing-message ledger when an id is present; live status author is participant
else self; self-chat + distinct peer LID queues the transient merge. Pump still logs / queues.

**`IncomingLiveDirectChatRouting` closed the rules chapter.** Live DM bucket order (self-chat lane,
from-me recipient / peer PN / peer LID, from-nonself, sender LID, fallbacks) is Core with host
callbacks for canonicalization and self-link. `ResolveLiveDirectChatJid` is a thin forward.

**3.10 rules extraction is closed.** Pure decisions that lived in `HandleDecryptedMessageAsync` (and
the live DM resolver it called) are in Core under tests.

**Apply progress:** live toast/badge/tile is `MessageFacade.NotifyLiveIncoming`; live and offline
list strip (create row, preview, unread, reposition/sort) is `ChatFacade` (`ApplyLiveIncomingChatListAsync`
/ `ApplyOfflineReplayChatSummariesAsync`, shared `UnreadDelta`). Timeline insert/dedupe is
`IMessageService.AcceptIncomingTimeline` over `ChatStateStore.MessagesByChat` (not a bounce into
the client). Persist enqueue is `IMessageService.QueueIncomingPersist` over the shared DI
`PendingMessageQueue` (`IPendingMessageFlushHost` for flush-now / idle timer;
`IChatCatalogPersistence.Schedule` for the catalogue). Offline duplicate name patch is
`IMessageService.EnrichOfflineDuplicateFast`. Revoke is `IMessageService.ApplyIncomingRevocationAsync`
(`MessageRevocationContent` tombstone over `ChatStateStore` + `IBoundMutationGate`). Inbound
pin-in-chat is `IMessageService.ApplyIncomingPinInChatAsync` → `ApplyPinnedMessageStateAsync`.
App-state delete-for-me is `ApplyAppStateDeleteMessageAsync`; chat delete RAM is
`IChatService.ApplyAppStateDeleteChatAsync` (durable half `IAppStateLocalHost.ForgetChatStorageAsync`).
Tip refresh / bulk re-tip / in-memory list reconcile is `IChatService.RefreshChatPreviewAsync` /
`RefreshAllChatPreviewsFromStoredAsync` / `ReconcileChatListFromStoredAsync` (bulk paths still walk
host `MessagesByChat`). Inbound chat content after routing is
`IMessageService.ProcessIncomingChatContentAsync` (render → push-name → sender/attention →
bubble → `AcceptIncomingTimeline` → alias/duplicate/insert). Alias/duplicate/insert follow-ups
also exist as discrete APIs. Live status is `IStatusService.IngestLiveFromDecryptedAsync`.
Envelope tip live/offline is `IChatService.ApplyIncomingEnvelopePreviewAsync`. Pump still owns
session/placeholder routing, alias hints, DM routing/self-collapse, revoke/pin enqueue, reaction
dispatch, the drain loop and the go/no-go before a resend; the wait, send and answer window are
`PlaceholderResendDispatch`. Which way an arriving message lands is `IncomingTimeline`. Offline
replay tips are `ChatFacade`'s. The missing-message ledger owns row transitions under the same
lock; `IncomingMessageQueue<T>` owns the lanes, generation, stage and timeout retry list.

### Pure-rule nibble (ongoing, before façade moves)

When a method is still too large to move whole, lift the pure decision first: write tests that
match today’s client code, add a Core helper, forward with a one-liner, commit extract then wire.
Recent lifts from `WhatsAppService` / façades include diagnostic JID masking, unix milliseconds,
self push-name capture, MessageKey alt-JID scan, outgoing message ids (`OutgoingMessageId`),
whether a text send may still promote a pending bubble (`OutgoingSendSettlement`), which duplicate
chat row a local send repositions (`DuplicateChatRowPick`), the text fallback for a fatal stream
code (`FatalStreamCodeText`), which log lines a replay may drop (`ReplayDiagnosticNoise`), the text-send retry (`OutgoingTextSendRetry`:
two attempts, invalidate only on a broken pipe, stop on a refusal), catch-up continue + freshness stale,
placeholder recovery trigger, explicit logout stream codes, list enrichment phases, user-JID
shape, on-demand sync type, group avatar fallback, the parent and community addresses that fallback
asks (`GroupAvatarFallbackJids`), relink disconnect reasons, WebP cache URI,
web-message status map, the open chat's in-memory window (`ActiveChatMessageWindow`) and the
dedupe pass's survivor order and row merge (`DuplicateChatRowMerge`). Orchestration stays behind the forward.

The last two each carried a defect of a shape already recorded here. The window chose its newest
messages with `OrderByDescending` on the raw `DateTime` — the fourth sighting of the un-normalised
comparison — and its `GroupBy` keyed id-less messages on a fresh `Guid`, so a pinned message with no
id was kept twice. The dedupe merge was written twice, once per branch, and the PN/LID branch copied
the text and the clock but not `LastPreview` or `LastMessageId`: the half-preview `TransientChatMerge`
already documents. Both branches now go through one `Absorb`. The rest of the per-group work — moving
messages between keys, carrying the push name, filing the alias — moved to `DuplicateChatFold` once
the timeline had a Core owner (see below). The client keeps the UI thread, the list removals and the
events.

The sender written on a `WebMessageInfo` is `WebMessageParticipant` (key participant, then
envelope participant, then the alt-JID scan; never a group or broadcast address). The resend path
and the history store both read it now; `HistorySyncContentFilter.ResolveParticipant` keeps its
own gates (group only, never own, never the chat). Fixed on the way: the history store did not
scan alt JIDs, so a group message carrying its sender only there had one live and none in SQLite.
Edge kept, not endorsed: the scan skips only field 4, so a direct envelope with no participant
answers its own `remoteJid` (the history gate drops it; the resend path passes it on).
`ResolveHistorySenderName` left with it: it had no caller.

Writing an accepted pair is `AliasPairFiling` (19 tests). It was three hand copies: the live path
(`TryRecordAliasMapping`) and the usync answer (`TryFileUsyncAlias`) file both ways and report news
from the LID side only; the duplicate-row merge in `DeduplicateChatsAsync` files the LID side and
leaves the phone side alone when it already points at an `@lid`, because two rows looking alike is
our inference and the server's pairing outranks it. The two self-pair writes in `.Connection.cs`
stay as they are (phase 4).

Reading one contact out of an app-state snapshot, history chunk or participant list is
`ContactUpdateFiling` (15 tests). `ApplyContactUpdatesAsync` now just loops: the `Id` fills whichever
half (LID or phone) the update left out, a pair is reported whenever both halves are known (named or
not), the name is `Name` else `Notify`, and a named contact is filed under its `Id` plus each other half
that differs. The client's private `IsLidJid` went with it. Fixed right after the move: the LID test
read the raw text, so a padded `"x@lid "` was taken for a phone (now `JidHelper.IsLidJid`, which trims),
and a whitespace-only `Name` blocked the `Notify` fallback and left the contact unnamed.

Folding every non-canonical message-cache key into its conversation is
`ChatMessageCache.CanonicalizeKeys` (5 tests). `DeduplicateChatsAsync` walked `ChatJids()` and called
`MergeInto` per key by hand; it now passes `GetCanonicalJid` and takes the count back. A blank
canonical form leaves the key alone, and an alias holding no list is not counted, as before.

*The store's own message writes:* `ChatStateStore.UpsertMessagesAsync` and `RemoveMessageAsync` edited
the lists by hand, bypassing the cache — no id index, and a raw `DateTime` sort (the sixth sighting).
They go through `ChatMessageCache.Upsert` / `.RemoveMessage` now, which sort through `ChatMessageOrder`
and keep `Ids` in step. Neither has a caller yet; they are what a façade will write through.

*Transitional seams closed:* `NotifyChangedExternally` is gone. Its two callers wanted two different
things behind one null check — the client's message-change relay now calls the new
`IChatStateStore.NotifyMessagesChanged`, and `RaiseDisplayNamesUpdated` calls the existing
`NotifyDisplayNamesChanged`. `HistoryFacade` reads the timeline through `GetMessages` (a snapshot
rather than the live list it used to hand to an async request) and takes `IChatStateStore`.
`MessagesByChat` is the last transitional member on `ChatStateStore`. The client no longer
exposes it; remaining client reads go through `_chatState` (counts via `GetMessageCount`,
on-demand pages via `GetMessages`, clear/evict via the cache on the store).

*The last three hand edits:*
- `LoadInitialMessagesAsync` built the window itself: pending-persist messages overlaid by the held ones (held wins by id, id-less kept from both), sorted, trimmed, `Set`. That is `ChatMessageCache.RebuildWindow`. Pending state is applied by the client to the list it hands back, as before.
- The load-more filter (skip known ids, `Take` the room left) is `AddMissing(chat, page, maxToAdd)`. The limit counts only messages that actually enter, so duplicates inside one page no longer use up room.
- The pinned-target insert ("unless the id is already held") is `InsertIfAbsent`.

What the client still does with a timeline is read it: find a message by id, count, take the newest, and hand the list to the reaction mapper.

Filtering a candidate for the account's own name is `SelfNameCandidate` (marker through
`ISelfMarkerNaming`, as `ContactLabelSanitizer` does). Fixed on the way: the self path compared the
candidate with the JID user part as an exact string, so `+55 11 99999-0001` passed as a name — and
`ResolveEstablishedSelfName` trusted it — while contacts already rejected it by digits with
`ContactLabelSanitizer.IsPhoneEcho`. Both paths now use that test.

Receipts and pins for a message not in memory are held by `PendingMessageState` (`Unison.Core/State`)
and applied once when the message next loads; the chat's status policy comes in as a function. The
client lost two dictionaries, the pin state class and the store method.

The group recipient-count cache is `GroupRecipientCountCache` (`Unison.Core/State`, 9 tests): the
member count a group receipt has to reach, kept per group for 30 minutes. It was the last thing under
`_messageStateLock`, so the lock left with it. The metadata query and the send-permission refresh it
triggers stay in the client. Edge kept, not endorsed: nothing expires an entry early, so a member who
joins or leaves inside the window is not counted until it lapses.

An app-state pin wrote the row's keys and then the wire address's keys, and the two are usually the
same conversation, so most keys were upserted twice. `ChatPinStoreKeys.Merge` (4 tests) joins them
without repeats and the client writes the merged list once. Two client-only dedupes went with it, no
rule involved: the app-state find-or-add row (`GetOrAddChatRowsForAppState`, which the read-state and
chat-flag paths had each written out) and building a list strip from a message and applying it
(`ApplyMessageAsChatTip`, three copies across `.AppState.cs` and `.IncomingPump.cs`). The other three
places that create a row stay separate: they differ in the name context and insert at the front.

**Façades read shared state, not the client.** Seven members left `IWhatsAppService` because the
façades now take the Core objects those members only forwarded to:

- `Chats`: the contact helpers and `ChatFacade` take `IChatStateStore`, the same collection.
- `PhoneContactNamesByJid`: `ContactFacade` and `AddressBookOverlay` get `ChatStateStore.AddressBookNames`.
- `GetTotalUnreadCount`: `ChatFacade` tallies over `IChatStateStore.Chats`.
- `GetLiveMessages`: replaced by `IChatStateStore.GetConversationMessages`, over
  `ChatMessageCache.CopyConversation` (4 tests). It tries the exact address first, then the first
  conversation with the same canonical form.
- `GetChatRowsForCanonicalJid`: `ChatRowIndex` is a DI singleton shared by the client and
  `ChatFacade`, with `FindConversation` (2 tests). It has to be one instance: the client re-keys rows
  in place and invalidates the index by hand, and a second index would never hear about that.
- `HasResolvedContactName`: replaced by `PushNameLookup` (Core, 4 tests). The client uses it too and
  passes its rejection log as the decision callback.
- `SeedChatMessagesInMemoryAsync`: replaced by `IChatStateStore.AddMissingMessagesAsync` (2 tests).
  It adds only what the timeline lacks, on the UI thread, and raises nothing, as before. One edge:
  the store's dispatcher runs inline when there is no window, where the client's dropped the seed.

The newest message time history freshness reads is `NewestStoredMessage.FindUtc` over the timeline
snapshot and the list strip (cold start has tips but no timelines), each value through
`ChatMessageOrder.ToComparableUtc`. The client passes `MessagesByChat` and `Chats` and its filters.

What a catalog save takes on the UI thread is `ChatCatalogSnapshot` (7 tests): the
`history_chat_preview` rows, mapped there through `HistoryChatPreviewApplier.FromChatItem`, and each
listed address once for the contact, phone-name and alias tables. `PersistDataAsync`,
`PersistCriticalSuspendStateAsync` and `PersistChatIdentityStateAsync` all take it. The first two
used to copy the live `ChatItem` references and map them after leaving the UI thread. The identity
save, which runs after every dedupe and transient merge, hand-built a `ChatItem` per row and left out
`LastMessageId`, the mentions, the group sender and every status but archived. The store keeps those
fields when the tip has not moved, but a row the identity save inserted, or whose newer tip it wrote
first, went to disk without them. It also copied every group roster for a mapping that never reads it.

What a burst of new alias pairs leaves owed is `AliasFollowUpQueue` (`Unison.Core/State`, 9 tests),
the same seam as `PersistScheduler`: it holds the avatar targets (each once, in order) and the latest
source, takes them all when a pass begins and refuses a second pass while one runs. The host keeps
the 750 ms debounce. The client lost three fields: the running flag, which sat outside the lock that
guarded the rest, and the source, which the re-arm path read without that lock. Fixed on the way: a
pass that threw had already cleared its targets, so those rows kept a missing picture until the next
pair for the same contact. They are restored now, without an automatic retry, as with the catalog save.

The dedupe pass's per-group loop is `DuplicateChatFold` (`Unison.Core/State`, 12 tests). Every
collaborator was already a Core table — `ChatMessageCache`, `DisplayNameTable`, `JidAliasTable` —
so the loop moved whole; it edits rows in place and reports the ones to remove, and
`DeduplicateChatsAsync` removes them, invalidates the row index and canonicalizes cache keys as
before. Fixed in the move: each row's newest message was measured *after* the secondary's messages
had moved under the survivor, so the secondary always read as empty (the old comment admitted it)
and an unstamped preview that was newer could never win. Both sides are measured first now. Left
as it was, not endorsed: with fewer than two rows the client rewrites the lone row's address to its
canonical form, which a list of two or more never does for its non-duplicate rows.

Writing resolved names onto the list is `ResolvedNameApply` (10 tests). `ChatNameReplacement` had
already taken the "should this title change" question; both the live pass (`ChatNameRefresh.ApplyAsync`)
and the startup titles-only pass (`NormalizePersistedTitlesAsync`) call it. The pass takes the name
lookup and the meaningful-label check as functions, renames members only when asked, and returns how
many labels changed. The startup pass still schedules persist when titles changed; both raise
`Applied` so the client can fire the names-changed event. A member keeps the weaker rule it had: any
non-blank name that differs is written, with no check that it is recognisable.

**Another sweep of the main file.** Reading a partial `ChatUpdate` (does it speak about pin or mute,
and the legacy shape that carried only a sort key or a deadline) is `AppStatePinMapping.FromChatUpdate`
/ `AppStateMuteMapping.FromChatUpdate`. When a pin-in-chat starts and expires is `MessagePinWindow`;
a local pin with no duration no longer expires the moment it is made. The repair of rows an old build
left showing "[Message Deleted]" is `LegacyDeletedPreview`, and its blank-out goes through
`ChatPreviewTip.Clear`, which also drops the stale `LastMessageId`. The canonical row index is
`ChatRowIndex` (`Unison.Core/State`): built on demand, published only when nothing invalidated it during
the build, copies handed out. Moving our message's status forward across every timeline holding it is
`ChatMessageCache.ApplyOutgoingStatus`. The badge total is `ChatUnreadTally.TotalAcrossConversations`.
The self base-name precedence (phone contact, WhatsApp cache, credentials, current name, number) and
the "established self name" gate are `SelfDisplayNameResolution`.

The reconcile pass's tip choice is `ChatPreviewTip.IsReconcileCandidate` / `PickNewestReconcileCandidate`.
A revoked message stays the tip on every path, as on the phone. A revoke is the `IsRevoked` flag
(`ChatMessage` and `history_message`) with an empty body; nothing below the UI writes or matches
"[Message Deleted]" any more. The list strip reads it as `ChatPreviewKind.Revoked` and the bubble as
`IsRevoked`, and both show the localized label. The marker string survives only to read data from older
builds: an unflagged marker body is never a tip, and `RepairLegacyDeletedPreviewsAsync` fixes a strip
still holding it once, because nothing writes it again.

Two edits that bypassed the cache are gone: the revoke apply removed the old synthetic envelope row with
`List.Remove` (the id stayed in the index), and the app-state delete wrote the remove-plus-forget pair by
hand. Both use `ChatMessageCache.RemoveMessage`; the revoke target is found with `AliasLinkedMessageFind`.

**Fixed in the same sweep:** `CheckAndMergeDuplicateChatsAsync` only moved messages and removed the LID
row, so a contact paired by usync or by a new chat lost the LID row's pin, mute, unread count, newer
preview and picture. When the pair is filed (both callers file it first) it now runs the dedupe pass,
which folds through `DuplicateChatFold` and persists identity. The bare message move stays for a pair
the table does not know.

### History on demand — the bookkeeping left first

The on-demand history cluster (per-chat `HISTORY_SYNC_ON_DEMAND`, the paginated
`FULL_HISTORY_SYNC_ON_DEMAND` catch-up, its watchdog and timeouts) is headed for `HistoryFacade`,
but it reaches the socket, the reconnect path, the catch-up banner and the enrichment gate, so it
moves in slices. **Slice 1 is done:** `HistoryOnDemandRegistry` (`Unison.Core/State`, 33 tests) owns
what was asked and is still owed — six collections, three full-history fields and a nested state
class that four partials reached into, some of them (`AppState`, the frugal-budget and enrichment
reads, the avatar-fetch deferral) outside the lock. The "forget this request" rule was written out
three times (the shared helper, an inline copy in the timeout, the stale-flags branch); it is one
method now, and the 479 / three-minute backoff moved with the ack.

Behaviour kept, two edges recorded rather than endorsed: a failed chat send keeps its anchor, so the
same page is not retried until the next connect; deleting a chat leaves a request already out to be
answered or time out. Deliberate differences: claiming the full catch-up is atomic (it was a
check-then-set on a volatile flag); a failure before an id is registered no longer drops the
*previous* request's entry; the quiet-SQLite hand-off finds the pending full request the same way
progress stamping does (full id, then repair id); the per-chat attempt counter is gone (written,
never read); and the banner is ended outside the lock instead of inside it.

**Slice 2 is done:** `HistoryCatchUpContinuation` (`Unison.Core/State`, 10 tests) owns where the
paginated catch-up stands between lots — round, stagnant count, tip watermark — and answers both
"what next" questions: after a quiet lot (wrapping `HistoryCatchUpContinueDecision`) and after an
offline drain with a fresh tip (continue, retry once more, or finish). The "catch-up is active" rule
(banner up *or* a round in progress) was written out five times across two partials; it is
`IsHistoryCatchUpActive()` over the type now. Quiet lots and empty drains still share one stagnant
count, as before. Deliberate difference: the three fields were plain, unlocked fields read and
written from the watchdog, the SQLite finalizer and the offline-drain callback; each decision now
reads and updates them under one lock, and `catch-up-finish` logs the values it reset rather than
re-reading them.

**Slice 3 is in steps.** *Chat page request:* `HistoryOnDemandDispatch` (`Unison.Core/State`)
sends the per-chat `HISTORY_SYNC_ON_DEMAND` over `IHistoryOnDemandSocket`, which the client
implements explicitly (the `PlaceholderResendDispatch` pattern). Anchor, gate, register, send and
give-the-slot-back-on-throw are one tested method; the client only hands it the timeline.
`UnixTime.ToMilliseconds` replaced the client's private copy. *Full-history request:* the same
type claims, registers and sends `FULL_HISTORY_SYNC_ON_DEMAND` and answers with a
`HistoryFullRequestOutcome`; on `Sent` the client still logs, raises the banner and the bounce
event. Deliberate difference: an exception in that post-send work used to release the claim of a
request that had already gone out; it now propagates instead of pretending the send failed.

*Catch-up watchdog:* `HistoryCatchUpWatchdog` (`Unison.Core/State`) owns the poll loop, the idle and
age clocks and the clear at the abort window, with the clock and delay injectable so the loop is
tested without waiting. It answers how the watch ended — cleared between polls, cleared by someone
else at the abort tick, or idle and cleared by the watch — and only the last one hands the lot to
`DecideCatchUpAfterIdleBatch`. The client keeps the `Task.Run`, the journal lines and the decision.

*Per-chat ask moved to `HistoryFacade`.* `HistoryOnDemandRegistry` is one DI singleton, shared by
the client (acks, timeouts, load-more) and the façade. `EnsureChatPageAsync` /
`IsChatPagePending` read the timeline from `ChatStateStore` and send through the same dispatch;
`MessageFacade` forwards there. `EnsureHistoryOnDemandAsync` and `IsHistoryOnDemandPending` left
`IWhatsAppService`. Load-more still asks from the client, because it carries the trigger reason
the façade does not.

*Accepted-request timeout:* `HistoryOnDemandTimeout` (`Unison.Core/State`) arms once per request,
picks the chat or full-history window, waits (delay injectable) and expires the request only if it
is still current, handing back the expired copy — null when the phone answered in time. The client
keeps the `Task.Run`, the timeout log, the `catch-up-hard-timeout` journal line and the hand-off
to `DecideCatchUpAfterIdleBatch`.

**Slice 3 is closed.** Idle decide and adjacent FULL_HISTORY moved to `HistoryFacade`
(Phase 4 wave 1). Soft-reconnect fallbacks stay on the client behind
`IConnectionSoftReconnect` / `IHistoryCatchUpHost` until wave 2 owns ConnectAsync.

### Phase 4 — What remains is connection

`.Connection.cs` is this phase's target file. **No further pure-rule extraction there:**
`ConnectionHealthPolicy` already left; connect / resume / pairing / broker / suspend are lifecycle
orchestration. Do not nibble that partial until the rest of phase 3 stops needing the god client.

`IWhatsAppService` is gone (last DI alias retired). UI stays on `IConnectionService`;
the client remains the concrete host for wire ports. Target connection surface:

- `InitializeAsync` / `ConnectAsync` / `ResumeAsync` / `EnsureConnectedAsync` / `Disconnect`
- `IsConnected` / `IsTransportReady`
- `IsRegisteredAsync` / `ClearSessionAsync` / `NotifyServerLogoutAsync`
- Suspend / broker / `ReleaseMemoryAsync` / `ShutdownAsync`
- Raw events **for façades only**

Drop `RunOnUiThreadAsync` (`IDispatcher`) and `SchedulePersistPublic` (the writer persists). `App` lifecycle calls `IConnectionService`, not `App.GetWhatsAppService()`.

**`RunOnUiThreadAsync` is gone.** The client calls its `AttachedUiDispatcher` directly, and
`IGroupHost` no longer carries a UI hop: `GroupMetadataApply`, `GroupRosterRestore` and
`GroupFacade` take `IDispatcher` in their constructors. The tests count hops on a
`CountingDispatcher` instead of the fake host. The debounced save left the port the same way:
`GroupMetadataApply` and `GroupFacade` call `IChatCatalogPersistence.Schedule`, like the other
façades. The roster and membership saves left too: `GroupRosterWriter` (Core) owns
`IGroupRosterStore` and `IPersonStore` and writes in the background, so a failed save is logged
and never reaches the UI pass. `FindGroupRowName` and `CacheGroupSubject` went next: the applier reads
the group's rows from `ChatRowIndex` and writes the subject into the push-name `DisplayNameTable`
(`ContactNames`) itself. So did `HasMeaningfulGroupLabel`, which only combined that cache with the label rule. The 1:1 avatar lookup is `ChatRowAvatarLookup` (Core), which tries the member's JID, phone and LID on `ChatRowIndex`. The name rules went last: `GroupNaming` (Core) implements the port over `ContactDisplayNames`, so the client no longer implements `IGroupHost` and `WhatsAppService.GroupHost.cs` is gone. The client builds its own `GroupNaming` for the receipt-path applier, the way it already builds `JidResolver`. With nothing of the client left behind it, the port was renamed `IGroupNaming`.

**Step 1 is done: the app lifecycle goes through the façade.** `IConnectionService` gained
`ResumeAsync`, `ReleaseMemoryAsync`, `TransferActiveSocketToBrokerAsync`, `PrepareForSuspendAsync`
and `ShutdownAsync`. `ConnectionFacade` forwards them to the client the way it already forwarded
startup. Before the client is attached, it answers as if nothing were there: the broker transfer
answers false and the rest do nothing. `App.GetWhatsAppService()` is gone; `App` resolves
`IConnectionService`. The client's reconnect, resume and broker logic did not change.

**Wave 1 closed (callers + catch-up).** `MediaAvailability` asks
`IConnectionService.EnsureConnectedAsync`. `IConnectionSoftReconnect` exposes soft
auto-reconnect and catch-up-continue. Idle-lot decide and adjacent FULL_HISTORY live on
`HistoryFacade.NotifyCatchUpIdleBatch` over shared `HistoryCatchUpContinuation` and
`IHistoryCatchUpHost`.

**Phase 4 orchestration closed.** Ensure / resume / pairing stage-2 / broker /
soft-reconnect / lifecycle / Connect gates + prepare + open / close disposition /
health-monitor live in WhatsAppClient helpers + hosts. `ConnectAsync` body is
orchestration over those helpers; `BuildSocketBridge` /
`SocketEventHandlerWiring` stay UWP (`IWhatsAppSocket`); broker consume gate is
`BrokerReconnectConsumePolicy`; startup alias restore is `PersistedAliasRestore`;
linked-account gates / on-demand health step / soft-reconnect pairing helpers
as above. **Soft-reconnect slot state** (lock + `isReconnecting`, schedule/claim/
complete/pairing-slot) is `SoftReconnectCoordinator` over `ISoftReconnectIoHost`
(client I/O only). **Health-monitor + periodic FULL_HISTORY safety CTS /
`Task.Run`** is `ConnectionHealthMonitorOwner` over `IConnectionHealthMonitorIoHost`
(loop bodies only). **Post-replay + post-message enrichment CTS / once-per-session
CAS** is `PostSyncMaintenanceOwner` over `IPostSyncMaintenanceIoHost` (quiet /
memory / façade bodies only; `ShutdownAsync` calls `CancelAll`). **Connect +
resume/ensure locks** are `ConnectionGateLocks`. **Hydration day-retry CTS /
day attempts / day-anchor** is `HydrationDayRetryOwner` over
`IHydrationDayRetryIoHost`. **Init connection-state lock** is `InitGateOwner`. **Persisted-UI load lock** is
`PersistedUiLoadGate`. **Deferred-startup once-per-session CAS** is
`DeferredStartupOnce`. **JID alias persistence** (register / try-record / persist /
pending-socket flush) is `JidAliasPersistence` over the four alias dispatch hosts;
`ISocketAliasRegistrar` stays on the client (pair + bulk + identity heal).
**Immediate chat-identity flush** (catalogue preview + contact/phone names +
aliases after list fold) is `ChatIdentityPersistence` over
`IPersistChatIdentityStateHost`; preview upsert is shared
`ChatCatalogPreviewPersist` until full catalogue persist leaves.
**Durable chat-delete I/O** (preview tombstone / history delete / RAM cache /
catalogue persist) is `ForgetChatStorageHost` + `AppStateLocalHost` over
`ForgetChatStorageDispatch`. **Freshness repair stamps** are
`HistoryFreshnessRepairPersistence`. **Live SQLite upsert** is
`LiveMessagePersist`. **Persisted find + sent-retry rebuild** is
`PersistedMessageLookup`. **Active-chat cache unload + sync clear** is
`ActiveChatCacheSideEffects`. The incoming pump drain loop is
`IncomingPumpDrainDispatch` (host still owns typed queue + handle). Offline pending-
notification release is `OfflineReplayRelease` over `IOfflineReplayReleaseHost`
(wire still attaches the event on the client). Thin-wire dispatches:
`ConnectionUpdateDispatch`, `SocketErrorDispatch`, `HistorySyncWireApply`,
`SessionInitializedDispatch`, `OfflineReplayRelease`, `QrCodeWireDispatch`,
`AuthStateWireDispatch`, `InboundWireNodeDispatch`, `DecryptedMessageWireDispatch`,
`SocketFacadeWireDispatch`, `PostReplayMaintenanceDispatch`,
`PostMessageEnrichmentDispatch`, and `OfflineReplayFlushDispatch` (each over a
host port).

**The open conversation is shared state.** `ActiveChat` (`Unison.Core/State`, DI singleton over
`JidAliasTable.GetCanonicalJid`) holds the conversation the user has open by its canonical
address. `ChatFacade.SetActiveChatJid` opens it there; `IWhatsAppService.SetActiveChatJid` is
gone, and so is the unused `IsChatCurrentlyOpen`. The client reads `ActiveChat` wherever it read
its own field (unread counts, timeline refreshes, the status banner, memory release) and
subscribes to `Changed` for the two side effects it kept: dropping the previous conversation's
messages from memory and clearing the list banner when a conversation opens. Both still run on
the caller's thread, after the new conversation is already open, as before.

**The sync line is shared state too.** `SyncStatusChannel` (`Unison.Core/State`, DI singleton over
`ActiveChat` and the `ShowSyncStatus` setting) owns the one status line the chat list shows and
the rules that were inside the client: a clear during history catch-up puts "Synchronizing
history…" back, list enrichment phases stay off an open conversation, and with the setting off
every status leaves as a clear. `GroupFacade`, `HistoryFacade`, `ContactNameResolver`,
`ChatAvatarPolicy` and `SettingsViewModel` raise on it directly; `HistoryFacade` relays its
`Changed` as `SyncStatusChanged`. `IWhatsAppService.RaiseSyncStatus` and `OnSyncStatus` are gone,
and `SettingsViewModel` no longer takes the client. The client keeps when history catch-up begins
and ends — that follows its FULL_HISTORY request — and only latches the banner on the channel.

**"Should enrichment wait?" is asked of `SyncWorkGate`.** `SyncWorkGate` (`Unison.Core/State`, DI
singleton over `HistoryOnDemandRegistry` and a lazy read of the socket's `IsAwaitingInitialSync`)
answers `IsReplayDrainActive` and `ShouldDeferAvatarFetch`. The replay-drain flag lives there now;
`.Connection.cs` still decides when it is set (auth loaded and registered) and cleared (offline
replay released) and only reports it. `GroupFacade`, `ContactNameResolver`, `ChatAvatarPolicy` and
`GroupRosterPolicy` ask the gate; both members left `IWhatsAppService`. The client's
`_historyBackfillActive` went with it: nothing ever set it, so it and the matching
`PlaceholderResendDeferral` argument were removed.

**Round trips through the façades are gone.** The incoming pump called `IMessageService`
(`AcceptIncomingTimeline`, `QueueIncomingPersist`, `ApplyIncomingRevocationAsync`,
`ApplyIncomingPinInChatAsync`) and `IChatService` (the offline-drain repair) only for
`MessageFacade` / `ChatFacade` to call straight back into the client. The pump now calls its own
methods; the members left both `IMessageService` and `IWhatsAppService`. `HistoryFacade` completes
on-demand chats on `HistoryOnDemandRegistry` itself, so `CompleteHistoryOnDemandForChats` left the
contract too. What the pump still asks of the façades (`ApplyLiveIncomingChatListAsync`, the
offline tips, reactions, notifications, media hydration) is real façade work.

**Names are answered by `ContactDisplayNames`.** (`Unison.Core/State`, DI singleton over
`JidAliasTable`, the push-name and address-book `DisplayNameTable`s, `IPersonStore` and
`ISelfMarkerNaming`.) It holds the lookups that feed `DisplayNameResolution` and the self-name
rule that were in `.Identity.cs`. The client binds it once, like `JidAliasTable.BindSelf`, with
the account's id, LID, credentials name, current name and phone, plus its label-decision log.
`ContactFacade.ResolveDisplayName` answers from it; the client's own `ResolveDisplayName` delegates
to it for its internal callers. `IWhatsAppService.ResolveDisplayName` is gone, and `ChatFacade`
names rows through `IContactService` only. Writing names (push names, the person store, the
overlay) did not move.

**Identity and transport readiness no longer go through the client.** `JidAliasTable.BindSelf`
takes the account's phone too and the table answers `Self` (`Id`, `Lid`, `Phone`; null before
pairing). `JidResolver` reads it there and lost its `IWhatsAppService` parameter;
`IJidResolver.Self` is identity only, and the profile (name, picture) stays with
`ProfileFacade`. `IWhatsAppSessionProvider.IsTransportReady` asks the current socket whether the
handshake finished, the same test the client made; `ProfileFacade`, `DebugSendService`,
`ContactNameResolver`, `ChatAvatarPolicy` and `GroupRosterPolicy` ask it, and the member left
`IWhatsAppService`. `AddressBookOverlay`, `ChatAvatarFetch` and `GroupRosterPolicy` no longer take
the client at all.

**The sync budget and first-sync progress are shared state.** `InitialSyncProgressState`
(`Unison.Core/State`, DI singleton) holds whether the first sync is active and its counters, and
raises `Changed`; the client still decides when it starts and ends and calls `Publish` from
`PublishInitialSyncProgress`, which keeps the diagnostics line. `SyncBudget` (DI singleton over the
chat store, that state, `SyncWorkGate`, `HistoryOnDemandRegistry`, a memory-headroom `Func` and the
Mobile flag) answers `IsFrugal`; the rule itself is the pure `SyncBudget.PreferFrugal`, tested.
`HistoryFacade` and `ChatAvatarPolicy` read both directly. `IsInitialSyncSafeMode`,
`PreferFrugalSyncBudget`, the two counters and `OnInitialSyncProgress` left `IWhatsAppService`.

**Presence and the verbose switch no longer go through the client.** `MessageFacade` subscribes to
presence on `IWhatsAppSessionProvider.Socket` when it is connected, the same test the client
made; `PresenceSubscribeAsync` left `IWhatsAppService`. The verbose trace switch is a persisted
setting, not connection state: `VerboseLog` (static, `Unison.Uwp/Services`) holds it, loads it and
persists it, `WhatsAppService.Log` reads it, and `RuntimeDiagnosticsService` sets it directly.
`VerboseLogging` and `SetVerboseLogging` left the contract.

**The account's own profile is shared state.** `SelfProfileState` (`Unison.Core/State`, DI
singleton, tested) holds the name and picture the app shows for the logged-in account, applies
the rule that the account's own phone digits are never kept as a name, and builds the `Profile`
snapshot, falling back to the credentials name and picture. The client binds it once with a
reader over its auth state, the phone-echo rule and how a new picture is persisted; it still writes
the name when a push name or the credentials arrive, and restores the saved picture without
saving it again. `ProfileFacade` reads and writes it directly and relays its `Changed`.
`CurrentUserName`, `CurrentUserAvatar`, `CurrentProfile` and `OnUserProfileChanged` left
`IWhatsAppService`.

**Two small leftovers moved.** Whether the saved chat rows are still being read back at startup
is the chat list's own state: `IChatStateStore.IsLoadingPersistedChats`, set by the client around
the read through `ChatStateStore.BeginPersistedChatsLoad()` (disposed even when the read fails).
`HistoryFacade` reads it there. The health journal samples the client through
`IRuntimeSnapshotSource` (Core), a narrow port the client implements explicitly like
`IOutgoingMessageHost`; `RuntimeDiagnosticsService.AttachSnapshotSource` replaced
`AttachWhatsAppService`. `IsLoadingPersistedChats` and `GetRuntimeDiagnosticsSnapshot` left the
contract.

**Message pins are sent by the façade.** `MessageFacade.SetMessagePinnedAsync` reconnects,
builds the key, sends on `IWhatsAppSessionProvider.Socket` and works out the pin window; then it
hands the local half to the client, the same split as chat pins. `IWhatsAppService.SetMessagePinnedAsync`
became `ApplyMessagePinAsync` (timeline, pending pin, SQLite and replay queue; no wire).

**Sync workload is a strategy per memory tier.** `SyncWorkloadStrategy` (`Unsupported` /
`Low` / `High`, from `DeviceCategory`) owns message caps, SQLite sub-batch size, whether history
thumbs materialize during sync, chat-list release size, avatar batch size and the
`HistorySyncConfig` storage quota, plus catch-up rounds, the finalize quiet window, group
metadata/roster batches, `LidMappingPersistBatchSize` / `LidMappingPersistDelayMs` (Unsupported
smallest lots), and `SyncPacing` (frugal and generous values; `SyncBudget` picks which).
Unsupported is always frugal in `SyncBudget`.

**LID disk writes are sequential after history.** `LidMappingStore` write-behind keeps pairs in
cache + a pending queue while `ILidMappingDiskPersistGate` is held; `HistoryFacade` releases on
initial-sync `IsCompleted` or when warm decides there is no first-sync hold. `LidMappingPersistHost`
arms the tier debounce; suspend flushes the queue. Not in parallel with first history SQLite.

**The first history sync holds the list on every platform.** `ChatListSyncHold` answers from
`IHistoryService.IsAwaitingFirstHistorySync` alone. While it holds, the chat `ListView` is not
created (`x:Load` on `ChatListViewModel.IsChatListMaterialized`), the per-chunk preview reconcile is
skipped and row property changes do not post refreshes; the completed event releases the list and
runs one full reconcile, which applies its tips to the UI thread in slices. Later syncs release
progressively.

**Renaming rows after names arrive is `ChatNameRefresh`.** (`Unison.Core`, DI singleton over the UI
dispatcher, `IChatStateStore`, `ContactDisplayNames` and `ISelfMarkerNaming`.) It runs
`ResolvedNameApply` over the rows in one UI pass and raises `Applied` inside that pass. `GroupFacade`,
`ContactFacade`, `ContactNameResolver` and `ContactUsyncResolver` call it directly, and
`ApplyResolvedDisplayNamesToChatsAsync` left `IWhatsAppService`. The client still runs it from its own
paths, and on `Applied` writes the diagnostics line and raises `OnDisplayNamesUpdated`, which
`ContactFacade` relays as `DisplayNamesUpdated`.

**Clearing unread is the chat façade's.** `ChatFacade.ClearUnreadForChatAsync` zeroes every row of
the conversation on the UI thread (`ChatUnreadTally.Clear`, tested), updates the badge and the
shortcut, and saves only the rows that changed through `IChatCatalogPersistence.PersistRows`.
It takes `IShortcutService` by constructor instead of reaching `App.Services`, and the member left
`IWhatsAppService`.

**A new chat row is the chat façade's too.** `StartNewChat` moved from `IMessageService` to
`IChatService`: `ChatFacade` inserts the row on the UI thread, names it through `IContactService`,
sets its kind from `IJidResolver.IsSelfLinked` and asks for a dedup pass through
`IChatService.RequestChatListDedup` (`ChatListDeduper`). `ChatListViewModel` and the legacy
`ShellView` call the chat façade; the member left `IMessageService` and `IWhatsAppService`.

**A stored tip reaches the strip through `ChatMessageTipApply`.** (Core.) It is the glue the
client called `ApplyMessageAsChatTip`: `ChatListStrip.FromMessage` plus
`LiveChatPreviewApplier.ApplyIfNewer`, with the yesterday label and the self name passed in.
The client method is a thin forward.

**Bulk in-memory preview refresh is `StoredChatPreviewRefresh`.** (Core, DI singleton over the UI
dispatcher, `ChatStateStore`, `IJidResolver`, `ContactDisplayNames`, `ISelfMarkerNaming` and
`IStringResources`.) `RefreshAsync` re-tips every row from `MessagesByChat`; `ReconcileAsync`
also creates missing rows, clears placeholder direct names and sorts the list. `ChatFacade`
owns both members on `IChatService`; the client calls the helper after a large offline drain.
`RefreshAllChatPreviewsFromStoredAsync` and `ReconcileChatListFromStoredAsync` left
`IWhatsAppService`.

**Chat-list dedupe is `ChatListDeduper`.** (Core, DI singleton over the UI dispatcher,
`ChatStateStore`, `ChatRowIndex`, `JidAliasTable` and the push-name table.) It runs
`DuplicateChatFold`, removes the folded rows, re-keys `MessagesByChat` and invalidates the
row index in one UI pass. `IChatService.RequestChatListDedup` lives on `ChatFacade`, which
schedules the catalogue save, raises display-name listeners through `IContactService` and
asks `IChatIdentityPersistence` (`ChatIdentityPersistence`) to write names and aliases
immediately when rows merged. The member left `IWhatsAppService`; catalogue debounce
and full persist still sit on the client.

**Alias-row merge is `ChatListAliasMerge`.** (Core, DI singleton over the UI dispatcher,
`ChatStateStore`, `JidAliasTable` and `ChatListDeduper`.) When a LID and a phone already
share a canonical id it runs ordinary dedupe; otherwise it folds the LID timeline into the
phone row and drops the LID chat. `ChatFacade` owns the fire-and-forget entry used when a
new list row arrives with an unresolved alias pair. `ContactUsyncResolver` calls the helper
directly (not through `IChatService`) so DI does not cycle through `IContactService`.
`MergeDuplicateChatsAsync` and `RequestAliasChatMerge` left `IWhatsAppService`.

**Usync table filing is `UsyncAliasFiling`.** (WhatsAppClient, DI singleton over `JidAliasTable`.)
It runs `AliasPairPolicy` and `AliasPairFiling.FileBothWays`, then returns the accepted pair.
`IUsyncAliasHost` is `UsyncAliasHost` (WhatsAppClient): filing then
`ISocketAliasRegistrar` (socket register + auth heal on the client).
`IJidAliasPersistence` shares that registrar for inbound/history pairs.

**Opening a chat merges SQLite and the live overlay in Core.** `OpenChatMessageMerge` is the
id-prefer-live merge `MessageFacade` used to inline twice (open page and sync reload). Live
still wins on collision and keeps SQLite media keys / quote / reactions when blank. The RAM
overlay itself stays on the client through `IOutgoingMessageHost.LoadLiveOverlayAsync`
(rebuild window, apply held receipts, queue dirty rows); `LoadMessagesForChatAsync` left
`IWhatsAppService`. UI keeps calling `IMessageService.LoadMessagesForChatAsync`.

**SQLite tip reconcile is the chat façade's.** `ChatFacade.ReconcileChatPreviewsFromSqliteAsync`
runs `ChatPreviewReconciler`, then notes catch-up progress and schedules persist through
`IOfflineReplayHost` — the same side effects the client attached. The member left
`IWhatsAppService`. Post-replay, history-quiet, deferred-startup and persisted-ui-loaded
call sites go through `IChatService` (no private client clone, no client-held reconciler).

**Resolved-name batch apply and persisted-title normalize are the contact façade's.**
`ContactFacade.ApplyResolvedNameBatchAsync` fills push / group-subject tables and relabels
list rows; remote subject / contact / account-contact paths call it. Startup
`NormalizePersistedChatNamesAsync` and catch-up / self-name
`ApplyResolvedNamesToChatListAsync` are on `IContactService` over `ChatNameRefresh`.
`ContactFacade` owns the `Applied` subscription (`DisplayNamesUpdated` + diagnostics).
`IResolvedNameApplyHost` is auth/sidebar write (`WriteSelfDisplayName`) plus identity reads;
capture policy is `ContactFacade.CaptureSelfPushName`. The client no longer holds
`ChatNameRefresh` or `StoredChatPreviewRefresh` (post-replay memory tip repair goes through
`IChatService.ReconcileChatListFromStoredAsync` / `RefreshAllChatPreviewsFromStoredAsync`).
`StartupPhaseTrace` lives beside `StartupQuietGate` in WhatsAppClient.

**Background enrichment orchestration left the client.** `BackgroundResolutionPass`
(WhatsAppClient) owns quiet → handshake → replay/catch-up → avatar-defer →
`NotifyBackgroundResolutionDueAsync`. The client implements `IBackgroundResolutionGate`
(quiet wait, handshake, replay, catch-up, avatar defer, status/diagnostics) and
`ContactFacade.TriggerBackgroundResolution` / `CancelBackgroundResolution` run the pass.
`ShutdownAsync` cancels via the contact façade. **Deferred avatar timer left too:**
`DeferredAvatarResolution` (WhatsAppClient) implements `IDeferredAvatarResolution` and
fires `TriggerBackgroundResolution` after the delay; the client only cancels it on
shutdown / socket replace. `ScheduleDeferredAvatarResolution` left `IWhatsAppService`.

**Chat-delete durable I/O is `IAppStateLocalHost.ForgetChatStorageAsync`**
(`AppStateLocalHost` → `ForgetChatStorageHost`). RAM delete,
badge, missing-message / on-demand forget live on `IChatService.ApplyAppStateDeleteChatAsync`.
Message delete and message pin live on `IMessageService` (`ApplyAppStateDeleteMessageAsync` /
`ApplyPinnedMessageStateAsync`). Archive / chat pin / mute / read live on `ChatFacade`.
`ChatFacade` no longer takes `IWhatsAppService`; `MessageFacade` still does for events and
`EnsureConnectedAsync`.

**Conversation resync primitives are `IConversationResyncHost`.** Cache wipe, app-state flag
re-report and the no-socket full resync fallback leave `IWhatsAppService`. `HistoryFacade`
owns the UI path and reaches those three through the host; ViewModels still call
`IHistoryService` / `IMessageService.ResyncConversationsAsync`.

**SQLite history notifies are `IHistorySyncHost`.** Chunk started/applied, LID mapping
filing and `RequestFullHistoryOnDemandAsync` leave `IWhatsAppService`. `HistoryFacade`
still takes the client for events and `EnsureConnectedAsync`.

**Usync socket filing is `IUsyncAliasHost` / `ISocketAliasRegistrar`.** `TryFileUsyncAlias`
left `IWhatsAppService` and left the client class entirely (`UsyncAliasHost`).
`ContactUsyncResolver` takes `IUsyncAliasHost` only; the client implements
`ISocketAliasRegistrar` for socket + heal.

**Startup UI load is `IStartupUiHost`.** `LoadPersistedUiStateAsync` and
`StartDeferredStartupMaintenance` leave `IWhatsAppService`. `ConnectionFacade` takes the
host and still attaches the client for events and the rest of lifecycle.

**Done when:** `.Connection.cs` + a small main file; no façade needs send/history/avatar/group/list methods on the client; leftover `IWhatsAppSocket` drop is [Migration](Migration) item 9, after this.

---

## Risks (do not skip)

1. **UI-thread mutations of `Chats`.** Off-thread `ObservableCollection` writes crash the list. Use `IChatStateStore` APIs that marshal.
2. **`GetCanonicalJid` everywhere.** Wrapper first (phase 1), table move later (3.7).
3. **Do not register** resurrected `MessageService` / `ContactService` / `ConnectionService` / `ProfileService` under `Services/WhatsApp/` — deleted on purpose; live types are `*Facade`.
4. **Broker transfer** is wired on `SocketBridge` (warm transfer and reclaim; no cold restore). Unrelated to this extraction; see [Background broker](Background-Broker).

---

## After each phase

Build `Unison.Uwp` (not only Core). Update this page (tick the phase), [Changelog](Changelog), and the façade table in [Application layer](Application-Layer) if a new contract appeared (`IJidResolver`, `IGroupService`).

## Related

- [Architecture](Architecture) — client vs policy; `WhatsAppService` today
- [Application layer](Application-Layer) — which VM talks to which façade
- [Coding standards](Coding-Standards) — do not grow `IWhatsAppService` for UI
- [Migration](Migration) — SQLite history, broker, `ChatsModule`, `SocketClient` on disk
