using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using Encoder = System.Drawing.Imaging.Encoder;

namespace ScanStudio;

public enum OutputFormat { Pdf, Tiff, Jpeg, Png, Bmp }

public static class DocumentWriter
{
    public static string Extension(OutputFormat f) => f switch
    {
        OutputFormat.Pdf => ".pdf",
        OutputFormat.Tiff => ".tif",
        OutputFormat.Jpeg => ".jpg",
        OutputFormat.Png => ".png",
        _ => ".bmp"
    };

    public static bool SupportsMultiPage(OutputFormat f) => f is OutputFormat.Pdf or OutputFormat.Tiff;

    /// <summary>Loads an image without keeping the file locked.</summary>
    public static Image LoadImage(string path)
    {
        var ms = new MemoryStream(File.ReadAllBytes(path));
        return Image.FromStream(ms);
    }

    /// <summary>
    /// Crops the area inside <paramref name="corners"/> (top-left, top-right, bottom-right, bottom-left, in image pixels)
    /// from the page at <paramref name="sourcePath"/> and writes it as a BMP to <paramref name="destPath"/> (may be the same file).
    /// An axis-aligned rectangle is copied exactly; any other 4-corner shape is straightened (perspective-corrected)
    /// into a flat rectangle. Keeps the pixel format (1-bit B&amp;W, grayscale, color) and DPI.
    /// Returns the size of the result.
    /// </summary>
    public static Size CropToFile(string sourcePath, PointF[] corners, string destPath)
    {
        using var src = (Bitmap)LoadImage(sourcePath);
        using var result = IsAxisRectangle(corners) ? CropRect(src, corners) : Straighten(src, corners);
        result.SetResolution(src.HorizontalResolution, src.VerticalResolution);
        result.Save(destPath, ImageFormat.Bmp);
        return result.Size;
    }

    public static bool IsAxisRectangle(PointF[] q) =>
        Math.Abs(q[0].Y - q[1].Y) < 0.5f && Math.Abs(q[3].Y - q[2].Y) < 0.5f &&
        Math.Abs(q[0].X - q[3].X) < 0.5f && Math.Abs(q[1].X - q[2].X) < 0.5f;

    private static Bitmap CropRect(Bitmap src, PointF[] q)
    {
        var area = Rectangle.FromLTRB((int)Math.Round(q[0].X), (int)Math.Round(q[0].Y), (int)Math.Round(q[2].X), (int)Math.Round(q[2].Y));
        area.Intersect(new Rectangle(Point.Empty, src.Size));
        if (area.Width < 1 || area.Height < 1) throw new ArgumentException("The crop area is empty.");
        return src.Clone(area, src.PixelFormat);
    }

