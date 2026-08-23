# Changelog

Newest first. This is a wiki-facing merge of the Unison.Socket architecture PR, the product “What’s New” notes, the Socket Broker work, v6.9, and v6.8. It is not a substitute for git history.

---

## German (`de-DE`) locale

- New UI pack `Strings/de-DE/Resources.resw` (parity with `en-US`; download draft + 92 missing keys filled)
- Registered in `AppLanguage.German = 9`, `AppLanguageInfo`, `Package.appxmanifest`, and `AppxDefaultResourceQualifiers` / `PRIResource`

---

## ViewModel slim-down (helpers)

- **`GroupParticipantLookup`** owns roster/name/avatar/1:1 indexes previously inlined on `ChatDetailViewModel`; VM keeps thin wrappers (`RebuildParticipantLookup`, `ResolveParticipantContactUri`) and timeline Mentions refresh
- **`MessageRunLayout`** owns run chrome, date chips, sender/quote labels, and contact slots; midnight refresh only rewrites separators
- **`SelfIdentity`** centralizes “is this JID me?” for You/Você labels (used by the participant lookup)
- **`ChatListDisplayOrder`** owns PN/LID dedupe + pin/timestamp/name sort; `ChatListViewModel` delegates instead of duplicating the merge logic
- Collaborators stay VM-owned (no new DI interfaces for these helpers)

---

## Bubble pin menu + clock format (i18n / Settings)

- Message long-press **pin / unpin** labels come from `.resw` (`ChatDetail_UnpinMessage`, `ChatDetail_PinFor24Hours`, `ChatDetail_PinFor7Days`, `ChatDetail_PinFor30Days`) in every shipped locale — no hardcoded PT-BR in `ChatDetailView`
- Settings **Customization:** clock after device time-zone conversion is **24-hour** or **12-hour (AM/PM)** (`TimeFormat` / `LocalSettingsConstants.TimeFormat`). Storage and tip compare stay **UTC**; only UI clocks (`WhatsAppMapper.FormatClock` / `LocalTimeConverter`) read `WhatsAppMapper.CurrentTimeFormat`
- **Fix (message pin persistence):** live pin/unpin now writes `history_message` immediately via `UpsertPinsAsync` (PN+LID keys + MessageId fallback). Body upserts preserve an existing pin unless the row already carries `IsPinned=true` or `WritePins` clears it — history sync `InsertOrReplace` no longer wipes the banner after a successful pin
- **Fix (chat-list pin persistence):** `ChatStore.UpsertAsync` already saved `IsChatPinned`, but `ApplyTo` ignored it (comment said “history is source of truth” while `history_chat_preview` has no pin columns). `ApplyLocalFields` now restores chat-list pin from SQLite on list hydrate so a restart does not wait for the next `pin_v1` app-state sync

---

## Chat list: Last Message stays in sync with SQLite

- **Rule:** load newest `history_message` for the chat by **TimestampUtc** (PN+LID keys); if that tip’s **MessageId** differs from `ChatItem.LastMessageId` (or body/fromMe), swap the strip — never on a strictly older timestamp
- **`LastMessageId`** on `history_chat_preview` (schema v4). No WhatsApp history resync: first reconcile after upgrade stamps Ids from `history_message` (ALTER COLUMN + backfill)
- **Startup:** reconcile Last Message **before** name/photo resolution
- Reconcile also considers in-memory tips; opening a chat picks newest tip by timestamp then reconciles
- **Fix (UTC kind):** `ChatMessageOrder.ToComparableUtc` now matches `WhatsAppMapper.ToUtc` — SQLite `Unspecified` is UTC wall-clock, not local. The old `ToUniversalTime()` path shifted Brazil UTC−3 by **+3h** into `LastMessageTimestampUtc`, so the strip looked “newer” than the real tip and reconcile kept the stale Last Message. Reconcile force-applies the chosen tip so already-poisoned strips heal
- **Sent checkmarks** on the Last Message strip follow `LastMessageSendState` (same send-state vocabulary as bubbles)

---

