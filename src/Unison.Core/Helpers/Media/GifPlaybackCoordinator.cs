using System;
using System.Collections.Generic;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Single-active GIF playback for 512 MB (<see cref="DeviceCategory.Unsupported"/>).
    /// All clients may register; enforcement only runs on Unsupported.
    /// </summary>
    public sealed class GifPlaybackCoordinator : IGifPlaybackCoordinator
    {
        private readonly DeviceCategory _category;
        private readonly object _gate = new object();
        private readonly List<IGifPlaybackClient> _clients = new List<IGifPlaybackClient>();

        public GifPlaybackCoordinator(DeviceCategory category)
        {
            _category = category;
        }

        public void Register(IGifPlaybackClient client)
        {
            if (client == null)
            {
                return;
            }

            lock (_gate)
            {
                if (!_clients.Contains(client))
                {
                    _clients.Add(client);
                }
            }
        }

        public void Unregister(IGifPlaybackClient client)
        {
            if (client == null)
            {
                return;
            }

            lock (_gate)
            {
                _clients.Remove(client);
            }
        }

        public void NotifyPlaying(IGifPlaybackClient client)
        {
            if (client == null || _category != DeviceCategory.Unsupported)
            {
                return;
            }

            IGifPlaybackClient[] others;
            lock (_gate)
            {
                if (_clients.Count <= 1)
                {
                    return;
                }

                others = new IGifPlaybackClient[_clients.Count];
                _clients.CopyTo(others);
            }

            for (int i = 0; i < others.Length; i++)
            {
                IGifPlaybackClient other = others[i];
                if (other == null || ReferenceEquals(other, client))
                {
                    continue;
                }

                try
                {
                    other.PauseFromCoordinator();
                }
                catch (Exception)
                {
                    // Keep notifying the rest; a dead bubble must not block exclusivity.
                }
            }
        }
    }
}
