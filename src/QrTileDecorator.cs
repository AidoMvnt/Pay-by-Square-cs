using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PayBySquare
{
    /// <summary>
    /// Decorates a raw QR module matrix into the standard "PAY by square"
    /// tile: dark page, white rounded card with a light-blue frame, the QR
    /// inside it, and the right-aligned caption "PAY by square" (rendered
    /// from a baked bitmap) plus the brand icon.
    /// </summary>
    /// <remarks>
    /// Two constructor forms:
    /// <list type="bullet">
    /// <item><see cref="QrTileDecorator()"/> — legacy; uses the
    ///   <see cref="ModuleScale"/> / <see cref="CapPay"/> / <see cref="CapBy"/>
    ///   / <see cref="ShowCaption"/> / <see cref="ShowIcon"/> properties.</item>
    /// <item><see cref="QrTileDecorator(Asset, Layout)"/> — new;
    ///   the <see cref="Layout"/> controls palette, caption sizing and, via
    ///   <see cref="Layout.QrPixel"/>, the size of a single QR module in
    ///   output pixels.</item>
    /// </list>
    /// Output is an <c>int[]</c> buffer of packed <c>0xRRGGBB</c> pixels
    /// (row-major, row 0 = top).
    /// <see cref="ToSvg"/> emits a self-contained vector version of the same
    /// tile. No System.Drawing / GDI — works on Linux.
    /// </remarks>
    public sealed class QrTileDecorator
    {
        // ---- palette (identical to the PHP / Python renderers) ----
        public const int BG_R = 0x21, BG_G = 0x21, BG_B = 0x21;
        public const int CARD_R = 0xFF, CARD_G = 0xFF, CARD_B = 0xFF;
        public const int BORDER_R = 0x7F, BORDER_G = 0xA8, BORDER_B = 0xD0;
        public const int ACCENT_R = 0x4A, ACCENT_G = 0x6D, ACCENT_B = 0x9C;
        public const int INK_R = 0xD8, INK_G = 0xDD, INK_B = 0xE2;
        public const int QR_DARK = 0x00, QR_LIGHT = 0xFF;

        internal static int Pack(int r, int g, int b) =>
            ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF);

        // ---- state ----
        private readonly Asset _asset;
        private readonly Layout? _layout;

        // ---- layout knobs (legacy path only) ----
        public int ModuleScale { get; set; } = 10;
        public string CapPay { get; set; } = "PAY";
        public string CapBy { get; set; } = "by square";
        public bool ShowCaption { get; set; } = true;
        public bool ShowIcon { get; set; } = true;

        private const int AA = 4;  // supersampling factor for bitmap blitting

        // ================= constructor =================

        /// <summary>Legacy constructor — uses <see cref="ModuleScale"/> for the QR module size.</summary>
        public QrTileDecorator() : this(Asset.Default, null) { }

        /// <summary>
        /// New constructor — the <see cref="Layout"/> controls all geometry and
        /// <see cref="Layout.QrPixel"/> sets the size of a single QR module in
        /// output pixels.
        /// </summary>
        public QrTileDecorator(Asset asset, Layout layout)
        {
            _asset = asset ?? throw new ArgumentNullException(nameof(asset));
            _layout = layout;  // null = legacy path (use ModuleScale + * properties)
        }

        // ============================ geometry ============================

        private sealed class Geo
        {
            public int n, scale, pad, border, frame;
            public int fontPx, iconPx, gapTop, capH, gapBot, W, H;
            public int qrX, qrY, cardRad, capTop, iconX, iconY;

            public Geo(int n, int scale, bool icon, bool caption)
            {
                this.n = n;
                this.scale = scale;
                int qrPx = n * scale;
                pad = Math.Max(12, (int)Math.Round(qrPx * 0.08));
                border = Math.Max(2, (int)Math.Round(qrPx * 0.02));
                frame = qrPx + 2 * pad + 2 * border;
                fontPx = Math.Max(12, (int)Math.Round(frame * 0.06));
                iconPx = Math.Max(24, (int)Math.Round(frame * 0.14));
                gapTop = caption ? Math.Max(10, (int)Math.Round(frame * 0.05)) : 0;
                capH = caption ? Math.Max(icon ? iconPx : 1, (int)Math.Round(fontPx * 1.5)) : 0;
                gapBot = caption ? Math.Max(8, (int)Math.Round(frame * 0.04)) : 0;
                W = frame;
                H = frame + gapTop + capH + gapBot;
                qrX = border + pad;
                qrY = border + pad;
                cardRad = Math.Max(4, (int)Math.Round(W * 0.045));
                if (caption)
                {
                    capTop = frame + gapTop;
                    iconY = capTop + (capH - iconPx) / 2;
                    iconX = W - pad - (icon ? iconPx : 0);
                }
            }
        }

        private Geo GetGeo(bool[,] modules)
        {
            int n = modules.GetLength(0);
            int scale = _layout?.QrPixel ?? ModuleScale;
            bool caption = (_layout is { ShowCaption: false }) ? false : ShowCaption;
            bool icon = (_layout is { ShowIcon: false }) ? false : ShowIcon;
            return new Geo(n, scale, icon, caption);
        }

        // ============================ RGB render ============================

        /// <summary>
        /// Renders the decorated tile. Returns canvas size and a single
        /// <c>int[]</c> buffer of <c>0xRRGGBB</c> pixels (row-major, row 0 = top).
        /// </summary>
        public (int width, int height, int[] rgb) RenderTile(bool[,] modules)
        {
            int n = modules.GetLength(0);
            if (modules.GetLength(1) != n) throw new ArgumentException("module matrix must be square", nameof(modules));
            var g = GetGeo(modules);
            int W = g.W, H = g.H;
            var buf = new int[W * H];

            int bg = Pack(BG_R, BG_G, BG_B);
            int card = Pack(CARD_R, CARD_G, CARD_B);
            int borderC = Pack(BORDER_R, BORDER_G, BORDER_B);
            int qrLight = Pack(QR_LIGHT, QR_LIGHT, QR_LIGHT);
            int qrDark = Pack(QR_DARK, QR_DARK, QR_DARK);

            // 1) dark page
            for (int i = 0; i < buf.Length; i++) buf[i] = bg;

            // 2) card: blue rounded frame, white rounded body
            void fillRounded(int x0, int y0, int w, int h, int rad, int color)
            {
                for (int y = 0; y < h; y++)
                {
                    int ty = y0 + y;
                    if (ty < 0 || ty >= H) continue;
                    for (int x = 0; x < w; x++)
                    {
                        int tx = x0 + x;
                        if (tx < 0 || tx >= W) continue;
                        if (InRounded(x, y, w, h, rad)) buf[ty * W + tx] = color;
                    }
                }
            }
            int side = g.frame - 2 * g.border;
            fillRounded(g.border, g.border, side, side, g.cardRad, borderC);
            int inner = g.frame - 4 * g.border;
            fillRounded(g.border * 2, g.border * 2, inner, inner, Math.Max(0, g.cardRad - g.border), card);

            // 3) QR
            void fillRect(int x, int y, int w, int h, int color)
            {
                for (int yy = y; yy < y + h; yy++)
                    for (int xx = x; xx < x + w; xx++)
                        if ((uint)xx < (uint)W && (uint)yy < (uint)H) buf[yy * W + xx] = color;
            }
            fillRect(g.qrX, g.qrY, n * g.scale, n * g.scale, qrLight);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    if (modules[y, x])
                        fillRect(g.qrX + x * g.scale, g.qrY + y * g.scale, g.scale, g.scale, qrDark);

            // 4) caption row: baked wordmark bitmap + brand icon
            if (g.capH > 0)
            {
                DrawWordmark(g.iconX, g.capTop, g.capH, g.fontPx, buf, W, H);
                if (g.iconPx > 0)
                    DrawIcon(g.iconX, g.iconY, g.iconPx, buf, W, H);
            }

            return (W, H, buf);
        }

        /// <summary>
        /// Draws the baked "PAY by square" wordmark bitmap, scaled to
        /// <paramref name="fontPx"/> px tall (preserving aspect ratio),
        /// vertically centred in the <paramref name="capH"/>-tall caption row,
        /// and right-aligned so its right edge sits just before the icon
        /// (or the card's right pad when the icon is hidden).
        /// </summary>
        private void DrawWordmark(int iconX, int capTop, int capH, int fontPx, int[] buf, int W, int H)
        {
            if (fontPx <= 0 || capH <= 0) return;
            int capW = _asset.CapW;
            int capHr = _asset.CapH;
            if (capW == 0 || capHr == 0) return;

            // Scale width: preserve the bitmap's aspect ratio.
            int dw = (int)Math.Round(fontPx * (double)capW / capHr);
            if (dw <= 0) return;

            // Right-alignment: leave a small gap before the icon; without an
            // icon, stop at the card's right pad.
            int minLeft = Math.Max(4, (int)Math.Round(W * 0.01));
            int right = (iconX < W) ? Math.Max(minLeft + 1, iconX - 4) : W - minLeft;
            int left = right - dw;

            // Vertical centre inside the caption row.
            int top = capTop + (capH - fontPx) / 2;

            for (int dy = 0; dy < fontPx; dy++)
            {
                int ty = top + dy;
                if (ty < 0 || ty >= H) continue;
                for (int dx = 0; dx < dw; dx++)
                {
                    int tx = left + dx;
                    if (tx < 0 || tx >= W) continue;

                    // 4×4 supersampled average of the wordmark bitmap.
                    int ar = 0, ag = 0, ab = 0;
                    for (int sy = 0; sy < AA; sy++)
                        for (int sx = 0; sx < AA; sx++)
                        {
                            int ix = (int)((tx - left + (sx + 0.5) / AA) * capW / (double)dw);
                            int iy = (int)((dy + (sy + 0.5) / AA) * capHr / (double)fontPx);
                            if (ix < 0) ix = 0; else if (ix >= capW) ix = capW - 1;
                            if (iy < 0) iy = 0; else if (iy >= capHr) iy = capHr - 1;
                            var (pr, pg, pb) = _asset.CapPixel(ix, iy);
                            ar += pr; ag += pg; ab += pb;
                        }
                    int r = ar / (AA * AA), gch = ag / (AA * AA), b = ab / (AA * AA);

                    // The wordmark was flattened on white. Ink opacity is
                    // derived from how far the pixel deviates from white.
                    int mean = (r + gch + b) / 3;
                    if (mean >= 254) continue;           // white → transparent
                    int al = 255 - mean;

                    int back = buf[ty * W + tx];
                    int br = (back >> 16) & 0xFF, bgr = (back >> 8) & 0xFF, bb = back & 0xFF;
                    buf[ty * W + tx] = Pack(
                        (r * al + br * (255 - al)) / 255,
                        (gch * al + bgr * (255 - al)) / 255,
                        (b * al + bb * (255 - al)) / 255);
                }
            }
        }

        /// <summary>Blends the brand icon (64×64 RGBA in the asset) into the buffer.</summary>
        private void DrawIcon(int ix, int iy, int size, int[] buf, int W, int H)
        {
            if (size <= 0) return;
            int iconSize = _asset.IconSize;
            for (int dy = 0; dy < size; dy++)
            {
                int ty = iy + dy;
                if (ty < 0 || ty >= H) continue;
                for (int dx = 0; dx < size; dx++)
                {
                    int tx = ix + dx;
                    if (tx < 0 || tx >= W) continue;
                    int a = 0, ar = 0, ag = 0, ab = 0;
                    for (int sy = 0; sy < AA; sy++)
                        for (int sx = 0; sx < AA; sx++)
                        {
                            int fx = (int)((tx - ix + (sx + 0.5) / AA) * iconSize / (double)size);
                            int fy = (int)((ty - iy + (sy + 0.5) / AA) * iconSize / (double)size);
                            if (fx < 0) fx = 0; else if (fx >= iconSize) fx = iconSize - 1;
                            if (fy < 0) fy = 0; else if (fy >= iconSize) fy = iconSize - 1;
                            var p = _asset.IconPixel(fx, fy);
                            if (p.a == 0) continue;
                            a += p.a;
                            ar += p.r * p.a; ag += p.g * p.a; ab += p.b * p.a;
                        }
                    if (a == 0) continue;
                    int al = a / (AA * AA);          // avg source alpha, 0..255
                    int ir = ar / a, ig = ag / a, ib = ab / a;
                    int back = buf[ty * W + tx];
                    int br = (back >> 16) & 0xFF, bgr = (back >> 8) & 0xFF, bb = back & 0xFF;
                    buf[ty * W + tx] = Pack(
                        (ir * al + br * (255 - al)) / 255,
                        (ig * al + bgr * (255 - al)) / 255,
                        (ib * al + bb * (255 - al)) / 255);
                }
            }
        }

        private static bool InRounded(int x, int y, int w, int h, int rad)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return false;
            if (rad <= 0) return true;
            bool corner = (x < rad || x >= w - rad) && (y < rad || y >= h - rad);
            if (!corner) return true;
            int cx = x < rad ? x : w - 1 - x;
            int cy = y < rad ? y : h - 1 - y;
            int dx = rad - cx, dy = rad - cy;
            return dx * dx + dy * dy <= rad * rad;
        }

        // ============================ SVG output ============================
        // SVG uses <text> for the caption (tests assert the literal strings
        // "PAY" and "by square" appear) and an embedded icon.

        /// <summary>
        /// Renders the same tile as a self-contained SVG (text via &lt;text&gt;,
        /// icon embedded as a base64 data URI of assets/card.svg, or a generic
        /// card glyph when the asset file is missing).
        /// </summary>
        public string ToSvg(bool[,] modules)
        {
            int n = modules.GetLength(0);
            if (modules.GetLength(1) != n) throw new ArgumentException("module matrix must be square", nameof(modules));
            var g = GetGeo(modules);
            string fam = "DejaVu Sans, Arial, Helvetica, sans-serif";

            var sb = new StringBuilder();
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(g.W).Append('"');
            sb.Append(" height=\"").Append(g.H).Append("\" viewBox=\"0 0 ").Append(g.W).Append(' ').Append(g.H).Append('"');
            sb.Append(" shape-rendering=\"crispEdges\">");
            sb.Append("<rect width=\"").Append(g.W).Append("\" height=\"").Append(g.H).Append("\" fill=\"#212121\"/>");

            int side = g.frame - 2 * g.border;
            sb.Append("<rect x=\"").Append(g.border).Append("\" y=\"").Append(g.border).Append('"');
            sb.Append(" width=\"").Append(side).Append("\" height=\"").Append(side).Append('"');
            sb.Append(" fill=\"#ffffff\" stroke=\"#7fa8d0\" stroke-width=\"").Append(g.border * 2).Append('"');
            sb.Append(" rx=\"").Append(g.cardRad).Append("\"/>");

            int qrPx = n * g.scale;
            sb.Append("<rect x=\"").Append(g.qrX).Append("\" y=\"").Append(g.qrY).Append('"');
            sb.Append(" width=\"").Append(qrPx).Append("\" height=\"").Append(qrPx).Append("\" fill=\"#ffffff\"/>");
            var d = new StringBuilder();
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    if (modules[y, x])
                        d.Append("M").Append(g.qrX + x * g.scale).Append(' ').Append(g.qrY + y * g.scale)
                         .Append("h").Append(g.scale).Append("v").Append(g.scale).Append("h-").Append(g.scale).Append('z');
            sb.Append("<path d=\"").Append(d).Append("\" fill=\"#000000\"/>");

            if (g.capH > 0)
            {
                int midY = g.capTop + g.capH / 2;
                // Approximate the baked wordmark's width at fontPx height, then
                // right-align the two <text> segments before the icon.
                int capW = _asset.CapW; int capHr = _asset.CapH;
                int wordPx = (capHr > 0) ? (int)Math.Round(g.fontPx * (double)capW / capHr) : g.fontPx * 7;
                int right = (g.iconPx > 0) ? g.iconX - 4 : g.W - Math.Max(4, (int)Math.Round(g.W * 0.01));
                int xBy = right - wordPx;
                sb.Append(SvgText(xBy, midY, CapBy, g.fontPx, "#d8dde2", fam, "400"));
                sb.Append(SvgText(Math.Max(4, xBy - (int)Math.Round(wordPx * 0.38) - 4), midY, CapPay, g.fontPx, "#4a6d9c", fam, "700"));
                if (g.iconPx > 0)
                    sb.Append(IconSvg(g.iconX, g.iconY, g.iconPx));
            }
            sb.Append("</svg>");
            return sb.ToString();
        }

        private static string SvgText(int x, int midY, string t, int fontPx, string fill, string family, string weight)
        {
            var sb = new StringBuilder();
            sb.Append("<text x=\"").Append(x).Append("\" y=\"").Append(midY).Append('"');
            sb.Append(" font-family=\"").Append(family).Append('"');
            sb.Append(" font-size=\"").Append(fontPx).Append("\" fill=\"").Append(fill).Append('"');
            sb.Append(" font-weight=\"").Append(weight).Append("\" dominant-baseline=\"central\">");
            sb.Append(WebUtility.HtmlEncode(t));
            sb.Append("</text>");
            return sb.ToString();
        }

        private static string IconSvg(int x, int y, int iconPx)
        {
            foreach (var p in new[] { "assets/card.svg", "../assets/card.svg", "../../assets/card.svg" })
                if (File.Exists(p))
                {
                    string raw = Regex.Replace(File.ReadAllText(p), @"^\s*<\?xml[^?]*\?>", "");
                    string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
                    var sb2 = new StringBuilder();
                    sb2.Append("<image x=\"").Append(x).Append("\" y=\"").Append(y).Append('"');
                    sb2.Append(" width=\"").Append(iconPx).Append("\" height=\"").Append(iconPx).Append('"');
                    sb2.Append(" preserveAspectRatio=\"xMidYMid meet\"");
                    sb2.Append(" xlink:href=\"data:image/svg+xml;base64,").Append(b64).Append('"');
                    sb2.Append(" href=\"data:image/svg+xml;base64,").Append(b64).Append("\"/>");
                    return sb2.ToString();
                }
            int rad = Math.Max(4, (int)Math.Round(iconPx * 0.18));
            var sb = new StringBuilder();
            sb.Append("<rect x=\"").Append(x).Append("\" y=\"").Append(y).Append('"');
            sb.Append(" width=\"").Append(iconPx).Append("\" height=\"").Append(iconPx).Append('"');
            sb.Append(" fill=\"#7fa8d0\" rx=\"").Append(rad).Append("\"/>");
            for (int k = 0; k < 3; k++)
                sb.Append("<rect x=\"").Append(x + (int)(iconPx * 0.16)).Append("\" y=\"")
                  .Append(y + (int)(iconPx * (0.28 + k * 0.24))).Append('"')
                  .Append(" width=\"").Append((int)(iconPx * 0.62)).Append("\" height=\"")
                  .Append(Math.Max(2, (int)(iconPx * 0.07))).Append("\" fill=\"#ffffff\"/>");
            return sb.ToString();
        }
    }
}
