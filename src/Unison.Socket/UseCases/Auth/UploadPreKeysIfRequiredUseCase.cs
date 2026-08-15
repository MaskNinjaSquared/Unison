// =============================================================================
// UploadPreKeysIfRequiredUseCase
//
// Asks the server how many one-time prekeys it still holds for this device, and
// publishes a fresh batch only when the supply is nearly gone.
//
// The check is the whole point. A batch is over eight hundred keys, each one
// generated, stored and - for a host that persists on change - written to disk;
// uploading unconditionally on every connect costs seconds of work, grows the
// key store without bound and leaves behind keys nobody will ever use. The
// server is the only one who knows how many are left, so it is asked.
//
// Ports: rc14 uploadPreKeysToServerIfRequired / getAvailablePreKeysOnServer in
// src/Socket/socket.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Session;
using Unison.Socket.WABinary;

namespace Unison.Socket.UseCases.Auth
{
    public sealed class UploadPreKeysIfRequiredUseCase
    {
        private readonly ConnectionHandler _connection;
        private readonly UploadPreKeysUseCase _upload;
        private readonly SocketConfig _config;
        private readonly ISocketLog _log;

        public UploadPreKeysIfRequiredUseCase(
            ConnectionHandler connection,
            UploadPreKeysUseCase upload,
            SocketConfig config,
            ISocketLog log = null)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            if (upload == null)
            {
                throw new ArgumentNullException(nameof(upload));
            }

            _connection = connection;
            _upload = upload;
            _config = config ?? new SocketConfig();
            _log = log ?? NullSocketLog.Instance;
        }

        /// <returns>How many keys were uploaded; zero when the server still has enough.</returns>
        public async Task<int> ExecuteAsync()
        {
            var available = await CountAsync().ConfigureAwait(false);
            _log.Info("[PreKeys] The server holds " + available + " key(s)");

            if (available > _config.MinPreKeyCount)
            {
                return 0;
            }

            return await _upload.ExecuteAsync(_config.InitialPreKeyCount).ConfigureAwait(false);
        }

        /// <summary>
        /// How many keys the server has left. A reply it cannot parse reads as zero, which errs
        /// towards uploading: a spare batch is wasteful, but no keys at all makes this device
        /// unreachable.
        /// </summary>
        public async Task<int> CountAsync()
        {
            var result = await _connection.QueryAsync(new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "id", _connection.GenerateMessageTag() },
                    { "to", JidUtils.ServerWhatsApp },
                    { "type", "get" },
                    { "xmlns", "encrypt" }
                },
                new List<BinaryNode> { new BinaryNode("count") })).ConfigureAwait(false);

            var count = result != null ? result.GetChild("count") : null;
            if (count == null)
            {
                return 0;
            }

            int value;
            return int.TryParse(count.GetAttribute("value"), out value) ? value : 0;
        }
    }
}
