// =============================================================================
// ConnectionLifecycle
//
// Handles the four nodes that decide whether a session is alive: <success>
// (login accepted), <failure> (rejected, with a reason), <stream:error> (the
// server hanging up with a code) and <xmlstreamend> (the server hanging up
// without one).
//
// It is a feature module like any other rather than part of ConnectionHandler,
// because "what a successful login should trigger" grows over time - pre-key
// upload, passive IQ, key-bundle digest - and none of that belongs in the class
// that owns the wire.
//
// Ports: rc14 the 'CB:success', 'CB:failure', 'CB:stream:error' and
//        'CB:xmlstreamend' handlers in src/Socket/socket.ts, with
//        getErrorCodeFromStreamError from src/Utils/generics.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Events;

namespace Unison.Socket.Session
{
    /// <summary>
    /// Turns the server's login verdict into <see cref="WaEventKind.ConnectionUpdate"/>.
    /// </summary>
    public sealed class ConnectionLifecycle : IDisposable
    {
        private readonly ConnectionHandler _handler;
        private readonly AuthState _auth;
        private readonly IWaEventBus _events;
        private readonly ISocketLog _log;

        private readonly List<IDisposable> _registrations = new List<IDisposable>();
        private bool _disposed;

        public ConnectionLifecycle(
            ConnectionHandler handler,
            AuthState auth,
            IWaEventBus events,
            ISocketLog log = null)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (auth == null)
            {
                throw new ArgumentNullException(nameof(auth));
            }

            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            _handler = handler;
            _auth = auth;
            _events = events;
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>
        /// Raised after the connection opens, so the host can run the post-login work that
        /// still lives outside this project (pre-key upload, passive IQ, key-bundle digest).
        /// </summary>
        public event Func<Task> Opened;

        public void Attach()
        {
            _registrations.Add(_handler.RegisterNodeHandler("success", OnSuccessAsync));
            _registrations.Add(_handler.RegisterNodeHandler("failure", OnFailureAsync));
            _registrations.Add(_handler.RegisterNodeHandler("stream:error", OnStreamErrorAsync));
            _registrations.Add(_handler.RegisterNodeHandler("xmlstreamend", OnStreamEndAsync));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (var registration in _registrations)
            {
                registration.Dispose();
            }

            _registrations.Clear();
        }

        private async Task OnSuccessAsync(BinaryNode node)
        {
            var lid = node.GetAttribute("lid");
            if (!string.IsNullOrEmpty(lid) && _auth.Me != null)
            {
                _auth.Me.Lid = lid;
                await _events.EmitAsync(WaEventKind.CredsUpdate, _auth).ConfigureAwait(false);
            }

            _log.Info("Opened connection to WhatsApp");
            await _events.EmitAsync(
                WaEventKind.ConnectionUpdate,
                new ConnectionUpdate { Connection = ConnectionStatus.Open }).ConfigureAwait(false);

            var opened = Opened;
            if (opened != null)
            {
                try
                {
                    await opened().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Post-login extras must never cost us an otherwise healthy connection.
                    _log.Error("Post-login work failed", ex);
                }
            }
        }

        private Task OnFailureAsync(BinaryNode node)
        {
            var code = node.GetAttribute("reason") ?? node.GetAttribute("code");
            var text = node.GetAttribute("text") ?? "stream failure";

            int parsed;
            var reason = int.TryParse(code, out parsed)
                ? (DisconnectReason)parsed
                : DisconnectReason.BadSession;

            _log.Warn($"Stream failure: {text} ({code})");
            return _handler.EndAsync(new WaConnectionException(text, reason));
        }

