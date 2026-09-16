using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Unison.Uwp.Helpers
{
    /// <summary>
    /// Static WebP decode via libwebp (ARM / W10M package). First frame only.
    /// </summary>
    internal static class WebPDecoder
    {
        [DllImport("libwebp.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr WebPDecodeRGBA(
            IntPtr data,
            UIntPtr dataSize,
            out int width,
            out int height);

        [DllImport("libwebp.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void WebPFree(IntPtr ptr);

        private static readonly object StatusLock = new object();
        private static string _lastDecodeStatus = "notProbed";
        private static string _libWebPInPackage;
        private static string _libSharpYuvInPackage;

        /// <summary>
        /// Last decode attempt: <c>ok</c>, <c>DllNotFound</c>, <c>fail</c>,
        /// <c>pngEncodeFail</c>, <c>empty</c>, or <c>notProbed</c>.
        /// </summary>
        public static string LastDecodeStatus
        {
            get
            {
                lock (StatusLock)
                {
                    return _lastDecodeStatus;
                }
            }
        }

        public static string LibWebPInPackage
        {
            get
            {
                EnsurePackageLayoutProbed();
                lock (StatusLock)
                {
                    return _libWebPInPackage ?? "unknown";
                }
            }
        }

        public static string LibSharpYuvInPackage
        {
            get
            {
                EnsurePackageLayoutProbed();
                lock (StatusLock)
                {
                    return _libSharpYuvInPackage ?? "unknown";
                }
            }
        }

        public static string FormatDiagnosticsLine()
        {
            EnsurePackageLayoutProbed();
            return "webpDecode=" + LastDecodeStatus
                + "; libwebpInPackage=" + LibWebPInPackage
                + "; libsharpyuvInPackage=" + LibSharpYuvInPackage;
        }

        /// <summary>
        /// Decodes WebP bytes to PNG. Returns null if libwebp is missing or decode fails.
        /// </summary>
        public static async Task<byte[]> TryDecodeToPngAsync(byte[] webpData)
        {
            if (webpData == null || webpData.Length == 0)
            {
                SetDecodeStatus("empty");
                return null;
            }

            DecodedFrame frame;
            try
            {
                frame = await Task.Run(() => DecodeBgra(webpData)).ConfigureAwait(false);
            }
            catch (DllNotFoundException ex)
            {
                SetDecodeStatus("DllNotFound");
                Debug.WriteLine("[WebPDecoder] libwebp.dll not found: " + ex.Message);
                return null;
            }
            catch (Exception ex)
            {
                SetDecodeStatus("fail");
                Debug.WriteLine("[WebPDecoder] Decode failed: " + ex.Message);
                return null;
            }

            if (frame == null || frame.Bgra == null || frame.Width <= 0 || frame.Height <= 0)
            {
                SetDecodeStatus("fail");
                return null;
            }

            try
            {
                using (var output = new InMemoryRandomAccessStream())
                {
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                    encoder.SetPixelData(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Straight,
                        (uint)frame.Width,
                        (uint)frame.Height,
                        96,
                        96,
                        frame.Bgra);
                    await encoder.FlushAsync();
                    output.Seek(0);
                    var reader = new DataReader(output.GetInputStreamAt(0));
                    await reader.LoadAsync((uint)output.Size);
                    byte[] png = new byte[output.Size];
                    reader.ReadBytes(png);
                    SetDecodeStatus("ok");
                    return png;
                }
            }
            catch (Exception ex)
            {
                SetDecodeStatus("pngEncodeFail");
                Debug.WriteLine("[WebPDecoder] PNG encode failed: " + ex.Message);
                return null;
            }
        }

        private static void SetDecodeStatus(string status)
        {
            lock (StatusLock)
            {
                _lastDecodeStatus = status ?? "fail";
            }
        }

        private static void EnsurePackageLayoutProbed()
        {
            lock (StatusLock)
            {
                if (_libWebPInPackage != null)
                {
                    return;
                }
            }

            string webp = "unknown";
            string sharp = "unknown";
            try
            {
                string root = Package.Current.InstalledLocation.Path;
                webp = File.Exists(Path.Combine(root, "libwebp.dll")) ? "true" : "false";
                sharp = File.Exists(Path.Combine(root, "libsharpyuv.dll")) ? "true" : "false";
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WebPDecoder] Package probe failed: " + ex.Message);
            }

            lock (StatusLock)
            {
                if (_libWebPInPackage == null)
                {
                    _libWebPInPackage = webp;
                    _libSharpYuvInPackage = sharp;
                }
            }
        }

        private static DecodedFrame DecodeBgra(byte[] webpData)
        {
            IntPtr dataPtr = IntPtr.Zero;
            IntPtr rgbaPtr = IntPtr.Zero;
            try
            {
                dataPtr = Marshal.AllocHGlobal(webpData.Length);
                Marshal.Copy(webpData, 0, dataPtr, webpData.Length);

                rgbaPtr = WebPDecodeRGBA(
                    dataPtr,
                    (UIntPtr)webpData.Length,
                    out int width,
                    out int height);
                if (rgbaPtr == IntPtr.Zero || width <= 0 || height <= 0)
                {
                    return null;
                }

                int pixelCount = width * height;
                int byteCount = pixelCount * 4;
                byte[] bgra = new byte[byteCount];
                for (int i = 0; i < pixelCount; i++)
                {
                    int src = i * 4;
                    bgra[src + 0] = Marshal.ReadByte(rgbaPtr, src + 2); // B
                    bgra[src + 1] = Marshal.ReadByte(rgbaPtr, src + 1); // G
                    bgra[src + 2] = Marshal.ReadByte(rgbaPtr, src + 0); // R
                    bgra[src + 3] = Marshal.ReadByte(rgbaPtr, src + 3); // A
                }

                return new DecodedFrame
                {
                    Width = width,
                    Height = height,
                    Bgra = bgra
                };
            }
            finally
            {
                if (dataPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(dataPtr);
                }

                if (rgbaPtr != IntPtr.Zero)
                {
                    WebPFree(rgbaPtr);
                }
            }
        }

        private sealed class DecodedFrame
        {
            public int Width;
            public int Height;
            public byte[] Bgra;
        }
    }
}
