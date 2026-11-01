using Proto;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class HistoryMediaFileLengthTests
    {
        [Fact]
        public void Image_history_keeps_file_length_before_download()
        {
            var row = new HistoryMessage();
            HistoryMediaFiller.Apply(row, new WebMessageInfo
            {
                Message = new Message
                {
                    ImageMessage = new Message.Types.ImageMessage { FileLength = 2048 }
                }
            });

            Assert.Equal(2048L, row.MediaFileLengthBytes);
        }

        [Fact]
        public void Image_history_without_file_length_stays_empty()
        {
            var row = new HistoryMessage();
            HistoryMediaFiller.Apply(row, new WebMessageInfo
            {
                Message = new Message
                {
                    ImageMessage = new Message.Types.ImageMessage { Mimetype = "image/jpeg" }
                }
            });

            Assert.Equal(0L, row.MediaFileLengthBytes);
        }

        [Fact]
        public void Image_history_keeps_jpeg_thumbnail_bytes_for_dm_and_group()
        {
            byte[] jpeg = { 0xFF, 0xD8, 0xFF, 0x00, 0x01, 0x02 };
            var row = new HistoryMessage();
            HistoryMediaFiller.Apply(row, new WebMessageInfo
            {
                Message = new Message
                {
                    ImageMessage = new Message.Types.ImageMessage
                    {
                        Mimetype = "image/jpeg",
                        JpegThumbnail = Google.Protobuf.ByteString.CopyFrom(jpeg)
                    }
                }
            });

            Assert.NotNull(row.MediaThumbnailJpeg);
            Assert.Equal(jpeg, row.MediaThumbnailJpeg);
        }

        [Fact]
        public void Reloaded_image_row_exposes_the_stored_length()
        {
            var message = new ChatMessage();
            HistoryMessageMapper.ApplyMediaEnvelope(
                message,
                new HistoryMessage { MediaFileLengthBytes = 4096 },
                ChatPreviewKind.Image,
                null);

            Assert.Equal(4096L, message.MediaFileLengthBytes);
            Assert.True(message.HasMediaFileSize);
        }
    }
}
