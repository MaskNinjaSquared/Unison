using Xunit;

namespace Unison.Core.Tests.Helpers
{
    /// <summary>
    /// <see cref="ConnectionStabilityExperiment.Enabled"/> is process-wide; tests that
    /// flip it must not run beside each other.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class ConnectionStabilityExperimentCollection
    {
        public const string Name = "ConnectionStabilityExperiment";
    }
}
