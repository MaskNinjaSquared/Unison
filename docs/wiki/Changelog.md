# Changelog

Newest first. This is a wiki-facing merge of the Unison.Socket architecture PR, the product “What’s New” notes, the Socket Broker work, v6.9, and v6.8. It is not a substitute for git history.

---

## Chat-info Media / Files load on tab

- Opening profile / group / member info no longer queries `history_message` for media. `EnsureMediaIndex` runs when the **Media** or **Files** pivot is selected (one shared SQLite index for both)
- Those panes show a centered `ProgressRing` while `IsMediaIndexLoading` is true; empty copy stays hidden until the first page lands. Members still binds the existing `ChatItem.GroupMembers` list

---

## Timeline SQLite open 100 + indexes

- Opening a chat reads the newest **100** `history_message` rows (`MessageFacade.SqlOpenPageSize`; was 200). UI still paints `InitialUiMessageWindow` **50** bubble VMs; load-more stays 30; cap stays `MaxUiMessageWindow` 150. Pinned + pending outgoing still merge from SQLite if they sit outside that page
- Schema **5**: composite indexes `ix_hm_chat_ts` `(ChatJid, TimestampUtc, MessageId)`, `ix_hm_chat_pin` `(ChatJid, IsPinned, PinnedAtUtc)`, `ix_hm_chat_kind` `(ChatJid, Kind, TimestampUtc)`. Open and load-more use the same `ORDER BY TimestampUtc DESC, MessageId DESC`
- Timeline SELECT omits `MediaThumbnailBase64`; image / video / sticker / document rows of that page fetch it in a second query. Chat-info Media / Files still `SELECT *`

---

## Group @number mentions in bubbles

- SQLite timeline dropped proto `ContextInfo.MentionedJid`, so group bubbles kept `@5511…` / `@…@lid` as plain text. `history_message` stores those JIDs (schema **4**); `history_chat_preview` stores them for the list strip (schema **2**)
- `ChatItem.GroupMembers` stays the list for chat-info Members. `ChatItem.MentionLookup` is a digit→name dictionary rebuilt when that roster is replaced (`MentionLookupBuilder`). Bubbles and the list strip bind that map — they do not walk every group on each parse
- `CommentRichService` consumes `@digits` **and** `@digits@lid` / `@s.whatsapp.net`. A JID user-part is not treated as a display name

---

## Show Unison contacts in Windows

- Settings toggle **Show Unison contacts in Windows** (`PublishContactsToWindowsEnabled`, default **off**). When on, **all 1:1 chats** (LID included; phone filled from canonical/PN/`Person` when known) go to a Unigram-shaped People account: `UserDataAccount` (`AppAccountsReadWrite`) + `CreateContactListAsync(name, account.Id)` + `ContactAnnotationList` on that account — not a bare app `ContactList` (People ignores those) and not the user agenda
- Name is `FirstName` / `LastName` (not `Contact.Name`). Photo is `SourceDisplayPicture` from a local `StorageFile` (not `Thumbnail`, not a raw `ms-appdata` URI). `RemoteId` is the JID with `@` replaced (`w5511….s.whatsapp.net`) so `SaveContactAsync` does not reject it. Annotations use `ContactProfile | Message | AudioCall` (+ Share) and `ContactPanelAppID` / `ContactShareAppID`
- Publish **upserts** and does not delete the rest of the list on a partial snapshot (chats still loading). Name/avatar refresh retries while the first pass was empty; otherwise at most every 30s. Logout or turning the toggle off **deletes the `UserDataAccount`**. One failed `SaveContactAsync` does not abort the batch. Account / list / annotation ids are `PublishWindowsUserDataAccountId` / `ContactListId` / `AnnotationListId`

---

## Chat info full screen under 800 epx

- Opening user, group, or member info uses `ChatDetailInfoStates`: `InfoFullScreen` when `ChatDetailView` is narrower than 800 epx (400 chat + 400 info), otherwise `InfoDocked` (400-wide column). Threshold is the detail pane, not the window. Closed state is `InfoClosed`

