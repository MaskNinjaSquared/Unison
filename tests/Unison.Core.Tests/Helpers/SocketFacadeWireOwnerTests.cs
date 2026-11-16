using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class SocketFacadeWireOwnerTests
    {
        [Fact]
        public void HandlePresence_forwards_to_port()
        {
            PresenceUpdateEventArgs seen = null;
            var owner = new SocketFacadeWireOwner(
                new IdentityJids(),
                args => seen = args,
                (a, b, c, d, e, f) => { },
                (a, b, c) => Task.CompletedTask,
                _ => Task.CompletedTask);

            var args = new PresenceUpdateEventArgs { Jid = "x@s.whatsapp.net" };
            owner.HandlePresence(args);

            Assert.Same(args, seen);
        }

        private sealed class IdentityJids : IJidResolver
        {
            public string GetCanonicalJid(string jid) => jid;

            public string PeekCanonicalJid(string jid) => jid;

            public bool TryGetAlias(string jid, out string alias)
            {
                alias = null;
                return false;
            }

            public bool IsSelfLinked(string jid) => false;

            public Profile Self => null;
        }
    }
}