## Chat detail: bubble cost (layout, tree, decode)

- **Participant resolve no longer walks every chat per bubble.** `RebuildParticipantLookup` indexes 1:1 avatars/names once; `GroupParticipantResolver` uses that map instead of scanning `Chats`. Roster `AvatarUrl` PropertyChanged patches visible `ContactUri` as hydrates land
- **`MergeTimelineFromService` is O(n)** via an id→row dictionary (was FirstOrDefault per service message). Midnight date chips only rewrite separator fields — no full run/avatar pass
- **Message templates use `x:Load`** for quote, media grids, sticker, caption, audio, document, body, and read-more — text bubbles no longer build 300×300 download trees. Dead `MessageBubbleChrome` ContactUri/ShowContact bindings removed
- **`StringToImageSourceConverter` caches** BitmapImage by URL+decode width (cap 96). Quote/document/`CanExpand` getters on `ChatMessageViewModel` are memoized. Unused `ChatMessage.ReactionChips` getter removed; no-op `FillTimelineThumbnailsAsync` gone

---

## Reactions: back to one path for Mobile and desktop (revert of the summary mode)

- **Timeline open loads the reactor rows again.** `AttachReactionsAsync` fills `HistoryMessage.Reactions` with one batched `ChatJid IN (…) AND MessageId IN (…)` query per 80 ids, explicit columns, oldest reactor first. Gone: `ReactionSummarySelectSql`, `COUNT(*)` / `GROUP_CONCAT` aggregation, `ReactionSummaryRow` / `ReactionEmojiRow` and the in-process fallback
- **One shape in the models.** `HistoryMessage.ReactionTotal` / `ReactionSummaryText` removed; `ChatMessage.Reactions` is the single source for `HasReactions` / `TotalReactions` / `ReactionsDisplayText` / `ReactionChips`, so `ApplyReactionSummary` and `ReactionsBuilder.BuildEmojiLineFromSummary` are gone. `ReactionMapper` always edits the list (no `SoftApplyToSummary`)
- **`AreReactionDetailsLoaded` kept, with a narrower meaning:** true only when the list came from the store. It is what scopes the reaction `DELETE` in a live upsert, so a live-only object still cannot wipe stored reactors
- Cost is back where it was before the optimization: opening a big group reads every reactor of the page (still one query per batch, not N+1)
- Unchanged from the previous fix: on-demand threshold follows `_sqlOpenPageSize`, and live reaction envelopes persist additively via `UpsertReactionsAsync`

---

## Reactions chips missing on Mobile: live upsert was deleting the rows (fix)

- **Live upsert no longer clears reactions of chip-summary rows.** `HistoryMessageWriteBatch.ReactionOwnerMessageIds` lists only the ids whose reactor rows the batch actually carries (`AreReactionDetailsLoaded`); `ClearReactionsForMessages` deletes just those. Before, any receipt/pin/state flush of a summary-only row ran `DELETE FROM history_message_reaction` and rewrote nothing, so `COUNT(*)` was 0 on the next open
- **Timeline merge carries the summary.** `ApplyLiveFieldsTo` assigned only the reactor list, never `ReactionsDisplayText` / `TotalReactions`, so a row already on screen could never receive a chip from a later SQL page — `HistoryMessageMapper.CopyReactionState` now applies details or summary (and never blanks a chip a partial read cannot confirm)
- **On-demand threshold follows the page size** (`_sqlOpenPageSize - 5`): the fixed `40` meant the 30-row Mobile page was always "thin", so every chat open asked the phone for history and ran an extra sync/merge/persist cycle — the cycle that tripped both bugs above on Mobile and never on desktop (50-row page)
- **Live reaction envelopes are durable again.** `IHistoryMessageStore.UpsertReactionsAsync` writes the single reactor row additively (empty emoji still removes it), so a reaction landing on a summary-only bubble survives a restart and can even arrive before its parent message
- Reaction rows already deleted on device do not come back on their own; they return with the next reaction, on-demand chunk, or resync (history-sync persist is additive)

---

## Reactions chips missing on Mobile (fix)

