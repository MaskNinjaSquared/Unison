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

## Done: phase 2 — invert `Attach*` (the reportable half)

Each `AttachFoo` is a place the flow is born in the client. Where the call was a **report** —
fire-and-forget, no return value, no ordering the client depends on — it is now an event the client
raises and the façade subscribes to in its own constructor.

| Client event | Subscriber | Replaced |
|---|---|---|
| `OnStreamError` | `ConnectionFacade` | `AttachConnectionService` (gone) |
| `OnInvalidSessionSuspected` | `ConnectionFacade` | same |
| `OnLiveStatusReceived` | `StatusFacade` | `AttachStatusService` (gone) |
| `OnBackgroundHistorySyncBounced` | `HistoryFacade` | `AttachHistoryService` (gone) |
| `OnAvatarCached` | `ContactFacade` | part of `AttachContactService` |
| `OnJidAliasResolved` | `ContactFacade` | part of `AttachContactService` |

That let the push-in doors close on the contracts too. `NotifyStreamError`,
`NotifySuspectedInvalidSession`, `TryIngestLiveAsync`, `NoteBackgroundHistorySyncBounce` and
`ClearAvatarAttempted` are private to their façades now, because the client was their only caller.
`StatusFacade` gained an `IWhatsAppService` constructor parameter so it has something to subscribe
to; the other three façades already had one.

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

`AttachContactService` survives too, reduced from nine call sites to the two deferred-startup
maintenance ones (`RefreshPhoneContactOverlayAsync`, `RetrieveContactPicturesAsync`) plus the
`IWhatsAppService` pass-throughs. Those are orchestration: the client sequences them and depends on
the order.

---

## Current coupling (start here next session)

### Facades still depend on the client

`ContactFacade`, `MessageFacade`, `ChatFacade`, `GroupFacade`, `HistoryFacade`, `ProfileFacade`, `StatusFacade`, `ConnectionFacade` take `IWhatsAppService` (or `AttachWhatsAppService`). Policy is already on the façade; **primitives** (fetch avatar, send bytes, persist, canonical JID) still run inside the client.

Helpers under `Contacts/` (`ContactNameResolver`, `ChatAvatarPolicy`, `GroupRosterPolicy`, `AddressBookOverlay`) are the largest non-UI consumers of client members (`RunOnUiThreadAsync`, `SchedulePersistPublic`, `RaiseSyncStatus`, `FetchAndApplyAvatarAsync`, `FetchGroupMemberAvatarAsync`, `IsTransportReady`, …). Extracting avatars/names is mostly moving those primitives so the helpers stop needing the god client.

### DI still injects façades *into* the client

After `BuildServiceProvider`, `App.ConfigureServices` does:

- `AttachMessageService` / `AttachContactService`
- `AttachPersonStore` / `AttachChatStore` / `AttachGroupRosterStore`

That is the wrong direction: live ingest (status, names) **starts** in `WhatsAppService` and calls up. Phase 2 inverted the reportable half of it (client publishes; façade subscribes); the rest is blocked on phase 3 and is described below.

### What still holds the client

`App.xaml.cs` for lifecycle (`ResumeAsync`, `TransferActiveSocketToBrokerAsync`, `PrepareForSuspendAsync`, `ShutdownAsync`, `ReleaseMemoryAsync`, `IsConnected`, `AttachUiDispatcher`), the façades and their policy helpers, and `RuntimeDiagnosticsService` for health snapshots.

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
| 3.2b | Group apply + roster persist | `GroupFacade`, after 3.6 / 3.7 / 3.9 |
| 3.3a | Media cache + file naming (done) | `IMediaCache` / `MediaCacheService`, `MediaFileExtensions` |
| 3.3b | Derived renditions — transcode / poster / WebP (done) | `MediaDerivationService` |
| 3.3c | Download reading (done) | `MediaDownloadPlan` (Core). Orchestration stays — see below |
| 3.4 | Send (main file) | `MessageFacade` over use cases; client only “send this node” |
| 3.5a | Receipt reading (done) | `ReceiptReader` |
| 3.5b | Receipt aggregation state (done) | `GroupReceiptTally` (Core) |
| 3.6 | Names / usync (`.Identity.cs`) | `ContactFacade` / `ContactDirectory`. **Name rules out — see below.** usync + merge remain |
| 3.7a | Alias LID/PN + canonical (done) | `JidAliasTable` (Core), read through `IJidResolver` |
| 3.7b | Converge onto `LidMappingStore` | Blocked: that store is async, canonicalization is not. See below |
| 3.8 | `.AppState.cs` | **Rewritten — the premise was stale.** See below |
| 3.9a | List display order (done) | `ChatDisplayOrder` (Core) |
| 3.9b | `.Persistence.cs` + preview reconcile + the appliers from 3.8 | `ChatFacade` + `ChatStateStore` + `IChatStore` / `IMessageStore`. Close the transitional public dictionaries on `ChatStateStore`. **Persistence rules out; appliers remain — see below** |
| 3.10 | `.IncomingPump.cs` | Decode/dispatch stays with connection. **Rules closed.** **Notify + timeline accept + persist enqueue + revoke + pin-in-chat on MessageFacade.** **Live + offline list apply + tip refresh/reconcile on ChatFacade.** Pump keeps queue/locks, alias-consolidate orchestration, offline summary *record* |

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

