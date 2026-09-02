using System;
using System.IO;

namespace PayBySquare
{
    /// <summary>
    /// Writes an RGB image as a 24-bit uncompressed BMP — pure .NET, no
    /// System.Drawing, no GDI, works on Linux. Rows are stored bottom-up,
    /// 4-byte padded, pixels in BGR order (BMP convention).
    /// </summary>
    /// <remarks>
    /// Each input pixel (packed <c>0xRRGGBB</c>) is stored as its three
    /// native color bytes — no threshold, no data loss. This is the desired
    /// codec for the <see cref="QrTileDecorator"/> output because the
    /// decorative tile uses several distinct colors (page, card, blue
    /// frame, "PAY" accent, "by square" ink, the brand icon...).
    /// </remarks>
    public sealed class Bmp24Saver : ImageCodec
    {
        /// <summary>Convenience overload: saves to a file.</summary>
        public void Save(int width, int height, int[] rgbPixels, string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            Save(width, height, rgbPixels, fs);
        }

        /// <inheritdoc />
        public override void Save(int width, int height, int[] rgbPixels, Stream stream)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentException("width and height must be positive");
            if (rgbPixels == null) throw new ArgumentNullException(nameof(rgbPixels));
            int n = width * height;
            if (rgbPixels.Length < n)
                throw new ArgumentException($"rgbPixels has {rgbPixels.Length} entries, need {n}");

            int rowBytes = width * 3;
            int rowBytesPadded = (rowBytes + 3) & ~3;
            int pixelDataSize = rowBytesPadded * height;
            int fileSize = 14 + 40 + pixelDataSize;

            // BITMAPFILEHEADER
            stream.WriteByte((byte)'B'); stream.WriteByte((byte)'M');
            BmpSaver.WriteInt32(stream, fileSize);
            BmpSaver.WriteUInt16(stream, 0); BmpSaver.WriteUInt16(stream, 0);
            BmpSaver.WriteInt32(stream, 14 + 40);

            // BITMAPINFOHEADER
            BmpSaver.WriteInt32(stream, 40);
            BmpSaver.WriteInt32(stream, width);
            BmpSaver.WriteInt32(stream, height);      // positive = bottom-up
            BmpSaver.WriteUInt16(stream, 1);          // planes
            BmpSaver.WriteUInt16(stream, 24);         // bpp
            BmpSaver.WriteUInt32(stream, 0);          // BI_RGB
            BmpSaver.WriteUInt32(stream, (uint)pixelDataSize);
            BmpSaver.WriteInt32(stream, 2835);        // 72 dpi
            BmpSaver.WriteInt32(stream, 2835);
            BmpSaver.WriteUInt32(stream, (uint)(width * 3)); // biClrUsed (hint)
            BmpSaver.WriteUInt32(stream, 0);          // biClrImportant

            // PIXEL DATA — bottom-up, BGR, padded to 4
            var row = new byte[rowBytesPadded];
            for (int y = height - 1; y >= 0; y--)
            {
                Array.Clear(row, 0, rowBytesPadded);
                for (int x = 0; x < width; x++)
                {
                    int rgb = rgbPixels[y * width + x];
                    row[x * 3 + 0] = (byte)(rgb & 0xFF);           // B
                    row[x * 3 + 1] = (byte)((rgb >> 8) & 0xFF);    // G
                    row[x * 3 + 2] = (byte)((rgb >> 16) & 0xFF);   // R
                }
                stream.Write(row, 0, rowBytesPadded);
            }
        }
    }
}
