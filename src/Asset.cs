using System;

namespace PayBySquare
{
    /// <summary>
    /// Public facade for the baked tile assets (wordmark + icon).
    ///
    /// <see cref="CapW"/> / <see cref="CapH"/> describe the "PAY by square"
    /// wordmark bitmap (white card background, colours baked).
    /// <see cref="IconSize"/> is the square icon dimension.
    ///
    /// <see cref="Default"/> is the canonical instance — one bitmap, one
    /// wordmark, shared by every decorator. Construct a custom asset only if
    /// you have a different baked set:
    /// <code>
    ///   // custom asset class (implement your own Cap/Icon arrays)
    ///   var a = new CustomAsset();   // subclass or another implementation
    ///   var deco = new QrTileDecorator(a, new Layout { QrPixel = 16 });
    /// </code>
    /// </summary>
    public sealed class Asset
    {
        /// <summary>Width of the "PAY by square" wordmark bitmap, in px.</summary>
        public int CapW => TileAssets.CapW;

        /// <summary>Height of the "PAY by square" wordmark bitmap, in px (~64 cap height).</summary>
        public int CapH => TileAssets.CapH;

        /// <summary>Icon edge length, in px.</summary>
        public int IconSize => TileAssets.IconSize;

        /// <summary>
        /// Sample the wordmark bitmap at <c>(x, y)</c> as RGBA (true alpha).
        /// Out-of-range coordinates return fully transparent.
        /// </summary>
        public (int r, int g, int b, int a) CapPixel(int x, int y) => TileAssets.CapPixel(x, y);

        /// <summary>
        /// Sample the icon at <c>(x, y)</c> as RGBA.
        /// Out-of-range coordinates return fully transparent.
        /// </summary>
        public (int r, int g, int b, int a) IconPixel(int x, int y) => TileAssets.IconPixel(x, y);

        /// <summary>Built-in asset (baked wordmark + brand icon).</summary>
        public static Asset Default { get; } = new Asset();
    }
}
