# Unison

UWP WhatsApp Multi-Device client, aimed mainly at Windows 10 Mobile. Built as a Baileys-style secondary device.

## Status

Works for day-to-day chat on a linked session:

- text/image/voice-note send & receive
- attach from gallery and in-chat microphone recording
- contact names & profile pictures
- on-demand image download + fullscreen viewer
- new chats by phone number; self-chat
- background socket
- real toast notifications for message envelopes
- UI localization

The client is still under development. Note: None of the early users who tested the app were banned. If you've been blocked, please let us know. 

## Layout

| Project | Role |
|---|---|
| `src/Unison.Uwp` | UWP app, views, DI adapters |
| `src/Unison.Core` | Contracts, models, ViewModels |
| `src/Unison.Baileys` | Protocol / crypto |
| `src/Unison.Background` | Out-of-process socket activity task |

Solution: `Unison.slnx`.

## Build & deploy

```powershell
.\scripts\build_sign.ps1          # ARM Release by default
.\scripts\deploy.ps1 -IP <device>
```

> [!WARNING]
> Debug/ARM builds do not work on Windows Phone unless compiled with the .NET Native compiler. This project uses newer .NET Standard APIs that require runtime support unavailable in non-.NET Native builds.
