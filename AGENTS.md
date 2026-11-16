# Unison

UWP WhatsApp Multi-Device client (Windows 10 Mobile first). Baileys-style companion.

## Before changing code

1. Read `docs/wiki/Home.md` and `docs/wiki/Architecture.md`.
2. Follow `docs/wiki/Coding-Standards.md` (MVVM, DI, folders, façades).
3. If the work is protocol/session, also read `docs/wiki/Socket-Stack.md`.
4. If the work is background/toasts/broker, read `docs/wiki/Background-Broker.md` — transfer is **not** wired on `SocketBridge`.
5. New UI locale: `docs/wiki/Adding-Languages.md` (copy `en-US` `.resw`, register csproj + manifest + `AppLanguage`).
6. Shrinking `WhatsAppService` / moving work to façades: `docs/wiki/WhatsAppService-Extraction.md` (phase 0 done; do not treat partials as façades).
7. Calls / VoIP: `docs/wiki/Voip.md` (meowcaller SOT; do not put engine in Baileys).
8. Product policy (tier / frugal out of the WhatsApp client) and Core **coordinators**: `docs/wiki/Netstandard-WhatsApp-Client.md` § Product policy / Product coordinators. Do **not** name product types `UseCase` (that word is Socket protocol only). Agent what-is-safe-on-a-cheaper-model table is in the same page.

Cursor project rules in `.cursor/rules/` repeat the must-not-break bits. The wiki is the full spec.

## Do not

- Put WinRT or XAML in `Unison.Core` or `Unison.Voip`.
- Reference Core or Socket from `Unison.Background`.
- Reference `Unison.Baileys`, `Unison.Socket` or `Unison.Proto` from `Unison.Core`. Core has no protocol reference; proto-shaped contracts live in `Unison.WhatsAppClient`.
- Call use cases from `ConnectionHandler` or store chats there.
- Subscribe ViewModels to raw client / host-port events (façades only).
- Add `Foo.Instance` services; use constructor DI in `App.ConfigureServices`.
- Revive `SocketClient.cs` (not in the csproj). Live path: `SocketBridge` → `WhatsAppSession`.
- Report a broker handoff that did not happen. `TransferSocketToBrokerAsync` answers true only when ownership moved; cold restore after process termination is not wired.

## Layout

| Path | Role |
|---|---|
| `src/Unison.Uwp` | App, views, DI, façades, SocketBridge |
| `src/Unison.Core` | Contracts, models, ViewModels |
| `src/Unison.WhatsAppClient` | Facades + domain helpers |
| `src/Unison.Voip` | Call session + media I/O ports (meowcaller map; no WinRT) |
| `src/Unison.Socket` | Session / protocol (Baileys 7.0.0-rc14) |
| `src/Unison.Baileys` | Noise, Signal, binary XML |
| `src/Unison.Proto` | Generated WAProto; referenced by Baileys, WhatsAppClient, Socket, UWP, Background (not Core) |
| `src/Unison.Background` | Out-of-process socket activity task |
| `docs/wiki/` | Architecture + coding spec (GitHub wiki source) |
