// =============================================================================
// OutgoingDocumentKinds
//
// Which files the composer may send as a DocumentMessage. Image, audio and
// video stay on their own attach routes; this list is office / archive / text
// only so a picked "file" cannot silently become a photo the phone shows as a
// document.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    /// <summary>Allowed extensions and MIME types for outgoing document attach.</summary>
    public static class OutgoingDocumentKinds
    {
        /// <summary>FileOpenPicker filter — document extensions only.</summary>
        public static readonly string[] FileExtensions =
        {
            ".pdf",
            ".doc",
            ".docx",
            ".xls",
            ".xlsx",
            ".ppt",
            ".pptx",
            ".txt",
            ".rtf",
            ".csv",
            ".json",
            ".xml",
            ".zip",
            ".rar",
            ".7z"
        };

        public static bool IsAllowedExtension(string extension)
        {
            string ext = NormalizeExtension(extension);
            if (ext == null)
            {
                return false;
            }

            for (int i = 0; i < FileExtensions.Length; i++)
            {
                if (string.Equals(FileExtensions[i], ext, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when the MIME is one of the document types we send. Image / audio / video
        /// MIME strings are rejected even when a file name looks document-like.
        /// </summary>
        public static bool IsAllowedMime(string mimeType)
        {
            string mime = (mimeType ?? string.Empty).Trim().ToLowerInvariant();
            if (mime.Length == 0)
            {
                return false;
            }

            if (mime.StartsWith("image/", StringComparison.Ordinal) ||
                mime.StartsWith("audio/", StringComparison.Ordinal) ||
                mime.StartsWith("video/", StringComparison.Ordinal))
            {
                return false;
            }

            return mime == "application/pdf" ||
                   mime == "application/msword" ||
                   mime == "application/vnd.openxmlformats-officedocument.wordprocessingml.document" ||
                   mime == "application/vnd.ms-excel" ||
                   mime == "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" ||
                   mime == "application/vnd.ms-powerpoint" ||
                   mime == "application/vnd.openxmlformats-officedocument.presentationml.presentation" ||
                   mime == "text/plain" ||
                   mime == "application/rtf" ||
                   mime == "text/rtf" ||
                   mime == "text/csv" ||
                   mime == "application/json" ||
                   mime == "application/xml" ||
                   mime == "text/xml" ||
                   mime == "application/zip" ||
                   mime == "application/x-zip-compressed" ||
                   mime == "application/vnd.rar" ||
                   mime == "application/x-rar-compressed" ||
                   mime == "application/x-7z-compressed";
        }

        /// <summary>MIME for a picker extension, or null when the extension is not allowed.</summary>
        public static string MimeForExtension(string extension)
        {
            string ext = NormalizeExtension(extension);
            if (ext == null || !IsAllowedExtension(ext))
            {
                return null;
            }

            switch (ext)
            {
                case ".pdf":
                    return "application/pdf";
                case ".doc":
                    return "application/msword";
                case ".docx":
                    return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                case ".xls":
                    return "application/vnd.ms-excel";
                case ".xlsx":
                    return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                case ".ppt":
                    return "application/vnd.ms-powerpoint";
                case ".pptx":
                    return "application/vnd.openxmlformats-officedocument.presentationml.presentation";
                case ".txt":
                    return "text/plain";
                case ".rtf":
                    return "application/rtf";
                case ".csv":
                    return "text/csv";
                case ".json":
                    return "application/json";
                case ".xml":
                    return "application/xml";
                case ".zip":
                    return "application/zip";
                case ".rar":
                    return "application/vnd.rar";
                case ".7z":
                    return "application/x-7z-compressed";
                default:
                    return null;
            }
        }

        private static string NormalizeExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return null;
            }

            string ext = extension.Trim().ToLowerInvariant();
            if (!ext.StartsWith(".", StringComparison.Ordinal))
            {
                ext = "." + ext;
            }

            return ext;
        }
    }
}
