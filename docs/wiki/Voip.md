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
 Unison.Uwp    — UwpCallIoFactory (DatagramSocket + AudioGraph mic/speaker)
```

Analogous to `IWaTransport`: behaviour ports, no WinRT in Voip.

**Relay transport (meowcaller `relay/relay.go`)** is not raw UDP: UDP → DTLS 1.2 client (self-signed cert, server cert not verified) → SCTP → pre-negotiated DataChannel id=0 label `pre-negotiated`. STUN allocate / WA ping (0x0801) / SRTP-RTP all travel as binary DataChannel messages. `ICallUdpTransport` stays the bottom layer; DTLS + SCTP sit above it inside `Unison.Voip` (no WinRT). This is the WebRTC *DataChannel* transport only — no ICE, no DTLS-SRTP, no browser peer connection.

## meowcaller → Unison map

| meowcaller | Unison |
|---|---|
| whatsmeow raw call nodes | `CallHandler` + Call UseCases |
| `Client` / `engine` / `Call` | `CallSession` + `CallEngine` |
| `signaling/` builders | Socket builders + Voip opaque payloads |
| `stun/` | `Engine/Stun` — `StunCodec`, `WaStunAllocate` (WASM / APK allocate, stream descriptors, WA ping 0x0801), `StunParse` (Go KATs) |
| `relay/` | `Engine/Relay` — `RelayPacketClassifier`, `GroupForwarding`, `UdpDatagramTransport` + BC DTLS client, minimal SCTP + DataChannel id=0, `RelayMediaChannel`, `RelayMediaLoop` (allocate+ping). `<relay>` stanza parse is Socket `CallRelayParser` |
| `srtp/` | `Engine/Crypto` — `Hkdf`, `E2eSrtp`, `HbhSrtp`, `Sframe`, `Warp` (Go KATs) |
| `rtp/` | `Engine/Rtp` — `SsrcDerivation`, `RtpPacket` / `RtpHeader` / `RtpStream`, `RtcpPacket` + reception stats (Go KATs) |
| `mlow/` | `Engine/Mlow` — encoder + decoder (byte-exact vs meowcaller; silence RTP on Answer) |
| `Source` / `Sink` | `ICallAudioSource` / `ICallAudioSink` — UWP AudioGraph 16 kHz mono (Communications) |
| UDP sockets | `ICallUdpTransport` — `UwpDatagramCallUdpTransport` + `LoopbackUdpTransport` KAT |

## Status

| Piece | Status |
|---|---|
| Package + ports + Null/UWP factory | Done — `UwpCallIoFactory`: `DatagramSocket` + AudioGraph Source/Sink |
| `RejectCall` / `TerminateCall` UseCases | Done (signaling MVP) |
| `CallSession` + `CallFacade` / `ICallService` | Done — session owns `CallEngine` (phase hooks; `IsMediaReady` false) |
| Active call (ring / end from live statuses) | Done — `ActiveCallEventPolicy`, `CallFacade.ActiveCall` |
| Call screen | Done — `CallScreenViewModel` (Core), `LegacyCallView` in a full-window popup (`ICallScreenPresenter`); every theme uses it for now |
| Incoming wake (V1) | Done — `VoipCallCoordinator` when window hidden; Unigram-aligned manifest (`windows.voipCall`, `ms-voip-call` + `unison-call`); broker hold while ringing; keep-alive re-apply. V2 broker / V3 media not done |
| Video offers | Companion **toast-only** — `ActiveCallEventPolicy` Ignores video (no ActiveCall / VoipUI); body says answer on phone |
| Audio vs video toast | Done — `NotifyCallAlert(..., isVideo)` + `Notification_IncomingVoiceCallBody` / `…VideoCallBody`; health `call-alert` includes `video=` |
| Engine modules (stun/relay/srtp/rtp/mlow) | Pure layers + DTLS/SCTP + allocate/ping + MLow + AudioGraph on Answer. `IsMediaAvailable` still false until a live call is verified |
| Media prerequisites in signaling | Done — `CallKeyDecryptor` (1:1 offer `<enc>` → `Message.Call.CallKey`), `CallRelayParser` (`<relay>` key / indexed tokens / IPv4 `te2`, `peer_pid`, media endpoint pick, relaylatency probes), callee `relaylatency` echo (`RelayLatencyCallUseCase` via `ICallSignalingChannel.SendRelayLatencyAsync`). Host maps them to `CallEventNotifyRequest.MediaHints`; `CallFacade.GetMediaHints(callId)` keeps them for the active call only |
| Control actions (`mute_v2` / `relaylatency` / `transport`) | Done — `CallSignalingControl.IsControlTag`; host flags `IsSignalingControl`, so `MessageFacade` never toasts or logs them (fixed duplicate ring toast on `mute_v2`) |
| Ended-offer notice | Done — `CallStatusReader`: offer with `is_call_ended` / `terminate_reason` is a Terminate, never rings |
| Real audio 1:1 | AudioGraph wired (mute → silence RTP). Live-relay + earpiece/speaker route still to verify |

`SocketBridgeRemoteHooks` hands live call statuses to `ICallService`: a fresh **audio** offer from someone else (younger than `ActiveCallEventPolicy.RingFreshWindow`, 60 s) opens the screen as ringing and fires **preaccept** (meowcaller eager prep); **video** offers stay toast-only. Missed / Completed / AnsweredElsewhere for the same call id closes it, and an unanswered ring drops itself after the same window. End call **rejects** while still ringing, or **terminates** after Answer (even if deferred accept never left). **Answer** (meowcaller): phase → `Connecting` + `acceptPending`; the **accept** stanza waits for the caller’s first **`mute_v2`**. Stanza `to=` is meowcaller `m.from` (outer `<call from>`, `SignalingToJid`), not only the UI peer JID. Media starts on Answer when relay hints exist: AudioGraph capture/playback + MLow + silence when muted or mic unavailable (`IsMediaAvailable` / `CallEngine.IsMediaReady` stay false until live verification). Speaker toggle from ringing onward via `IVoicePlaybackRoutingService.SetPreferredRoute` (MediaPlayer path; AudioGraph still uses Communications default — earpiece/speaker binding TBD); mute / bluetooth stay gated on a connected/media call.

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
4. Answer → `AnswerActiveCallAsync` (local Connecting) + **`Launcher.LaunchUriAsync(unison-call:answer?callId=…)`** (own PFN) + `Window.Activate` + call screen; Reject/End → `NotifyCallEnded` + `EndActiveCallAsync`.
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
- [x] Reject stanza: Baileys companion shape (`from=me`, `count="0"`) + meowcaller `id` on `<call>`
- [x] Terminate (hangup after accept): meowcaller shape — wrapper `to`+`id` only (no `from`), no reason on plain hang-up
- [x] End signals **before** clearing ActiveCall; failures land in Runtime Health as `call|facade` / `call|signaling` (`[Call] …`) — chat `send=*` counters ignore `<call>` nodes
- [x] Preaccept on ring; Accept deferred to first `mute_v2` after Answer (meowcaller); terminate uses `SignalingToJid` = outer `from`
- [x] Ports are Source/Sink/UDP — not a fake browser WebRTC peer connection API
- [x] Engine stubs do not pretend to encode MLow (`IsMediaReady` / audio stub throw NotSupported)
- [x] DI registers `ICallIoFactory` → `UwpCallIoFactory`; no new `Foo.Instance`
- [x] Incoming wake uses `VoipCallCoordinator` (UWP); Background never references Core/Socket
- [x] Min platform floor Creators **16299** (+ PolySharp where needed); no WebRTC dependency
