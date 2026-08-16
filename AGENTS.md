# Unison

UWP WhatsApp Multi-Device client (Windows 10 Mobile first). Baileys-style companion.

## Before changing code

1. Read `docs/wiki/Home.md` and `docs/wiki/Architecture.md`.
2. Follow `docs/wiki/Coding-Standards.md` (MVVM, DI, folders, façades).
3. If the work is protocol/session, also read `docs/wiki/Socket-Stack.md`.
4. If the work is background/toasts/broker, read `docs/wiki/Background-Broker.md` — transfer is **not** wired on `SocketBridge`.
5. New UI locale: `docs/wiki/Adding-Languages.md` (copy `en-US` `.resw`, register csproj + manifest + `AppLanguage`).

Cursor project rules in `.cursor/rules/` repeat the must-not-break bits. The wiki is the full spec.

## Do not

- Put WinRT or XAML in `Unison.Core`.
- Reference Core or Socket from `Unison.Background`.
- Call use cases from `ConnectionHandler` or store chats there.
- Subscribe ViewModels to raw `IWhatsAppService` events (façades only).
- Add `Foo.Instance` services; use constructor DI in `App.ConfigureServices`.
- Revive `SocketClient.cs` (not in the csproj). Live path: `SocketBridge` → `WhatsAppSession`.
- Pretend broker socket handoff works; `TransferSocketToBrokerAsync` returns false.

## Layout

| Path | Role |
|---|---|
| `src/Unison.Uwp` | App, views, DI, façades, SocketBridge |
| `src/Unison.Core` | Contracts, models, ViewModels |
| `src/Unison.Socket` | Session / protocol (Baileys 7.0.0-rc14) |
| `src/Unison.Baileys` | Noise, Signal, binary XML, proto |
| `src/Unison.Background` | Out-of-process socket activity task |
| `docs/wiki/` | Architecture + coding spec (GitHub wiki source) |
