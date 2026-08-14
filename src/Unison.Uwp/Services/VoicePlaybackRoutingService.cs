using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Windows.Devices.Enumeration;
using Windows.Devices.Sensors;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Media.Playback;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// Mobile voice-note routing: loudspeaker by default, earpiece + screen-off when
    /// the proximity sensor reports near-ear. Desktop is a no-op (media defaults).
    /// </summary>
    public sealed class VoicePlaybackRoutingService : IVoicePlaybackRoutingService
    {
        private readonly ISystemInfoProvider _systemInfo;
        private readonly object _gate = new object();

        private MediaPlayer _player;
        private bool _sessionActive;
        private bool _nearEar;
        private VoicePlaybackRoute _currentRoute = VoicePlaybackRoute.Speaker;

        private ProximitySensor _proximitySensor;
        private TypedEventHandler<ProximitySensor, ProximitySensorReadingChangedEventArgs> _proximityHandler;
        private ProximitySensorDisplayOnOffController _displayController;
        private bool _proximityLookupStarted;
        private CoreDispatcher _dispatcher;

        public VoicePlaybackRoutingService(ISystemInfoProvider systemInfo)
        {
            _systemInfo = systemInfo ?? throw new ArgumentNullException(nameof(systemInfo));
        }

        public bool IsSupported =>
            _systemInfo.IsMobile() && !_systemInfo.IsContinuum();

        public VoicePlaybackRoute CurrentRoute
        {
            get
            {
                lock (_gate)
                {
                    return _currentRoute;
                }
            }
        }

        public string PreferredAudioCategory =>
            IsSupported ? "Communications" : "Media";

        public event EventHandler<VoicePlaybackRoute> RouteChanged;

        public void AttachPlayer(object mediaPlayer)
        {
            var player = mediaPlayer as MediaPlayer;
            lock (_gate)
            {
                _player = player;
            }
        }

        public void DetachPlayer()
        {
            EndSession();
            lock (_gate)
            {
                _player = null;
            }
        }

        public void BeginSession()
        {
            if (!IsSupported)
            {
                return;
            }

            CaptureDispatcher();
            lock (_gate)
            {
                _sessionActive = true;
                _nearEar = false;
            }

            // Communications defaults to earpiece on Mobile — force speaker now and
            // again shortly after Play when the render graph is live (SetAudioEndpoint
            // is ignored before an active Communications stream exists).
            ApplyRoute(VoicePlaybackRoute.Speaker, "session-begin");
            _ = EnsureProximityAsync();
            _ = ReassertSpeakerAfterStreamStartsAsync();
        }

        public void EndSession()
        {
            if (!IsSupported)
            {
                return;
            }

            lock (_gate)
            {
                _sessionActive = false;
                _nearEar = false;
            }

            ReleaseDisplayController();
            DetachProximity();
            ApplyRoute(VoicePlaybackRoute.Speaker, "session-end");
        }

        private async Task ReassertSpeakerAfterStreamStartsAsync()
        {
            try
            {
                // First assert after a tick (Play may not have opened yet).
                await Task.Delay(120).ConfigureAwait(true);
                if (!IsSessionActive() || IsNearEar())
                {
                    return;
                }

                ApplyRoute(VoicePlaybackRoute.Speaker, "reassert-early");

                // Second assert once the decoder usually has a live render path.
                await Task.Delay(350).ConfigureAwait(true);
                if (!IsSessionActive() || IsNearEar())
                {
                    return;
                }

                ApplyRoute(VoicePlaybackRoute.Speaker, "reassert-late");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[VoicePlaybackRouting] ReassertSpeaker: " + ex.Message);
            }
        }

        private bool IsSessionActive()
        {
            lock (_gate)
            {
                return _sessionActive;
            }
        }

        private bool IsNearEar()
        {
            lock (_gate)
            {
                return _nearEar;
            }
        }

        private void CaptureDispatcher()
        {
            try
            {
                _dispatcher = Window.Current?.Dispatcher ?? _dispatcher;
            }
            catch
            {
            }
        }

        private async Task EnsureProximityAsync()
        {
            if (_proximitySensor != null || _proximityLookupStarted)
            {
                return;
            }

            _proximityLookupStarted = true;
            try
            {
                if (!ApiInformation.IsTypePresent("Windows.Devices.Sensors.ProximitySensor"))
                {
                    return;
                }

                string selector = ProximitySensor.GetDeviceSelector();
                DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);
                if (devices == null || devices.Count == 0)
                {
                    Debug.WriteLine("[VoicePlaybackRouting] No proximity sensor");
                    return;
                }

                _proximitySensor = ProximitySensor.FromId(devices[0].Id);
                if (_proximitySensor == null)
                {
                    return;
                }

                _proximityHandler = OnProximityReadingChanged;
                _proximitySensor.ReadingChanged += _proximityHandler;
                Debug.WriteLine("[VoicePlaybackRouting] Proximity hooked");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[VoicePlaybackRouting] Proximity: " + ex.Message);
            }
        }

        private void DetachProximity()
        {
            try
            {
                if (_proximitySensor != null && _proximityHandler != null)
                {
                    _proximitySensor.ReadingChanged -= _proximityHandler;
                }
            }
            catch
            {
            }

            _proximitySensor = null;
            _proximityHandler = null;
            _proximityLookupStarted = false;
        }

        private async void OnProximityReadingChanged(
            ProximitySensor sender,
            ProximitySensorReadingChangedEventArgs args)
        {
            bool near = args?.Reading != null && args.Reading.IsDetected;
            try
            {
                var dispatcher = _dispatcher;
                if (dispatcher != null && !dispatcher.HasThreadAccess)
                {
                    await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => ApplyProximity(near));
                }
                else
                {
                    ApplyProximity(near);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[VoicePlaybackRouting] Proximity dispatch: " + ex.Message);
            }
        }

        private void ApplyProximity(bool near)
        {
            if (!IsSessionActive())
            {
                return;
            }

            bool changed;
            lock (_gate)
            {
                changed = _nearEar != near;
                _nearEar = near;
            }

            if (!changed)
            {
                return;
            }

            if (near)
            {
                ApplyRoute(VoicePlaybackRoute.Earpiece, "proximity-near");
                EnsureDisplayController();
            }
            else
            {
                ReleaseDisplayController();
                ApplyRoute(VoicePlaybackRoute.Speaker, "proximity-far");
            }
        }

        private void ApplyRoute(VoicePlaybackRoute route, string reason)
        {
            MediaPlayer player;
            lock (_gate)
            {
                player = _player;
                _currentRoute = route;
            }

            bool speaker = route == VoicePlaybackRoute.Speaker;
            bool routed = false;

            try
            {
                if (player != null)
                {
                    // Communications is required for AudioRoutingManager endpoints.
                    player.AudioCategory = MediaPlayerAudioCategory.Communications;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[VoicePlaybackRouting] AudioCategory Communications: " + ex.Message);
            }

            routed = TrySetAudioEndpoint(speaker, reason);

            // Fallback when routing APIs/capability are missing: Media = loudspeaker on Mobile.
            if (speaker && !routed && player != null)
            {
                try
                {
                    player.AudioCategory = MediaPlayerAudioCategory.Media;
                    Debug.WriteLine(
                        "[VoicePlaybackRouting] Fallback AudioCategory=Media (speaker) reason=" +
                        (reason ?? string.Empty));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[VoicePlaybackRouting] Media fallback: " + ex.Message);
                }
            }

            try
            {
                RouteChanged?.Invoke(this, route);
            }
            catch
            {
            }

            Debug.WriteLine(
                "[VoicePlaybackRouting] route=" + route +
                " routed=" + routed +
                " reason=" + (reason ?? string.Empty));
        }

        private bool TrySetAudioEndpoint(bool speakerphone, string reason)
        {
            try
            {
                if (!ApiInformation.IsTypePresent("Windows.Phone.Media.Devices.AudioRoutingManager"))
                {
                    return false;
                }

                var manager = Windows.Phone.Media.Devices.AudioRoutingManager.GetDefault();
                if (manager == null)
                {
                    return false;
                }

                var available = manager.AvailableAudioEndpoints;
                var endpoint = speakerphone
                    ? Windows.Phone.Media.Devices.AudioRoutingEndpoint.Speakerphone
                    : Windows.Phone.Media.Devices.AudioRoutingEndpoint.Earpiece;

                // Bail if the endpoint is not currently available (wired headset, etc.).
                if (speakerphone &&
                    (available & Windows.Phone.Media.Devices.AvailableAudioRoutingEndpoints.Speakerphone) == 0)
                {
                    Debug.WriteLine("[VoicePlaybackRouting] Speakerphone unavailable (" + reason + ")");
                    return false;
                }

                if (!speakerphone &&
                    (available & Windows.Phone.Media.Devices.AvailableAudioRoutingEndpoints.Earpiece) == 0)
                {
                    Debug.WriteLine("[VoicePlaybackRouting] Earpiece unavailable (" + reason + ")");
                    return false;
                }

                manager.SetAudioEndpoint(endpoint);
                Debug.WriteLine(
                    "[VoicePlaybackRouting] SetAudioEndpoint " + endpoint +
                    " available=" + available + " reason=" + (reason ?? string.Empty));
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[VoicePlaybackRouting] SetAudioEndpoint failed: " + ex.Message);
                return false;
            }
        }

        private void EnsureDisplayController()
        {
            if (_displayController != null || _proximitySensor == null)
            {
                return;
            }

            try
            {
                if (!ApiInformation.IsTypePresent(
                        "Windows.Devices.Sensors.ProximitySensorDisplayOnOffController"))
                {
                    return;
                }

                _displayController = _proximitySensor.CreateDisplayOnOffController();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[VoicePlaybackRouting] DisplayOnOff: " + ex.Message);
            }
        }

        private void ReleaseDisplayController()
        {
            if (_displayController == null)
            {
                return;
            }

            try
            {
                _displayController.Dispose();
            }
            catch
            {
            }

            _displayController = null;
        }
    }
}
