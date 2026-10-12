namespace Unison.Core.Helpers
{
    /// <summary>
    /// Whether a chat still needs the full-size group picture.
    /// </summary>
    /// <remarks>
    /// A direct chat used to take the same CDN round trip. Nothing reads that file: the cache
    /// hydration that restores <c>AvatarHighUrl</c> is itself group-only, so the picture was
    /// fetched and then forgotten.
    /// </remarks>
    public static class GroupHighAvatar
    {
        public static bool ShouldFetch(bool isGroup, string jid, string highUrl)
        {
            return isGroup &&
                   !string.IsNullOrWhiteSpace(jid) &&
                   string.IsNullOrWhiteSpace(highUrl);
        }
    }
}
