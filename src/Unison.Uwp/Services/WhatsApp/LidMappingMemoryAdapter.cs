using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Socket.Signal;
using Unison.Socket.WABinary;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// Sync peek + fire-and-forget write of <see cref="LidMappingStore"/> for
    /// <see cref="Unison.Core.State.JidAliasTable"/>.
    /// </summary>
    public sealed class LidMappingMemoryAdapter : ILidMappingMemory
    {
        private readonly LidMappingStore _store;

        public LidMappingMemoryAdapter(LidMappingStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
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

            if (normalized.EndsWith("@lid", StringComparison.OrdinalIgnoreCase))
            {
                string pnUser;
                if (!_store.TryGetCachedPnUserForLid(user, out pnUser))
                {
                    return false;
                }

                pairedJid = pnUser + "@" + JidUtils.ServerWhatsApp;
                return true;
            }

            if (normalized.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase))
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

        public void RememberPair(string jidA, string jidB)
        {
            RememberPairs(new[] { new KeyValuePair<string, string>(jidA, jidB) });
        }

        public void RememberPairs(IEnumerable<KeyValuePair<string, string>> pairs)
        {
            if (pairs == null)
            {
                return;
            }

            var mappings = new List<LidMapping>();
            foreach (KeyValuePair<string, string> pair in pairs)
            {
                LidMapping mapping = ToMapping(pair.Key, pair.Value);
                if (mapping != null)
                {
                    mappings.Add(mapping);
                }
            }

            if (mappings.Count == 0)
            {
                return;
            }

            // Same posture as SocketBridge.Store: announce from sync paths, do not await.
            _ = ObserveStoreAsync(_store.StoreMappingsAsync(mappings));
        }

        private static LidMapping ToMapping(string jidA, string jidB)
        {
            if (string.IsNullOrEmpty(jidA) || string.IsNullOrEmpty(jidB))
            {
                return null;
            }

            string a = JidHelper.Normalize(jidA);
            string b = JidHelper.Normalize(jidB);
            string lid = JidUtils.IsLidUser(a) ? a : (JidUtils.IsLidUser(b) ? b : null);
            string pn = JidUtils.IsPnUser(a) ? a : (JidUtils.IsPnUser(b) ? b : null);
            return lid != null && pn != null ? new LidMapping(lid, pn) : null;
        }

        private static async Task ObserveStoreAsync(Task write)
        {
            try
            {
                await write.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[LidMappingMemory] RememberPair store failed: " + ex.Message);
            }
        }
    }
}
