using System;
using System.IO;

namespace PayBySquare
{
    /// <summary>
    /// Base class for image codecs. A codec serializes an RGB pixel buffer —
    /// a single <see cref="int"/> array where each element is one pixel packed
    /// as <c>0xRRGGBB</c> (row-major, row 0 = top of the image) — into a
    /// concrete binary pixel format (BMP today; other formats may derive).
    /// </summary>
    /// <remarks>
    /// Typical flow:
    /// <code>
    ///   int w, h; int[] rgb;
    ///   (w, h, rgb) = new QrTileDecorator().RenderTile(modules);
    ///   using var fs = File.Create("tile.bmp");
    ///   new BmpSaver().Save(w, h, rgb, fs);   // or Bmp24Saver for lossless
    /// </code>
    /// </remarks>
    public abstract class ImageCodec
    {
        /// <summary>
        /// A pixel is classified as light if <b>all three</b> of R, G and B
        /// are strictly greater than this threshold. Otherwise it is dark.
        /// Default 0x80; a subclass may override to taste.
        /// </summary>
        public virtual int Threshold => 0x80;

        public static int R(int rgb) => (rgb >> 16) & 0xFF;
        public static int G(int rgb) => (rgb >> 8) & 0xFF;
        public static int B(int rgb) => rgb & 0xFF;

        /// <summary>True when the packed 0xRRGGBB pixel is light.</summary>
        protected bool IsLight(int rgb, int threshold) =>
            R(rgb) > threshold && G(rgb) > threshold && B(rgb) > threshold;

        /// <summary>
        /// Serializes an RGB image (each element of <paramref name="rgbPixels"/>
        /// is one pixel packed as <c>0xRRGGBB</c>, row-major, row 0 = top)
        /// into <paramref name="stream"/> in this codec's pixel format.
        /// </summary>
        /// <param name="width">Pixels per row.</param>
        /// <param name="height">Number of rows.</param>
        /// <param name="rgbPixels">width*height ints of the form 0xRRGGBB.</param>
        /// <param name="stream">Target stream; not closed by this method.</param>
        public abstract void Save(int width, int height, int[] rgbPixels, Stream stream);
    }
}