---

## Add contact (People card)

- **Adicionar contato** also appears on the green chat-info command bar (`ChatDetailInfoControl`) when `CanAddToAddressBook` — 1:1 with a phone, not groups. Same command as the link under the number
- Phone on the info pane uses `IContactService.TryResolvePhone` (not only `TryPhoneFromJid` on a LID). Overlay name maps still prefer the user agenda; they skip Unison-owned lists so publishing to Windows does not hide Add
- ViewModels call `IContactService.CanAddToAddressBook` / `ShowAddToAddressBookAsync`. WinRT stays on `ILocalContactsService`. Desktop / Continuum opens `ShowFullContactCard` (a People window, not the light-dismiss flyout that Windows 11 closes with the menu). Mobile still uses `ShowContactCard` after UI idle, anchored to the focused control. Agenda overlay refreshes when the app is foreground again — not in the same turn as the click
- `ms-people:` only if showing the card throws. LID-only people (no phone) do not get the action. The group overflow menu does not

---

## Group member pictures in idle batches

- `GroupRosterPolicy` (on `IContactService`) fetches member photos **16 at a time**, then schedules the next 16 — not a one-shot on open and not a migration table
- `GroupMember.AvatarFetchedAtUtc` is stamped on a hit **and** on a confirmed miss (`no-picture`), so people without a photo are not asked again for 7 days. Timeouts use a 30-minute backoff
- Picture IQs prefer the phone JID. Opening a group also harvests LID↔PN pairs from interactive metadata (the participating listing already did)

---

## Timeline date chips (Hoje / Ontem / date)

- First bubble of each **local** calendar day shows a centered pill (`ChatDateSeparator`) above the row — not a synthetic list item. Flags `IsFirstOfDay` / `DateSeparatorText` are layout-only on `ChatMessage`, set in `ApplyMessageRunLayout`
- Labels: `Common_Today` / `Common_Yesterday` / culture short date (`d`). Midnight relabel is a `DispatcherTimer` on `ChatDetailView` (Core stays free of WinRT clocks)
- Fill is `ChatDetailDateSeparatorBackgroundBrush` (same as wallpaper underfill). Bold text uses `ChatDetailDateSeparatorTextStyle` → `ChatDetailDateSeparatorForegroundBrush`

---

## Bubble time uses the device time zone

- Chat stamps (send and receive) are stored as GMT 0 / UTC. `LocalTimeConverter` maps them to `TimeZoneInfo.Local` on the bubble. SQLite Unspecified Kind is treated as UTC

---

## Chat list "Updating..." stuck after connect

- Header "Updating..." is `IConnectionService` status `open`; it only clears on `synced` (offline drain). The safety timeout now emits pending-notifications so the banner cannot stay forever when `ib/offline` never arrives
- Debug log `[ChatList/Sync]` names the façade that last changed the banner (`IConnectionService`, `IHistoryService`, …)

---

## Live messages + chat list in SQLite

- Send/receive/outbox and media/pin/revoke/reaction updates upsert `history_message` (no per-chat JSON rewrite). Outgoing pending/failed rows stay in that table until they complete
- Chat catalog persist is `history_chat_preview` (`SyncType=live`, no `ChunkPersisted` storm). Startup loads that table instead of `chats.json`. `SaveChatsAsync` is unused
- Open chat still overlays RAM; pinned + pending outgoing are merged from SQLite if they sit outside the newest SQL page

---

## Timeline open window 50

- Opening a chat materializes `InitialUiMessageWindow` **50** bubble VMs (was 80). SQLite open page was 200 (now 100; see above); load-more stays 30; cap stays `MaxUiMessageWindow` 150

---

## History SQLite = timeline (quote, pin, revoke, reactions)

- `history_message` schema 3: quote snapshot, pin timestamps, `IsRevoked`, local media URI / poster. New 1:N table `history_message_reaction` (one emoji per reactor; empty emoji deletes the row)
- History chunks persist those side-effect envelopes into SQLite; JIDs are stored normalized (envelope LID or PN, not canonical)
- Open chat and load-more read SQLite (`GetForChatAsync` with a timestamp cursor, page of 30). JSON message files are no longer the history source — do a conversation resync after updating
- Live RAM overlay on open still covers the current session (send/receive before the next chunk)

