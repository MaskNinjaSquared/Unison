# Unison
<img width="1024" height="505" alt="UNISON LOGO" src="https://github.com/user-attachments/assets/9f2675d6-6c8c-4e4c-8578-e2575d80af49" />


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
| `src/Unison.Uwp` | UWP app, views, DI adapters, SocketBridge |
| `src/Unison.Core` | Contracts, models, ViewModels |
| `src/Unison.Socket` | WhatsApp session / protocol (Baileys 7.0.0-rc14) |
| `src/Unison.Baileys` | Noise, Signal, binary XML, protobuf, crypto |
| `src/Unison.Background` | Out-of-process socket activity task |

Solution: `Unison.slnx`. Docs: [`docs/wiki/`](docs/wiki/Home.md) (architecture) · [`Coding-Standards`](docs/wiki/Coding-Standards.md) (how to write code). Agents: [`AGENTS.md`](AGENTS.md).


<img width="2314" height="1136" alt="image" src="https://github.com/user-attachments/assets/6869747d-5ef8-47fb-9c04-bb2d3f81f2ca" />


## Build & deploy

```powershell
.\scripts\build_sign.ps1          # ARM Release by default
.\scripts\deploy.ps1 -IP <device>
```

> [!WARNING]
> Debug/ARM builds do not work on Windows Phone unless compiled with the .NET Native compiler. This project uses newer .NET Standard APIs that require runtime support unavailable in non-.NET Native builds.

## License

Source-available under the [PolyForm Noncommercial License 1.0.0](LICENSE).

You may use, modify, and share Unison for **non-commercial** purposes (personal, hobby, research, and the non-commercial organizations listed in the license). Selling Unison, or using it to provide a commercial product or service, is not allowed.

This is not an OSI-approved open-source license. The full terms are in [`LICENSE`](LICENSE).
