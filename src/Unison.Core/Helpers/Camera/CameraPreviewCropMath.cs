using System;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Center crop for <c>Stretch.UniformToFill</c>: visible source rect that fills a viewport.
    /// Used so the sent JPEG matches the camera preview framing.
    /// </summary>
    public static class CameraPreviewCropMath
    {
        public readonly struct PixelRect
        {
            public PixelRect(int x, int y, int width, int height)
            {
                X = x;
                Y = y;
                Width = width;
                Height = height;
            }

            public int X { get; }
            public int Y { get; }
            public int Width { get; }
            public int Height { get; }

            public bool CoversSource(int sourceWidth, int sourceHeight)
            {
                return X == 0 &&
                       Y == 0 &&
                       Width == sourceWidth &&
                       Height == sourceHeight;
            }
        }

        /// <summary>
        /// Pixel bounds (inclusive origin, exclusive size) of the source region shown when
        /// the image is scaled with UniformToFill into the viewport.
        /// </summary>
        public static PixelRect ComputeUniformToFillCrop(
            int sourceWidth,
            int sourceHeight,
            double viewportWidth,
            double viewportHeight)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                return new PixelRect(0, 0, 0, 0);
            }

            if (viewportWidth <= 0 || viewportHeight <= 0)
            {
                return new PixelRect(0, 0, sourceWidth, sourceHeight);
            }

            double sourceAspect = (double)sourceWidth / sourceHeight;
            double viewportAspect = viewportWidth / viewportHeight;

            if (AspectsMatch(sourceAspect, viewportAspect))
            {
                return new PixelRect(0, 0, sourceWidth, sourceHeight);
            }

            if (sourceAspect > viewportAspect)
            {
                // Source wider than viewport — crop left/right.
                int visibleWidth = Clamp(
                    (int)Math.Round(sourceHeight * viewportAspect),
                    1,
                    sourceWidth);
                int x = (sourceWidth - visibleWidth) / 2;
                return new PixelRect(x, 0, visibleWidth, sourceHeight);
            }

            // Source taller than viewport — crop top/bottom.
            int visibleHeight = Clamp(
                (int)Math.Round(sourceWidth / viewportAspect),
                1,
                sourceHeight);
            int y = (sourceHeight - visibleHeight) / 2;
            return new PixelRect(0, y, sourceWidth, visibleHeight);
        }

        private static bool AspectsMatch(double a, double b)
        {
            double scale = Math.Max(Math.Max(Math.Abs(a), Math.Abs(b)), 1.0);
            return Math.Abs(a - b) <= 0.002 * scale;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
        }
    }
}
