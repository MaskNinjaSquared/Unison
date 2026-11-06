using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// WhatsApp Channels (<c>@newsletter</c>) this account creates or administers.
    /// </summary>
    public interface IChannelService
    {
        /// <summary>
        /// Baileys <c>newsletterCreate</c>, then the picture when one was picked.
        /// Never throws for a refused create; see <see cref="ChannelCreateResult.FailureReason"/>.
        /// </summary>
        Task<ChannelCreateResult> CreateChannelAsync(ChannelCreateRequest request);

        /// <summary>
        /// Stamps this account's role on a channel row (last known first, then the server's
        /// answer), so owners and admins get the composer. No-op for non-channels.
        /// </summary>
        Task RefreshViewerRoleAsync(ChatItem channel);
    }
}
