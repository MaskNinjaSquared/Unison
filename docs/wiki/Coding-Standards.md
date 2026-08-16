# Coding standards

How to write Unison code. Architecture (what exists) is in [Architecture](Architecture). This page is the **spec**: required patterns, folder layout, and known exceptions.

Agents: read this before adding a view, service, façade, or socket use case.

---

## 1. Layer boundaries (must not break)

| Project | May reference | Must not |
|---|---|---|
| `Unison.Core` | `Unison.Baileys` | XAML, WinRT, `Unison.Uwp` |
| `Unison.Socket` | `Unison.Baileys` | WinRT, SQLite, Core ViewModels |
| `Unison.Baileys` | NuGet only | Other Unison projects |
| `Unison.Background` | `Unison.Baileys` | Core, Socket, XAML |
| `Unison.Uwp` | All of the above | Protocol logic that belongs in Socket |

- Contracts and ViewModels live in **Core**.
- WinRT implementations live in **UWP** (`Services/`, `Data/`, `Transport/`, `UI/`).
- WhatsApp wire logic lives in **Socket** (`UseCases/`, `Session/`, modules).
- `ConnectionHandler` never calls a use case and never holds chats/messages.
- Reconnect policy stays in the UWP host (`ConnectionUpdate`).

---

## 2. MVVM

### Every view has a ViewModel

| View / surface | ViewModel (Core) |
|---|---|
| `BootView` | `ShellViewModel` (boot navigation) |
| `StartView` | `StartViewModel` |
| `LoginView` / `LoginControl` | `LoginViewModel` |
| `MainView` (AppShell) | `ShellViewModel` |
| `ChatsView` / `ChatListView` | `ChatListViewModel` |
| `ChatDetailView` | `ChatDetailViewModel` |
| Chat info pane | `ChatDetailInfoViewModel` |
| `SettingsView` / `SettingsControl` | `SettingsViewModel` |
| `DebugView` | `DebugViewModel` |
| `ImageViewerView` | `ImageViewerViewModel` |
| `VideoViewerView` | `VideoViewerViewModel` |
| `NewChatDialog` | `NewChatDialogViewModel` |

- ViewModels inherit `Observable` (`Set` / `OnPropertyChanged` / `RaiseProperties`) unless a dedicated INPC type already exists (`ChatMessageViewModel`).
- Commands are `RelayCommand` / `RelayCommand<T>` (`ICommand`). Bind `Command` in XAML.
- ViewModels take **interfaces** in the constructor, never UWP types.

### Collection items with actions are ViewModels

A row/bubble that can be tapped, downloaded, pinned, or played is **not** bound straight to the domain model.

| Item | ViewModel | Created by |
|---|---|---|
| Chat list row | `ChatItemViewModel` | `IChatItemVmFactory` |
| Timeline bubble | `ChatMessageViewModel` | `IChatMessageVmFactory` |
| Chat info (user/group) | `ChatDetailInfoViewModel` | `IChatDetailInfoViewModelFactory` |

Do not `new ChatMessageViewModel(...)` from a view. Use the factory from DI.

### Bindings always; code-behind only what XAML cannot do

**Put in the ViewModel:** state, commands, formatting, when to send, when to download, presence subscribe, dialog/picker/mic calls through contracts.

**Allowed in code-behind:**

- `InitializeComponent`, `OnNavigatedTo` / `From`, system Back
- Resolve VM from `App.Services` and set `DataContext`
- `Loaded` → `InitializeAsync`; `Unloaded` → `UninitializeAsync`
- Storyboards, `MediaElement` / `MediaPlayer`, `ScrollViewer` snap
- Control-to-control focus, visual states, pointer capture
- `Microsoft.Xaml.Interactivity` `EventTriggerBehavior` → `InvokeCommandAction`

**Forbidden in code-behind:** calling Socket/Baileys, building send payloads, parsing protocol nodes, `WhatsAppService.Instance`, business policy (when to refresh names, when to mark read).

Prefer Behaviors over `Click=` handlers.

### Page vs control

