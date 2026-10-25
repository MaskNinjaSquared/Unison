using System.Diagnostics;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {
        private bool TryGetSelfIdentityAliasPair(out string id, out string lid)
        {
            id = null;
            lid = null;
            if (_authState?.Me == null)
            {
                return false;
            }

            return SelfIdentityAliasPair.TryCreate(
                _authState.Me.Id,
                _authState.Me.Lid,
                NormalizeJid,
                out id,
                out lid);
        }

        private void FileSelfIdentityAliases(string source)
        {
            string id;
            string lid;
            if (!TryGetSelfIdentityAliasPair(out id, out lid))
            {
                return;
            }

            JidAlias[id] = lid;
            JidAlias[lid] = id;
            RegisterSocketAlias(id, lid, source);
            Debug.WriteLine("[WhatsAppService] Self identity alias: " + id + " <-> " + lid + " (" + source + ")");
        }

        private void RegisterSocketAlias(string jidA, string jidB, string source)
        {
            _socket?.RegisterJidAlias(jidA, jidB, source);
        }

        internal void RegisterSocketAliases(string source)
        {
            var aliases = JidAlias.Snapshot();
            _socket?.RegisterJidAliases(aliases, source);
        }

        public string ResolveDisplayName(string jid, string context = null) =>
            _displayNames.Resolve(jid, context);

        public string GetCanonicalJid(string jid) => JidAlias.GetCanonicalJid(jid);
    }
}
