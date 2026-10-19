using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;

namespace Unison.Core.Tests.Helpers
{
    /// <summary>Runs inline and counts the UI-thread hops a flow asked for.</summary>
    internal sealed class CountingDispatcher : IDispatcher
    {
        public int Passes { get; private set; }

        public Task RunAsync(Action action)
        {
            Passes++;
            action();
            return Task.CompletedTask;
        }
    }
}
