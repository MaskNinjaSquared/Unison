using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Socket.Signal;
using Unison.Socket.WABinary;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// Sync peek of <see cref="LidMappingStore"/> for <see cref="Unison.Core.State.JidAliasTable"/>.
    /// </summary>
    public sealed class LidMappingMemoryAdapter : ILidMappingMemory
    {
        private readonly LidMappingStore _store;

        public LidMappingMemoryAdapter(LidMappingStore store)
        {
            _store = store ?? throw new System.ArgumentNullException(nameof(store));
        }

        public bool TryGetPairedJid(string normalizedJid, out string pairedJid)
        {
            pairedJid = null;
            if (string.IsNullOrEmpty(normalizedJid))
            {
                return false;
            }

            string normalized = JidHelper.Normalize(normalizedJid);
            string user = JidUtils.GetUser(normalized);
            if (string.IsNullOrEmpty(user))
            {
                return false;
            }

            if (normalized.EndsWith("@lid", System.StringComparison.OrdinalIgnoreCase))
            {
                string pnUser;
                if (!_store.TryGetCachedPnUserForLid(user, out pnUser))
                {
                    return false;
                }

                pairedJid = pnUser + "@" + JidUtils.ServerWhatsApp;
                return true;
            }

            if (normalized.EndsWith("@s.whatsapp.net", System.StringComparison.OrdinalIgnoreCase))
            {
                string lidUser;
                if (!_store.TryGetCachedLidUserForPn(user, out lidUser))
                {
                    return false;
                }

                pairedJid = lidUser + "@lid";
                return true;
            }

            return false;
        }
    }
}
