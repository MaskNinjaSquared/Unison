// =============================================================================
// Tests for AutomaticPlaceholderRecoveryTrigger.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class AutomaticPlaceholderRecoveryTriggerTests
    {
        [Fact]
        public void Null_or_blank_is_not_automatic()
        {
            Assert.False(AutomaticPlaceholderRecoveryTrigger.Matches(null));
            Assert.False(AutomaticPlaceholderRecoveryTrigger.Matches(""));
            Assert.False(AutomaticPlaceholderRecoveryTrigger.Matches("   "));
        }

        [Fact]
        public void Offline_complete_is_automatic()
        {
            Assert.True(AutomaticPlaceholderRecoveryTrigger.Matches("offline-complete"));
            Assert.True(AutomaticPlaceholderRecoveryTrigger.Matches("drain:offline-complete:42"));
        }

        [Fact]
        public void Deferred_drain_is_automatic()
        {
            Assert.True(AutomaticPlaceholderRecoveryTrigger.Matches("deferred-drain"));
            Assert.True(AutomaticPlaceholderRecoveryTrigger.Matches("startup:deferred-drain"));
        }

        [Fact]
        public void Decrypt_failed_is_automatic()
        {
            Assert.True(AutomaticPlaceholderRecoveryTrigger.Matches("socket:decrypt-failed"));
            Assert.True(AutomaticPlaceholderRecoveryTrigger.Matches("retry:socket:decrypt-failed:msg"));
        }

        [Fact]
        public void User_or_unrelated_triggers_are_not_automatic()
        {
            Assert.False(AutomaticPlaceholderRecoveryTrigger.Matches("user-open-chat"));
            Assert.False(AutomaticPlaceholderRecoveryTrigger.Matches("manual-resync"));
            Assert.False(AutomaticPlaceholderRecoveryTrigger.Matches("history-sqlite:Full"));
        }
    }
}
