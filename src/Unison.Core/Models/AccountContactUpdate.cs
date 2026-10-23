// =============================================================================
// AccountContactUpdate
//
// One contact as the account publishes it (app-state / history / group roster).
// Socket ContactUpdate maps here at the bridge so Core contracts stay free of
// Unison.Socket types.
// =============================================================================
namespace Unison.Core.Models
{
    public sealed class AccountContactUpdate
    {
        public string Id { get; set; }

        public string Lid { get; set; }

        public string PhoneNumber { get; set; }

        public string Name { get; set; }

        public string Notify { get; set; }
    }
}
