using System;
using System.IO;

namespace PayBySquare
{
    /// <summary>
    /// Writes an RGB image as a valid monochrome 1-bit BMP file — pure .NET,
    /// no System.Drawing, no GDI, works on Linux.
    /// </summary>
    /// <remarks>
    /// Inherits from <see cref="ImageCodec"/>. Each input pixel (packed
    /// <c>0xRRGGBB</c>) is classified with the <c>R&gt;T &amp; G&gt;T &amp; B&gt;T</c>
    /// rule (default T = 0x80): if <b>all three</b> are above the threshold
    /// the output bit is WHITE (palette index 0), otherwise BLACK (index 1).
    /// This is the "lossy" path — decorative tiles lose text/icon contrast —
    /// but it is the smallest possible representation of a two-tone QR tile.
    /// For lossless output use <see cref="Bmp24Saver"/>.
    /// </remarks>
    public sealed class BmpSaver : ImageCodec
    {
        public override int Threshold => 0x80;

        /// <summary>
        /// Convenience overload for the classic boolean matrix (true = dark
        /// module). Fills a temporary RGB buffer internally.
        /// data[row, col], row 0 = top of the image.
        /// </summary>
        public void Save(bool[,] data, string filePath)
        {
            int w = data.GetLength(1), h = data.GetLength(0);
            var rgb = new int[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    rgb[y * w + x] = data[y, x] ? 0x000000 : 0xFFFFFF;   // dark = black, light = white
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            Save(w, h, rgb, fs);
        }

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

            int rowBytes = (width + 7) / 8;
            int rowBytesPadded = (rowBytes + 3) & ~3;
            int paletteSize = 8;                                 // 2 entries * 4 bytes
            int pixelDataSize = rowBytesPadded * height;
            int fileSize = 14 + 40 + paletteSize + pixelDataSize;

            // BITMAPFILEHEADER (14)
            stream.WriteByte((byte)'B'); stream.WriteByte((byte)'M');
            WriteInt32(stream, fileSize);
            WriteUInt16(stream, 0); WriteUInt16(stream, 0);
            WriteInt32(stream, 14 + 40 + paletteSize);

            // BITMAPINFOHEADER (40)
            WriteInt32(stream, 40);
            WriteInt32(stream, width);
            WriteInt32(stream, height);                  // positive = bottom-up
            WriteUInt16(stream, 1);                       // planes
            WriteUInt16(stream, 1);                       // biBitCount
            WriteUInt32(stream, 0);                       // BI_RGB
            WriteUInt32(stream, (uint)pixelDataSize);
            WriteInt32(stream, 2835);                     // 72 dpi
            WriteInt32(stream, 2835);
            WriteUInt32(stream, 2);                       // biClrUsed
            WriteUInt32(stream, 0);

            // PALETTE (mandatory for 1bpp): index 0 = WHITE, index 1 = BLACK (BGRA)
            stream.WriteByte(255); stream.WriteByte(255); stream.WriteByte(255); stream.WriteByte(0);
            stream.WriteByte(0);   stream.WriteByte(0);   stream.WriteByte(0);   stream.WriteByte(0);

            // PIXEL DATA — bottom-up, each row padded to 4 bytes,
            // BMP bit ordering: leftmost pixel in a row is bit 7 (MSB) of the first byte.
            var row = new byte[rowBytesPadded];
            for (int y = height - 1; y >= 0; y--)
            {
                Array.Clear(row, 0, rowBytesPadded);
                for (int x = 0; x < width; x++)
                {
                    if (!IsLight(rgbPixels[y * width + x], Threshold))
                    {
                        int byteIdx = x >> 3;
                        int bitIdx = 7 - (x & 7);
                        row[byteIdx] |= (byte)(1 << bitIdx);
                    }
                }
                stream.Write(row, 0, rowBytesPadded);
            }
        }

        // ---- little helpers (static so Bmp24Saver can reuse) ----
        internal static void WriteInt32(Stream s, int v)
        {
            s.WriteByte((byte)(v & 0xFF));
            s.WriteByte((byte)((v >> 8) & 0xFF));
            s.WriteByte((byte)((v >> 16) & 0xFF));
            s.WriteByte((byte)((v >> 24) & 0xFF));
        }
        internal static void WriteUInt32(Stream s, uint v)
        {
            s.WriteByte((byte)(v & 0xFF));
            s.WriteByte((byte)((v >> 8) & 0xFF));
            s.WriteByte((byte)((v >> 16) & 0xFF));
            s.WriteByte((byte)((v >> 24) & 0xFF));
        }
        internal static void WriteUInt16(Stream s, ushort v)
        {
            s.WriteByte((byte)(v & 0xFF));
            s.WriteByte((byte)((v >> 8) & 0xFF));
        }
    }
}
