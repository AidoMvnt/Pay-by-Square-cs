using System;

namespace PayBySquare
{
    /// <summary>
    /// Fixed geometry, palette and caption strings for the "PAY by square"
    /// decorated tile. Every length is expressed in pixels, and
    /// <see cref="QrPixel"/> — the size of a single QR module in output
    /// pixels — is owned by the layout so the caller sizes the tile exactly.
    /// </summary>
    /// <remarks>
    /// Use <see cref="Default"/> for the built-in palette and proportions, or
    /// construct/modify your own instance:
    /// <code>
    ///   var layout = new Layout
    ///   {
    ///       QrPixel   = 12,                          // one QR module = 12 px
    ///       QrPad     = 24,
    ///       QrBorder  = 6,
    ///       CaptionFont = 26,                          // wordmark height
    ///       IconPx    = 56,
    ///       Page      = (0x12, 0x12, 0x12),
    ///       PayInk    = (0x15, 0x5E, 0xC7),
    ///   };
    ///   var deco = new QrTileDecorator(Asset.Default, layout);
    /// </code>
    /// </remarks>
    public sealed class Layout
    {
        // ============================ palette ============================
        // (R, G, B) tuples, each component 0..255.

        /// <summary>Dark page background (around the card).</summary>
        public (int R, int G, int B) Page { get; set; } = (0x21, 0x21, 0x21);

        /// <summary>Card body colour (white by default).</summary>
        public (int R, int G, int B) Card { get; set; } = (0xFF, 0xFF, 0xFF);

        /// <summary>Blue frame between the card body and the QR code.</summary>
        public (int R, int G, int B) Frame { get; set; } = (0x7F, 0xA8, 0xD0);

        /// <summary>Ink of the "PAY" word (used by the SVG renderer).</summary>
        public (int R, int G, int B) PayInk { get; set; } = (0x4A, 0x6D, 0x9C);

        /// <summary>Ink of the "by square" words (used by the SVG renderer).</summary>
        public (int R, int G, int B) ByInk { get; set; } = (0xD8, 0xDD, 0xE2);

        /// <summary>Dark modules of the QR code.</summary>
        public (int R, int G, int B) QrDark { get; set; } = (0x00, 0x00, 0x00);

        /// <summary>Light (empty) modules of the QR code.</summary>
        public (int R, int G, int B) QrLight { get; set; } = (0xFF, 0xFF, 0xFF);

        // ============================ QR geometry ============================

        /// <summary>
        /// The size of a single QR module in output pixels (module scale).
        /// This is the knob the layout owns: it fully determines the QR's
        /// pixel dimensions (<c>n * QrPixel</c> for an <c>n x n</c> code) and,
        /// proportionally, the whole card size.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// <see cref="QrPixel"/> must be &gt;= 1.</exception>
        public int QrPixel
        {
            get => _qrPx;
            set { if (value < 1) throw new InvalidOperationException("Layout.QrPixel must be >= 1"); _qrPx = value; }
        }
        int _qrPx = 10;

        /// <summary>Padding (px) between the card body and the QR code.</summary>
        public int QrPad { get; set; } = 20;

        /// <summary>Blue frame thickness (px) around the QR code.</summary>
        public int QrBorder { get; set; } = 6;

        /// <summary>Rounded-corner radius (px) of the card body.</summary>
        public int CardCorner { get; set; } = 14;

        // ============================ caption row ============================

        /// <summary>Height (px) of the "PAY by square" wordmark in the caption row.</summary>
        public int CaptionFont { get; set; } = 22;

        /// <summary>Brand icon size in the caption row, in px (0 hides it).</summary>
        public int IconPx { get; set; } = 48;

        /// <summary>Gap (px) between the QR frame and the caption row.</summary>
        public int GapAboveCaption { get; set; } = 16;

        /// <summary>Gap (px) below the caption row, to the page background.</summary>
        public int GapBelowCaption { get; set; } = 12;

        /// <summary>Turn the caption wordmark + icon row off entirely when false.</summary>
        public bool ShowCaption { get; set; } = true;

        /// <summary>Turn the brand icon off (wordmark only) when false.</summary>
        public bool ShowIcon { get; set; } = true;

        /// <summary>
        /// Horizontal alignment of the caption row within the card:
        /// <c>Left</c>, <c>Center</c> or <c>Right</c> (default <c>Right</c>,
        /// matching the right-aligned caption the tile has always used).
        /// </summary>
        public QrAlignment Align { get; set; } = QrAlignment.Right;

        // ============================ factory ============================

        /// <summary>Built-in palette and proportions (used when no layout is supplied).</summary>
        public static Layout Default { get; } = new Layout();

        /// <summary>Value-copy so shared instances can't be mutated by accident.</summary>
        public Layout Clone()
        {
            return new Layout
            {
                Page = Page, Card = Card, Frame = Frame,
                PayInk = PayInk, ByInk = ByInk,
                QrDark = QrDark, QrLight = QrLight,
                QrPixel = QrPixel, QrPad = QrPad, QrBorder = QrBorder, CardCorner = CardCorner,
                CaptionFont = CaptionFont, IconPx = IconPx,
                GapAboveCaption = GapAboveCaption, GapBelowCaption = GapBelowCaption,
                ShowCaption = ShowCaption, ShowIcon = ShowIcon, Align = Align,
            };
        }
    }

    /// <summary>Horizontal alignment options for the caption row.</summary>
    public enum QrAlignment
    {
        Left,
        Center,
        Right,
    }
}
