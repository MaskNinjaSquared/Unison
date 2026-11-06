// =============================================================================
// ProfilePictureEncoder
//
// Square JPEG for w:profile:picture (group / community picture at create time).
// Mirrors Baileys generateProfilePicture: centre crop, 640×640, JPEG.
// =============================================================================
using System;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Unison.Uwp.Services
{
    public sealed class ProfilePictureEncoder : IProfilePictureEncoder
    {
        private const uint TargetSide = 640;
        private const float JpegQuality = 0.8f;

        public async Task<byte[]> EncodeSquareJpegAsync(byte[] image)
        {
            if (image == null || image.Length == 0)
            {
                return null;
            }

            try
            {
                using (var input = new InMemoryRandomAccessStream())
                {
                    await input.WriteAsync(image.AsBuffer());
                    input.Seek(0);

                    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
                    PixelDataProvider pixels = await decoder.GetPixelDataAsync(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Ignore,
                        new BitmapTransform(),
                        ExifOrientationMode.RespectExifOrientation,
                        ColorManagementMode.DoNotColorManage);

                    uint width = decoder.OrientedPixelWidth;
                    uint height = decoder.OrientedPixelHeight;
                    uint side = Math.Min(width, height);
                    if (side == 0)
                    {
                        return null;
                    }

                    uint target = Math.Min(TargetSide, side);
                    double scale = (double)target / side;
                    uint scaledWidth = Math.Max(target, (uint)Math.Round(width * scale));
                    uint scaledHeight = Math.Max(target, (uint)Math.Round(height * scale));

                    using (var output = new InMemoryRandomAccessStream())
                    {
                        var options = new BitmapPropertySet
                        {
                            { "ImageQuality", new BitmapTypedValue(JpegQuality, PropertyType.Single) }
                        };
                        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(
                            BitmapEncoder.JpegEncoderId,
                            output,
                            options);
                        encoder.SetPixelData(
                            BitmapPixelFormat.Bgra8,
                            BitmapAlphaMode.Ignore,
                            width,
                            height,
                            decoder.DpiX,
                            decoder.DpiY,
                            pixels.DetachPixelData());
                        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
                        encoder.BitmapTransform.ScaledWidth = scaledWidth;
                        encoder.BitmapTransform.ScaledHeight = scaledHeight;
                        encoder.BitmapTransform.Bounds = new BitmapBounds
                        {
                            X = (scaledWidth - target) / 2,
                            Y = (scaledHeight - target) / 2,
                            Width = target,
                            Height = target
                        };
                        await encoder.FlushAsync();

                        output.Seek(0);
                        var buffer = new byte[output.Size];
                        await output.ReadAsync(buffer.AsBuffer(), (uint)output.Size, InputStreamOptions.None);
                        return buffer;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ProfilePictureEncoder] " + ex.GetBaseException().Message);
                return null;
            }
        }
    }
}
