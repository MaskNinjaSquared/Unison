// =============================================================================
// IVoipSystemCallHost
//
// UWP VoipCallCoordinator seam. Core/WhatsAppClient never see WinRT.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>What the wake bridge needs to raise a system incoming-call UI.</summary>
    public sealed class VoipSystemIncomingRequest
    {
        public VoipSystemIncomingRequest(
            string callId,
            string contactName,
            string contactNumber,
            bool isVideo,
            TimeSpan ringTimeout)
        {
            CallId = callId;
            ContactName = contactName;
            ContactNumber = contactNumber;
            IsVideo = isVideo;
            RingTimeout = ringTimeout;
        }

        public string CallId { get; }

        public string ContactName { get; }

        public string ContactNumber { get; }

        public bool IsVideo { get; }

        public TimeSpan RingTimeout { get; }
    }

    public sealed class VoipSystemCallIdEventArgs : EventArgs
    {
        public VoipSystemCallIdEventArgs(string callId)
        {
            CallId = callId;
        }

        public string CallId { get; }
    }

    /// <summary>
    /// Host for Windows.ApplicationModel.Calls.VoipCallCoordinator. No-op when the API
    /// is missing (Desktop without VoIP stack, tests).
    /// </summary>
    public interface IVoipSystemCallHost
    {
        /// <summary>True when RequestNewIncomingCall can run on this device.</summary>
        bool IsAvailable { get; }

        /// <summary>Call id currently shown by the system UI, or null.</summary>
        string CurrentCallId { get; }

        /// <summary>Best-effort ReserveCallResourcesAsync (general CallTask).</summary>
        Task EnsureResourcesReservedAsync();

        /// <summary>Shows the system incoming-call UI for this offer.</summary>
        void RingIncoming(VoipSystemIncomingRequest request);

        /// <summary>Dismisses the system UI for <paramref name="callId"/> (or current).</summary>
        void NotifyEnded(string callId);

        /// <summary>User answered on the system UI — bring Unison forward.</summary>
        event EventHandler<VoipSystemCallIdEventArgs> AnswerRequested;

        /// <summary>User rejected on the system UI.</summary>
        event EventHandler<VoipSystemCallIdEventArgs> RejectRequested;
    }
}