Root **pages** (`*View` : `Page`) host **controls** (`*Control` : `UserControl`) when the surface is large (Login, Settings, Debug). The page owns navigation chrome; the control owns the bound layout.

---

## 3. UI folders

```
src/Unison.Uwp/UI/
  Views/              pages and large surface controls
  Controls/           reusable pieces (avatar, setting box, bubbles chrome, chat-info panes)
  Dialogs/            ContentDialog XAML only
  Converters/         IValueConverter
  Templates/          DataTemplates (messages, chat items, preview kinds)
  TemplateSelectors/  DataTemplateSelector
  Helpers/            view-only helpers (rich text, presentation)
```

- **Reusable** visual → `Controls/`. Do not paste a second copy into a page.
- **Dialog** → `Dialogs/` + a method on `IDialogService`. Views do not `new ContentDialog` for product flows.
- **Converter** → `Converters/`. Do not put convert logic in the code-behind.
- Themes → `Themes/Unison/` and `Themes/WhatsApp/` (`Theme.xaml`, `Styles.xaml`, `Controls.xaml`). Use `ThemeResource`, not hardcoded brushes that already exist.
- Do not put ViewModels in UWP. They stay in `Unison.Core/ViewModels/`.

---

## 4. Dialogs

`IDialogService` is the only entry. Methods that need form state take the **target ViewModel** (Imgur pattern):

```csharp
Task ShowPairingCodeAsync(LoginViewModel loginVm, string code);
Task<string> ShowNewChatDialogAsync(NewChatDialogViewModel newChatVm);
```

`DialogService` (UWP) constructs `UI/Dialogs/*` and sets `DataContext`. Catch the “single ContentDialog” COM error; do not crash.

Simple confirm/message/input may be inline `ContentDialog` **inside** `DialogService`, not inside a view.

---

## 5. Dependency injection

Composition root: `App.ConfigureServices` in `Unison.Uwp/App.xaml.cs`.

| Kind | Lifetime |
|---|---|
| Stores, façades, platform adapters, `ShellViewModel` | Singleton |
| Page ViewModels (`Login`, `ChatList`, `ChatDetail`, …) | Transient |
| Collection item VMs | Factory (singleton factory, transient instances) |

**Required:**

- New service → interface in `Unison.Core/Contracts` (WhatsApp domain → `Contracts/WhatsApp/`).
- Implementation in UWP `Services/` (WhatsApp → `Services/WhatsApp/<Area>/` as a **Facade**).
- Register in `ConfigureServices`. Constructor-inject everywhere else.
- After adding a façade that listens to client events, resolve it once at startup (see Profile/History) so it does not miss the first events.

**Forbidden:**

- New `Foo.Instance` / service locator except `App.Services` and `App.GetWhatsAppService()` at the existing composition edges.
- Registering leftover `MessageService` / `ContactService` / `ConnectionService` / `ProfileService` under `Services/WhatsApp/` (not façades). Those files are dead.
- `WhatsAppService.Instance` in new code. Use `IWhatsAppService` from DI, and prefer the façade that owns the subject.

Existing singletons (`SocketBrokerCoordinator.Instance`, `RuntimeDiagnosticsService.Instance`, `LiveTilesService.Instance`) are **legacy**. New code takes the interface (`ISocketBrokerService`, `IRuntimeDiagnostics`, `ILiveTilesService`). Do not add more `.Instance` services.

---

## 6. WhatsApp façades vs client

Screens and ViewModels use:

`IConnectionService`, `IMessageService`, `IChatService`, `IContactService`, `IProfileService`, `IHistoryService`.

They do **not** subscribe to raw `IWhatsAppService` events. Those events are for façades only.

Need a live `WhatsAppSession`? `IWhatsAppSessionProvider` — do not cache the session across reconnects.

`LoginViewModel` talks **only** to `IConnectionService`.

Do not grow `IWhatsAppService` for UI features. Add the member on the façade that owns the subject.

`SocketBridge` implements `IWhatsAppSocket` so the client can keep working. New protocol features go on Socket use cases / modules, then a façade — not on `IWhatsAppSocket` unless the client still must see them.

