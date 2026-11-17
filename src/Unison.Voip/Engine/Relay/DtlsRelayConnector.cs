// =============================================================================
// DtlsRelayConnector
//
// Opens a DTLS 1.2 session over ICallUdpTransport. ConnectClient is the live
// path; AcceptServer exists so unit tests can complete a local handshake.
// =============================================================================
using System;
using System.IO;
using System.Net;
using System.Threading;
using Org.BouncyCastle.Tls;
using Unison.Voip.Abstractions;

namespace Unison.Voip.Engine.Relay
{
    public static class DtlsRelayConnector
    {
        public static DtlsTransport ConnectClient(
            ICallUdpTransport udp,
            IPEndPoint relay,
            TimeSpan timeout)
        {
            if (udp == null)
            {
                throw new ArgumentNullException(nameof(udp));
            }

            if (relay == null)
            {
                throw new ArgumentNullException(nameof(relay));
            }

            WaVoipIdentity identity = WaVoipIdentity.Create();
            var datagram = new UdpDatagramTransport(udp, relay);
            datagram.StartPump();

            var protocol = new DtlsClientProtocol();
            var client = new WaVoipDtlsClient(identity);
            try
            {
                return RunWithTimeout(
                    () => protocol.Connect(client, datagram),
                    timeout,
                    "DTLS client handshake timed out");
            }
            catch (Exception ex)
            {
                string alert = client.LastAlert;
                if (!string.IsNullOrEmpty(alert))
                {
                    throw new IOException("DTLS handshake failed: " + alert, ex);
                }

                throw;
            }
        }

        public static DtlsTransport AcceptServer(
            ICallUdpTransport udp,
            IPEndPoint remote,
            TimeSpan timeout)
        {
            if (udp == null)
            {
                throw new ArgumentNullException(nameof(udp));
            }

            if (remote == null)
            {
                throw new ArgumentNullException(nameof(remote));
            }

            WaVoipIdentity identity = WaVoipIdentity.Create("wa-voip-test-server");
            var datagram = new UdpDatagramTransport(udp, remote);
            datagram.StartPump();

            var protocol = new DtlsServerProtocol();
            var server = new WaVoipDtlsServer(identity);
            return RunWithTimeout(
                () => protocol.Accept(server, datagram),
                timeout,
                "DTLS server handshake timed out");
        }

        private static DtlsTransport RunWithTimeout(
            Func<DtlsTransport> work,
            TimeSpan timeout,
            string timeoutMessage)
        {
            DtlsTransport result = null;
            Exception error = null;
            var done = new ManualResetEventSlim(false);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    result = work();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    done.Set();
                }
            });

            if (!done.Wait(timeout))
            {
                throw new TimeoutException(timeoutMessage);
            }

            if (error != null)
            {
                throw new IOException("DTLS handshake failed: " + error.Message, error);
            }

            return result;
        }
    }
}
