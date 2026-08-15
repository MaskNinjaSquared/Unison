# Changelog

Newest first. This is a wiki-facing merge of the Unison.Socket architecture PR, the product “What’s New” notes, the Socket Broker work, v6.9, and v6.8. It is not a substitute for git history.

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
