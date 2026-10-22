using Unison.Core.Helpers;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>
    /// How many media downloads may run at once. A phone has one slot; a desktop has three.
    /// </summary>
    public static class MediaDownloadSlots
    {
        public static int ForDevice(bool isMobile)
        {
            return isMobile ? 1 : 3;
        }
    }
}
