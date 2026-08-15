// =============================================================================
// IUSyncProtocol
//
// One column of a usync query.
//
// A usync request is a matrix: the rows are the users being asked about, the
// columns are the pieces of information wanted about each of them. Every column
// contributes a node to <query>, may contribute a node inside each <user>, and
// knows how to read its own answer back. Splitting it this way is what lets a
// caller ask for "contact and lid" without any code understanding both.
//
// Ports: rc14 USyncQueryProtocol in src/Types/USync.ts
// =============================================================================
using Unison.Baileys.Protocol;

namespace Unison.Socket.USync
{
    public interface IUSyncProtocol
    {
        /// <summary>The node tag this protocol owns, both in the request and in the reply.</summary>
        string Name { get; }

        /// <summary>The node placed under &lt;query&gt; to request this column.</summary>
        BinaryNode GetQueryElement();

        /// <summary>
        /// The node placed inside a &lt;user&gt; row, or null when this protocol needs nothing
        /// per user.
        /// </summary>
        BinaryNode GetUserElement(USyncUser user);

        /// <summary>Reads this protocol's answer out of one user's reply, or null when absent.</summary>
        object Parse(BinaryNode node);
    }
}