- Live/client rows winning the open merge dropped SQLite reaction summaries — `CopyReactionsIfMissing` keeps the chip when the winner has none
- Reaction attach falls back to an explicit `MessageId, Emoji` query + in-process tally when `GROUP BY` / `GROUP_CONCAT` fails or returns empty (common on older Mobile SQLite)
- `COUNT(*)` mapped as `long`; `GROUP_CONCAT` no longer uses `DISTINCT` (optional; fallback still dedupes)

---

## Sync StatusBar: no sticky settling / “0 of N” over open chats

- Opening a chat (`SetActiveChatJid`) clears the global sync banner so Mobile StatusBar does not keep “Finishing startup…” / list enrichment over chat detail
- List enrichment phases (`settling` / `names` / `avatars` / `groups` / `lowmemory`) are suppressed while a conversation is active; work still runs in the background
- Early exits from post-replay / background resolution / cancelled quiet-wait always `RaiseSyncStatus(null)` so settling cannot stick forever
- Avatar batch no longer reports `0 of N`; progress starts after the first completed fetch. UI also strips zero-current counts to bare phase text

---

## Reactions: summary on open, details on dialog (no SELECT *)

- Timeline attach uses `GROUP BY MessageId` with `COUNT(*)` + `GROUP_CONCAT(DISTINCT Emoji)` instead of loading every reactor row
- Bubble chip binds cached `ReactionsDisplayText` / `ReactionTotal`; full rows load in `MessageReactionsViewModel` via `GetReactionsForMessageAsync` (explicit columns)
- Live reaction updates on summary-only bubbles soft-adjust the chip without wiping other reactors; dialog always reloads from SQLite
- Pinned / media history queries use `TimelineSelectSql` (no `SELECT *`)
- **Fix:** dropped `GROUP_CONCAT(DISTINCT …, ' ')` (needs SQLite 3.44+); that SQL failed on UWP/Mobile and aborted the whole history page, leaving only the list-preview placeholder. Summaries now use comma separator; attach is try/caught so reactions never block the timeline

---

## Mobile performance: timeline windows, selective avatars, roster merge

- **Timeline windows by device** (`ISystemInfoProvider`): chat open UI window is **30/80** on Mobile vs **50/150** on desktop; SQLite open/load-more pages are **30/20** vs **50/30** (`ChatDetailViewModel`, `MessageFacade`)
- **Group member avatars not on open**: `RefreshGroupSendPermissionsAsync` uses `hydrateAvatars: false`. Visible bubble authors hydrate via `HydrateGroupMemberAvatarsForJidsAsync` / `GroupRosterPolicy.HydrateVisibleAsync` (no full-roster next-batch). Full roster hydrate waits for the Members pivot (`EnsureMembersAvatarsHydratedAsync` + `IsMembersAvatarsLoading` progress)
- **Roster diff**: when metadata returns the same JID set, `ApplyGroupMembersToChat` merges name/avatar fields in place instead of replacing `GroupMembers` (avoids PropertyChanged relayout)
- **Mobile `CacheLength`**: message `ItemsStackPanel.CacheLength = 0.5` after list load / chat open (default is much higher)
- **Selective `RefreshMentions`**: run layout only refreshes bubbles with `HasMentions` (skips the common no-@ case)

---

## Lighter “Loading photos…” batch (Mobile)

- Avatar batch no longer calls `SchedulePersist` after every download — one debounced write at the end of the batch
- Sync-status banner updates are throttled (every few items + start/end) instead of rewriting the StatusBar on each photo
- `HydrateCachedAvatarUris` probes disk off the UI thread and applies URIs in one dispatcher pass
- Startup batch fetches preview only (`fetchHighQuality: false`); high-res group art waits for visible-row refresh
- Mobile uses a smaller batch (8) and a shorter inter-request delay (400 ms) than desktop

---

## Protocol thumbs behind the download placeholder

