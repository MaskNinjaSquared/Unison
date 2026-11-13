# UI work scheduling

Background work that touches the UI (tip reconcile, name relabels, group listing, alias follow-up, app-state flags, receipts) yields to chat open and to the offline tip flush through **one** rule. This page is that rule. It applies to desktop and Mobile alike; only the numbers vary by tier.

## Waiting: `IUiWorkHolds`

Do not write a new `while (gate) await Task.Delay(...)` loop. Ask `IUiWorkHolds` instead:

```csharp
IUiWaitRun holds = _uiHolds.BeginRun("my-reason");
await holds.WaitAsync(UiWorkKind.SilentNames, cancellationToken);
```

- `UiWorkHoldPolicy` (Core, pure) decides which gate holds which kind of work.
- `UiWorkHolds` (Core) polls the gates, applies the budget, and logs to the `ui-hold` category: `ui-hold-paused`, `-ongoing`, `-timeout`, `-sticky`, `-released`, `-failed`. Each line has `kind`, `reason` and `gate`.
- The host builds the gate snapshot once, in `App.ConfigureServices`. Façades take `IUiWorkHolds` through the constructor.
- `Describe(kind)` answers "is this held right now?" for yield checks between slices.

### Kinds and gates

| Kind | Gates (first match wins) | Max wait |
|---|---|---|
| `TipSlice` | deeplink, timeline, hidden, cold-populate, offline-tip | 8 s |
| `GroupListing` | offline-tip | 12 s |
| `GroupMetadata` | timeline | 12 s |
| `SilentNames` | offline-tip | 20 s |
| `AliasFollowUp` | timeline | 20 s |

Every kind polls at 40 ms. Time is measured with `Stopwatch`, never wall clock.

PreferFrugal defers offline tip paint (and the post-flush quiet that keeps `OfflineTipBusy`) **only while** `SyncWorkGate.IsReplayDrainActive`. After `offline-complete`, late `Append` frames paint progressively without re-arming a pushable backstop, and send echo (`Append` + from-me) takes the live tip path — Append after settle must not hold the offline-tip gate forever (7a57ffb9).

### Sticky timeouts

A run is one `BeginRun`. Within a run, a gate that already used its whole budget is skipped on the next wait (`ui-hold-sticky`). A stuck gate costs one timeout per run, not one per slice. A new run waits again.

### Timeline gate

`ChatTimelineLoadPriorityGate.WaitWhileHeldAsync` keeps its post-release admit stagger. Without a `maxWait` it stops at `ChatTimelineLoadPriorityGate.DefaultMaxWait` (12 s), so a leaked `Hold` cannot park author projection or roster demand for the rest of the session.

## Storms: `KeyedLatestCoalescer`

Per-key update storms fold into one batch per window.

- The first post arms the window. Later posts join it **without** pushing the flush back, so latency is bounded by one window.
- The merge function decides which value survives per key.
- Batches are applied in order, one at a time.
- The window is `StormWindowPolicy.For(preferFrugal)`: 250 ms by default, 1000 ms when frugal.

| Storm | Key | Merge | Caller waits? |
|---|---|---|---|
| Receipts (`MessageFacade`) | message id | strongest status (`MessageStatusProgression`) | yes: the wire path is fire-and-forget |
| App-state chat flags (`ChatFacade`) | chat id | per field, latest wins (`RemoteChatSettings.Merge`) | no: the bridge awaits each mutation in turn |

Flags apply in slices of 32 rows per UI hop (Low lane when frugal) with one list sort per batch.

Presence is **not** coalesced: its only subscriber stores a field without a UI hop. (`WaEventBuffer` does not help here: it has no mergers, see [Socket stack](Socket-Stack.md#events).)

A history-sync chunk raises `ChatMessagesChanged` for every chat it touched in **one** UI hop, not one hop per chat.

Never coalesce a new message, the live strip (`RunUrgentAsync`) or the open chat.

## Deferring is not waiting

`IUiWorkHolds` is for background loops that must pause in place. Work that can simply be redone later should defer and resume on the gate's release event instead of polling:

- `refresh-visible` (`ChatListViewModel`) sets a deferred flag while the timeline gate is held or the window is hidden, and reruns on release. It is bounded because the timeline gate always has a deadline.
- `ChatOpenPaintSchedule` only holds fixed paint delays (surface yield, Mobile materialize delay, scroll settle). Those are UX timings, not gates.
- `SurfacePaintPolicy.For(DeviceCategory)` (Core) owns those numbers per tier: Unsupported is more conservative than Low; High skips delays. `SurfacePaintSchedule.ResolvePolicy()` reads `ISystemInfoProvider.GetDeviceCategory()` (not a mobile bool). On Low/Unsupported, `ShowLoadingAfterFirstPaint` means: shell/list chrome first → yield → preparing overlay → `Attach` / section create. `MainView` defers `EnsureShellContent` until after that first yield; `ChatListViewModel` starts with the overlay off when deferred.
- Deep-link open waits with `ChatListDeepLinkWait` (one `CollectionChanged` + a single timeout), not a `Task.Delay` poll loop.

## Repeated reads: RAM caches

Every cache below is invalidated on its own write path; adding one without that invalidation (and its test) is not allowed. Caps come from `SyncWorkloadStrategy`.

| Data | Cache | Kept until | Cap |
|---|---|---|---|
| Profile-picture IQ (`SocketBridge`) | `ProfilePictureQueryCache` over `AsyncQueryCache` (in-flight dedupe) | found 6 h, not-found 30 min; `picture` notification drops it; opening a chat revalidates (60 s cooldown per JID) | `ProfilePictureQueryCacheMaxEntries` |
| Avatar files on disk (`AvatarCacheService`) | `FileStampIndex` (name → write time) | save / delete update it | folder size |
| Contact name warm-up (`ContactDisplayNames`) | warmed-JID set | `PersonChanged` or a failed warm | 4096 |
| Mention resolvers (`CommentRichService`) | resolved once | process lifetime | 1 |
| `LocalSettings` reads (`LocalSettingsService`) | value per key, misses included | `Set` / `Remove` | keys in use |
| Group roster (`GroupRosterStore`) | `VersionedSnapshotCache` per group | any write to that group; a read racing a write is not stored | `GroupRosterMemoryMaxGroups` |

Settings written by the Background task are not visible through the `LocalSettingsService` cache; read those keys straight from `ApplicationData`.

Timeline extras (pins and unsent outgoing) for every alias key of a chat come from one `IN (...)` query (`GetTimelineExtrasForChatKeysAsync`).

Display names themselves are not cached: push names, the address book and aliases change them without raising `PersonChanged`, so a name cache would serve stale labels.

## Adding work

1. Pick an existing `UiWorkKind`. Add one only when no existing gate set fits, and add its row to the policy tests and to the table above.
2. If a caller awaits each item of a burst in sequence, return once posted. Do not return the batch task there.
3. Keep numbers in `UiWorkHoldPolicy.BudgetFor`, `StormWindowPolicy` or `SyncWorkloadStrategy`, never inline.
