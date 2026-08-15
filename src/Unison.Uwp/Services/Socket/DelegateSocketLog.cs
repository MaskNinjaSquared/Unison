// =============================================================================
// DelegateSocketLog
//
// Adapts Unison.Socket's ISocketLog onto any sink the host provides. Keeping the
// sink a delegate means the socket layer can log into the session log, into the
// debug pane, or into a test collector without knowing any of them exist.
// =============================================================================
using System;
using Unison.Socket.Abstractions;

namespace Unison.Uwp.Services.Socket
{
    internal sealed class DelegateSocketLog : ISocketLog
    {
        private readonly Action<string> _sink;
        private readonly bool _includeTrace;

        public DelegateSocketLog(Action<string> sink, bool includeTrace = false)
        {
            if (sink == null)
            {
                throw new ArgumentNullException(nameof(sink));
            }

            _sink = sink;
            _includeTrace = includeTrace;
        }

        public void Trace(string message)
        {
            if (_includeTrace)
            {
                _sink("[trace] " + message);
            }
        }

        public void Debug(string message)
        {
            _sink("[debug] " + message);
        }

        public void Info(string message)
        {
            _sink("[info] " + message);
        }

        public void Warn(string message, Exception error = null)
        {
            _sink("[warn] " + message + Describe(error));
        }

        public void Error(string message, Exception error = null)
        {
            _sink("[error] " + message + Describe(error));
        }

        private static string Describe(Exception error)
        {
            return error == null ? string.Empty : " -- " + error.GetBaseException().Message;
        }
    }
}
