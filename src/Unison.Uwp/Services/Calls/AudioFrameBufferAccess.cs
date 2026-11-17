// =============================================================================
// AudioFrameBufferAccess
//
// Float32 view of an AudioFrame buffer via IMemoryBufferByteAccess (AudioGraph
// IEEE float samples). Requires AllowUnsafeBlocks on Unison.Uwp.
// =============================================================================
using System;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Media;

namespace Unison.Uwp.Services.Calls
{
    internal static class AudioFrameBufferAccess
    {
        public static unsafe float[] ReadAllFloats(AudioFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            using (AudioBuffer buffer = frame.LockBuffer(AudioBufferAccessMode.Read))
            using (IMemoryBufferReference reference = buffer.CreateReference())
            {
                byte* data;
                uint capacity;
                ((IMemoryBufferByteAccess)reference).GetBuffer(out data, out capacity);
                int available = (int)(capacity / sizeof(float));
                if (available <= 0)
                {
                    return new float[0];
                }

                var destination = new float[available];
                fixed (float* dest = destination)
                {
                    System.Buffer.MemoryCopy(data, dest, capacity, capacity);
                }

                return destination;
            }
        }

        public static unsafe void WriteFloats(AudioFrame frame, float[] source, int sourceOffset, int count)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (sourceOffset < 0 || count < 0 || sourceOffset + count > source.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            using (AudioBuffer buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
            using (IMemoryBufferReference reference = buffer.CreateReference())
            {
                byte* data;
                uint capacity;
                ((IMemoryBufferByteAccess)reference).GetBuffer(out data, out capacity);
                int available = (int)(capacity / sizeof(float));
                int toCopy = Math.Min(count, available);
                if (toCopy <= 0)
                {
                    return;
                }

                fixed (float* src = source)
                {
                    System.Buffer.MemoryCopy(
                        src + sourceOffset,
                        data,
                        toCopy * sizeof(float),
                        toCopy * sizeof(float));
                }

                // Zero leftover samples so we do not leak prior audio.
                float* floats = (float*)data;
                for (int i = toCopy; i < available; i++)
                {
                    floats[i] = 0f;
                }
            }
        }

        public static unsafe void WriteSilence(AudioFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            using (AudioBuffer buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
            using (IMemoryBufferReference reference = buffer.CreateReference())
            {
                byte* data;
                uint capacity;
                ((IMemoryBufferByteAccess)reference).GetBuffer(out data, out capacity);
                float* floats = (float*)data;
                int count = (int)(capacity / sizeof(float));
                for (int i = 0; i < count; i++)
                {
                    floats[i] = 0f;
                }
            }
        }

        [ComImport]
        [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private unsafe interface IMemoryBufferByteAccess
        {
            void GetBuffer(out byte* buffer, out uint capacity);
        }
    }
}
