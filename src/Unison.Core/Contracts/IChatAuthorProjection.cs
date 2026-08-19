namespace Unison.Core.Contracts
{
    /// <summary>
    /// Keeps each chat's group author strip (<c>ChatItem.LastMessageAuthor</c>) in sync with the
    /// names the app learns over time. A history chunk often lands the strip as a bare LID/phone
    /// because the sender's push name arrives later (roster fetch, address book, usync); this
    /// rewrites the strip when that name shows up, whether or not the chat list is on screen.
    /// </summary>
    public interface IChatAuthorProjection
    {
        /// <summary>Subscribes to the name sources and does one initial pass. Idempotent.</summary>
        void Start();
    }
}