---

## WhatsAppService phase 0 — dead code out, partials in

- Deleted the unreachable legacy JSON history apply (`ProcessHistorySyncBodyAsync` + `StoreConversationTcTokenAsync` + `ApplyHistoryConversationPin` + `UseHistorySqliteApplyPath`). `ProcessHistorySyncCoreAsync` now only forwards progress to the SQLite path
- Deleted leftover duplicates that were never in the container: `MessageService` / `ContactService` / `ConnectionService` / `ProfileService` and the extra `DebugSendService` next to the façades (`Diagnostics/DebugSendService` stays)
- `SettingsViewModel` no longer takes unused `IWhatsAppService` (logout already goes through `IConnectionService`)
- The compatibility client is now `partial`: `WhatsAppService.cs` keeps fields/send/history notify, with Connection / Media / Groups / Avatars / Identity / AppState / Persistence / Receipts / IncomingPump files beside it — same type, no behaviour change, so the next extractions are diffs against one cluster
- Full leftover leaks + phases 1–4: [WhatsAppService extraction](WhatsAppService-Extraction)

---

## Open chat paints first, then messages

- `ChatsView` used to `await SetActiveChatAsync` (SQLite + bubble VMs) **before** `ApplyChatPaneState`, so a tap stayed on the list until history returned — especially noticeable on Mobile
- Open is now two steps: `PrepareActiveChatAsync` shows header/composer and empty wallpaper immediately; the host switches to NarrowDetail; `CompleteActiveChatLoadAsync` yields one frame (48 ms extra on Mobile) then loads the window
- Mark-read no longer blocks first paint (`MarkChatOpenedAsync` is fire-and-forget after chrome)

---

## History sync status banner (SQLite path)

- After the SQLite history path landed, each chunk called `PublishInitialSyncProgress(active)` and immediately `completed` in the same method, then raised `HistorySyncReceived(null)`, which cleared the chat-list banner — so the UI looked frozen with no “Syncing conversations…” feedback during import
- `NotifyHistorySqliteChunkApplied` now accumulates conversation counts across chunks, keeps safe-mode/progress active, and only finalizes after a quiet period (~2.8 s, or ~0.9 s after a Full chunk)
- `NotifyHistorySqliteChunkStarted` fires before SQLite writes so the banner appears while Mobile is still persisting
- `ChatListViewModel` no longer treats null `HistorySyncReceived` as “sync over” while safe-mode or preview hydrate is still running; when the preview queue drains after finalize, the batch banner completes and clears

---

## Open chat lands at bottom

- Switching conversations reused the same `ListView`/`ScrollViewer`, so the previous `VerticalOffset` survived `Clear` + `ReplaceTimelineWindow` — opening chat B mid-timeline looked like the position from chat A had stuck
- On open: zero the offset after clear, arm stick-to-bottom / load-more suppress for ~2.5 s, then `ScrollToBottom` plus a few deferred retries until `IsNearBottom` (or the load is cancelled by another switch)

---

## Media tab Creators Update crash

- Opening the Media pivot on Windows 10 Mobile / Creators Update (`10.0.15063`, the package `MinVersion`) closed the app: `ChatInfoMediaPane` set `CornerRadius` on `ChatInfoMediaTile` (`Control.CornerRadius`, UniversalApiContract **7.0** / 1809) and the tile root was a `Grid` with `CornerRadius` (same late API). The XAML parser throws as soon as the pivot materializes the template — the build already warned `WMC0151` for that line
- Fix: drop `Control.CornerRadius` from the DataTemplate; round the tile with `Border.CornerRadius` (contract 1, safe on 15063). `Border` on chrome buttons elsewhere was already fine

---

## Reactions viewer

