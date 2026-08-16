# Unison

Unison is a **UWP WhatsApp Multi-Device client**, aimed mainly at **Windows 10 Mobile**. It links as a Baileys-style companion device: the phone stays the primary, Unison is a secondary session.

This wiki describes the **current architecture** of the tree in `Unison.slnx`. Earlier v6.8 / v6.9 notes are folded into [Changelog](Changelog) and [Migration](Migration).

## What works today

Day-to-day chat on a linked session:

- Text, image and voice-note send and receive
- Gallery attach and in-chat microphone recording
- Contact names and profile pictures (including the rc14 LID flow)
- On-demand image / video / document download and fullscreen viewers
- New chats by phone number, plus self-chat
- Group and user chat-info panes
- Background socket activity task and real message toasts
- Pin chats to Start
- Localized UI (eight shipped languages plus System)

The **Unison.Socket** stack (Baileys **7.0.0-rc14**) is now the foundation for WhatsApp communication. `WhatsAppService` is still present as a compatibility client and will shrink as remaining legacy flows move to facades.

## Wiki map

| Page | Contents |
|---|---|
| [Architecture](Architecture) | Projects, layers, and the client vs policy split |
| [Socket stack](Socket-Stack) | `Unison.Socket`, Baileys, use cases, protocol |
| [Application layer](Application-Layer) | Core, UWP facades, DI, ViewModels |
| [Background broker](Background-Broker) | Out-of-process socket, journal, toasts |
| [UI and shell](UI-and-Shell) | Navigation, themes, i18n, chat surface |
| [Coding standards](Coding-Standards) | Spec: MVVM, DI, folders, façades — how to write new code |
| [Adding languages](Adding-Languages) | Tutorial: new `.resw` pack, package registration, `AppLanguage` enum |
| [Migration](Migration) | What moved, what is still legacy |
| [Changelog](Changelog) | Newest → oldest product and architecture notes |

## Solution layout

```
Unison.slnx
├── src/Unison.Uwp          UWP app, views, DI adapters, SocketBridge
├── src/Unison.Core         Contracts, models, ViewModels (no XAML)
├── src/Unison.Socket       WhatsApp session / protocol (netstandard2.0)
├── src/Unison.Baileys      Noise, Signal, binary XML, protobuf, crypto
└── src/Unison.Background   Out-of-process SocketActivity task
```

Build and deploy (ARM Release by default):

```powershell
.\scripts\build_sign.ps1
.\scripts\deploy.ps1 -IP <device>
```

Debug/ARM builds do not run on Windows Phone unless compiled with the .NET Native compiler.

## Publishing these pages to GitHub Wiki

The files in `docs/wiki/` are the source. Copy them into the repository wiki:

```powershell
git clone https://github.com/<org>/<repo>.wiki.git
copy docs\wiki\*.md <repo>.wiki\
cd <repo>.wiki
git add .
git commit -m "Sync architecture wiki from docs/wiki"
git push
```

GitHub Wiki page names match the file names (`Architecture.md` → **Architecture**). Keep `_Sidebar.md` so the left nav stays in sync.
