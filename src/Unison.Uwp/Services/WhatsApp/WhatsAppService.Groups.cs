using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {
        private void ApplyGroupSendPermissionsFromMetadata(BinaryNode response, string groupJid)
        {
            _ = _groupApply.ApplyMetadataAsync(groupJid, _groupMetadata.ReadMetadata(response, groupJid));
        }
    }
}
