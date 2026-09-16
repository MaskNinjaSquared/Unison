// =============================================================================
// IAvatarSubject
//
// The four fields that record how a picture lookup ended, on anything that can
// have a picture -- a chat row or a group participant.
//
// They exist as a named group because they are only meaningful together: the
// url, whether we got an answer, whether we failed to get one, and why. Reading
// or writing one without the others is what lets "has no photo" and "could not
// ask" blur into each other, and that difference is what decides whether the
// app ever asks again.
// =============================================================================
using System;

namespace Unison.Core.Models
{
    public interface IAvatarSubject
    {
        /// <summary>Local cache URI when a picture is known.</summary>
        string AvatarUrl { get; set; }

        /// <summary>
        /// Last completed lookup, including a confirmed miss. An empty
        /// <see cref="AvatarUrl"/> alongside this stamp means "asked, there is no picture".
        /// </summary>
        DateTime? AvatarFetchedAtUtc { get; set; }

        /// <summary>A lookup that did not complete. Distinct from a confirmed miss.</summary>
        DateTime? AvatarFetchFailedAtUtc { get; set; }

        /// <summary>Why the last lookup ended the way it did, for diagnostics.</summary>
        string AvatarFetchFailureReason { get; set; }
    }
}
