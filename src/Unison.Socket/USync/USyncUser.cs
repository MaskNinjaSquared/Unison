// =============================================================================
// USyncUser
//
// One row of a usync query: who is being asked about.
//
// A user can be named by JID, by phone number, or by username, and which one is
// set changes the shape of the request - a JID becomes an attribute on the row,
// a phone number becomes content inside the contact column. The builder keeps
// that decision in one type instead of scattering it over the call sites.
//
// Ports: rc14 src/WAUSync/USyncUser.ts
// =============================================================================
namespace Unison.Socket.USync
{
    public sealed class USyncUser
    {
        public string Id { get; set; }

        public string Lid { get; set; }

        /// <summary>Phone number in international form, including the leading "+".</summary>
        public string Phone { get; set; }

        public string Username { get; set; }

        public string UsernameKey { get; set; }

        public string Type { get; set; }

        public string PersonaId { get; set; }

        public USyncUser WithId(string id)
        {
            Id = id;
            return this;
        }

        public USyncUser WithLid(string lid)
        {
            Lid = lid;
            return this;
        }

        public USyncUser WithPhone(string phone)
        {
            Phone = phone;
            return this;
        }

        public USyncUser WithUsername(string username)
        {
            Username = username;
            return this;
        }

        public USyncUser WithUsernameKey(string usernameKey)
        {
            UsernameKey = usernameKey;
            return this;
        }

        public USyncUser WithType(string type)
        {
            Type = type;
            return this;
        }

        public USyncUser WithPersonaId(string personaId)
        {
            PersonaId = personaId;
            return this;
        }
    }
}