- History `_thumb` URIs map to `ThumbnailUri` (images) / `VideoPosterUri` (video), not full `ImageUri`/`VideoUri`, so `NeedsImageDownload` stays true and the bubble can still offer CDN download
- Sent/received download overlays show the protocol thumb (or video poster) under the download button; the generic placeholder glyph only appears when no thumb is on disk

---

## Transparent message ListViewItem (no white recycle flash)

- Timeline `MessageListView` used the default opaque `ListViewItem` chrome, so virtualization recycle flashed white blocks over the tiled wallpaper while scrolling up. Containers are now a transparent `ContentPresenter`-only template (same idea as Unigram’s wallpaper-friendly history items); the list itself is `Background="Transparent"`

---

## Timeline load progress + scroll lock

- `IsLoadingMore` already gated load-more (`CanLoadMore`); first open now sets `IsLoadingMessages` via `BeginLoadingMessages` / `EndLoadingMessages`. `IsTimelineBusy` drives a 2px indeterminate `ProgressBar` on the bottom edge of the chat header
- While busy, vertical scroll on the message list is disabled (and `ViewChanged` ignores load-more) so Mobile does not stack flings on top of materialization; scroll is restored when the load finishes

---

## Dropped MediaThumbnailBase64 from SQLite

- Removed the fat `MediaThumbnailBase64` column/property from `history_message`, `history_status`, `ChatMessage`, mappers, and UI. Protocol thumbs live only as `MediaCache/Images/*_thumb` URIs (`MediaLocalUri` / `MediaPosterUri` / `ThumbnailUri`)
- On init, stores attempt `ALTER TABLE … DROP COLUMN MediaThumbnailBase64` (schema message **7**, status **2**). Chat-info preview is URI-only; `Base64ToImageSourceConverter` deleted

---

## History thumbs on disk + light group participant lookup

- History sync no longer stores protocol `jpegThumbnail` as base64 in `history_message`. Bytes are written to `MediaCache/Images/*_thumb` and the URI goes on `MediaLocalUri` / `MediaPosterUri` (full media is never overwritten by a thumb). Timeline open skips the fat second thumbnail query
- Opening a large group no longer runs `ResolveDisplayName`/`ResolveAvatar` for every roster member. `RebuildParticipantLookup` only indexes names/avatars already on the roster; the full resolver runs on demand for JIDs that appear on visible bubbles. `PersonGroup` roster persist uses one transaction instead of N async inserts

---

## Chat open: fewer SQLite round-trips + participant lookup cache

- Opening a conversation used to call `GetForChatAsync` once per PN/LID/canonical key (timeline + thumbs + reactions each), then resolve every group author name by walking the roster. `GetForChatKeysAsync` loads with `ChatJid IN (...)` in one query; open page size is **50** (matches the UI window). Live `ChatMessagesChanged` uses `LoadRecentMessagesForSyncAsync` (RAM + 30-row SQL tail, no pinned/pending extras)
- `ChatDetailViewModel` builds `participant name/avatar` dictionaries once from the roster (`RebuildParticipantLookup`) and injects them into run layout / sender labels / avatars so bubbles hit `TryGetValue` instead of resolving per message

---

## Composer grows upward while typing

- Chat detail `MessageInput` mirrors Unigram: `TextWrapping="Wrap"`, `MinHeight="40"`, `MaxHeight="192"`, `VerticalAlignment`/`VerticalContentAlignment` Bottom so the box expands upward; attach / mic / send stay Bottom on the single-line baseline. Enter sends; Shift+Enter inserts a newline. Starting a voice note clears `MessageText` so the box collapses before the recording overlay

---

## Mark-read no longer rewrites the chat catalogue

- Opening a chat called `ClearUnreadForChatAsync` → `SchedulePersist` → `PersistDataAsync`, which upserted **every** `history_chat_preview` row and rewrote three contact JSON maps, then published `OnSyncStatus("Saving chats...")`. On Mobile (Unison theme) that landed on the StatusBar and contended with SQLite message load on eMMC — Unigram stays fast because TDLib patches one chat and the UI only updates that row
- `ClearUnreadForChatAsync` now no-ops when no alias row had unread, otherwise upserts **only the dirty rows** via `PersistChatCatalogSliceAsync` / `PersistChatListRowsPublic`. Preview refresh after open uses the same slice path instead of a full `SchedulePersistPublic`
- Routine `PersistDataAsync` no longer raises `"Saving chats..."`; sync phases already report through `SyncPhaseStatus`

