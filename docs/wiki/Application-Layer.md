# Application layer

UWP hosts the app. Core holds contracts and ViewModels. WhatsApp **facades** are the API screens should use. `WhatsAppService` remains the compatibility client underneath several of those facades.

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
| `IContactService` | `ContactFacade` | Address-book overlay, name refresh, avatar policy, phone search |
| `IProfileService` | `ProfileFacade` | “Me” hydrate + profile picture IQ |
| `IHistoryService` | `HistoryFacade` | Sync status, chunks, on-demand full resync |
| `IDebugSendService` | `DebugSendService` | File-watch test send (`#if DEBUG`) |
| `IWhatsAppService` | `WhatsAppService` | Compatibility client (socket, in-memory chats, persist) |

`IPairingService` stays in `Contracts/` (not under `WhatsApp/`). Login talks to `IConnectionService`, which owns pairing.

Raw events on `IWhatsAppService` are **for facades only**. Each facade re-publishes the subject it owns. A ViewModel that subscribes to the client directly is coupling itself to the class that happens to produce the event today.

### Platform contracts (implemented in UWP)

`INavigator`, `IDispatcher`, `IDialogService`, `IFilePicker`, `IAudioRecordingService` / `IAudioRecordingSession`, `IShareService`, `IUriLauncher`, `IVoicePlaybackRoutingService`, `ILocalSettings`, `IStringResources`, `IAppLanguageService`, `IShellThemeService`, `INotificationService`, `ILiveTilesService`, `IShortcutService`, `IStatusBarService`, `ILocationKeepAliveService`, `ILocalContactsService`, `ISocketBrokerService`, `IChatStore`, `IPersonStore`, `IMessageStore`.

### Chat kinds (three enums)

| Type | Meaning |
|---|---|
| `ChatKind` | Conversation: Direct / Group / Personal (self-chat) |
| `ChatMessageKind` | Message: Text / Image / Video / Sticker / Voice / Audio / Document |
| `ChatPreviewKind` | Chip + text on the **chat list** |

`Kind` comes from protocol flags via `ChatMessageContentSnapshot` → `IMessageService.GetChatMessage`. The list preview must not infer media from a literal `[Image]` string.

## Facades

Registered in `App.ConfigureServices`. Older `*Service` classes under `Services/WhatsApp/` (except `WhatsAppService`) are leftovers and are **not** in the container.

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
  Diagnostics/DebugSendService.cs
  WhatsAppService.cs         compatibility client
```

`IWhatsAppSessionProvider` / `BridgeSessionProvider` hand the **live** `WhatsAppSession` to facades. They do not cache it: reconnect replaces the session.

### HistoryFacade

On-demand resync wipes local messages (`IMessageStore` + conversation caches) and asks the session for history, **waiting for chunks** rather than a timer. Fallback: `IWhatsAppService.ResyncConversationsAsync`. Initial handshake history sync is handled on the socket path (buffered events).

### ContactFacade

Owns **when** names and avatars refresh (cooldown, batches, per-session dedup). Protocol primitives (`ResolveContactsAsync`, `FetchAndApplyAvatarAsync`, defer during history-on-demand) stay on the client or on Socket use cases.

## DI bootstrap

Single composition root: `App.ConfigureServices` in `Unison.Uwp/App.xaml.cs` (`Microsoft.Extensions.DependencyInjection`, `validateScopes: true`).

Order that matters:

1. Dispatcher, `ChatStateStore`, navigator, dialogs, settings
2. `IAuthPersistence` → `AuthStore`; `IKeyStore` → `FileKeyStore`
3. `IWhatsAppService` → `WhatsAppService.Create(ChatStateStore)`
4. `IWhatsAppSessionProvider` from `WhatsAppService.Socket` (the `SocketBridge`)
5. Facades (`Profile`, `History`, `Message`, `Contact`, `Chat`, `Connection`)
6. Stores + `LidMappingStore`
7. ViewModel factories and platform adapters
8. ViewModels: `ShellViewModel` **singleton**; others transient

After `BuildServiceProvider`, `WhatsAppService.Attach*` wires satellites. `IConnectionService.AttachWhatsAppService` breaks the cycle. Profile and History are resolved immediately so they do not miss events.

`App.GetWhatsAppService()` is the only remaining central resolve for the concrete client.

## ViewModels and what they consume

| ViewModel | WhatsApp contracts |
|---|---|
| `LoginViewModel` | **Only** `IConnectionService` |
| `StartViewModel` | Language + `ShellViewModel` (no WhatsApp) |
| `ShellViewModel` | `IWhatsAppService` (session/unread), `IConnectionService`, `IProfileService` |
| `ChatListViewModel` | Message, Contact, Connection, History, Chat facades; `IChatStateStore` for the list |
| `ChatDetailViewModel` | `IMessageService`, `IChatService`; `IWhatsAppService` for load / presence / group lock |
| `ChatDetailInfoViewModel` | `IChatService` (pin); `IWhatsAppService` for group permissions / HQ avatar |
| `ChatMessageViewModel` | `IMessageService` (media ensure, message pin) |
| `NewChatDialogViewModel` | `IContactService.SearchContactAsync` |
| `SettingsViewModel` | `IConnectionService.LogoutAsync` |
| `DebugViewModel` | `IWhatsAppService` (verbose, wipe, snapshot) |
| `ImageViewerViewModel` / `VideoViewerViewModel` | Share + picker (constructed from the view) |

Chat bubbles are **entities with a ViewModel** (`ChatMessageViewModel` + `.Actions.cs`): images, videos, reactions, quotes, and interaction commands. Many former code-behind handlers moved to ViewModels via **Microsoft.Xaml.Behaviors**.

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

The client is still large on purpose. The [Migration](Migration) page lists what should leave it next.
