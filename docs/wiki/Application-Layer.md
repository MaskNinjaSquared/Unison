# Application layer

UWP hosts the app. Core holds contracts and ViewModels. WhatsApp **facades** are the API screens should use. `WhatsAppService` remains the compatibility client underneath several of those facades.

Rules for new code (MVVM, DI, folders): [Coding standards](Coding-Standards).

## Unison.Core

netstandard2.0, no XAML, no WinRT.

```
Unison.Core/
  Contracts/           platform adapters + stores
  Contracts/WhatsApp/  domain façades
  ViewModels/
  Models/
  Factories/
  State/               IChatStateStore
  Mappers/
  Helpers/
  Constants/           NavigationRoutes, LocalSettingsConstants
```

### WhatsApp contracts

| Interface | Facade (UWP) | Responsibility |
|---|---|---|
| `IConnectionService` | `ConnectionFacade` | Pairing (QR / code), disconnect policy, server logout, session wipe |
| `IMessageService` | `MessageFacade` | Send, on-demand media, message pin, reactions, `GetChatMessage`, new chat |
| `IChatService` | `ChatFacade` | Account pin/unpin (app-state) + mark-read |
| `IContactService` | `ContactFacade` | Address-book overlay, name refresh, avatar policy, phone search, People add-contact card, optional Unison `UserDataAccount` in People |
| `IProfileService` | `ProfileFacade` | “Me” hydrate + profile picture IQ |
| `IHistoryService` | `HistoryFacade` | Sync status, chunks, on-demand full resync |
| `IStatusService` | `StatusFacade` | Active Status authors/items, live status@broadcast ingest, on-demand media |
| `IDebugSendService` | `DebugSendService` | File-watch test send (`#if DEBUG`) |
| `IWhatsAppService` | `WhatsAppService` | Compatibility client (socket, in-memory chats, persist) |

`IPairingService` stays in `Contracts/` (not under `WhatsApp/`). Login talks to `IConnectionService`, which owns pairing.

Raw events on `IWhatsAppService` are **for facades only**. Each facade re-publishes the subject it owns. A ViewModel that subscribes to the client directly is coupling itself to the class that happens to produce the event today.

### Platform contracts (implemented in UWP)

`INavigator`, `IDispatcher`, `IDialogService`, `IFilePicker`, `IAudioRecordingService` / `IAudioRecordingSession`, `IShareService`, `IUriLauncher`, `IVoicePlaybackRoutingService`, `ILocalSettings`, `IStringResources`, `IAppLanguageService`, `IShellThemeService`, `INotificationService`, `ILiveTilesService`, `IShortcutService`, `IStatusBarService`, `ILocationKeepAliveService`, `ILocalContactsService`, `ISocketBrokerService`, `IChatStore`, `IPersonStore`, `IMessageStore`, `IHistoryMessageStore`, `IHistoryStatusStore`.

### Chat kinds (three enums)

| Type | Meaning |
|---|---|
| `ChatKind` | Conversation: Direct / Group / Personal (self-chat) |
| `ChatMessageKind` | Message: Text / Image / Video / Sticker / Voice / Audio / Document |
| `ChatPreviewKind` | Chip + text on the **chat list** |

`Kind` comes from protocol flags via `ChatMessageContentSnapshot` → `IMessageService.GetChatMessage`. The list preview must not infer media from a literal `[Image]` string.

## Facades

Registered in `App.ConfigureServices`. Do not recreate leftover `MessageService` / `ContactService` / `ConnectionService` / `ProfileService` next to the façades.

```
Unison.Uwp/Services/WhatsApp/
  Connection/ConnectionFacade.cs
  Messages/MessageFacade.cs
  Chats/ChatFacade.cs
  Contacts/ContactFacade.cs
    ContactDirectory.cs      Socket use cases + LidMappingStore
    AddressBookOverlay.cs    device contacts → Person
    ContactNameResolver.cs   throttle / cooldown
    ChatAvatarPolicy.cs      dedup / backoff (fetch still on the client)
  Profiles/ProfileFacade.cs
  History/HistoryFacade.cs
  Status/StatusFacade.cs
  Diagnostics/DebugSendService.cs
  WhatsAppService.cs         compatibility client (partial)
  WhatsAppService.*.cs       Connection / Media / Groups / Avatars / Identity /
                             AppState / Persistence / Receipts / IncomingPump
```

