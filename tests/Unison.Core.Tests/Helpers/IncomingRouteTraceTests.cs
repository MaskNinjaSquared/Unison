using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class IncomingRouteTraceTests
    {
        [Fact]
        public void Route_stage_carries_the_last_marked_step()
        {
            var trace = new IncomingRouteTrace();
            trace.Mark("alias-hints");
            trace.Mark("duplicate-preview");

            Assert.Equal("route:duplicate-preview", IncomingRouteTrace.DescribeStage("route", trace));
        }

        [Fact]
        public void Route_stage_without_step_stays_route()
        {
            Assert.Equal("route", IncomingRouteTrace.DescribeStage("route", new IncomingRouteTrace()));
            Assert.Equal("route", IncomingRouteTrace.DescribeStage("route", null));
        }

        [Fact]
        public void Other_stages_ignore_the_trace()
        {
            var trace = new IncomingRouteTrace();
            trace.Mark("timeline");

            Assert.Equal("routing", IncomingRouteTrace.DescribeStage("routing", trace));
            Assert.Equal("reset:message-timeout-skip", IncomingRouteTrace.DescribeStage("reset:message-timeout-skip", trace));
        }
    }
}
