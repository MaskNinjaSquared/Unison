using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Call signaling and the one call the screen tracks. Media accept/offer stay gated until
    /// the Voip engine lands (<see cref="IsMediaAvailable"/>).
    /// </summary>
    public interface ICallService
    {
        /// <summary>
        /// Places a 1:1 audio call (meowcaller placeCall). Opens the call screen in Dialing.
        /// </summary>
        Task StartOutgoingAudioCallAsync(
            string peerJid,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>Reject an inbound ringing offer.</summary>
        Task RejectAsync(
            string callId,
            string peerJid,
            string callCreatorJid,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>Terminate / hang up.</summary>
        Task HangupAsync(
            string callId,
            string peerJid,
            string callCreatorJid,
            string reason = null,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>True when the host can place/answer with media (engine ready).</summary>
        bool IsMediaAvailable { get; }

        /// <summary>The call the screen shows, or null when none is up.</summary>
        ActiveCallSnapshot ActiveCall { get; }

        /// <summary>Raised off the UI thread whenever <see cref="ActiveCall"/> changes.</summary>
        event EventHandler ActiveCallChanged;

        /// <summary>Live call statuses from the socket: a fresh offer rings, a later status ends it.</summary>
        void NotifyCallEvents(IReadOnlyList<CallEventNotifyRequest> events);

        /// <summary>Rejects a ringing offer, hangs up anything else. No-op when nothing is up.</summary>
        Task EndActiveCallAsync(CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Accepts an inbound ring (meowcaller): phase → Connecting and arms deferred accept;
        /// the accept stanza goes out on the caller's first <c>mute_v2</c>.
        /// Media/engine still gated by <see cref="IsMediaAvailable"/>. No-op when nothing is ringing.
        /// </summary>
        Task AnswerActiveCallAsync(CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>Mute applies once dialing or connected.</summary>
        void SetMuted(bool muted);

        /// <summary>Speaker flag applies while ringing, connecting, dialing or active.</summary>
        void SetSpeakerOn(bool on);

        /// <summary>Bluetooth applies once dialing or connected.</summary>
        void SetBluetoothOn(bool on);
    }
}
