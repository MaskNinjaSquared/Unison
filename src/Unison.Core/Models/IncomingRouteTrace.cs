using System.Threading;

namespace Unison.Core.Models
{
    /// <summary>
    /// Last step one route attempt reached. A fresh instance per attempt: a timed-out (zombie)
    /// route keeps marking its own trace, never the retry's, and never the pump queue stage.
    /// </summary>
    public sealed class IncomingRouteTrace
    {
        private const string RouteStage = "route";

        private string _step;

        public string Step => Volatile.Read(ref _step);

        public void Mark(string step) => Volatile.Write(ref _step, step);

        public static string DescribeStage(string stage, IncomingRouteTrace trace)
        {
            string step = trace?.Step;
            if (string.IsNullOrEmpty(step) || stage != RouteStage)
            {
                return stage;
            }

            return RouteStage + ":" + step;
        }
    }
}
