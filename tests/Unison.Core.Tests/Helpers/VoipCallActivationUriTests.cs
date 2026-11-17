using Unison.Core.Helpers.Calls;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class VoipCallActivationUriTests
    {
        [Fact]
        public void ForAnswer_with_call_id_builds_unison_call_deep_link()
        {
            Assert.Equal(
                "unison-call:answer?callId=ABC%201",
                VoipCallActivationUri.ForAnswer("ABC 1"));
        }

        [Fact]
        public void ForAnswer_without_call_id_still_uses_answer_path()
        {
            Assert.Equal("unison-call:answer", VoipCallActivationUri.ForAnswer(null));
            Assert.Equal("unison-call:answer", VoipCallActivationUri.ForAnswer("  "));
        }
    }
}
