using System.Threading.Tasks;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class RaiseSessionClearedOwnerTests
    {
        [Fact]
        public async Task Run_resets_notifies_and_raises_ui()
        {
            bool holdReset = false;
            bool historyNotified = false;
            bool uiRaised = false;
            bool uiStartPairing = false;
            var owner = new RaiseSessionClearedOwner(
                _ => holdReset = true,
                () => historyNotified = true,
                _ => { },
                startPairing =>
                {
                    uiRaised = true;
                    uiStartPairing = startPairing;
                    return Task.CompletedTask;
                });

            await owner.RunAsync(startPairing: true);

            Assert.True(holdReset);
            Assert.True(historyNotified);
            Assert.True(uiRaised);
            Assert.True(uiStartPairing);
        }
    }
}