- Tapping the reaction chip under a bubble opens `ShowReactionsDialogAsync(ChatMessageViewModel)` on `IDialogService` — the command was a stub until now. `ChatMessageViewModel.ShowReactionsAsync` swallows dialog failures so a bubble tap can never crash the timeline
- `MessageReactionsViewModel` (Core, transient) builds the dialog: title (`Reactions_TitleOne` / `Reactions_TitleMany`), the per-emoji tally through the existing `ReactionsBuilder.BuildChips`, and one row per reactor
- Reactor identity comes from `IPersonStore`, not from the reaction envelope: each `ReactorJid` is looked up under its canonical and normalized form (history files the LID it saw, a later usync files the phone JID), falling back to the envelope's `ReactorName`, then to the number. Lookups are memoized per dialog, so reacting twice does not query twice
- The phone line is suppressed when it would only repeat the name — an unnamed reactor already shows their number as the name
- `ReactionsDialog` renders chips side by side on `ReactionsDialogChipBrush`, a fill added to both themes' Default/Dark/Light dictionaries. Light cannot be lightened past a white dialog, so there the chip reads as a raised grey panel instead

---

## Group author strip on history sync

- History chunks put push names in `HistorySync.Pushnames`, not on each envelope, so `WebMessageInfo.PushName` is usually empty. The old code read only that field (plus a hardcoded `"You: "`), which is why a synced group row showed the kind chip with no name while the kind — read from the message body — always worked
- `HistorySyncContentFilter` no longer decides authorship: `TryGetListableContent` just extracts body/kind/timestamp, and the new `BuildPushNameMap` / `ResolveSenderName` / `ResolveParticipant` (moved from `HistoryMessageBuilder`) answer who wrote it. Preview and message builders now share one implementation
- `HistoryChatPreviewBuilder` composes the strip through `ChatPreviewNormalizer.FormatListAuthorPrefix`, so it inherits the existing fallback to a short participant label instead of dropping the strip
- `HistoryMessageBuilder` fills `SenderName` from the same map — SQLite timeline rows were also landing nameless
- `history_chat_preview` gained `LastMessageIsFromMe` / `LastMessageSenderName` / `LastMessageParticipantJid` (sqlite-net adds missing columns on `CreateTable`, so existing DBs migrate silently). `HistoryChatPreviewApplier` recomposes the strip in the current UI language instead of replaying a label frozen at sync time, and never blanks an author the live path already resolved
- `ChatItem` carries the same three parts so the strip can be recomposed later from the raw sender identity

---

## Group author strip resolves independently of the chat list

- `IChatAuthorProjection` (`ChatAuthorProjection`, Core singleton started at app init) owns strip recomposition. It listens to `IPersonStore.PersonChanged`, `IChatStateStore.DisplayNamesChanged` and `Chats.CollectionChanged`, and rewrites `ChatItem.LastMessageAuthor` when a name arrives — the strip catches up even when the chat list is not the active screen (the old `ChatListViewModel.RefreshGroupAuthorStrips` only ran while the list VM was attached)
- New `IPersonStore.PersonChanged` (payload: normalized JID) is raised after a committed `UpsertIfChangedAsync` write, so a roster/usync/address-book resolution pushes the exact JID instead of only the coarse `DisplayNamesUpdated`
- Name-arrival bursts during sync are coalesced into one sweep per UI turn; resolution still tries the resolved-name map, the Person cache and the 1:1 chat, across the JID's own and canonical (LID → PN) forms, and refuses a label equal to the JID's own digits
- `ChatListViewModel` no longer resolves author strips or depends on `IPersonStore`; it just re-renders visible rows on `DisplayNamesUpdated`
- Event-driven alone was not enough at launch: both maps the sweep reads start cold. `IPersonStore` only caches a JID once someone asks for it, and the contact-name sidecar is loaded by deferred maintenance (25 s on Mobile, skipped entirely when the window is hidden or memory is not `Low`) with a direct dictionary write that raises no event. A strip synced in an earlier session would sit on a bare LID until an unrelated write happened to warm the cache
- So the sweep now feeds itself: participants it cannot name in memory are read from the `Person` table off the UI thread, and a re-sweep is scheduled if any row came back. Attempts are tracked per JID, so a participant with no row is queried once per launch rather than on every sweep

