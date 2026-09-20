// =============================================================================
// Characterization tests for GroupNameSyncBlacklist.
//
// The rule these encode: a synced subject that looks like an invite-link
// placeholder must not overwrite a name the user can actually read, but it is
// still better than nothing when the chat has no name at all.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class GroupNameSyncBlacklistTests
    {
        [Theory]
        [InlineData("Invite")]
        [InlineData("invite")]
        [InlineData("INVITE")]
        [InlineData("Group invite link")]
        [InlineData("  chat.whatsapp.com invite  ")]
        public void Subjects_containing_the_token_are_blacklisted(string subject)
        {
            Assert.True(GroupNameSyncBlacklist.IsBlacklisted(subject));
        }

        [Theory]
        [InlineData("Família")]
        [InlineData("Convite")] // Portuguese for invite - does not contain the token
        [InlineData("Trabalho 2026")]
        public void Ordinary_subjects_are_not_blacklisted(string subject)
        {
            Assert.False(GroupNameSyncBlacklist.IsBlacklisted(subject));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Empty_subjects_are_not_blacklisted(string? subject)
        {
            Assert.False(GroupNameSyncBlacklist.IsBlacklisted(subject));
        }

        [Fact]
        public void Matching_is_by_substring_not_by_word()
        {
            // Current behaviour, recorded rather than endorsed: a real group named
            // "Reinvite" would be caught. Change this test if the rule tightens.
            Assert.True(GroupNameSyncBlacklist.IsBlacklisted("Reinvite"));
        }

        // --- ShouldApplySyncedSubject ---------------------------------------

        [Fact]
        public void Blacklisted_subject_does_not_replace_a_name_the_user_can_read()
        {
            Assert.False(GroupNameSyncBlacklist.ShouldApplySyncedSubject(
                "Group invite", incomingMeaningful: true, existingMeaningful: true));
        }

        [Fact]
        public void Blacklisted_subject_is_still_better_than_no_name_at_all()
        {
            Assert.True(GroupNameSyncBlacklist.ShouldApplySyncedSubject(
                "Group invite", incomingMeaningful: true, existingMeaningful: false));
        }

        [Fact]
        public void Ordinary_meaningful_subject_replaces_whatever_is_there()
        {
            Assert.True(GroupNameSyncBlacklist.ShouldApplySyncedSubject(
                "Família", incomingMeaningful: true, existingMeaningful: true));
        }

        [Fact]
        public void Meaningless_subject_does_not_overwrite_a_meaningful_name()
        {
            Assert.False(GroupNameSyncBlacklist.ShouldApplySyncedSubject(
                "12036312345", incomingMeaningful: false, existingMeaningful: true));
        }

        [Fact]
        public void Meaningless_subject_is_accepted_when_nothing_better_exists()
        {
            Assert.True(GroupNameSyncBlacklist.ShouldApplySyncedSubject(
                "12036312345", incomingMeaningful: false, existingMeaningful: false));
        }

        // --- ShouldCacheSyncedSubject ---------------------------------------

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Ordinary_subject_always_lands_in_the_cache(bool existingCacheMeaningful)
        {
            Assert.True(GroupNameSyncBlacklist.ShouldCacheSyncedSubject(
                "Família", existingCacheMeaningful));
        }

        [Fact]
        public void Blacklisted_subject_does_not_overwrite_a_cached_name()
        {
            Assert.False(GroupNameSyncBlacklist.ShouldCacheSyncedSubject(
                "Group invite", existingCacheMeaningful: true));
        }

        [Fact]
        public void Blacklisted_subject_is_cached_when_the_cache_is_empty()
        {
            Assert.True(GroupNameSyncBlacklist.ShouldCacheSyncedSubject(
                "Group invite", existingCacheMeaningful: false));
        }
    }
}
