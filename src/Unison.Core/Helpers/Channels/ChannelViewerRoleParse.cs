// =============================================================================
// ChannelViewerRoleParse — WMex viewer_metadata.role onto ChannelViewerRole.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.Helpers.Channels
{
    public static class ChannelViewerRoleParse
    {
        public static ChannelViewerRole FromWire(string role)
        {
            if (string.IsNullOrWhiteSpace(role))
            {
                return ChannelViewerRole.Unknown;
            }

            switch (role.Trim().ToUpperInvariant())
            {
                case "OWNER":
                    return ChannelViewerRole.Owner;
                case "ADMIN":
                    return ChannelViewerRole.Admin;
                case "SUBSCRIBER":
                    return ChannelViewerRole.Subscriber;
                case "GUEST":
                    return ChannelViewerRole.Guest;
                default:
                    return ChannelViewerRole.Unknown;
            }
        }
    }
}