Candidates are passed in rather than resolved inside, for two reasons: the PN/LID table is still the
client's until 3.7, and injecting `IJidResolver` here would close a DI cycle (`IJidResolver`
eagerly resolves `IWhatsAppService`, which now takes the fetcher).

What is left of avatars in the client is the **apply**, and it is deliberately parked: it writes
`ChatItem` rows on the UI thread and calls `SchedulePersist` (3.9), and the group-avatar fallback is
group-metadata protocol (3.2). Also staying is `ShouldDeferAvatarFetch` — it reads history backfill
and on-demand state, so it is history gating wearing an avatar name, and belongs with 3.9/3.10.

**3.2a is done: the parser left, the apply stayed.** `GroupMetadataReader` turns a `w:g2` response
into plain objects — subject, announce-only, member count, participant drafts, my role — plus the
list algebra the roster merge needs (`RosterJidSetsEqual`, `MergeInPlace`) and the placeholder-name
test. It holds no socket, no chat state and no dispatcher, so it is the first piece of the group
cluster that can be checked without watching rows change. `GroupListingEntry` and `GroupMemberDraft`
moved with it.

It takes **two functions** rather than `IJidResolver`: canonical JID, and "is this the logged-in
account". The second one reads the PN/LID alias table, which is still the client's until 3.7 — a
participant row can carry a device-suffixed PN whose base LID is the account's real identity, and
`IsSelfLinkedJid` is the only place that knows it. Reimplementing that in the reader would have been
a silent behaviour change in the role the composer trusts for announce-only groups. At 3.7 both
functions collapse into the resolver.

The rest of `.Groups.cs` is apply and orchestration, and it is blocked on three other steps, not on
itself: it walks `Chats` on the UI thread and calls `SchedulePersist` (3.9), writes `ContactNames`
and calls `ResolveDisplayName` (3.6), and canonicalises through the client's alias table (3.7). Move
it when those land, not before.

**3.3a is done: storage left, downloading stayed.** `IMediaCache` / `MediaCacheService` owns
`LocalFolder/MediaCache/{Images,Audio,Documents,Video,VideoPosters}`; `MediaFileExtensions` (Core,
pure) owns MIME to extension. Callers name a `MediaCacheKind` and a file base and get back an
`ms-appdata:` URI, instead of writing the "open MediaCache, then open the subfolder, then build the
URI by concatenation" sequence by hand at every save and lookup — 217 lines out of `.Media.cs`.

`SaveAsync` takes `reuseExisting` because the old call sites disagreed on purpose and the difference
is worth keeping: an image or a video keyed by message id is byte-identical on redownload, so the
write is skipped, while documents, posters and transcodes are things we produced and may reproduce.

Two follow-ups, both real:

- The same folder boilerplate is still duplicated in `OggOpusToWavConverter`,
  `OggOpusHandlerService` and `HistoryThumbnailMaterializer`. They should take `IMediaCache`. Left
  out of 3.3a deliberately — the audio path is the fragile one on Mobile and deserves its own change.
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

What is still in the client is `EnsureWebPDisplayUriAsync` and `EnsurePlayableAudioUriAsync`. Those
read and write `ChatMessage` and persist it, so they follow message state rather than media.

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
resolver, closing that note.

The table learns the logged-in account through `BindSelf`, two functions rather than two strings.
The account's own LID arrives after pairing and is written in place on the existing auth state, so a
snapshot taken at construction would be a snapshot of null and the self-poisoning guards would
quietly stop firing.

3.7b is the storage swap behind this seam: reconcile the key shapes, then let the table read
`LidMappingStore` through its memory cache. Nothing above it has to change again.