---

## Chat timeline keeps position on load-more

- `MessageListView` still declares `ItemsStackPanel ItemsUpdatingScrollMode="KeepItemsInView"`, but that alone failed near the top: variable bubble heights, many one-by-one `Insert`s, and a `VerticalOffset` already close to 0 left the viewport on the newly prepended rows
- `ChatDetailView.LoadMoreMessagesAsync` now captures the first realized bubble intersecting the viewport (and its Y in the ScrollViewer) **before** the fetch, then after prepend + run layout restores with `ScrollIntoView` and a fine-tune via `TransformToVisual` (extent-delta fallback when the container is not ready yet)
- Suppress starts before the await (1.5 s) so a settled `ViewChanged` cannot start a second load-more while the first is still in flight; after a successful prepend it is re-armed for 800 ms
- `ScrollViewer_ViewChanged` still ignores intermediate events so load-more does not fire mid-fling

---

## Chat detail — leak fix, one timeline path, legacy pruning

- `ChatDetailView` subscribed five ViewModel events in its constructor and only unhooked `PropertyChanged` on `Unloaded`, so the ViewModel (kept alive by `ChatItem.PropertyChanged`) held the whole visual tree after Back. Subscriptions now pair `Loaded` → `AttachViewModelEvents` / `Unloaded` → `DetachViewModelEvents`, and the handlers are named methods so they can actually be removed
- Timeline mutation lives in `ChatDetailViewModel` only: `MergeTimelineFromService` (strip preview bubbles → refresh visible rows → ordered insert → trim to `MaxUiMessageWindow`), `ApplyPreviewFallback`, `StampGroupRemoteJid`, `InsertTimelineMessage`, `TakeLastWindow`. Code-behind keeps what XAML cannot do: scroll, run layout, pinned banner
- The dead `ChatDetailViewModel.SetActiveChatAsync` / `AppendLiveMessages` / `TryApplyPreviewFallback` / `IsHistoryOnDemandPending()` are gone — opening a chat has one owner (the view), so there is no second copy to drift
- `_messageService != null ? façade : _whatsAppService` fallbacks removed; `IMessageService` is a required constructor dependency
- Removed with no call sites: `ChatItemViewModel` + `IChatItemVmFactory` (chat rows bind `ChatItem` and get local state from `IChatStore.ApplyTo`), `IMessageStore.LoadChatsBackupAsync`, the four granular `IChatStore` setters (everything writes through `UpsertAsync`), `IHistoryMigrationStore.IsSucceededAsync` (`GetAsync().IsSucceeded` already says it)
- Still there on purpose: `IHistoryChatPreviewStore.GetAllAsync` (the read a cold start from SQLite needs)

---

## History bodies no longer truncated to 50 chars

- `HistoryMessageBuilder` / `HistoryStatusBuilder` were running the chat-list preview normalizer on the message body, so every SQLite history row was stored capped at 50 chars + `...` with line breaks flattened
- New `ChatPreviewNormalizer.NormalizeBody`: same placeholder stripping, no cap, keeps line breaks. `Normalize` stays the one-line preview for chat list / quotes
- Rows written before this keep the cut text (the rest was never stored) — full text returns when the phone re-delivers those messages
- The bubble `...` at 12 lines is a separate, intentional collapse (`ContentMaxLines` + Read more)

---

## Chat info Media / Files — right source, paged tiles

- Rows now come from `IMessageService.LoadChatMediaIndexAsync` (SQLite `history_message` media rows + live/JSON cache); the pane no longer reads only the legacy per-chat JSON, so history photos appear again
- New `IHistoryMessageStore.GetMediaForChatAsync` queries media/document kinds directly (text rows no longer crowd out photos)
- `ChatDetailInfoViewModel` keeps a model index (cap 400) and materializes 30 tile VMs per page (`CanLoadMoreMedia` / `LoadMoreMedia`, same for Files)
- Refresh diffs the bound collection instead of clear-and-refill (large groups crashed the app)
- Subscribes `IMessageService.ChatMessagesChanged` (SQLite chunks only raise the façade event) with a 400 ms debounce
- Stickers stay out of Media (`ChatMediaFilter`)