---

## Chat list filter flyout

- Filter menu items bind `FilterChatsCommand` with integer `CommandParameter` values that map to `ChatListFilter` (`All = 0` … `Drafts = 6`). UWP does not pass enums reliably from XAML, so the command is `RelayCommand<int>` and the view model casts after `Enum.IsDefined`
- `RefreshVisibleChats` applies the active filter with LINQ before the search box filter, so both compose with AND. Incremental list patches fall back to a full rebuild while a non-All filter is active
- `ChatItem.IsFavorite` and `HasDraft` currently return false (stubs) so Favorites / Drafts ship empty until those features exist. Contacts / Non-contacts use the address-book overlay (`PhoneContactNamesByJid`); Groups uses `IsGroup`; Unread uses `HasUnread`

---

## Startup phases are visible and localized

- The service published finished English sentences through `OnSyncStatus` ("Fetching contact names…"), which the chat list showed verbatim — the only part of the UI that never translated. A phase now travels as a `SyncPhaseStatus` token (`phase:names:12/40`) and `ChatListViewModel.TranslateSyncPhase` is the single place that turns it into words. Anything that is not a token still passes through untouched
- Five phases that ran silently now report: settling after replay (`ChatList_Settling`), name resolution, avatar fetch and group metadata (`ChatList_ResolvingNames` / `_FetchingAvatars` / `_FetchingGroups`, each with a running count), and the low-memory pause (`ChatList_PausedLowMemory`). All nine packs carry the keys
- Post-replay maintenance no longer sleeps a flat 25 s on Windows Mobile. `WaitForStartupQuietAsync` polls every 500 ms and stops as soon as safe mode and the replay drain are both clear, past a 3 s floor (1 s on desktop); the ceilings are 8 s / 10 s / 6 s
- Memory pressure no longer abandons enrichment on the spot. `WaitForMemoryHeadroomAsync` retries at 10 s / 20 s / 40 s and only gives up after the last one, so a device that was briefly above the low watermark still gets its names. `TriggerBackgroundResolution` also runs on Mobile now — it was desktop-only, which is why Mobile never showed those phases at all
- `RuntimeDiagnosticsService` gains a `startup-phase` category: begin/end per phase with elapsed ms, `AppMemoryUsageLevel` at both ends, plus `quiet-wait`, `memory-retry` and `memory-abandoned` records

---

## Avatars decode at the size they are drawn

- `BitmapImage(Uri)` starts decoding in the constructor, so a `DecodePixelWidth` assigned in the object initializer that follows arrives too late and the full-resolution frame is decoded **on the UI thread**. `ChatAvatarControl`, `StringToImageSourceConverter` and `TiledBackground` all had that shape — a 640-square group photo cost more than the info panel around it, and the tiled background ignored its 256 px Mobile cap. All three now set the decode properties first and assign `UriSource` last
- `ChatAvatarControl` asked for `size * 2` under `DecodePixelType.Logical`, which is already display-scaled — four times the drawn area. It now decodes at `size`, and remembers the applied URL so `ApplyVisual` (which runs for any visual property change) stops re-decoding an unchanged picture

---

## Live media rows no longer store preview tags

- `HistoryLiveMessageMapper` ran `ChatMessage.Content` into `history_message.Body` verbatim, so a live sticker / captionless image / video landed as `[Sticker]` / `[Image]` / `[Video]`. On read-back `HistoryMessageMapper.ApplyMediaEnvelope` promotes a non-empty body to `ChatMessage.Caption`, which is what the media bubble renders — history-sync rows were clean because they already went through `ChatPreviewNormalizer.NormalizeBody`
- Live writes now normalize body and quoted body the same way; reads normalize again so rows written by older builds stop showing the tag without a schema bump or resync

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
