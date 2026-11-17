// =============================================================================
// UwpVoipSystemCallHost
//
// VoipCallCoordinator adapter. Raises the system incoming-call UI so Mobile can
// bring Unison forward on Answer. No-op when the Calls API is absent.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers.Calls;
using Unison.Uwp.Services;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Calls;
using Windows.Foundation.Metadata;
using Windows.System;
using Windows.UI.Xaml;

namespace Unison.Uwp.Services.Calls
{
    internal sealed class UwpVoipSystemCallHost : IVoipSystemCallHost
    {
        public const string CallTaskEntryPoint = "Unison.Background.VoipCallResourceTask";

        /// <summary>System VoIP scheme (Unigram RS2); used as RequestNewIncomingCall context.</summary>
        public const string SystemProtocolScheme = "ms-voip-call";

        /// <summary>App-owned deep link; also accepted in OnActivated.</summary>
        public const string ProtocolScheme = VoipCallActivationUri.AppScheme;

        private readonly object _gate = new object();
        private VoipPhoneCall _current;
        private string _currentCallId;
        private bool _resourcesReserved;
        private bool _reserveAttempted;

        public bool IsAvailable =>
            ApiInformation.IsTypePresent("Windows.ApplicationModel.Calls.VoipCallCoordinator");

        public string CurrentCallId
        {
            get
            {
                lock (_gate)
                {
                    return _currentCallId;
                }
            }
        }

        public event EventHandler<VoipSystemCallIdEventArgs> AnswerRequested;

        public event EventHandler<VoipSystemCallIdEventArgs> RejectRequested;

        public async Task EnsureResourcesReservedAsync()
        {
            if (!IsAvailable || _resourcesReserved || _reserveAttempted)
            {
                return;
            }

            _reserveAttempted = true;
            try
            {
                await VoipCallCoordinator.GetDefault()
                    .ReserveCallResourcesAsync(CallTaskEntryPoint);
                _resourcesReserved = true;
            }
            catch
            {
                // Slot / task registration may fail on Desktop; RingIncoming still tried.
            }
        }

        public void RingIncoming(VoipSystemIncomingRequest request)
        {
            if (!IsAvailable || request == null || string.IsNullOrWhiteSpace(request.CallId))
            {
                return;
            }

            try
            {
                VoipPhoneCallMedia media = request.IsVideo
                    ? (VoipPhoneCallMedia.Audio | VoipPhoneCallMedia.Video)
                    : VoipPhoneCallMedia.Audio;

                string context = SystemProtocolScheme + ":incoming?callId=" +
                    Uri.EscapeDataString(request.CallId);
                string contactName = string.IsNullOrWhiteSpace(request.ContactName)
                    ? request.ContactNumber
                    : request.ContactName;
                string contactNumber = string.IsNullOrWhiteSpace(request.ContactNumber)
                    ? request.CallId
                    : request.ContactNumber;

                VoipPhoneCall call = VoipCallCoordinator.GetDefault().RequestNewIncomingCall(
                    context,
                    contactName ?? string.Empty,
                    contactNumber ?? string.Empty,
                    contactImage: null,
                    serviceName: "Unison",
                    brandingImage: null,
                    callDetails: request.IsVideo ? "Video call" : "Voice call",
                    ringtone: null,
                    media,
                    request.RingTimeout <= TimeSpan.Zero
                        ? TimeSpan.FromSeconds(45)
                        : request.RingTimeout);

                call.AnswerRequested += OnAnswerRequested;
                call.RejectRequested += OnRejectRequested;
                call.EndRequested += OnEndRequested;

                lock (_gate)
                {
                    DetachCurrent_NoLock();
                    _current = call;
                    _currentCallId = request.CallId;
                }
            }
            catch
            {
                // Coordinator can throw when resources were not reserved.
            }
        }

        public void NotifyEnded(string callId)
        {
            VoipPhoneCall ending;
            lock (_gate)
            {
                if (_current == null)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(callId) &&
                    !string.Equals(_currentCallId, callId, StringComparison.Ordinal))
                {
                    return;
                }

                ending = _current;
                DetachCurrent_NoLock();
            }

            try
            {
                ending.NotifyCallEnded();
            }
            catch
            {
            }
        }

        /// <summary>
        /// Foregrounds Unison after a system Answer via own-protocol activation (Mobile-safe).
        /// </summary>
        public static async Task ActivateAppForAnswerAsync(string callId)
        {
            string uri = VoipCallActivationUri.ForAnswer(callId);
            RuntimeDiagnosticsService.Instance.Write("call", "activate-launch", "uri=" + uri);

            try
            {
                var options = new LauncherOptions();
                try
                {
                    options.TargetApplicationPackageFamilyName = Package.Current.Id.FamilyName;
                }
                catch
                {
                }

                await Launcher.LaunchUriAsync(new Uri(uri), options);
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.Write(
                    "call",
                    "activate-launch-failed",
                    ex.Message);
            }

            try
            {
                Window.Current?.Activate();
            }
            catch
            {
            }
        }

        private void OnAnswerRequested(VoipPhoneCall sender, CallAnswerEventArgs args)
        {
            string callId;
            lock (_gate)
            {
                callId = _currentCallId;
                // Drop the slot without NotifyCallEnded — Accepted already owns the call.
                DetachCurrent_NoLock();
            }

            try
            {
                VoipPhoneCallMedia media = VoipPhoneCallMedia.Audio;
                sender.NotifyCallAccepted(media);
            }
            catch
            {
            }

            AnswerRequested?.Invoke(this, new VoipSystemCallIdEventArgs(callId));
        }

        private void OnRejectRequested(VoipPhoneCall sender, CallRejectEventArgs args)
        {
            string callId;
            lock (_gate)
            {
                callId = _currentCallId;
                DetachCurrent_NoLock();
            }

            try
            {
                sender.NotifyCallEnded();
            }
            catch
            {
            }

            RejectRequested?.Invoke(this, new VoipSystemCallIdEventArgs(callId));
        }

        private void OnEndRequested(VoipPhoneCall sender, CallStateChangeEventArgs args)
        {
            string callId;
            lock (_gate)
            {
                callId = _currentCallId;
                DetachCurrent_NoLock();
            }

            try
            {
                sender.NotifyCallEnded();
            }
            catch
            {
            }

            RejectRequested?.Invoke(this, new VoipSystemCallIdEventArgs(callId));
        }

        private void DetachCurrent_NoLock()
        {
            if (_current == null)
            {
                return;
            }

            try
            {
                _current.AnswerRequested -= OnAnswerRequested;
                _current.RejectRequested -= OnRejectRequested;
                _current.EndRequested -= OnEndRequested;
            }
            catch
            {
            }

            _current = null;
            _currentCallId = null;
        }
    }
}