**Before attempting 3.7b, settle this.** `JidHelper.Normalize` now has characterization tests, and
writing them surfaced a mismatch 3.7b will run straight into. Group addresses take an early return
before the server part is lowercased, so `@G.US` and `@g.us` normalize to different strings. The client
compares group addresses case-insensitively and does not care; `ChatStateStore` keys chats and messages
with `StringComparer.Ordinal` and does. `FindChat` is what `UpsertChats` consults before deciding a chat
is new, so a mixed-case `@g.us` reaching the store is two rows for one group.

That is the same class of question 3.7b exists to answer — the two stores disagree on key shape — and
it is why 3.7b is not a mechanical swap. Decide the case rule for the whole seam first, with a device
pass behind it, then move the storage.

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

**The appliers from 3.8 have started.** `AppStateChatMutation` took the first two: the unread count
when the account marks a conversation read or unread, and the archive / pin / mute flags. Note where
the difficulty actually lives — not in the flags themselves but in the fact that one mutation lands on
every row sharing a canonical identity. "Mark as unread" carries no number, so it reads the highest
count across the alias rows before falling back to 1; an unpin writes `0` rather than null, so a row
that has not received the mutation yet cannot resurrect the pin through dedupe. Both are now tests
rather than comments.

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
onto façades, with the socket and the UI thread on both sides. 3.2b still waits on 3.6; 3.3c is a
façade move too. The test suite does not reach any of them, because they are all in the UWP head.

**None of this has been run.** Every slice from the pending message queue onward is compile-checked
and test-covered and has not been on a device. That debt keeps growing and is the next thing to spend
time on, ahead of any further extraction.

### 3.6 — the name rules are out; usync is not

Two pieces moved, and they are the two that decide what the user reads.

`ContactLabelSanitizer` answers whether an offered name is a name at all. Four sources feed names in
and they did not agree on what one is: a push name the contact chose, an address-book entry the user
typed, and a label WhatsApp masked before storing. It rejects masked numbers, phone echoes and
self-marker spoofs, and strips marker suffixes. **The self-marker rule is spoof prevention** — a
contact can set their push name to "(You)" and, without it, their messages render as the user's own.
That had no test.

`DisplayNameResolution` is the precedence between the survivors: what the app learned, then the
device address book, then the contact's own push name, then the number. Only the third gets the `~`,
and not in groups or above message bubbles.

Both take the shape this extraction keeps arriving at. The marker is localized, so recognising it
stays in the UWP head behind `ISelfMarkerNaming`; the lookups stay too, because they warm a SQLite
cache as a side effect. What moved is the decision.

`MergeTransientDirectChatIntoCanonicalAsync` then gave up its decision too. The method is chat surgery
— UI thread, SQLite, two collections — but underneath it answers one question: when the same contact
turns out to occupy two rows, which side of each field survives. That is `TransientChatMerge`, and
stating it separately exposed a gap: the transient row's name was checked for blank and for
number-as-name but not for a self-marker, so a contact pushing "(You)" could be copied onto the
canonical row during a merge even though the sanitizer blocks that everywhere else. The orchestration
stays; only the decision left.

What remains in `.Identity.cs` is the larger half and is not this shape: `ResolveContactsAsync` and
`RefreshContactNamesAsync` (usync over the socket, ~380 lines) and the alias follow-up scheduling.
Those are 3.6's move onto `ContactFacade`, not a rule to lift out of a method.

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
`IMessageService.AcceptIncomingTimeline` (host still holds `MessagesByChat` + id index);
persist enqueue is `QueueIncomingPersist`. Revoke is `IMessageService.ApplyIncomingRevocationAsync`
(`MessageRevocationContent` tombstone; host still finds the row in `MessagesByChat` / SQLite).
Inbound pin-in-chat is `IMessageService.ApplyIncomingPinInChatAsync` (host still owns pin state /
SQLite). Tip refresh / bulk re-tip / in-memory list reconcile is `IChatService.RefreshChatPreviewAsync` /
`RefreshAllChatPreviewsFromStoredAsync` / `ReconcileChatListFromStoredAsync` (bulk paths still walk
host `MessagesByChat`). Pump still owns alias-consolidate side-effects, offline summary
*recording*, placeholder/missing ledger and the queue itself.

### Phase 4 — What remains is connection

`.Connection.cs` is this phase's target file. **No further pure-rule extraction there:**
`ConnectionHealthPolicy` already left; connect / resume / pairing / broker / suspend are lifecycle
orchestration. Do not nibble that partial until the rest of phase 3 stops needing the god client.

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
