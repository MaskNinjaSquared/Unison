// =============================================================================
// ISocketLog
//
// The logging seam of the socket layer. Every class here logs through this
// interface, never through a static or a platform API, which is what allows
// Unison.Socket to stay a portable netstandard2.0 library and to be exercised
// in tests with no UWP host present.
//
// Ports: rc14 src/Utils/logger.ts
// =============================================================================
using System;

namespace Unison.Socket.Abstractions
{
    /// <summary>
    /// Logging sink for the socket layer. The host supplies the implementation so that
    /// <c>Unison.Socket</c> stays free of platform and app-wide statics.
    /// </summary>
    public interface ISocketLog
    {
        void Trace(string message);

        void Debug(string message);

        void Info(string message);

        void Warn(string message, Exception error = null);

        void Error(string message, Exception error = null);
    }

    /// <summary>No-op sink, used when the host does not supply one.</summary>
    public sealed class NullSocketLog : ISocketLog
    {
        public static readonly NullSocketLog Instance = new NullSocketLog();

        private NullSocketLog()
        {
        }

        public void Trace(string message)
        {
        }

        public void Debug(string message)
        {
        }

        public void Info(string message)
        {
        }

        public void Warn(string message, Exception error = null)
        {
        }

        public void Error(string message, Exception error = null)
        {
        }
    }
}
