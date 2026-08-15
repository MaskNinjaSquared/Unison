// =============================================================================
// CompanionRegistration
//
// Assembles the QR payload the phone scans: a URL fragment holding the server
// ref, our noise and identity public keys, the ADV secret and the companion
// platform id.
//
// The platform id is computed from the same Browser tuple that builds
// DeviceProps, so the two can never disagree. The legacy client hard-codes it,
// which lets the QR claim one client while the payload announces another.
//
// Ports: rc14 src/Utils/companion-reg-client-utils.ts
// =============================================================================
using System;

namespace Unison.Socket.Session.Pairing
{
    /// <summary>How this device identifies itself to the phone. Values are the wire numbers.</summary>
    public enum CompanionWebClientType
    {
        Unknown = 0,
        Chrome = 1,
        Edge = 2,
        Firefox = 3,
        Ie = 4,
        Opera = 5,
        Safari = 6,
        Electron = 7,
        Uwp = 8,
        OtherWebClient = 9
    }

    /// <summary>
    /// Derives the companion identity that goes into the QR payload.
    /// </summary>
    /// <remarks>
    /// The platform id is computed from the same browser tuple that builds DeviceProps, so the
    /// two can never disagree. The legacy client hard-coded "7" (Electron) in the QR while
    /// announcing Chrome in DeviceProps.
    /// </remarks>
    public static class CompanionRegistration
    {
        public static CompanionWebClientType GetWebClientType(string[] browser)
        {
            if (browser == null || browser.Length < 2)
            {
                return CompanionWebClientType.OtherWebClient;
            }

            var os = browser[0];
            var browserName = browser[1];

            if (string.Equals(browserName, "Desktop", StringComparison.Ordinal))
            {
                return string.Equals(os, "Windows", StringComparison.Ordinal)
                    ? CompanionWebClientType.Uwp
                    : CompanionWebClientType.Electron;
            }

            switch (browserName)
            {
                case "Chrome": return CompanionWebClientType.Chrome;
                case "Edge": return CompanionWebClientType.Edge;
                case "Firefox": return CompanionWebClientType.Firefox;
                case "IE": return CompanionWebClientType.Ie;
                case "Opera": return CompanionWebClientType.Opera;
                case "Safari": return CompanionWebClientType.Safari;
                default: return CompanionWebClientType.OtherWebClient;
            }
        }

        public static string GetCompanionPlatformId(string[] browser)
        {
            return ((int)GetWebClientType(browser)).ToString();
        }

        public static string BuildPairingQrData(
            string reference,
            string noiseKeyB64,
            string identityKeyB64,
            string advSecretKeyB64,
            string[] browser)
        {
            return "https://wa.me/settings/linked_devices#" + string.Join(
                ",",
                new[]
                {
                    reference,
                    noiseKeyB64,
                    identityKeyB64,
                    advSecretKeyB64,
                    GetCompanionPlatformId(browser)
                });
        }
    }
}