---

## Chat timeline memory — detach bubble VMs

- `ChatMessageViewModel.Detach()` drops `Model.PropertyChanged`
- Timeline clear/trim/remove (and chat-info media lists) call Detach so switching chats does not keep orphan VMs alive via the model event
- Open materializes only `InitialUiMessageWindow` (80) bubble VMs via `IChatMessageVmFactory`; hard cap `MaxUiMessageWindow` (150)
- Scroll near top → code-behind checks `ChatDetailViewModel.CanLoadMore` → `LoadMoreMessagesAsync` prepends factory VMs; stickers hydrate on tap (not bulk on open)

---

## Live stickers classified as image

- `StickerMessage` is classified **before** `ImageMessage` (live `MergeFrom` can leave both; the image field is often a thumbnail)
- `ResolveKind` / media filler / toast preview follow the same order
- Unwrap peels `DeviceSentMessage` and the same future-proof wrappers as Socket `MessageContent.Normalize`

---

## Status — façade, list, and viewer

- Shell **Status** item enabled (`NavigationRoutes.Status` → `StatusView`)
- `IStatusService` / `StatusFacade`: authors from `history_status`, oldest→newest items, `EnsureMediaAsync` via `IMessageService`, live `status@broadcast` ingest (no `ChatItem`)
- List: one row per author (avatar, name, relative `TimestampUtc`)
- Viewer: black photo chrome, white segment bars, 5s photo/sticker, video uses proto duration (else `NaturalDuration`); auto-advance; close after last item
- ViewModels talk to `IStatusService` only (not `IWhatsAppService`)

---

## Status vs chat (SQLite)

- Wire chat for Status is always `status@broadcast`; the person is `participant`
- Lista: `HistoryChatPreviewBuilder` / `IsListable` skip that JID
- Table `history_status`: AuthorJid + MessageId, media envelope, `ExpiresAtUtc` = timestamp + 24h
- `HistoryFacade` persists via `HistoryStatusBuilder`; wipe clears the table

---

## History SQLite — media envelope for on-demand download

- `history_message` stores Url / DirectPath / MediaKey / FileEncSha256 / mime / duration / fileName / jpegThumbnail
- `HistoryMessageMapper` maps those onto `ChatMessage` image/video/audio/document fields
- Merge keeps SQLite keys when a live/JSON row wins but has no key
- Existing DBs pick up columns via sqlite-net `CreateTable` migrate (schema 2)

---

## History SQLite phase 4 — façade ownership + detail hydrate

- `HistoryFacade.PersistHistorySqliteChunkAsync`: LID mappings → previews → messages → gate → progress notify
- `MessageFacade.SyncMessageHistoryAsync`: Person upsert + persist + `ChatMessagesChanged` per touched chat
- Open detail reloads via `IMessageService.LoadMessagesForChatAsync` (view sync + VM); load-more / on-demand also on the message façade
- `IWhatsAppService.ApplyHistoryLidMappings` restores PN/LID bookkeeping skipped when the legacy body was turned off
- Thin timeline (&lt; 40 msgs): opening a chat seeds RAM then requests `HISTORY_SYNC_ON_DEMAND`; on-demand latch cleared on SQLite apply (`CompleteHistoryOnDemandForChats`)

---

## History SQLite path (phase 3+) — legacy body off

- `MessageFacade` persists previews + messages to SQLite; the old JSON apply body is gone
- `WhatsAppService.NotifyHistorySqliteChunkApplied` completes resync wait / initial-sync progress
- `ProcessHistorySyncCoreAsync` only notifies SQLite-path progress (no legacy UI apply)
- Chat detail loads via `IMessageService.LoadMessagesForChatAsync` (SQLite + live/JSON merge)
- **Listable filter** (`HistorySyncContentFilter`): skip protocol/revoke/pin/reaction and empty bodies — same spirit as legacy JSON apply (no ghost empty chats)