---

## 7. Navigation, settings, i18n

- Route **keys** in `Unison.Core/Constants/NavigationRoutes.cs`.
- Page **types** only in `NavigatorService`. Core never names a view type.
- Auth boundaries: `NavigateAndClear` (Boot / Start / Login / AppShell). No back stack into login.
- Master-detail stays on `ChatsView` (not a Frame push list → detail).
- Settings keys and defaults: `LocalSettingsConstants`. Do not invent a parallel key string; Background toasts that share a key must keep the **same literal**.
- UI strings: `Strings/{tag}/Resources.resw` + `x:Uid` and/or `IStringResources`. English fallback for missing keys.
- Shipped languages: `en-US`, `pt-BR`, `es-ES`, `it-IT`, `nl-NL`, `id-ID`, `pl-PL`. Add the key to **all** packs, or English-only with fallback — never a hardcoded sentence in a ViewModel.
- Language packs stay in the **main** package (`AppxDefaultResourceQualifiers`). Do not split resource packs.
- Apply language **before** `InitializeComponent` (already in `App` ctor).

---

## 8. Socket / protocol code

- One operation per `UseCase` class. No domain collections inside a use case.
- File header: why it exists, what it does **not** do, `Ports: rc14 <ts path>`.
- Features register on `NodeDispatcher`. Do not add a giant switch on `ConnectionHandler`.
- Host seams go in `Unison.Socket/Abstractions/` (`IWaTransport`, `ISocketLog`, stores). UWP implements them.
- Do not compile or “revive” `SocketClient.cs` / `PairingHandler.cs` (on disk, out of the csproj). Live path is `SocketBridge` → `WhatsAppSession`.
- Do not implement `TransferSocketToBrokerAsync` as a fake success. Until the session can lend the transport, it returns `false`.

---

## 9. C# / XAML style (match the tree)

- Allman braces (opening brace on its own line).
- `sealed` on UWP `Page` / `UserControl` / façade classes.
- Namespaces follow folders (`Unison.Uwp.UI.Views`, `Unison.Core.ViewModels`, `Unison.Socket.UseCases.Messages`).
- New comments and public remarks in **English**.
- Target Core/Socket at **netstandard2.0**. Do not use APIs that break that or .NET Native on ARM.
- UWP min version **10.0.16299** (Windows 10 Mobile). Prefer WinUI 2.7 controls already referenced (`Microsoft.UI.Xaml` 2.7.3).
- Do not add NuGet packages unless the existing stack cannot do the job.

```csharp
// BAD — protocol in a view
private async void Send_Click(...) { await WhatsApp.Socket.SendAsync(...); }

// GOOD — command on the VM, façade in the constructor
SendMessageCommand = new RelayCommand(async () => await _messages.SendTextMessageAsync(...), () => CanCompose);
```

```xml
<!-- BAD -->
<Button Click="Send_Click" Content="Send"/>

<!-- GOOD -->
<Button Command="{Binding SendMessageCommand}" x:Uid="ChatDetail_Send"/>
```

---

## 10. Known exceptions (do not “fix” in passing)

These are hybrid **on purpose** until [Migration](Migration) says otherwise:

- `WhatsAppService` still owns in-memory chats, history body, send transport, persist.
- `ChatDetailView` code-behind still owns the message list, scroll, `MediaElement` chrome.
- `ChatListView` may still mirror `VisibleChats` during initial-sync safe mode.
- `SocketBridge` broker transfer / reclaim / cold restore return false / unused.
- `ChatsModule` in Socket is not instantiated by the bridge yet.
- A few platform helpers still use `.Instance`; new code uses the interface.

When touching these files, move a slice **toward** the spec (VM + façade), do not expand the hybrid.

---

## Related pages

- [Architecture](Architecture) — layers and SocketBridge
- [Application layer](Application-Layer) — façades, DI order, ViewModels
- [UI and shell](UI-and-Shell) — navigation, themes, chat surface
- [Socket stack](Socket-Stack) — use cases and handler split
- [Migration](Migration) — remaining work