    private static double Dist(PointF a, PointF b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>
    /// Perspective-corrects the 4-corner area into a rectangle. The output is as wide as the longer of the top/bottom
    /// sides and as tall as the longer of the left/right sides, so no detail is lost.
    /// </summary>
    private static Bitmap Straighten(Bitmap src, PointF[] q)
    {
        int ow = Math.Max(1, (int)Math.Round(Math.Max(Dist(q[0], q[1]), Dist(q[3], q[2]))));
        int oh = Math.Max(1, (int)Math.Round(Math.Max(Dist(q[0], q[3]), Dist(q[1], q[2]))));

        // Projective map from the unit square (u,v) to the source shape (Heckbert's square-to-quad):
        // (0,0)→TL, (1,0)→TR, (1,1)→BR, (0,1)→BL.
        double x0 = q[0].X, y0 = q[0].Y, x1 = q[1].X, y1 = q[1].Y, x2 = q[2].X, y2 = q[2].Y, x3 = q[3].X, y3 = q[3].Y;
        double dx1 = x1 - x2, dx2 = x3 - x2, dx3 = x0 - x1 + x2 - x3;
        double dy1 = y1 - y2, dy2 = y3 - y2, dy3 = y0 - y1 + y2 - y3;
        double a, b, c = x0, d, e, f = y0, g, h;
        if (Math.Abs(dx3) < 1e-9 && Math.Abs(dy3) < 1e-9)
        {
            // Parallelogram: plain affine map.
            a = x1 - x0; b = x2 - x1; d = y1 - y0; e = y2 - y1; g = 0; h = 0;
        }
        else
        {
            double den = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(den) < 1e-9) throw new ArgumentException("The 4 corners don't form a valid shape.");
            g = (dx3 * dy2 - dx2 * dy3) / den;
            h = (dx1 * dy3 - dx3 * dy1) / den;
            a = x1 - x0 + g * x1; b = x3 - x0 + h * x3;
            d = y1 - y0 + g * y1; e = y3 - y0 + h * y3;
        }

        // Read the source as 24-bit BGR (GDI+ converts 1-bit/8-bit on the fly).
        int sw = src.Width, sh = src.Height;
        var sData = src.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        int sStride = sData.Stride;
        byte[] sBuf = new byte[sStride * sh];
        try { System.Runtime.InteropServices.Marshal.Copy(sData.Scan0, sBuf, 0, sBuf.Length); }
        finally { src.UnlockBits(sData); }

        int oStride = (ow * 3 + 3) & ~3;
        byte[] oBuf = new byte[oStride * oh];
        Parallel.For(0, oh, oy =>
        {
            double v = (oy + 0.5) / oh;
            int row = oy * oStride;
            for (int ox = 0; ox < ow; ox++)
            {
                double u = (ox + 0.5) / ow;
                double w = g * u + h * v + 1;
                // Bilinear sample around the mapped point (pixel centers at +0.5).
                double sx = (a * u + b * v + c) / w - 0.5, sy = (d * u + e * v + f) / w - 0.5;
                int ix = (int)Math.Floor(sx), iy = (int)Math.Floor(sy);
                double fx = sx - ix, fy = sy - iy;
                int ix0 = Math.Clamp(ix, 0, sw - 1), ix1 = Math.Clamp(ix + 1, 0, sw - 1);
                int iy0 = Math.Clamp(iy, 0, sh - 1), iy1 = Math.Clamp(iy + 1, 0, sh - 1);
                int p00 = iy0 * sStride + ix0 * 3, p10 = iy0 * sStride + ix1 * 3;
                int p01 = iy1 * sStride + ix0 * 3, p11 = iy1 * sStride + ix1 * 3;
                for (int ch = 0; ch < 3; ch++)
                {
                    double top = sBuf[p00 + ch] + (sBuf[p10 + ch] - sBuf[p00 + ch]) * fx;
                    double bot = sBuf[p01 + ch] + (sBuf[p11 + ch] - sBuf[p01 + ch]) * fx;
                    oBuf[row + ox * 3 + ch] = (byte)Math.Clamp((int)Math.Round(top + (bot - top) * fy), 0, 255);
                }
            }
        });

        return FromBgr(oBuf, oStride, ow, oh, src);
    }

