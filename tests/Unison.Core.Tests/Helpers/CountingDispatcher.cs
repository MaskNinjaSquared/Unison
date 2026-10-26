using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;

namespace Unison.Core.Tests.Helpers
{
    /// <summary>Runs inline and counts the UI-thread hops a flow asked for.</summary>
    internal sealed class CountingDispatcher : IDispatcher
    {
        public int Passes { get; private set; }

        /// <summary>Peak nested RunAsync depth (1 = no re-entrancy).</summary>
        public int MaxDepth { get; private set; }

        private int _depth;

        public Task RunAsync(Action action, string caller = null, string callerFile = null)
        {
            Passes++;
            _depth++;
            if (_depth > MaxDepth)
            {
                MaxDepth = _depth;
            }

            try
            {
                action();
            }
            finally
            {
                _depth--;
            }

            return Task.CompletedTask;
        }
    }
}
