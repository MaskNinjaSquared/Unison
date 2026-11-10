using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class HistorySyncApplyPipelineDecisionTests
    {
        [Fact]
        public void Conversation_bearing_outranks_lid_directory()
        {
            Assert.True(
                HistorySyncApplyPipelineDecision.CompareApplyOrder(
                    "InitialBootstrap",
                    conversationsA: 400,
                    sequenceA: 2,
                    "PushName",
                    conversationsB: 0,
                    sequenceB: 1) < 0);
        }

        [Fact]
        public void Same_class_keeps_fifo_by_sequence()
        {
            Assert.True(
                HistorySyncApplyPipelineDecision.CompareApplyOrder(
                    "PushName",
                    0,
                    sequenceA: 1,
                    "PushName",
                    0,
                    sequenceB: 2) < 0);
        }

        [Theory]
        [InlineData("PushName", 0, true)]
        [InlineData("NonBlockingData", 0, true)]
        [InlineData("NonBlockingData", 30, false)]
        [InlineData("InitialBootstrap", 400, false)]
        [InlineData("InitialStatusV3", 0, false)]
        public void Lid_directory_detection(string syncType, int conversations, bool expected)
        {
            Assert.Equal(
                expected,
                HistorySyncApplyPipelineDecision.IsLidDirectoryChunk(syncType, conversations));
        }

        [Fact]
        public void Lid_directory_skips_phone_alias_and_lid_people_upsert()
        {
            Assert.False(
                HistorySyncApplyPipelineDecision.ShouldRegisterPhoneNumberLidAliases(
                    "PushName",
                    0));
            Assert.False(
                HistorySyncApplyPipelineDecision.ShouldUpsertPeopleFromPhoneNumberLidMappings(
                    "PushName",
                    0));
            Assert.False(
                HistorySyncApplyPipelineDecision.ShouldHarvestConversationLidAliases(0));
            Assert.False(
                HistorySyncApplyPipelineDecision.ShouldRunHistoryChunkTipReconcile(0));
        }

        [Fact]
        public void Conversation_bearing_keeps_alias_harvest_and_tip()
        {
            Assert.True(
                HistorySyncApplyPipelineDecision.ShouldRegisterPhoneNumberLidAliases(
                    "InitialBootstrap",
                    400));
            Assert.True(
                HistorySyncApplyPipelineDecision.ShouldUpsertPeopleFromPhoneNumberLidMappings(
                    "InitialBootstrap",
                    400));
            Assert.True(
                HistorySyncApplyPipelineDecision.ShouldHarvestConversationLidAliases(400));
            Assert.True(
                HistorySyncApplyPipelineDecision.ShouldRunHistoryChunkTipReconcile(400));
        }
    }
}
