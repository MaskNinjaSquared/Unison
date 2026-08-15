// =============================================================================
// RefreshMediaConnUseCase
//
// Asks the server where to upload and with what credentials.
//
// Uploads do not go through the socket. The server hands out a short-lived auth
// token and a list of CDN hostnames, and the file is POSTed there over plain
// HTTPS. The token expires, so the answer is cached for the TTL the server
// states and re-fetched after that - asking once per attachment would add a
// round trip to every send for a value that rarely changes.
//
// Ports: rc14 refreshMediaConn in src/Socket/messages-send.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Session;

namespace Unison.Socket.UseCases.Media
{
    /// <summary>One CDN host, with the size it refuses to go over.</summary>
    public sealed class MediaHost
    {
        public string Hostname { get; set; }

        public long MaxContentLengthBytes { get; set; }
    }

    public sealed class MediaConnInfo
    {
        public List<MediaHost> Hosts { get; set; }

        /// <summary>Opaque token that authorises the upload; goes in the query string.</summary>
        public string Auth { get; set; }

        /// <summary>Seconds the token stays valid for.</summary>
        public int Ttl { get; set; }

        public DateTime FetchedAt { get; set; }

        public bool IsExpired
        {
            get { return DateTime.UtcNow - FetchedAt >= TimeSpan.FromSeconds(Ttl); }
        }
    }

    public sealed class RefreshMediaConnUseCase
    {
        private readonly ConnectionHandler _connection;
        private readonly ISocketLog _log;
        private readonly object _gate = new object();

        private MediaConnInfo _cached;

        public RefreshMediaConnUseCase(ConnectionHandler connection, ISocketLog log = null)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            _connection = connection;
            _log = log ?? NullSocketLog.Instance;
        }

        /// <param name="force">
        /// Fetches even when the cached token still looks valid. Used after an upload is refused,
        /// since the server can revoke a token before its stated TTL.
        /// </param>
        public async Task<MediaConnInfo> ExecuteAsync(bool force = false, TimeSpan? timeout = null)
        {
            lock (_gate)
            {
                if (!force && _cached != null && !_cached.IsExpired)
                {
                    return _cached;
                }
            }

            var iq = new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", WA.S_WHATSAPP_NET },
                    { "type", "set" },
                    { "xmlns", "w:m" }
                },
                new List<BinaryNode> { new BinaryNode("media_conn") });

            var response = await _connection.QueryAsync(iq, timeout).ConfigureAwait(false);

            var mediaConn = response != null ? response.GetChild("media_conn") : null;
            if (mediaConn == null)
            {
                throw new InvalidOperationException("The server did not return a media connection");
            }

            var info = new MediaConnInfo
            {
                Auth = mediaConn.GetAttribute("auth"),
                Ttl = ReadInt(mediaConn.GetAttribute("ttl"), 300),
                FetchedAt = DateTime.UtcNow,
                Hosts = new List<MediaHost>()
            };

            var hosts = mediaConn.GetChildren("host");
            if (hosts != null)
            {
                foreach (var host in hosts)
                {
                    var hostname = host.GetAttribute("hostname");
                    if (string.IsNullOrEmpty(hostname))
                    {
                        continue;
                    }

                    info.Hosts.Add(new MediaHost
                    {
                        Hostname = hostname,
                        MaxContentLengthBytes = ReadInt(host.GetAttribute("maxContentLengthBytes"), 0)
                    });
                }
            }

            lock (_gate)
            {
                _cached = info;
            }

            _log.Debug("[Media] Upload connection refreshed: " + info.Hosts.Count + " host(s), ttl=" + info.Ttl + "s");
            return info;
        }

        private static int ReadInt(string value, int fallback)
        {
            int parsed;
            return int.TryParse(value, out parsed) ? parsed : fallback;
        }
    }
}
