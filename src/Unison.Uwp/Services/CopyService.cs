using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Unison.Uwp.Services
{
    public sealed class CopyService : ICopyService
    {
        private const ulong MaxImageBytes = 25UL * 1024UL * 1024UL;
        private const ulong MaxAudioBytes = 20UL * 1024UL * 1024UL;
        private const ulong MaxDocumentBytes = 100UL * 1024UL * 1024UL;
        private const int MaxFiles = 10;

        public void CopyText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            try
            {
                Clipboard.Flush();
            }
            catch
            {
                // Flush keeps the text after the app suspends. SetContent already
                // published it for the current session when Flush is refused.
            }
        }

        public bool HasPasteableMedia()
        {
            try
            {
                DataPackageView data = Clipboard.GetContent();
                if (data == null)
                {
                    return false;
                }

                return data.Contains(StandardDataFormats.Bitmap) ||
                       data.Contains(StandardDataFormats.StorageItems);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[CopyService] HasPasteableMedia: " + ex.Message);
                return false;
            }
        }

        public async Task<ClipboardPasteResult> ReadPasteAsync()
        {
            DataPackageView data;
            try
            {
                data = Clipboard.GetContent();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[CopyService] GetContent: " + ex.Message);
                return ClipboardPasteResult.Empty;
            }

            if (data == null)
            {
                return ClipboardPasteResult.Empty;
            }

            var media = new List<PickedChatMedia>();
            string text = null;

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
                Debug.WriteLine("[CopyService] StorageItems: " + ex.Message);
            }

            if (media.Count == 0)
            {
                try
                {
                    if (data.Contains(StandardDataFormats.Bitmap))
                    {
                        PickedChatMedia bitmap = await TryReadBitmapAsync(data);
                        if (bitmap != null)
                        {
                            media.Add(bitmap);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[CopyService] Bitmap: " + ex.Message);
                }
            }

            if (media.Count == 0)
            {
                try
                {
                    if (data.Contains(StandardDataFormats.Text))
                    {
                        text = await data.GetTextAsync();
                    }
                    else if (data.Contains(StandardDataFormats.WebLink))
                    {
                        Uri link = await data.GetWebLinkAsync();
                        text = link?.AbsoluteUri;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[CopyService] Text: " + ex.Message);
                }
            }

            return BuildResult(media, text);
        }

        private static ClipboardPasteResult BuildResult(List<PickedChatMedia> media, string text)
        {
            if (media != null && media.Count > 0)
            {
                return new ClipboardPasteResult
                {
                    Kind = ResolveMediaKind(media),
                    Media = media,
                    Text = null
                };
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                return new ClipboardPasteResult
                {
                    Kind = ClipboardPasteKind.Text,
                    Text = text.Trim(),
                    Media = null
                };
            }

            return ClipboardPasteResult.Empty;
        }

        private static ClipboardPasteKind ResolveMediaKind(IReadOnlyList<PickedChatMedia> media)
        {
            bool anyImage = false;
            bool anyAudio = false;
            bool anyDocument = false;
            for (int i = 0; i < media.Count; i++)
            {
                PickedChatMedia item = media[i];
                if (item == null)
                {
                    continue;
                }

                if (item.IsImage)
                {
                    anyImage = true;
                }
                else if (item.IsAudio)
                {
                    anyAudio = true;
                }
                else if (item.IsDocument)
                {
                    anyDocument = true;
                }
            }

            if (anyImage)
            {
                return ClipboardPasteKind.Image;
            }

            if (anyAudio)
            {
                return ClipboardPasteKind.Audio;
            }

            if (anyDocument)
            {
                return ClipboardPasteKind.Document;
            }

            return ClipboardPasteKind.None;
        }

        private static async Task<PickedChatMedia> TryReadBitmapAsync(DataPackageView data)
        {
            RandomAccessStreamReference streamRef = await data.GetBitmapAsync();
            if (streamRef == null)
            {
                return null;
            }

            using (IRandomAccessStreamWithContentType input = await streamRef.OpenReadAsync())
            {
                if (input.Size > MaxImageBytes)
                {
                    return null;
                }

                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
                PixelDataProvider pixels = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform(),
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);
                byte[] pixelBytes = pixels.DetachPixelData();

                using (var output = new InMemoryRandomAccessStream())
                {
                    BitmapEncoder encoder = await BitmapEncoder.CreateAsync(
                        BitmapEncoder.JpegEncoderId,
                        output);
                    encoder.SetPixelData(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        decoder.PixelWidth,
                        decoder.PixelHeight,
                        decoder.DpiX,
                        decoder.DpiY,
                        pixelBytes);
                    await encoder.FlushAsync();
                    output.Seek(0);
                    var buffer = new byte[output.Size];
                    await output.ReadAsync(
                        buffer.AsBuffer(),
                        (uint)output.Size,
                        InputStreamOptions.None);
                    return new PickedChatMedia
                    {
                        Bytes = buffer,
                        MimeType = "image/jpeg",
                        FileName = "clipboard.jpg",
                        IsImage = true
                    };
                }
            }
        }

        private static async Task<PickedChatMedia> TryReadFileAsync(StorageFile file)
        {
            try
            {
                string extension = (file.FileType ?? string.Empty).ToLowerInvariant();
                if (IsVideoExtension(extension))
                {
                    return null;
                }

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

                if (IsAudioExtension(extension))
                {
                    var props = await file.GetBasicPropertiesAsync();
                    if (props.Size > MaxAudioBytes)
                    {
                        return null;
                    }

                    byte[] bytes = await ReadBytesAsync(file);
                    return new PickedChatMedia
                    {
                        Bytes = bytes,
                        MimeType = GetAudioMimeType(extension),
                        FileName = file.Name,
                        IsAudio = true
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
                Debug.WriteLine("[CopyService] file " + file?.Name + ": " + ex.Message);
                return null;
            }
        }

        private static bool IsVideoExtension(string extension)
        {
            switch ((extension ?? string.Empty).ToLowerInvariant())
            {
                case ".mp4":
                case ".mov":
                case ".mkv":
                case ".avi":
                case ".webm":
                case ".3gp":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsImageExtension(string extension)
        {
            switch ((extension ?? string.Empty).ToLowerInvariant())
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

        private static bool IsAudioExtension(string extension)
        {
            switch ((extension ?? string.Empty).ToLowerInvariant())
            {
                case ".m4a":
                case ".mp3":
                case ".aac":
                case ".wav":
                case ".amr":
                case ".ogg":
                    return true;
                default:
                    return false;
            }
        }

        private static string GetAudioMimeType(string extension)
        {
            switch ((extension ?? string.Empty).ToLowerInvariant())
            {
                case ".mp3":
                    return "audio/mpeg";
                case ".aac":
                    return "audio/aac";
                case ".wav":
                    return "audio/wav";
                case ".amr":
                    return "audio/amr";
                case ".ogg":
                    return "audio/ogg";
                case ".m4a":
                default:
                    return "audio/mp4";
            }
        }

        private static async Task<byte[]> ReadBytesAsync(StorageFile file)
        {
            IBuffer buffer = await FileIO.ReadBufferAsync(file);
            return buffer.ToArray();
        }
    }
}
