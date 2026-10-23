using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {
        /// <summary>
        /// Receipt path: apply a metadata IQ the client already fetched (no second query).
        /// </summary>
        private void ApplyGroupSendPermissionsFromMetadata(BinaryNode response, string groupJid)
        {
            GroupMetadataSnapshot snapshot = _groupMetadata.ReadMetadata(response, groupJid);
            IGroupService groups = Groups;
            if (groups == null)
            {
                Debug.WriteLine("[WhatsAppService] Group metadata apply skipped: IGroupService missing");
                return;
            }

            _ = groups.ApplyGroupMetadataAsync(groupJid, snapshot);
        }
    }
}
