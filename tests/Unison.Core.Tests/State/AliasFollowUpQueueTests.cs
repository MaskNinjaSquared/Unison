// =============================================================================
// Tests for AliasFollowUpQueue.
//
// What a burst of new alias pairs leaves owed until the debounced follow-up
// pass runs. Used to be four fields and a lock in WhatsAppService:
//
//   - every phone address that gained a pair is an avatar target, each once;
//   - the pass reports the latest source asked for, or "alias" when none;
//   - starting a pass takes everything owed; a second start while one runs is
//     refused, and what arrives meanwhile waits for the next pass;
//   - a pass that failed puts its avatar targets back instead of losing them.
// =============================================================================
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class AliasFollowUpQueueTests
    {
        private const string PnA = "5511999990001@s.whatsapp.net";
        private const string PnB = "5511999990002@s.whatsapp.net";

        [Fact]
        public void PassTakesTargetsOnceEach()
        {
            var queue = new AliasFollowUpQueue();
            queue.NoteAvatarTarget(PnA);
            queue.NoteAvatarTarget(PnA.ToUpperInvariant());
            queue.NoteAvatarTarget(PnB);
            queue.Request("history");

            Assert.True(queue.TryBegin(out var batch));
            Assert.Equal(new[] { PnA, PnB }, batch.AvatarTargets);
        }

        [Fact]
        public void BlankTargetsAreIgnored()
        {
            var queue = new AliasFollowUpQueue();
            queue.NoteAvatarTarget(null);
            queue.NoteAvatarTarget("  ");

            Assert.True(queue.TryBegin(out var batch));
            Assert.Empty(batch.AvatarTargets);
        }

        [Fact]
        public void PassReportsLatestSource()
        {
            var queue = new AliasFollowUpQueue();
            queue.Request("message");
            queue.Request("history");

            Assert.True(queue.TryBegin(out var batch));
            Assert.Equal("history", batch.Source);
        }

        [Fact]
        public void SourceDefaultsToAlias()
        {
            var queue = new AliasFollowUpQueue();

            Assert.True(queue.TryBegin(out var batch));
            Assert.Equal("alias", batch.Source);
        }

        [Fact]
        public void SecondBeginWhileRunningIsRefused()
        {
            var queue = new AliasFollowUpQueue();
            Assert.True(queue.TryBegin(out _));

            Assert.False(queue.TryBegin(out var refused));
            Assert.Null(refused);
        }

        [Fact]
        public void WorkArrivingDuringPassWaitsForNext()
        {
            var queue = new AliasFollowUpQueue();
            queue.NoteAvatarTarget(PnA);
            queue.Request("message");
            Assert.True(queue.TryBegin(out _));

            queue.NoteAvatarTarget(PnB);
            queue.Request("history");
            Assert.False(queue.TryBegin(out _));
            Assert.Equal("history", queue.PendingSource);

            queue.End();
            Assert.True(queue.TryBegin(out var next));
            Assert.Equal(new[] { PnB }, next.AvatarTargets);
            Assert.Equal("history", next.Source);
        }

        [Fact]
        public void BeginClearsPendingSource()
        {
            var queue = new AliasFollowUpQueue();
            queue.Request("message");
            Assert.True(queue.TryBegin(out _));

            Assert.Null(queue.PendingSource);
        }

        [Fact]
        public void RestorePutsTargetsBack()
        {
            var queue = new AliasFollowUpQueue();
            queue.NoteAvatarTarget(PnA);
            Assert.True(queue.TryBegin(out var failed));
            queue.NoteAvatarTarget(PnB);

            queue.Restore(failed);
            queue.End();

            Assert.True(queue.TryBegin(out var retry));
            Assert.Equal(new[] { PnB, PnA }, retry.AvatarTargets);
        }

        [Fact]
        public void RestoreNullIsHarmless()
        {
            var queue = new AliasFollowUpQueue();
            queue.Restore(null);

            Assert.True(queue.TryBegin(out var batch));
            Assert.Empty(batch.AvatarTargets);
        }
    }
}