        /// <summary>
        /// The server's way of hanging up with a reason attached. Reading the code matters more
        /// than it looks: 515 arrives right after pairing and means "reconnect now", while a
        /// removed companion arrives here too and means the opposite. Without this the socket
        /// only notices when the transport dies, and every one of them looks like a dropped
        /// connection worth retrying.
        /// </summary>
        /// <remarks>
        /// Unlink while the app is open is not a <c>&lt;failure reason="401"&gt;</c>. That
        /// packet only shows up on the next handshake. The live hangup is
        /// <c>&lt;stream:error&gt;&lt;conflict type="device_removed"/&gt;</c>, often with no
        /// numeric code on the parent. Missing that is what used to look like a dropped
        /// connection, reconnect, then 401.
        /// </remarks>
        private Task OnStreamErrorAsync(BinaryNode node)
        {
            DisconnectReason reason;
            string description;
            ClassifyStreamError(node, out reason, out description);

            _log.Warn("Stream error: " + description + " (" + reason + ")");

            return _handler.EndAsync(new WaConnectionException("Stream errored (" + description + ")", reason));
        }

        /// <summary>
        /// Maps a <c>stream:error</c> node to a <see cref="DisconnectReason"/>.
        /// <c>device_removed</c> wins over a missing or generic code: that is the phone
        /// unlinking this companion, not a replaced web session.
        /// </summary>
        internal static void ClassifyStreamError(
            BinaryNode node,
            out DisconnectReason reason,
            out string description)
        {
            string conflictType = null;
            bool deviceRemoved = false;
            string firstChildTag = null;

            var children = node != null ? node.GetAllChildren() : null;
            if (children != null)
            {
                for (int i = 0; i < children.Count; i++)
                {
                    var child = children[i];
                    if (child == null)
                    {
                        continue;
                    }

                    if (firstChildTag == null)
                    {
                        firstChildTag = child.Tag;
                    }

                    if (IsDeviceRemovedTag(child.Tag))
                    {
                        deviceRemoved = true;
                    }

                    if (string.Equals(child.Tag, "conflict", StringComparison.OrdinalIgnoreCase))
                    {
                        var type = child.GetAttribute("type");
                        if (!string.IsNullOrEmpty(type))
                        {
                            conflictType = type;
                        }

                        if (IsDeviceRemovedType(type) || ContainsDeviceRemoved(child))
                        {
                            deviceRemoved = true;
                        }
                    }
                    else if (ContainsDeviceRemoved(child))
                    {
                        deviceRemoved = true;
                    }
                }
            }

            if (deviceRemoved || IsDeviceRemovedType(conflictType))
            {
                reason = DisconnectReason.LoggedOut;
                description = string.IsNullOrEmpty(conflictType)
                    ? "conflict/device_removed"
                    : "conflict/" + conflictType;
                return;
            }

            var code = node != null ? node.GetAttribute("code") : null;
            int parsed;
            if (int.TryParse(code, out parsed))
            {
                reason = (DisconnectReason)parsed;
                description = firstChildTag ?? code;
                return;
            }

            if (string.Equals(firstChildTag, "conflict", StringComparison.OrdinalIgnoreCase))
            {
                reason = DisconnectReason.ConnectionReplaced;
                description = string.IsNullOrEmpty(conflictType)
                    ? "conflict"
                    : "conflict/" + conflictType;
                return;
            }

            reason = DisconnectReason.BadSession;
            description = firstChildTag ?? "unknown";
        }

        private static bool ContainsDeviceRemoved(BinaryNode node)
        {
            if (node == null)
            {
                return false;
            }

            if (IsDeviceRemovedTag(node.Tag) || IsDeviceRemovedType(node.GetAttribute("type")))
            {
                return true;
            }

            var nested = node.GetAllChildren();
            if (nested == null)
            {
                return false;
            }

            for (int i = 0; i < nested.Count; i++)
            {
                if (ContainsDeviceRemoved(nested[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsDeviceRemovedTag(string tag)
        {
            return string.Equals(tag, "device_removed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tag, "device-removed", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDeviceRemovedType(string type)
        {
            return string.Equals(type, "device_removed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "device-removed", StringComparison.OrdinalIgnoreCase);
        }

        private Task OnStreamEndAsync(BinaryNode node)
        {
            return _handler.EndAsync(
                new WaConnectionException("Connection terminated by server", DisconnectReason.ConnectionClosed));
        }
    }
}
