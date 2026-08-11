using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Unison.Uwp.Services
{
    public sealed class FilePickerService : IFilePicker
    {
        private const ulong MaxImageBytes = 25UL * 1024UL * 1024UL;
        private const ulong MaxAudioBytes = 20UL * 1024UL * 1024UL;

        public async Task<string> PickOpenImagePathAsync()
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".gif");
                picker.FileTypeFilter.Add(".webp");

                StorageFile file = await picker.PickSingleFileAsync();
                return file?.Path;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[FilePickerService] Open: " + ex.Message);
                return null;
            }
        }

        public async Task<string> PickSaveTextFileAsync(string suggestedFileName, string content)
        {
            try
            {
                var picker = new FileSavePicker();
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                picker.FileTypeChoices.Add("Text File", new System.Collections.Generic.List<string> { ".txt" });
                picker.SuggestedFileName = string.IsNullOrWhiteSpace(suggestedFileName)
                    ? "unison.txt"
                    : suggestedFileName;

                StorageFile file = await picker.PickSaveFileAsync();
                if (file == null)
                    return null;

                await FileIO.WriteTextAsync(file, content ?? string.Empty);
                return file.Path;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[FilePickerService] Save: " + ex.Message);
                return null;
            }
        }

        public async Task<PickedChatMedia> PickChatAttachmentAsync()
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.ViewMode = PickerViewMode.Thumbnail;
                picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".m4a");
                picker.FileTypeFilter.Add(".mp3");
                picker.FileTypeFilter.Add(".aac");
                picker.FileTypeFilter.Add(".wav");
                picker.FileTypeFilter.Add(".amr");
                picker.FileTypeFilter.Add(".ogg");

                StorageFile file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return null;
                }

                string extension = (file.FileType ?? string.Empty).ToLowerInvariant();
                if (IsAudioExtension(extension))
                {
                    var properties = await file.GetBasicPropertiesAsync();
                    if (properties.Size > MaxAudioBytes)
                    {
                        throw new InvalidOperationException("O áudio selecionado é maior que 20 MB.");
                    }

                    byte[] bytes = await ReadStorageFileBytesAsync(file);
                    return new PickedChatMedia
                    {
                        Bytes = bytes,
                        MimeType = GetAudioMimeType(extension),
                        FileName = file.Name,
                        IsAudio = true,
                        IsImage = false
                    };
                }

                var imageProps = await file.GetBasicPropertiesAsync();
                if (imageProps.Size > MaxImageBytes)
                {
                    throw new InvalidOperationException("The selected image is larger than 25 MB.");
                }

                byte[] optimized = await ReadOptimizedImageAsync(file, 1600);
                return new PickedChatMedia
                {
                    Bytes = optimized,
                    MimeType = "image/jpeg",
                    FileName = file.Name,
                    IsAudio = false,
                    IsImage = true
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[FilePickerService] PickChatAttachment: " + ex.Message);
                throw;
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
                case ".mp3": return "audio/mpeg";
                case ".aac": return "audio/aac";
                case ".wav": return "audio/wav";
                case ".amr": return "audio/amr";
                case ".ogg": return "audio/ogg";
                case ".m4a":
                default: return "audio/mp4";
            }
        }

        private static async Task<byte[]> ReadStorageFileBytesAsync(StorageFile file)
        {
            var buffer = await FileIO.ReadBufferAsync(file);
            return buffer.ToArray();
        }

        private static async Task<byte[]> ReadOptimizedImageAsync(StorageFile file, uint maxDimension)
        {
            using (IRandomAccessStream input = await file.OpenAsync(FileAccessMode.Read))
            {
                var decoder = await BitmapDecoder.CreateAsync(input);
                uint width = decoder.PixelWidth;
                uint height = decoder.PixelHeight;
                double scale = Math.Min(1.0, maxDimension / (double)Math.Max(width, height));
                uint scaledWidth = Math.Max(1, (uint)Math.Round(width * scale));
                uint scaledHeight = Math.Max(1, (uint)Math.Round(height * scale));

                using (var output = new InMemoryRandomAccessStream())
                {
                    var encoder = await BitmapEncoder.CreateForTranscodingAsync(output, decoder);
                    encoder.BitmapTransform.ScaledWidth = scaledWidth;
                    encoder.BitmapTransform.ScaledHeight = scaledHeight;
                    encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
                    await encoder.FlushAsync();

                    if (output.Size > int.MaxValue)
                    {
                        throw new InvalidOperationException("The optimized image is too large.");
                    }

                    byte[] bytes = new byte[(int)output.Size];
                    output.Seek(0);
                    using (var reader = new DataReader(output.GetInputStreamAt(0)))
                    {
                        await reader.LoadAsync((uint)output.Size);
                        reader.ReadBytes(bytes);
                    }

                    return bytes;
                }
            }
        }

        public async Task<string> PickSaveLocalImageAsync(string sourceUriOrPath, string suggestedFileName)
        {
            try
            {
                StorageFile source = await ShareService.ResolveLocalFileAsync(sourceUriOrPath);
                if (source == null)
                {
                    throw new InvalidOperationException("Arquivo local da imagem não encontrado.");
                }

                var picker = new FileSavePicker();
                picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
                picker.FileTypeChoices.Add("JPEG", new System.Collections.Generic.List<string> { ".jpg", ".jpeg" });
                picker.FileTypeChoices.Add("PNG", new System.Collections.Generic.List<string> { ".png" });
                picker.SuggestedFileName = string.IsNullOrWhiteSpace(suggestedFileName)
                    ? "Unison_image.jpg"
                    : suggestedFileName;

                StorageFile dest = await picker.PickSaveFileAsync();
                if (dest == null)
                {
                    return null;
                }

                await source.CopyAndReplaceAsync(dest);
                return dest.Path;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[FilePickerService] SaveImage: " + ex.Message);
                throw;
            }
        }
    }
}
