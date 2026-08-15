// =============================================================================
// WaPadding
//
// Removes the random padding WhatsApp appends to an encrypted payload.
//
// The last byte says how many bytes to drop, up to sixteen. A value outside that
// range means the payload was never padded - some message variants are not - so
// the bytes are returned untouched rather than truncated into nonsense.
//
// Ports: rc14 unpadRandomMax16 in src/Utils/generics.ts
// =============================================================================
using System;

namespace Unison.Socket.Utils
{
    public static class WaPadding
    {
        private const int MaxPadding = 16;

        public static byte[] UnpadRandomMax16(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                throw new InvalidOperationException("Cannot unpad an empty payload");
            }

            int padding = data[data.Length - 1];
            if (padding == 0 || padding > MaxPadding || padding > data.Length)
            {
                return data;
            }

            var result = new byte[data.Length - padding];
            Buffer.BlockCopy(data, 0, result, 0, result.Length);
            return result;
        }
    }
}
