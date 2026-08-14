namespace Unison.Core.Models
{
    /// <summary>UI / playback state for a voice or audio bubble.</summary>
    public enum AudioPlaybackStatus
    {
        /// <summary>No local file; keys available — show download.</summary>
        NotDownloaded = 0,

        /// <summary>Decrypt/download in progress.</summary>
        Downloading = 1,

        /// <summary>Local file ready; not currently playing.</summary>
        Ready = 2,

        /// <summary>Currently playing — show pause + live position.</summary>
        Playing = 3,

        /// <summary>Paused — show play + frozen position.</summary>
        Paused = 4,

        /// <summary>Cannot download (missing keys/url) — download + red error glyph.</summary>
        NotAvailable = 5
    }
}
