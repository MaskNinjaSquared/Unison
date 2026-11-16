// =============================================================================
// ConnectionSlot<T>
//
// The one live instance of a connection-scoped object (socket, auth state).
// DI singleton per T so the host and the adapters that left it share the slot.
// =============================================================================
namespace Unison.WhatsAppClient.Helpers
{
    public sealed class ConnectionSlot<T> where T : class
    {
        private volatile T _current;

        public T Current
        {
            get => _current;
            set => _current = value;
        }

        public bool Holds(T candidate) =>
            candidate != null && ReferenceEquals(_current, candidate);

        /// <summary>Empties the slot only if it still holds <paramref name="expected"/>.</summary>
        public bool ClearIf(T expected)
        {
            if (!Holds(expected))
            {
                return false;
            }

            _current = null;
            return true;
        }
    }
}
