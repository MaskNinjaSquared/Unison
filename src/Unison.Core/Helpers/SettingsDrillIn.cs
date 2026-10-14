namespace Unison.Core.Helpers
{
    /// <summary>
    /// Settings root versus one open category. Back closes the category before it leaves the page.
    /// </summary>
    public sealed class SettingsDrillIn
    {
        public string OpenId { get; private set; }

        public bool IsRoot => string.IsNullOrEmpty(OpenId);

        public void Open(string id)
        {
            OpenId = string.IsNullOrWhiteSpace(id) ? null : id;
        }

        /// <summary>True when a category was open and is now closed.</summary>
        public bool TryClose()
        {
            if (IsRoot)
            {
                return false;
            }

            OpenId = null;
            return true;
        }
    }
}
