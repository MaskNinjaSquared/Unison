namespace Unison.Core.Models
{
    /// <summary>
    /// One 1:1 person to write into Unison's Windows People account (no WinRT types).
    /// PhotoUri is an ms-appdata / local path; the publisher opens a StorageFile for SourceDisplayPicture.
    /// </summary>
    public sealed class AppContactExport
    {
        public string RemoteId { get; set; }

        public string DisplayName { get; set; }

        public string PhoneDigits { get; set; }

        public string PhotoUri { get; set; }
    }
}
