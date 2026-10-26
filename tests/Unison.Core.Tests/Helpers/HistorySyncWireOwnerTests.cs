using System.Threading.Tasks;
using Proto;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class HistorySyncWireOwnerTests
    {
        [Fact]
        public void Fatal_session_skips_apply()
        {
            bool applied = false;
            var owner = new HistorySyncWireOwner(
                fatalSessionEnded: () => true,
                hasAuthState: () => true,
                writeBegin: (_, __) => { },
                writeEnd: (_, __) => { },
                apply: _ =>
                {
                    applied = true;
                    return Task.CompletedTask;
                });

            owner.Run(new HistorySync());

            Assert.False(applied);
        }

        [Fact]
        public void Ready_session_applies_chunk()
        {
            bool applied = false;
            var owner = new HistorySyncWireOwner(
                fatalSessionEnded: () => false,
                hasAuthState: () => true,
                writeBegin: (_, __) => { },
                writeEnd: (_, __) => { },
                apply: _ =>
                {
                    applied = true;
                    return Task.CompletedTask;
                });

            owner.Run(new HistorySync());

            Assert.True(applied);
        }
    }
}