`IWhatsAppSessionProvider` / `BridgeSessionProvider` hand the **live** `WhatsAppSession` to facades. They do not cache it: reconnect replaces the session.

### HistoryFacade

On-demand resync wipes local messages (`IMessageStore` + conversation caches) and asks the session for history, **waiting for chunks** rather than a timer. Fallback: `IWhatsAppService.ResyncConversationsAsync`. Initial handshake history sync is handled on the socket path (buffered events).

### ContactFacade

Owns **when** names and avatars refresh (cooldown, batches, per-session dedup). Protocol primitives (`ResolveContactsAsync`, `FetchAndApplyAvatarAsync`, defer during history-on-demand) stay on the client or on Socket use cases.

### StatusFacade

Reads `history_status` (written by `HistoryFacade` from history chunks). Groups unexpired items by author for the Status list; serves oldest→newest items to the viewer; downloads media via `IMessageService.EnsureImage/VideoAvailableAsync`. Live `status@broadcast` messages are ingested here and **must not** become `ChatItem`s.

`IStatusService.StatusUpdated` fires when the store changes. ViewModels talk to `IStatusService` (names/avatars are resolved on the façade via `IPersonStore`), not to `IWhatsAppService` for Status.

## DI bootstrap

Single composition root: `App.ConfigureServices` in `Unison.Uwp/App.xaml.cs` (`Microsoft.Extensions.DependencyInjection`, `validateScopes: true`).

Order that matters:

1. Dispatcher, `ChatStateStore`, navigator, dialogs, settings
2. `IAuthPersistence` → `AuthStore`; `IKeyStore` → `FileKeyStore`
3. `IWhatsAppService` → `WhatsAppService.Create(ChatStateStore)`
4. `IWhatsAppSessionProvider` from `WhatsAppService.Socket` (the `SocketBridge`)
5. Facades (`Profile`, `History`, `Status`, `Message`, `Contact`, `Chat`, `Connection`)
6. Stores + `LidMappingStore`
7. ViewModel factories and platform adapters
8. ViewModels: `ShellViewModel` **singleton**; others transient

After `BuildServiceProvider`, `WhatsAppService.Attach*` wires satellites. `IConnectionService.AttachWhatsAppService` breaks the cycle. Profile, History, and Status are resolved immediately so they do not miss events.

`App.GetWhatsAppService()` is the only remaining central resolve for the concrete client.

## ViewModels and what they consume

| ViewModel | WhatsApp contracts |
|---|---|
| `LoginViewModel` | **Only** `IConnectionService` |
| `StartViewModel` | Language + `ShellViewModel` (no WhatsApp) |
| `ShellViewModel` | `IWhatsAppService` (session/unread), `IConnectionService`, `IProfileService` |
| `ChatListViewModel` | Message, Contact, Connection, History, Chat facades; `IChatStateStore` for the list |
| `StatusListViewModel` / `StatusDetailViewModel` | **Only** `IStatusService` (+ `IDispatcher`) |
| `ChatDetailViewModel` | `IMessageService`, required (load / SQLite load-more / on-demand / send / presence); `IChatService`, `IPersonStore`; `IContactService` for the 1:1 **Add contact** overflow; `IWhatsAppService` for canonical JIDs / group lock. Timeline UI window: `InitialUiMessageWindow` / `MaxUiMessageWindow`; `CanLoadMore` + `LoadMoreMessagesAsync` for top-scroll prepend; bubbles via `IChatMessageVmFactory` |
| `ChatDetailInfoViewModel` | `IMessageService` (media/files index on Media/Files pivot + `ChatMessagesChanged`); `IChatService` (pin); `IPersonStore` (groups in common); `IContactService` (Add contact when not in the agenda); `IWhatsAppService` for group permissions / HQ avatar |
| `ChatMessageViewModel` | `IMessageService` (media ensure, message pin); `IDialogService` for the reactions viewer |
| `MessageReactionsViewModel` | `IPersonStore` (who reacted); `IWhatsAppService` for canonical JIDs |
| `NewChatDialogViewModel` | `IContactService.SearchContactAsync` |
| `SettingsViewModel` | `IConnectionService.LogoutAsync`; `IContactService.SetPublishContactsToWindowsAsync` |
| `DebugViewModel` | `IWhatsAppService` (verbose, wipe, snapshot) |
| `ImageViewerViewModel` / `VideoViewerViewModel` | Share + picker (constructed from the view) |

