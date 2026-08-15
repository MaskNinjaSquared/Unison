// =============================================================================
// MediaRetryProto
//
// The two tiny protobuf messages a media retry is made of, written by hand.
//
// Neither ServerErrorReceipt nor MediaRetryNotification is in the generated
// protobuf set the app carries, and both are three fields long. Encoding them
// directly is a few lines against regenerating a file of thousands, and the
// wire format for a handful of optional scalars is stable enough that the
// trade holds.
//
// Ports: rc14 proto.ServerErrorReceipt and proto.MediaRetryNotification, used
// by encryptMediaRetryRequest and decryptMediaRetryData in
// src/Utils/messages-media.ts
// =============================================================================
using System;
using System.IO;
using Google.Protobuf;

namespace Unison.Socket.Media
{
    /// <summary>How the phone says the re-upload went.</summary>
    public enum MediaRetryResult
    {
        GeneralError = 0,
        Success = 1,
        NotFound = 2,
        DecryptionError = 3
    }

    public sealed class MediaRetryNotification
    {
        public string StanzaId { get; set; }

        /// <summary>Where the file now lives, set only when the result is success.</summary>
        public string DirectPath { get; set; }

        public MediaRetryResult Result { get; set; }
    }

    public static class MediaRetryProto
    {
        /// <summary>
        /// ServerErrorReceipt: one string field carrying the id of the message that failed. The
        /// server does not read it - the phone does, and only to know which upload to redo.
        /// </summary>
        public static byte[] EncodeServerErrorReceipt(string stanzaId)
        {
            using (var buffer = new MemoryStream())
            {
                var writer = new CodedOutputStream(buffer);

                if (!string.IsNullOrEmpty(stanzaId))
                {
                    writer.WriteTag(1, WireFormat.WireType.LengthDelimited);
                    writer.WriteString(stanzaId);
                }

                writer.Flush();
                return buffer.ToArray();
            }
        }

        public static MediaRetryNotification DecodeNotification(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                return null;
            }

            var notification = new MediaRetryNotification();
            var reader = new CodedInputStream(payload);

            uint tag;
            while ((tag = reader.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 1:
                        notification.StanzaId = reader.ReadString();
                        break;

                    case 2:
                        notification.DirectPath = reader.ReadString();
                        break;

                    case 3:
                        notification.Result = (MediaRetryResult)reader.ReadEnum();
                        break;

                    default:
                        reader.SkipLastField();
                        break;
                }
            }

            return notification;
        }

        /// <summary>Plain-language reason for a failed retry, for logs and error messages.</summary>
        public static string Describe(MediaRetryResult result)
        {
            switch (result)
            {
                case MediaRetryResult.Success:
                    return "the media was re-uploaded";

                case MediaRetryResult.NotFound:
                    return "the phone no longer has the file";

                case MediaRetryResult.DecryptionError:
                    return "the phone could not decrypt the request";

                default:
                    return "the phone reported a general error";
            }
        }
    }
}
