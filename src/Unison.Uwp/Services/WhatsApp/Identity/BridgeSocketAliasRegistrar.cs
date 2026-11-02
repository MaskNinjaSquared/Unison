// =============================================================================
// BridgeSocketAliasRegistrar
//
// Live-session alias registration + self-identity heal after usync. Socket
// pairs go through the bridge; heal stays on ISelfIdentityHealHost (auth).
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Contracts.WhatsApp;
using Unison.Uwp.Client;
using UwpSessionProvider = Unison.Uwp.Services.Socket.IWhatsAppSessionProvider;

namespace Unison.Uwp.Services.WhatsApp.Identity
{
    internal sealed class BridgeSocketAliasRegistrar : ISocketAliasRegistrar
    {
        private readonly UwpSessionProvider _sessions;
        private readonly ISelfIdentityHealHost _heal;

        public BridgeSocketAliasRegistrar(
            UwpSessionProvider sessions,
            ISelfIdentityHealHost heal)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _heal = heal ?? throw new ArgumentNullException(nameof(heal));
        }

        public void RegisterPair(string jidA, string jidB, string source)
        {
            IWhatsAppSocket socket = _sessions.Socket;
            socket?.RegisterJidAlias(jidA, jidB, source);
        }

        public void RegisterPairs(
            IReadOnlyDictionary<string, string> aliases,
            string source)
        {
            if (aliases == null || aliases.Count == 0)
            {
                return;
            }

            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null)
            {
                return;
            }

            socket.RegisterJidAliases(
                aliases as Dictionary<string, string> ??
                    new Dictionary<string, string>(aliases, StringComparer.OrdinalIgnoreCase),
                source);
        }

        public void HealOwnIdentityAfterUsync(string normalizedPn, string normalizedLid) =>
            _heal.HealOwnIdentityAfterUsync(normalizedPn, normalizedLid);
    }
}
