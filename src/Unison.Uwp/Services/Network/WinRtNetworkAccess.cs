// =============================================================================
// WinRtNetworkAccess
//
// UWP adapter for INetworkAccess. Keeps NetworkInformation out of ConnectionFacade
// so the façade can move to Unison.WhatsAppClient without WinRT.
// =============================================================================
using System;
using System.Diagnostics;
using Unison.Core.Contracts;
using Windows.Networking.Connectivity;

namespace Unison.Uwp.Services.Network
{
    public sealed class WinRtNetworkAccess : INetworkAccess
    {
        public bool HasInternetAccess
        {
            get
            {
                try
                {
                    ConnectionProfile profile = NetworkInformation.GetInternetConnectionProfile();
                    if (profile == null)
                    {
                        return false;
                    }

                    NetworkConnectivityLevel level = profile.GetNetworkConnectivityLevel();
                    return level == NetworkConnectivityLevel.InternetAccess ||
                           level == NetworkConnectivityLevel.ConstrainedInternetAccess;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[WinRtNetworkAccess] HasInternetAccess failed: " + ex.Message);
                    // Prefer "assume online" so a probe failure does not block reconnect / pairing.
                    return true;
                }
            }
        }
    }
}
