# VoIP (calls)

Unison call **media** lives outside Baileys. Signaling stanzas stay on the chat socket (`Unison.Socket`); the engine and I/O ports live in `Unison.Voip`, with UWP injecting UDP + mic/speaker.

## References

| Source | Role |
|---|---|
| [purpshell/meowcaller](https://github.com/purpshell/meowcaller) | **Primary SOT** — clean-room Go VoIP (signaling, stun, relay, SRTP/WARP, MLow). MIT. |
| [WACRG](https://wacrg.org) | Protocol datasheets behind meowcaller |
| [SheIITear/baileys-caller](https://github.com/SheIITear/baileys-caller) | Secondary — WA Web WASM path (not the Unison default) |
| [Auties00/Cobalt](https://github.com/auties00/cobalt) | Secondary — JVM + downloaded natives |
| [HirCoir/Baileys.cpp](https://github.com/HirCoir/Baileys.cpp) | **Discarded** — Baileys + Llama.cpp chatbot; no VoIP |
| WhiskeySockets/Baileys | Listen + `rejectCall` only; full VoIP PRs rejected (hardcoded stanzas ≠ engine) |

**Do not** put call engine code in `Unison.Baileys`.

## Layering

```
UI / CallFacade (WhatsAppClient)
        │
        ▼
 Unison.Voip  — CallSession + engine modules (port of meowcaller)
        │ ports: ICallAudioSource/Sink, ICallUdpTransport, ICallSignalingChannel
        ▼
 Unison.Socket — <call> send/recv (RejectCall / TerminateCall UseCases)
        │
 Unison.Uwp    — UwpCallIoFactory (DatagramSocket + MediaCapture later)
```

Analogous to `IWaTransport`: behaviour ports, no WinRT in Voip.

## meowcaller → Unison map

| meowcaller | Unison |
|---|---|
| whatsmeow raw call nodes | `CallHandler` + Call UseCases |
| `Client` / `engine` / `Call` | `CallSession` |
| `signaling/` builders | Socket builders + Voip opaque payloads |
| `stun/` `relay/` `srtp/` `mlow/` | `Unison.Voip.Engine.*` (scaffold → port with KAT) |
| `Source` / `Sink` | `ICallAudioSource` / `ICallAudioSink` |
| UDP sockets | `ICallUdpTransport` (UWP `DatagramSocket`) |

## Status

| Piece | Status |
|---|---|
| Package + ports + Null/UWP stub factory | Done (skeleton) |
| `RejectCall` / `TerminateCall` UseCases | Done (signaling MVP) |
| `CallSession` + `CallFacade` / `ICallService` | Done (no media yet) |
| Engine modules (stun/relay/srtp/mlow) | Scaffold only — Opus review before port |
| Real audio 1:1 | Not started |

Inbound offer toast/log (pre-existing) is unchanged. Outbound offer/accept wait on engine payloads — UI “calling unavailable” stays until media lands.

## Opus review checklist

- [ ] Voip has no WinRT / no Baileys VoIP growth
- [ ] Reject/terminate stanza shape matches meowcaller `BuildReject` / `BuildTerminate` (attrs `call-id`, `call-creator`, wrap `<call to=…>`)
- [ ] Ports are Source/Sink/UDP — not a fake browser WebRTC peer connection API
- [ ] Engine stubs do not pretend to encode MLow
- [ ] DI registers factory; no new `Foo.Instance`
