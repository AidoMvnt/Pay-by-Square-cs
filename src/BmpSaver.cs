using System;
using System.IO;

namespace PayBySquare
{
    /// <summary>
    /// Writes a boolean matrix (true = dark pixel) as a valid 1-bit monochrome
    /// BMP file — pure .NET, no System.Drawing, no Windows GDI, works on Linux.
    /// </summary>
    public static class BmpSaver
    {
        /// <summary>
        /// Saves the matrix as 1-bit BMP. data[row, col], row 0 = TOP of image.
        /// true = black (dark module), false = white.
        /// </summary>
        public static void Save(bool[,] data, string filePath)
        {
            int width = data.GetLength(1);
            int height = data.GetLength(0);

            // BMP requirements for 1bpp, uncompressed:
            //  - rows stored bottom-to-top
            //  - each row padded to 4-byte boundary
            //  - a 2-entry color table (4 bytes each) is MANDATORY
            int rowBytes = (width + 7) / 8;
            int rowBytesPadded = (rowBytes + 3) & ~3;
            int paletteSize = 8;                       // 2 entries * 4 bytes
            int pixelDataSize = rowBytesPadded * height;
            int fileSize = 14 + 40 + paletteSize + pixelDataSize;

            using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                // ---- BITMAPFILEHEADER (14 bytes) ----
                w.Write((byte)'B'); w.Write((byte)'M');  // bfType
                w.Write(fileSize);                        // bfSize (uint32 LE)
                w.Write((ushort)0);                       // bfReserved1
                w.Write((ushort)0);                       // bfReserved2
                w.Write(14 + 40 + paletteSize);           // bfOffBits

                // ---- BITMAPINFOHEADER (40 bytes) ----
                w.Write(40);                              // biSize
                w.Write(width);                           // biWidth  (int32)
                w.Write(height);                          // biHeight (int32, positive = bottom-up)
                w.Write((ushort)1);                       // biPlanes
                w.Write((ushort)1);                       // biBitCount
                w.Write((uint)0);                         // biCompression = BI_RGB
                w.Write(pixelDataSize);                   // biSizeImage
                w.Write((int)2835);                        // biXPelsPerMeter (72 dpi)
                w.Write((int)2835);                        // biYPelsPerMeter
                w.Write((uint)2);                          // biClrUsed = 2
                w.Write((uint)0);                          // biClrImportant

                // ---- COLOR TABLE (mandatory, 2 entries, BGRA, little-endian) ----
                w.Write((byte)255); w.Write((byte)255); w.Write((byte)255); w.Write((byte)0); // index 0 = WHITE
                w.Write((byte)0);   w.Write((byte)0);   w.Write((byte)0);   w.Write((byte)0); // index 1 = BLACK

                // ---- PIXEL DATA (bottom-up) ----
                byte[] row = new byte[rowBytesPadded];
                for (int y = height - 1; y >= 0; y--)
                {
                    Array.Clear(row, 0, rowBytesPadded);
                    for (int x = 0; x < width; x++)
                    {
                        // BMP bit order: leftmost pixel of the row = most significant bit (bit 7)
                        if (data[y, x]) // dark = palette index 1
                        {
                            int byteIdx = x / 8;
                            int bitIdx = 7 - (x % 8);
                            row[byteIdx] |= (byte)(1 << bitIdx);
                        }
                    }
                    w.Write(row);
                }
            }
        }
    }
}
