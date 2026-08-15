using System;

namespace Unison.Core.Exceptions
{
    /// <summary>
    /// Domain / UX failures that the ViewModel can map to a simple dialog
    /// without crashing the app.
    /// </summary>
    public abstract class UnisonUserException : Exception
    {
        protected UnisonUserException(
            string resourceKey,
            string fallbackMessage,
            string technicalMessage = null,
            Exception innerException = null)
            : base(technicalMessage ?? fallbackMessage, innerException)
        {
            ResourceKey = resourceKey ?? string.Empty;
            FallbackMessage = fallbackMessage ?? string.Empty;
        }

        /// <summary>LocalizedStrings key (e.g. ChatDetail_TextSendFailed).</summary>
        public string ResourceKey { get; }

        /// <summary>English fallback when the resource is missing.</summary>
        public string FallbackMessage { get; }
    }

    /// <summary>Failed to send a text chat message.</summary>
    public sealed class TextSendException : UnisonUserException
    {
        public TextSendException(string technicalMessage = null, Exception innerException = null)
            : base(
                "ChatDetail_TextSendFailed",
                "Could not send the message.",
                technicalMessage,
                innerException)
        {
        }
    }

    /// <summary>Failed to send an image attachment.</summary>
    public sealed class ImageSendException : UnisonUserException
    {
        public ImageSendException(string technicalMessage = null, Exception innerException = null)
            : base(
                "ChatDetail_ImageSendFailed",
                "Could not send the image.",
                technicalMessage,
                innerException)
        {
        }
    }

    /// <summary>Failed to send audio / voice note.</summary>
    public sealed class AudioSendException : UnisonUserException
    {
        public AudioSendException(string technicalMessage = null, Exception innerException = null)
            : base(
                "ChatDetail_AudioSendFailed",
                "Could not send the audio.",
                technicalMessage,
                innerException)
        {
        }
    }

    /// <summary>Failed to send a generic chat attachment.</summary>
    public sealed class AttachmentSendException : UnisonUserException
    {
        public AttachmentSendException(string technicalMessage = null, Exception innerException = null)
            : base(
                "ChatDetail_AttachSendFailed",
                "Could not send the file.",
                technicalMessage,
                innerException)
        {
        }
    }
}