    /// <summary>Builds the output bitmap in the same kind of pixel format as <paramref name="like"/>.</summary>
    private static Bitmap FromBgr(byte[] bgr, int stride, int w, int h, Bitmap like)
    {
        static int Lum(byte[] p, int i) => (p[i + 2] * 299 + p[i + 1] * 587 + p[i] * 114) / 1000;

        if (like.PixelFormat == PixelFormat.Format1bppIndexed)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format1bppIndexed); // default palette: 0 = black, 1 = white
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format1bppIndexed);
            byte[] bits = new byte[data.Stride * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (Lum(bgr, y * stride + x * 3) >= 128)
                        bits[y * data.Stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            System.Runtime.InteropServices.Marshal.Copy(bits, 0, data.Scan0, bits.Length);
            bmp.UnlockBits(data);
            return bmp;
        }

        if (like.PixelFormat == PixelFormat.Format8bppIndexed && like.Palette.Entries.All(cl => cl.R == cl.G && cl.G == cl.B))
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format8bppIndexed);
            var pal = bmp.Palette;
            for (int i = 0; i < 256; i++) pal.Entries[i] = Color.FromArgb(i, i, i);
            bmp.Palette = pal;
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
            byte[] gray = new byte[data.Stride * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    gray[y * data.Stride + x] = (byte)Lum(bgr, y * stride + x * 3);
            System.Runtime.InteropServices.Marshal.Copy(gray, 0, data.Scan0, gray.Length);
            bmp.UnlockBits(data);
            return bmp;
        }

        var color = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var cd = color.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        for (int y = 0; y < h; y++)
            System.Runtime.InteropServices.Marshal.Copy(bgr, y * stride, cd.Scan0 + y * cd.Stride, w * 3);
        color.UnlockBits(cd);
        return color;
    }

    /// <summary>
    /// Writes the scanned pages. PDF/TIFF produce one file; JPEG/PNG/BMP produce one file per page
    /// (numbered when there is more than one). Returns the files written.
    /// </summary>
    /// <param name="pageNames">Optional per-page names (null entry = not renamed). Renamed pages are saved under their
    /// name when each page is its own file, and become bookmarks in a PDF.</param>
    public static List<string> Save(IReadOnlyList<string> pages, OutputFormat format, string folder, string baseName, int dpi,
        IReadOnlyList<string> pageNames = null)
    {
        Directory.CreateDirectory(folder);
        string ext = Extension(format);
        var written = new List<string>();
        string PageName(int i) => pageNames != null && i < pageNames.Count && !string.IsNullOrWhiteSpace(pageNames[i]) ? pageNames[i].Trim() : null;

        if (SupportsMultiPage(format))
        {
            string path = UniquePath(Path.Combine(folder, baseName + ext));
            if (format == OutputFormat.Pdf) WritePdf(pages, path, dpi, Enumerable.Range(0, pages.Count).Select(PageName).ToList());
            else WriteTiff(pages, path, dpi);
            written.Add(path);
            return written;
        }

        for (int i = 0; i < pages.Count; i++)
        {
            string name = PageName(i) is string custom && IsValidFileName(custom) ? custom
                : pages.Count == 1 ? baseName : $"{baseName}_{i + 1:D3}";
            string path = UniquePath(Path.Combine(folder, name + ext));
            using var img = LoadImage(pages[i]);
            switch (format)
            {
                case OutputFormat.Jpeg:
                    using (var rgb = ToRgb(img, dpi))
                        rgb.Save(path, JpegCodec, JpegParams(90));
                    break;
                case OutputFormat.Png:
                    SetDpi(img, dpi);
                    img.Save(path, ImageFormat.Png);
                    break;
                default:
                    SetDpi(img, dpi);
                    img.Save(path, ImageFormat.Bmp);
                    break;
            }
            written.Add(path);
        }
        return written;
    }

    private static void WriteTiff(IReadOnlyList<string> pages, string path, int dpi)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Tiff.Guid);
        var images = pages.Select(LoadImage).ToList();
        try
        {
            foreach (var im in images) SetDpi(im, dpi);
            Image first = images[0];
            first.Save(path, codec, TiffParams(first, EncoderValue.MultiFrame));
            for (int i = 1; i < images.Count; i++)
                first.SaveAdd(images[i], TiffParams(images[i], EncoderValue.FrameDimensionPage));
            using var flush = new EncoderParameters(1);
            flush.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.Flush);
            first.SaveAdd(flush);
        }
        finally
        {
            foreach (var im in images) im.Dispose();
        }
    }

    private static EncoderParameters TiffParams(Image img, EncoderValue saveFlag)
    {
        var compression = img.PixelFormat == PixelFormat.Format1bppIndexed
            ? EncoderValue.CompressionCCITT4
            : EncoderValue.CompressionLZW;
        var p = new EncoderParameters(2);
        p.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)saveFlag);
        p.Param[1] = new EncoderParameter(Encoder.Compression, (long)compression);
        return p;
    }

    public static bool IsValidFileName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name.Trim().Trim('.').Length > 0;

    /// <summary>A PDF text string in UTF-16BE hex form, so any language (e.g. Khmer) works in bookmark titles.</summary>
    private static string PdfText(string s) =>
        "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(s)) + ">";

    /// <summary>
    /// Minimal PDF 1.4 writer: one JPEG-compressed image per page, sized to the scan DPI.
    /// Pages with a name in <paramref name="names"/> get a bookmark (outline entry) pointing to them.
    /// </summary>
    private static void WritePdf(IReadOnlyList<string> pages, string path, int dpi, IReadOnlyList<string> names)
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        var offsets = new List<long>();
        var inv = CultureInfo.InvariantCulture;

        void W(string s) { var b = Encoding.ASCII.GetBytes(s); fs.Write(b, 0, b.Length); }
        void BeginObj(int num) { while (offsets.Count < num) offsets.Add(0); offsets[num - 1] = fs.Position; W($"{num} 0 obj\n"); }

        W("%PDF-1.4\n");
        fs.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }); // binary marker comment
        int n = pages.Count;
        // Object layout: 1 catalog, 2 pages, then per page: page, image, content.
        int PageObj(int i) => 3 + i * 3;

        // Bookmarks follow the page objects: one /Outlines root, then one item per renamed page.
        var marks = Enumerable.Range(0, n).Where(i => names != null && names[i] != null).ToList();
        int outlinesObj = 3 + n * 3;
        int MarkObj(int k) => outlinesObj + 1 + k;

        BeginObj(1);
        W(marks.Count == 0
            ? "<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"
            : $"<< /Type /Catalog /Pages 2 0 R /Outlines {outlinesObj} 0 R /PageMode /UseOutlines >>\nendobj\n");
        BeginObj(2);
        W("<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(0, n).Select(i => $"{PageObj(i)} 0 R")) + $"] /Count {n} >>\nendobj\n");

        for (int i = 0; i < n; i++)
        {
            byte[] jpeg;
            int w, h;
            float dx, dy;
            using (var img = LoadImage(pages[i]))
            {
                w = img.Width; h = img.Height;
                dx = img.HorizontalResolution > 1 ? img.HorizontalResolution : dpi;
                dy = img.VerticalResolution > 1 ? img.VerticalResolution : dpi;
                // Trust the requested DPI if the file carries the GDI+ default of 96.
                if (Math.Abs(dx - 96) < 0.5f) dx = dpi;
                if (Math.Abs(dy - 96) < 0.5f) dy = dpi;
                using var rgb = ToRgb(img, dpi);
                using var ms = new MemoryStream();
                rgb.Save(ms, JpegCodec, JpegParams(85));
                jpeg = ms.ToArray();
            }

            string pw = (w * 72.0 / dx).ToString("0.###", inv);
            string ph = (h * 72.0 / dy).ToString("0.###", inv);
            int pageObj = PageObj(i), imgObj = pageObj + 1, contentObj = pageObj + 2;

            BeginObj(pageObj);
            W($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pw} {ph}] /Resources << /XObject << /Im0 {imgObj} 0 R >> >> /Contents {contentObj} 0 R >>\nendobj\n");

            BeginObj(imgObj);
            W($"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
            fs.Write(jpeg, 0, jpeg.Length);
            W("\nendstream\nendobj\n");

            string content = $"q {pw} 0 0 {ph} 0 0 cm /Im0 Do Q";
            BeginObj(contentObj);
            W($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        }

        if (marks.Count > 0)
        {
            BeginObj(outlinesObj);
            W($"<< /Type /Outlines /First {MarkObj(0)} 0 R /Last {MarkObj(marks.Count - 1)} 0 R /Count {marks.Count} >>\nendobj\n");
            for (int k = 0; k < marks.Count; k++)
            {
                string links = (k > 0 ? $" /Prev {MarkObj(k - 1)} 0 R" : "") + (k < marks.Count - 1 ? $" /Next {MarkObj(k + 1)} 0 R" : "");
                BeginObj(MarkObj(k));
                W($"<< /Title {PdfText(names[marks[k]])} /Parent {outlinesObj} 0 R{links} /Dest [{PageObj(marks[k])} 0 R /Fit] >>\nendobj\n");
            }
        }

        long xref = fs.Position;
        W($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets) W($"{off:D10} 00000 n \n");
        W($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private static EncoderParameters JpegParams(long quality)
    {
        var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        return p;
    }

    /// <summary>
    /// The DPI the scanner actually used (stored in the scanned file), falling back to the requested DPI
    /// when the file has none or carries GDI+'s 96 DPI default. Drivers may round the request (e.g. 150 → 200).
    /// </summary>
    private static int EffectiveDpi(Image img, int fallback)
    {
        float r = img.HorizontalResolution;
        return r > 1 && Math.Abs(r - 96) >= 0.5f ? (int)Math.Round(r) : fallback;
    }

    private static Bitmap ToRgb(Image src, int dpi)
    {
        var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        int d = EffectiveDpi(src, dpi);
        bmp.SetResolution(d, d);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
        return bmp;
    }

    private static void SetDpi(Image img, int dpi)
    {
        if (img is Bitmap b) { int d = EffectiveDpi(img, dpi); b.SetResolution(d, d); }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
