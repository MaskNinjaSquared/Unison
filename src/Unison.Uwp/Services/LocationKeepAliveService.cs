using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Windows.ApplicationModel.ExtendedExecution;
using Windows.Devices.Geolocation;
using Windows.Foundation.Metadata;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// Unogram/tdlib keep-alive: ExtendedExecutionReason.LocationTracking + live
    /// Geolocator subscription (position ignored). Only runs when the user setting
    /// is enabled.
    /// </summary>
    public sealed class LocationKeepAliveService : ILocationKeepAliveService
    {
        private readonly ILocalSettings _localSettings;
        private readonly object _gate = new object();

        private ExtendedExecutionSession _session;
        private Geolocator _geolocator;
        private bool _shuttingDown;

        public LocationKeepAliveService(ILocalSettings localSettings)
        {
            _localSettings = localSettings;
        }

        public bool IsActive
        {
            get { lock (_gate) { return _session != null; } }
        }

        public async Task ApplyConfigAsync()
        {
            if (_shuttingDown)
            {
                return;
            }

            bool enabled = _localSettings.Get<bool>(LocalSettingsConstants.LocationKeepAliveEnabled);
            if (enabled)
            {
                if (!IsActive)
                {
                    await StartAsync().ConfigureAwait(true);
                }
            }
            else
            {
                Stop();
            }
        }

        public async Task<bool> StartAsync()
        {
            if (_shuttingDown)
            {
                return false;
            }

            lock (_gate)
            {
                if (_session != null)
                {
                    return true;
                }
            }

            bool coarseAvailable = false;
            try
            {
                // Lightest configuration — network/cell, rare reports. Position unused.
                var geolocator = new Geolocator
                {
                    DesiredAccuracy = PositionAccuracy.Default,
                    DesiredAccuracyInMeters = 3000,
                    MovementThreshold = 1000,
                    ReportInterval = 600000
                };

                if (ApiInformation.IsMethodPresent(
                        "Windows.Devices.Geolocation.Geolocator",
                        "AllowFallbackToConsentlessPositions"))
                {
                    geolocator.AllowFallbackToConsentlessPositions();
                    coarseAvailable = true;
                }

                geolocator.PositionChanged += OnPositionChanged;
                geolocator.StatusChanged += OnGeolocatorStatusChanged;

                lock (_gate)
                {
                    _geolocator = geolocator;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[KeepAlive] geolocator setup failed: " + ex.Message);
                return false;
            }

            GeolocationAccessStatus access = GeolocationAccessStatus.Unspecified;
            try
            {
                access = await Geolocator.RequestAccessAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[KeepAlive] RequestAccessAsync failed: " + ex.Message);
            }

            if (access != GeolocationAccessStatus.Allowed)
            {
                Debug.WriteLine("[KeepAlive] location access " + access
                    + (coarseAvailable ? ", continuing with coarse" : ", giving up"));
                if (!coarseAvailable)
                {
                    StopGeolocator();
                    return false;
                }
            }

            var session = new ExtendedExecutionSession
            {
                Reason = ExtendedExecutionReason.LocationTracking,
                Description = "Unison keep-alive (location tracking justification)"
            };
            session.Revoked += OnSessionRevoked;

            try
            {
                var result = await session.RequestExtensionAsync();
                if (result == ExtendedExecutionResult.Allowed)
                {
                    lock (_gate)
                    {
                        _session = session;
                    }

                    Debug.WriteLine("[KeepAlive] ALLOWED (location tracking)");
                    return true;
                }

                Debug.WriteLine("[KeepAlive] DENIED");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[KeepAlive] RequestExtensionAsync failed: " + ex.Message);
            }

            try
            {
                session.Revoked -= OnSessionRevoked;
                session.Dispose();
            }
            catch
            {
            }

            StopGeolocator();
            return false;
        }

        public void Stop()
        {
            ExtendedExecutionSession session;
            lock (_gate)
            {
                session = _session;
                _session = null;
            }

            if (session != null)
            {
                try
                {
                    session.Revoked -= OnSessionRevoked;
                    session.Dispose();
                }
                catch
                {
                }

                Debug.WriteLine("[KeepAlive] stopped");
            }

            StopGeolocator();
        }

        /// <summary>Called when the process is exiting and must not restart.</summary>
        public void Shutdown()
        {
            _shuttingDown = true;
            Stop();
        }

        private void StopGeolocator()
        {
            Geolocator geolocator;
            lock (_gate)
            {
                geolocator = _geolocator;
                _geolocator = null;
            }

            if (geolocator == null)
            {
                return;
            }

            try
            {
                geolocator.PositionChanged -= OnPositionChanged;
                geolocator.StatusChanged -= OnGeolocatorStatusChanged;
            }
            catch
            {
            }
        }

        private void OnPositionChanged(Geolocator sender, PositionChangedEventArgs args)
        {
            // Unused — subscription only justifies LocationTracking.
        }

        private void OnGeolocatorStatusChanged(Geolocator sender, StatusChangedEventArgs args)
        {
            if (args.Status == PositionStatus.Disabled ||
                args.Status == PositionStatus.NotAvailable)
            {
                Debug.WriteLine("[KeepAlive] geolocator status " + args.Status);
            }
        }

        private async void OnSessionRevoked(object sender, ExtendedExecutionRevokedEventArgs args)
        {
            Debug.WriteLine("[KeepAlive] REVOKED " + args.Reason);
            Stop();

            if (args.Reason == ExtendedExecutionRevokedReason.SystemPolicy
                && !_shuttingDown
                && _localSettings.Get<bool>(LocalSettingsConstants.LocationKeepAliveEnabled))
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1));
                    if (!_shuttingDown
                        && !IsActive
                        && _localSettings.Get<bool>(LocalSettingsConstants.LocationKeepAliveEnabled))
                    {
                        await StartAsync();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[KeepAlive] retry failed: " + ex.Message);
                }
            }
        }
    }
}
