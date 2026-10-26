using System.Threading;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class GroupListingCancellationTests
    {
        [Fact]
        public void Begin_returns_live_token_Cancel_signals_it()
        {
            var listing = new GroupListingCancellation();
            CancellationToken token = listing.Begin();

            Assert.False(token.IsCancellationRequested);
            listing.Cancel();
            Assert.True(token.IsCancellationRequested);
        }

        [Fact]
        public void Begin_replaces_prior_token()
        {
            var listing = new GroupListingCancellation();
            CancellationToken first = listing.Begin();
            CancellationToken second = listing.Begin();

            Assert.True(first.IsCancellationRequested);
            Assert.False(second.IsCancellationRequested);
        }

        [Fact]
        public async Task Cancel_unblocks_token_wait()
        {
            var listing = new GroupListingCancellation();
            CancellationToken token = listing.Begin();
            Task wait = Task.Delay(Timeout.InfiniteTimeSpan, token);

            listing.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        }
    }
}