---

## History messages SQLite (phase 3)

- Table `history_message` with `SendState` INTEGER (NotApplicable…Failed); capped at 250 msgs/chat/chunk
- Persist owned by `HistoryFacade` (`HistoryMessageBuilder`); cleared on wipe
- `HistoryMessageChunkPersisted` carries `ChatJids` for open-detail hydrate

---

## History chat preview SQLite (phase 1–2) + façade ownership

- Table `history_chat_preview`: list rows from history chunks; built off-thread in `HistoryFacade`
- `history_migration` gate + preview clear owned by **`HistoryFacade`** (`Track*` / `ResetHistorySqliteAsync`); no `WhatsAppService.AttachHistory*`
- Phase 2: `ChatPreviewChunkPersisted` → `ChatListViewModel` hydrates list
- Wire root remains `ConnectionHandler`; `WhatsAppService` stays the compatibility in-memory client until richer history/SQLite replaces remaining JSON uses

---

## History migration gate (current tree)

- SQLite `history_migration` owned by `HistoryFacade` (`Track*` / `ResetHistorySqliteAsync`)
- `MessageFacade` marks InProgress/Succeeded around sync; wipe via `OnSessionCleared` / resync wipe
- Gate only — chats/messages remain on JSON until a later migration step

---

## Group members, Person source, bubble avatars (current tree)

- `ChatItem.GroupMembers` persists the group roster (capped); Members pivot lists them with optional avatars
- `PersonSource` on SQLite `Person` (INTEGER): address-book names stick; push names cannot overwrite them; `Phone` is indexed
- After deferred startup, address-book overlay runs with `force: true` and reapplies distinct names to chats / roster
- Group timeline author photos: `ChatDetailViewModel.ApplyMessageRunLayout` resolves once (roster → canonical 1:1 → Person) and sets `ContactUri`; the bubble only binds
- SQLite `PersonGroup` (Person↔Group) updated when a roster applies; rows keyed by Jid **and** LID/PN/phone aliases so lookup survives LID↔PN mismatch
- Member info “groups in common” includes the open group; factory is `CreateGroupMember`
- Tap author name/avatar (or Members list) opens member info (Profile / Media / Files; no Calls/pins)
- Chat-info UI: `ChatDetailInfoControl` hosts user + group only; `ChatDetailGroupMemberInfoPane` is a **separate** shell (avoids shared-host conflicts). Opening any info resets Pivot `SelectedIndex` to 0
- Group header status loop: hint → alphabetical member names → fade; repeats ~every 90s
- Image download chrome: themed wallpaper placeholders (`Assets/Media/wallpaper-placeholder-*.png`) behind a rounded download button in bubbles and the info media tile (`#00210F` tile background)

---

## Unison.Socket (current foundation)

Baileys **7.0.0-rc14** session and protocol, separated from the UWP app.

### Architecture

- Added `Unison.Socket` (netstandard2.0), mirroring the rc14 structure
- `ConnectionHandler` owns socket-level work only (Noise, framing, IQ correlation, keep-alive)
- `WhatsAppSession` is the session composition root
- ~38 use cases: messaging, receipts, retries, media, groups, profiles, USync, app-state, history, authentication
- UWP façades for connection, messages, contacts, chats, profiles, history
- ViewModels consume the new façades
- `SocketBridge` keeps the existing UI working during the migration (`IWhatsAppSocket` over `WhatsAppSession`)

### Protocol

- Ported Baileys 7.0.0-rc14 session and protocol flows
- Buffered event processing during history synchronization
- Sequential offline node processing
- `MessageRetryManager` with LRU tracking and retry reasons
- Replaced the JidAlias protocol flow with `LidMappingStore`
- Pre-key generation at rc14 volume (812 initial, refill below 5)
- USync for contacts, devices, and LID
- App-state patches (mute / archive / pin / read)
- Media encryption and downloading (CDN HTTPS)
- Group metadata synchronization
- Phone-number pairing (link code) in addition to QR
- Server-side logout notification
- Outgoing audio converted to OGG
- Synchronized chat pin/unpin via app-state

