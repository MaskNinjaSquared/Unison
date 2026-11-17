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
| Active call (ring / end from live statuses) | Done — `ActiveCallEventPolicy`, `CallFacade.ActiveCall` |
| Call screen | Done — `CallScreenViewModel` (Core), `LegacyCallView` in a full-window popup (`ICallScreenPresenter`); every theme uses it for now |
| Incoming wake (V1) | Done — `VoipCallCoordinator` when window hidden; Unigram-aligned manifest (`windows.voipCall`, `ms-voip-call` + `unison-call`); broker hold while ringing; keep-alive re-apply. V2 broker / V3 media not done |
| Engine modules (stun/relay/srtp/mlow) | Scaffold only — Opus review before port |
| Real audio 1:1 | Not started |

Inbound offer toast/log (pre-existing) is unchanged. `SocketBridgeRemoteHooks` also hands the live call statuses to `ICallService`: a fresh offer from someone else (younger than `ActiveCallEventPolicy.RingFreshWindow`, 60 s) opens the screen as ringing; Missed / Completed / AnsweredElsewhere for the same call id closes it, and an unanswered ring drops itself after the same window. End call rejects a ringing offer or terminates anything else. Answer and outbound offer wait on engine payloads, so speaker / mute / bluetooth stay disabled until a call is connected — which cannot happen before media lands.

## Incoming call while background (Mobile wake)

### Unigram RS2 (guide) vs Unison V1 vs WebRTC sketch

| Piece | Unigram RS2 Mobile | Unison V1 | WebRTC / ControlChannel sketch |
|---|---|---|---|
| Process alive | Location ExtendedExecution | Same setting + re-apply on ring; broker hold while inbound ringing | N/A |
| Foreground UI | `OverlayPage` full-stretch | `LegacyCallView` Popup | In-app or system |
| Background bring-to-front | Overlay only (does **not** maximize from Start) | `VoipCallCoordinator.RequestNewIncomingCall` when window hidden | System UI |
| Manifest | `voipCall` + `ms-voip-call` (+ `windows.voipCall` on package) | Same + `unison-call` + `VoipCallResourceTask` | ControlChannel tasks |
| Signaling channel | TDLib updates | Existing WA chat socket | Separate MessageWebSocket |

Do **not** add a second ControlChannel WebSocket for WhatsApp calls. Signaling stays on the WA socket; broker delivery is `SocketActivityTrigger` (see [Background broker](Background-Broker)). Media is meowcaller ports, not browser WebRTC.

### Current path

1. Offer on the WA socket → `CallFacade` rings.
2. Window visible → in-app Popup only (Unigram-style).
3. Window hidden → `VoipIncomingCallWakeBridge` → system call UI (`ms-voip-call:` context).
4. Answer → `NotifyCallAccepted` + `Window.Activate` + existing call screen; Reject/End → `NotifyCallEnded` + `EndActiveCallAsync`.
5. While inbound ringing, `BrokerHandoffHold.ActiveIncomingCall` keeps the socket in-process so statuses still reach `CallFacade`.

### Target flow

```
WA socket (process alive)
        │ call offer
        ▼
 VoipIncomingCallWakeBridge
   ├─ visible → LegacyCallView Popup
   └─ hidden  → VoipCallCoordinator.RequestNewIncomingCall (ms-voip-call:…)
        │ user answers
        ▼
 Protocol activation (ms-voip-call / unison-call) + Window.Activate + CallScreenPresenter
```

### Phases

| Phase | Work | Accept |
|---|---|---|
| V1 | **Done** — coordinator + Unigram-aligned manifest (`windows.voipCall`, `ms-voip-call`); ring when window hidden; Answer/Reject; keep-alive re-apply; broker hold while ringing | Background (process alive) shows system call UI; Answer brings Unison forward |
| V2 | Broker path: call offer out-of-proc → same coordinator (no Core/Socket from Background) | Suspended + socket in broker still rings system UI |
| V3 | Media → `NotifyCallActive`; GSM hold; optional WNS only if needed | Bidirectional audio + hold |

Do **not** treat `Window.Current.Activate()` alone as the Mobile fix — it does not replace `VoipCallCoordinator` for bringing the app forward from Start/lock screen.

## Opus review checklist

- [ ] Voip has no WinRT / no Baileys VoIP growth
- [ ] Reject/terminate stanza shape matches meowcaller `BuildReject` / `BuildTerminate` (attrs `call-id`, `call-creator`, wrap `<call to=…>`)
- [ ] Ports are Source/Sink/UDP — not a fake browser WebRTC peer connection API
- [ ] Engine stubs do not pretend to encode MLow
- [ ] DI registers factory; no new `Foo.Instance`
- [ ] Incoming wake uses `VoipCallCoordinator` (UWP); Background never references Core/Socket
