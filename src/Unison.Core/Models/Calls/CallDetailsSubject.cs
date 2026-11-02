// =============================================================================
// CallDetailsSubject
//
// Selection for the Call Details pane (favorite or recent).
// =============================================================================
namespace Unison.Core.Models
{
    public sealed class CallDetailsSubject
    {
        public string Jid { get; set; }

        public string DisplayName { get; set; }

        public string AvatarUrl { get; set; }

        public bool IsGroup { get; set; }
    }
}
