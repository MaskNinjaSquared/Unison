namespace Unison.Core.Models
{
    /// <summary>
    /// Conversation category for list/detail UI (not message preview kind).
    /// </summary>
    public enum ChatKind
    {
        /// <summary>1:1 chat with another person.</summary>
        Direct = 0,

        /// <summary>Group (@g.us).</summary>
        Group = 1,

        /// <summary>Chat with yourself (notes / “Message yourself”).</summary>
        Personal = 2
    }
}
