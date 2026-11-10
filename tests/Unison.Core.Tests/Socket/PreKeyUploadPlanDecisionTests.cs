using System.Collections.Generic;
using System.Linq;
using Unison.Socket.UseCases.Auth;
using Xunit;

namespace Unison.Core.Tests.Socket
{
    public sealed class PreKeyUploadPlanDecisionTests
    {
        [Fact]
        public void Fresh_install_mints_the_whole_batch_after_the_last_issued_id()
        {
            PreKeyUploadPlan plan = PreKeyUploadPlanDecision.Plan(
                firstUnuploadedId: 0,
                lastIssuedId: 1,
                count: 100,
                storedIds: new int[0]);

            Assert.Empty(plan.ReuseIds);
            Assert.Equal(2, plan.FirstNewId);
            Assert.Equal(100, plan.MintCount);
            Assert.Equal(101, plan.LastIssuedIdAfterMint);
        }

        [Fact]
        public void Failed_upload_is_resumed_from_the_stored_keys_without_minting()
        {
            // Lumia 512 (8e31b77c): 812 keys stored, the IQ never got its ack.
            IEnumerable<int> stored = Enumerable.Range(2, 812);

            PreKeyUploadPlan plan = PreKeyUploadPlanDecision.Plan(
                firstUnuploadedId: 0,
                lastIssuedId: 813,
                count: 100,
                storedIds: stored);

            Assert.Equal(Enumerable.Range(2, 100), plan.ReuseIds);
            Assert.Equal(0, plan.MintCount);
            Assert.Equal(813, plan.LastIssuedIdAfterMint);
        }

        [Fact]
        public void Partial_stock_is_reused_and_only_the_gap_is_minted()
        {
            PreKeyUploadPlan plan = PreKeyUploadPlanDecision.Plan(
                firstUnuploadedId: 10,
                lastIssuedId: 40,
                count: 50,
                storedIds: Enumerable.Range(2, 39));

            Assert.Equal(Enumerable.Range(10, 31), plan.ReuseIds);
            Assert.Equal(41, plan.FirstNewId);
            Assert.Equal(19, plan.MintCount);
            Assert.Equal(59, plan.LastIssuedIdAfterMint);
        }

        [Fact]
        public void Keys_below_the_upload_watermark_are_never_announced_again()
        {
            PreKeyUploadPlan plan = PreKeyUploadPlanDecision.Plan(
                firstUnuploadedId: 501,
                lastIssuedId: 500,
                count: 10,
                storedIds: Enumerable.Range(2, 499));

            Assert.Empty(plan.ReuseIds);
            Assert.Equal(501, plan.FirstNewId);
            Assert.Equal(10, plan.MintCount);
        }

        [Fact]
        public void Consumed_ids_missing_from_the_store_are_skipped()
        {
            PreKeyUploadPlan plan = PreKeyUploadPlanDecision.Plan(
                firstUnuploadedId: 2,
                lastIssuedId: 6,
                count: 3,
                storedIds: new[] { 2, 4, 6 });

            Assert.Equal(new[] { 2, 4, 6 }, plan.ReuseIds);
            Assert.Equal(0, plan.MintCount);
        }

        [Fact]
        public void Stored_ids_above_an_unsaved_counter_are_not_overwritten()
        {
            // NextPreKeyId was not persisted after the failed batch; minting from it would
            // overwrite keys that already exist on disk.
            PreKeyUploadPlan plan = PreKeyUploadPlanDecision.Plan(
                firstUnuploadedId: 2,
                lastIssuedId: 1,
                count: 5,
                storedIds: new[] { 2, 3 });

            Assert.Equal(new[] { 2, 3 }, plan.ReuseIds);
            Assert.Equal(4, plan.FirstNewId);
            Assert.Equal(3, plan.MintCount);
            Assert.Equal(6, plan.LastIssuedIdAfterMint);
        }

        [Fact]
        public void Watermark_moves_past_the_highest_acked_id_and_never_back()
        {
            Assert.Equal(102, PreKeyUploadPlanDecision.FirstUnuploadedAfterAck(0, 101));
            Assert.Equal(900, PreKeyUploadPlanDecision.FirstUnuploadedAfterAck(900, 101));
        }

        [Fact]
        public void Non_positive_count_plans_nothing()
        {
            PreKeyUploadPlan plan = PreKeyUploadPlanDecision.Plan(2, 10, 0, new[] { 2, 3 });

            Assert.Empty(plan.ReuseIds);
            Assert.Equal(0, plan.MintCount);
        }
    }
}