Chat bubbles are **entities with a ViewModel** (`ChatMessageViewModel` + `.Actions.cs`): images, videos, reactions, quotes, and interaction commands. Many former code-behind handlers moved to ViewModels via **Microsoft.Xaml.Behaviors**.

Opening a chat is driven by `ChatDetailView` in two steps: `PrepareActiveChatAsync` shows the header (the host then switches VisualState), `CompleteActiveChatLoadAsync` loads the UI window. The view owns cancellation, scroll and run layout; every write to `Messages` is a ViewModel method: `ReplaceTimelineWindow` for the opening window, `MergeTimelineFromService` for a reload (strip preview bubbles → refresh rows already on screen → ordered insert → trim to `MaxUiMessageWindow`), `ApplyPreviewFallback` for an empty timeline that has a list preview, `StampGroupRemoteJid` for older rows missing the group JID. The view has no second copy of that logic.

Group author photos are **not** resolved by the bubble. `ChatDetailViewModel.ApplyMessageRunLayout` walks the visible timeline once, resolves avatar URI (group roster → canonical 1:1 chat → `IPersonStore` cache), and sets `ChatMessage.ContactUri` / `ShowContact`. The template only binds those fields. LID vs PN matching goes through `GetCanonicalJid`. Member picture GETs are `GroupRosterPolicy` on `IContactService` (batches of 16; `AvatarFetchedAtUtc` remembers misses). Roster apply also persists `PersonGroup` memberships (Jid + Lid/phone aliases) for the “groups in common” member pane. UI shells: user/group in `ChatDetailInfoControl`; member in `ChatDetailGroupMemberInfoPane` — keep them separate.

## ChatDetail composer

`ChatDetailViewModel` owns compose; the view does not call the recorder or the socket.

| Command | Behavior |
|---|---|
| `SendMessageCommand` | Text |
| `AttachMediaCommand` | Image picker → preview dialog → `SendImageAsync` |
| `AttachAudioCommand` | Audio file → `SendAudioMessageAsync` |
| `StartRecordingCommand` / `Cancel` / `Send` | Mic session → voice note (`IsVoiceNote`) |
| Camera / File / Contact / Location | Commands exist, `CanExecute = false` (no send path yet) |

`IAudioRecordingService` is a **singleton** (one capture at a time). `StartAsync()` returns `IAudioRecordingSession`. Elapsed is `UtcNow - StartedAtUtc` while active; the VM ticks ~250 ms for `RecordingElapsedText`.

Lifecycle: `Loaded` → `InitializeAsync` / `Attach`; `Unloaded` → `UninitializeAsync` (cancel mic, stop presence). Switching chats stops and restarts presence watch.

On-demand media: incoming/history stores keys and paths on `ChatMessage`; tap downloads through `EnsureImage/Audio/Video/DocumentAvailableAsync`. Video opens fullscreen and updates the message statement; audio played on speaker stays on speaker, played “close to you” routes to the earpiece and can turn the screen off (`IVoicePlaybackRoutingService`).

## WhatsAppService vs facades

```
UI / ViewModels
    ├── platform adapters (picker, dialogs, mic, theme, i18n)
    └── façades (connection, message, chat, contact, profile, history)
            ├── IWhatsAppSessionProvider → SocketBridge → WhatsAppSession
            └── IWhatsAppService (chats in RAM, send/history body, persist)
                    └── IWhatsAppSocket (SocketBridge)
```

The client is still large on purpose. How it shrinks (phase 0 done, phases 1–4): [WhatsAppService extraction](WhatsAppService-Extraction). SQLite history / broker / `SocketClient` leftovers: [Migration](Migration).
