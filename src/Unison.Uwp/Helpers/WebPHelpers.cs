using System;
using Windows.Foundation.Metadata;
using Windows.Graphics.Imaging;

namespace Unison.Uwp.Helpers
{
    /// <summary>
    /// Detects whether the OS exposes a WebP <see cref="BitmapDecoder"/> (desktop RS4+).
    /// Windows 10 Mobile typically does not — use <see cref="WebPDecoder"/> there.
    /// Manual codec installs can register the decoder id without BitmapImage actually working.
    /// </summary>
    internal static class WebPHelpers
    {
        private static readonly Lazy<WebPSupportProbe> Probe = new Lazy<WebPSupportProbe>(RunProbe);

        public static bool HasWebPCodec => Probe.Value.SupportEnabled;

        public static bool HasApiContract7 => Probe.Value.ApiContract7;

        public static bool WebPDecoderIdListed => Probe.Value.DecoderIdListed;

        /// <summary>One-line probe for logs / diagnostics.</summary>
        public static string FormatDiagnosticsLine()
        {
            var p = Probe.Value;
            return "webpSupportEnabled=" + (p.SupportEnabled ? "true" : "false")
                + "; webpApiContract7=" + (p.ApiContract7 ? "true" : "false")
                + "; webpDecoderIdListed=" + (p.DecoderIdListed ? "true" : "false");
        }

        private static WebPSupportProbe RunProbe()
        {
            bool contract7 = ApiInformation.IsApiContractPresent(
                "Windows.Foundation.UniversalApiContract",
                7,
                0);
            bool listed = false;
            if (contract7)
            {
                try
                {
                    foreach (var item in BitmapDecoder.GetDecoderInformationEnumerator())
                    {
                        if (item.CodecId == BitmapDecoder.WebpDecoderId)
                        {
                            listed = true;
                            break;
                        }
                    }
                }
                catch
                {
                    listed = false;
                }
            }

            return new WebPSupportProbe
            {
                ApiContract7 = contract7,
                DecoderIdListed = listed,
                // Same rule used by media save/repair paths.
                SupportEnabled = contract7 && listed
            };
        }

        public static bool IsWebPMime(string mimeType)
        {
            return !string.IsNullOrWhiteSpace(mimeType)
                && mimeType.IndexOf("webp", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>RIFF....WEBP container signature.</summary>
        public static bool IsWebPPayload(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 12)
            {
                return false;
            }

            return bytes[0] == (byte)'R'
                && bytes[1] == (byte)'I'
                && bytes[2] == (byte)'F'
                && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W'
                && bytes[9] == (byte)'E'
                && bytes[10] == (byte)'B'
                && bytes[11] == (byte)'P';
        }

        private sealed class WebPSupportProbe
        {
            public bool ApiContract7;
            public bool DecoderIdListed;
            public bool SupportEnabled;
        }
    }
}