### Runtime and reliability

- Fixed 1:1 messaging after application restart with persisted authentication/session state
- Fixed disconnect and unpair session cleanup
- Fixed QR state getting stuck after disconnect
- QR refresh after timeout
- On-demand history resynchronization through `HistoryFacade`
- Fixed history synchronization during the initial handshake
- Fixed self-chat read state
- Profile, contact, and group avatar sync updated for the rc14 LID flow

### Migration status (this release)

- The new socket stack is the foundation for WhatsApp communication
- `SocketBridge` keeps the UWP layer functional
- `WhatsAppService` is still present for compatibility and will be removed as remaining legacy flows migrate
- The background broker still hosts the existing raw-socket infrastructure; transferring that socket onto the new stack is a subsequent step

---

## Product surface (shell, chat UI, media)

Shipped around the same era as the façade work; still current.

- Language selector on Boot and Settings
- Language packs shipped **inside the main package** (not only OS + pt-BR on sideload)
- QR code pop-up for lower-resolution devices
- White / light theme for the Unison shell; WhatsApp shell still available
- Shell reload after theme/language changes
- Image viewer: pinch, pan, wheel zoom, double-tap
- Navbar no longer opens accidentally on Minimal (W10M)
- Settings shell with account info and disconnect
- Boot shell (extended splash) with animations
- Settings collaborators with GitHub links
- Chat message balloons as an entity + ViewModel (images, videos, reactions, quotes, interaction events)
- Code-behind interactions moved to ViewModels via Microsoft.Xaml.Behaviors
- WinUI 2.7
- Chat info window for groups and users
- Notifications closer to WhatsApp UWP (circular avatar, group vs direct layout)
- Pin Tile — pin chats to Start
- Audio/video in bubbles: video opens fullscreen; audio on speaker vs earpiece (screen off when held to the ear); statement updates
- Hardcoded Baileys-core strings replaced with resources
- Audio recorder UI (overlay + elapsed)

---

## Socket Broker (out-of-process)

Predecessor of the current background task; journal format still in use.

- Reliable Socket Broker foundation with an out-of-process `SocketActivity` task
- Noise handoff, frame journal (UBJ2 / UBD3), cold restore on the **legacy** `SocketClient` path
- Real contact toasts when minimized or screen-off
- Filter non-message frames
- Single disconnect toast

Cold restore and transfer are **not** wired through `SocketBridge` yet. See [Background broker](Background-Broker).

---

## v6.9

Theme: `WhatsAppService` becomes a **connection client**; policy moves to WhatsApp contracts/services; ChatDetail composer becomes MVVM.

- Move `ContactService` ownership (names + avatars: cooldown, batches, dedup)
- ChatDetail composer with MVVM (attach, microphone, overlay)
- Attachments and microphone recording
- On-demand image loading and fullscreen viewer
- Domain WhatsApp façades (`Contracts/WhatsApp`, `Services/WhatsApp`)
- `IDebugSendService` extracted from the client (`#if DEBUG`)
- `ChatKind` (Direct / Group / Personal) distinct from `ChatMessageKind` / `ChatPreviewKind`
- Message kind from protocol flags, not `[Image]` text
- Toast circular avatar
- Refresh README, `.gitignore`, and `Unison.slnx`
- Build and deployment scripts for `src/Unison.Uwp`
- Missing translations with English fallback

Explicitly **not** rewritten in 6.9: Noise, Signal, Socket Broker, journal, cold restore.

---

## v6.8

- Split Core, Uwp, Baileys, and Background projects
- MVVM + DI architecture
- Add `en-US`, `pt-BR`, and `id-ID` localization (later expanded; see UI page)
- Add Boot, Start, Login, and AppShell navigation
- Add shell themes and settings
- `Unison.Core` has no XAML; `Unison.Background` has no Core
- Auth-boundary navigation uses `NavigateAndClear`
- Master-detail remains on `ChatsView`
