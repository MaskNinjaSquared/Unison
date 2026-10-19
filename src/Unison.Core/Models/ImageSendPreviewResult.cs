namespace Unison.Core.Models
{
    /// <summary>Outcome of the image send confirmation dialog.</summary>
    public sealed class ImageSendPreviewResult
    {
        /// <summary>False when the user cancelled or the dialog could not be shown.</summary>
        public bool Confirmed { get; set; }

        /// <summary>Caption typed in the dialog; null/whitespace means send the image alone.</summary>
        public string Caption { get; set; }
    }
}
