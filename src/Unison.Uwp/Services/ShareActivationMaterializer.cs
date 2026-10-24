using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// Reads a <see cref="ShareOperation"/> into an <see cref="IncomingSharePayload"/>
    /// (text + images/documents as <see cref="PickedChatMedia"/>).
    /// </summary>
    public static class ShareActivationMaterializer
    {
        private const ulong MaxImageBytes = 25UL * 1024UL * 1024UL;
        private const ulong MaxDocumentBytes = 100UL * 1024UL * 1024UL;
        private const int MaxFiles = 10;

        public static async Task<IncomingSharePayload> MaterializeAsync(ShareOperation operation)
        {
            if (operation?.Data == null)
            {
                return null;
            }

            var payload = new IncomingSharePayload();
            DataPackageView data = operation.Data;

            try
            {
                if (data.Contains(StandardDataFormats.Text))
                {
                    payload.Text = await data.GetTextAsync();
                }
                else if (data.Contains(StandardDataFormats.WebLink))
                {
                    Uri link = await data.GetWebLinkAsync();
                    payload.Text = link?.AbsoluteUri;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShareMaterializer] text: " + ex.Message);
            }

            var media = new List<PickedChatMedia>();
            try
            {
                if (data.Contains(StandardDataFormats.StorageItems))
                {
                    IReadOnlyList<IStorageItem> items = await data.GetStorageItemsAsync();
                    int taken = 0;
                    foreach (IStorageItem item in items)
                    {
                        if (taken >= MaxFiles)
                        {
                            break;
                        }

                        var file = item as StorageFile;
                        if (file == null)
                        {
                            continue;
                        }

                        PickedChatMedia picked = await TryReadFileAsync(file);
                        if (picked == null)
                        {
                            continue;
                        }

                        media.Add(picked);
                        taken++;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShareMaterializer] files: " + ex.Message);
            }

            payload.Media = media;
            return payload.IsEmpty ? null : payload;
        }

        private static async Task<PickedChatMedia> TryReadFileAsync(StorageFile file)
        {
            try
            {
                string extension = (file.FileType ?? string.Empty).ToLowerInvariant();
                if (IsImageExtension(extension))
                {
                    var props = await file.GetBasicPropertiesAsync();
                    if (props.Size > MaxImageBytes)
                    {
                        return null;
                    }

                    byte[] bytes = await ReadBytesAsync(file);
                    return new PickedChatMedia
                    {
                        Bytes = bytes,
                        MimeType = "image/jpeg",
                        FileName = file.Name,
                        IsImage = true
                    };
                }

                string mime = OutgoingDocumentKinds.MimeForExtension(extension);
                if (mime == null || !OutgoingDocumentKinds.IsAllowedMime(mime))
                {
                    return null;
                }

                var docProps = await file.GetBasicPropertiesAsync();
                if (docProps.Size > MaxDocumentBytes)
                {
                    return null;
                }

                byte[] docBytes = await ReadBytesAsync(file);
                return new PickedChatMedia
                {
                    Bytes = docBytes,
                    MimeType = mime,
                    FileName = file.Name,
                    IsDocument = true
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShareMaterializer] file " + file?.Name + ": " + ex.Message);
                return null;
            }
        }

        private static bool IsImageExtension(string extension)
        {
            switch (extension)
            {
                case ".jpg":
                case ".jpeg":
                case ".png":
                case ".gif":
                case ".bmp":
                case ".webp":
                    return true;
                default:
                    return false;
            }
        }

        private static async Task<byte[]> ReadBytesAsync(StorageFile file)
        {
            IBuffer buffer = await FileIO.ReadBufferAsync(file);
            var bytes = new byte[buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer))
            {
                reader.ReadBytes(bytes);
            }

            return bytes;
        }
    }
}
