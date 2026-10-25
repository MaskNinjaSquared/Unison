using System;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
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
                args => seen = args,
                (a, b, c, d, e, f) => { },
                (a, b, c) => Task.CompletedTask,
                _ => Task.CompletedTask,
                _ => { });

            var args = new PresenceUpdateEventArgs { Jid = "x@s.whatsapp.net" };
            owner.HandlePresence(args);

            Assert.Same(args, seen);
        }

        [Fact]
        public void HandleLinkCode_skips_stale_socket()
        {
            bool raised = false;
            var owner = new SocketFacadeWireOwner(
                _ => { },
                (a, b, c, d, e, f) => { },
                (a, b, c) => Task.CompletedTask,
                _ => Task.CompletedTask,
                _ => raised = true);

            owner.HandleLinkCodeCompanion(new BinaryNode { Tag = "link" }, isCurrentSocket: false);

            Assert.False(raised);
        }
    }
}
