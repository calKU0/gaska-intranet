using System.Runtime.InteropServices;
using Tesseract;

namespace Gaska.Intranet.Infrastructure.BusinessCards;

/// <summary>
/// Locates the business card on a photo. Phone photos show a small card on a desk; OCR on the whole photo reads
/// the background texture as text and cuts lines at the card's edge. The card is found as the area of bright
/// or colourful pixels (white paper, coloured bands) standing out from a duller background.
/// </summary>
internal static class CardImage
{
    private const int Step = 4;

    /// <param name="rgb">32-bit colour image.</param>
    /// <param name="gray">8-bit grayscale version of <paramref name="rgb"/>.</param>
    /// <param name="shadowTolerant">Also count shaded paper as card (for photos with a shadow across the card).</param>
    /// <returns>Bounding box of the card with a small margin, or the whole image when no card stands out.</returns>
    public static Rect FindCard(Pix rgb, Pix gray, bool shadowTolerant = false)
    {
        int w = gray.Width, h = gray.Height;
        var whole = new Rect(0, 0, w, h);
        if (rgb.Depth != 32 || gray.Depth != 8)
        {
            return whole;
        }

        var color = rgb.GetData();
        var luma = gray.GetData();
        byte Gray(int x, int y) =>
            (byte)((uint)Marshal.ReadInt32(luma.Data, (y * luma.WordsPerLine + x / 4) * 4) >> (24 - 8 * (x % 4)));

        int Saturation(int x, int y)
        {
            var word = (uint)Marshal.ReadInt32(color.Data, (y * color.WordsPerLine + x) * 4);
            int r = (int)(word >> 24) & 255, g = (int)(word >> 16) & 255, b = (int)(word >> 8) & 255;
            return Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
        }

        // "Bright" is relative to the photo: 82% of the 98th brightness percentile (copes with dim light).
        var histogram = new int[256];
        for (var y = 0; y < h; y += Step)
        {
            for (var x = 0; x < w; x += Step)
            {
                histogram[Gray(x, y)]++;
            }
        }

        var samples = histogram.Sum();
        int seen = 0, p98 = 255;
        for (var v = 255; v >= 0; v--)
        {
            seen += histogram[v];
            if (seen > samples * 0.02)
            {
                p98 = v;
                break;
            }
        }

        // A shadow over part of the card (phone or hand above it) darkens the paper well below the brightest part –
        // shadow-tolerant search caps the threshold at the Otsu split between the dark background and the paper.
        var threshold = (int)(p98 * 0.82);
        if (shadowTolerant)
        {
            threshold = Math.Min(threshold, Otsu(histogram));
        }
        var columns = new int[w];
        var rows = new int[h];
        for (var y = 0; y < h; y += Step)
        {
            for (var x = 0; x < w; x += Step)
            {
                if (Gray(x, y) >= threshold || Saturation(x, y) > 70)
                {
                    columns[x]++;
                    rows[y]++;
                }
            }
        }

        // A column/row belongs to the card when a sixth of it is card-like.
        int minColumn = h / Step / 6, minRow = w / Step / 6;
        int x1 = Array.FindIndex(columns, c => c > minColumn), x2 = Array.FindLastIndex(columns, c => c > minColumn);
        int y1 = Array.FindIndex(rows, r => r > minRow), y2 = Array.FindLastIndex(rows, r => r > minRow);
        if (x1 < 0 || y1 < 0 || x2 - x1 < w / 5 || y2 - y1 < h / 8)
        {
            return whole;
        }

        int padX = (x2 - x1) / 30, padY = (y2 - y1) / 30;
        x1 = Math.Max(0, x1 - padX);
        y1 = Math.Max(0, y1 - padY);
        x2 = Math.Min(w - 1, x2 + padX);
        y2 = Math.Min(h - 1, y2 + padY);
        return new Rect(x1, y1, x2 - x1 + 1, y2 - y1 + 1);
    }

    /// <summary>
    /// Solid coloured panels on the card – a vertical band (name on a blue stripe) or a horizontal one (address on a
    /// blue footer). Text on them is often merged with logos when the whole card is read, but reads well on its own.
    /// </summary>
    public static IReadOnlyList<Rect> FindColorPanels(Pix rgb, Rect card)
    {
        if (rgb.Depth != 32)
        {
            return [];
        }

        var color = rgb.GetData();
        bool Colorful(int x, int y)
        {
            var word = (uint)Marshal.ReadInt32(color.Data, (y * color.WordsPerLine + x) * 4);
            int r = (int)(word >> 24) & 255, g = (int)(word >> 16) & 255, b = (int)(word >> 8) & 255;
            return Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 70;
        }

        var columns = new double[card.Width];
        var rows = new double[card.Height];
        int sampledRows = 0, sampledColumns = 0;
        for (var y = 0; y < card.Height; y += Step)
        {
            sampledRows++;
        }

        for (var x = 0; x < card.Width; x += Step)
        {
            sampledColumns++;
        }

        for (var y = 0; y < card.Height; y += Step)
        {
            for (var x = 0; x < card.Width; x += Step)
            {
                if (Colorful(card.X1 + x, card.Y1 + y))
                {
                    columns[x]++;
                    rows[y]++;
                }
            }
        }

        var panels = new List<Rect>();

        // Columns mostly coloured → a vertical band; rows mostly coloured → a horizontal band.
        foreach (var (start, end) in Runs(columns, sampledRows, Step, minLength: card.Width / 6))
        {
            panels.Add(new Rect(card.X1 + start, card.Y1, end - start + 1, card.Height));
        }

        foreach (var (start, end) in Runs(rows, sampledColumns, Step, minLength: card.Height / 6))
        {
            panels.Add(new Rect(card.X1, card.Y1 + start, card.Width, end - start + 1));
        }

        return panels;
    }

    /// <summary>Otsu's threshold: the brightness that best splits the histogram into a dark and a bright group.</summary>
    internal static int Otsu(int[] histogram)
    {
        long total = 0, sum = 0;
        for (var v = 0; v < histogram.Length; v++)
        {
            total += histogram[v];
            sum += (long)v * histogram[v];
        }

        long darkCount = 0, darkSum = 0;
        double best = -1;
        var threshold = 128;
        for (var v = 0; v < histogram.Length - 1; v++)
        {
            darkCount += histogram[v];
            darkSum += (long)v * histogram[v];
            var brightCount = total - darkCount;
            if (darkCount == 0 || brightCount == 0)
            {
                continue;
            }

            var darkMean = (double)darkSum / darkCount;
            var brightMean = (double)(sum - darkSum) / brightCount;
            var between = (double)darkCount * brightCount * (darkMean - brightMean) * (darkMean - brightMean);
            if (between > best)
            {
                best = between;
                threshold = v + 1;
            }
        }

        return threshold;
    }

    /// <summary>
    /// Evens out uneven lighting (a shadow of the phone or hand across the card): every pixel is divided by the local
    /// paper brightness, estimated as the brightest values in 32×32 blocks, smoothed and spread back over the image.
    /// Tesseract's single global threshold otherwise loses the text in the shaded part. Dark background (the desk) is
    /// left as it is.
    /// </summary>
    public static Pix EvenOutLighting(Pix gray)
    {
        const int block = 32;
        int w = gray.Width, h = gray.Height;
        var pixels = Read(gray);

        int bw = (w + block - 1) / block, bh = (h + block - 1) / block;
        var paper = new double[bw * bh];
        var values = new List<byte>(block * block);
        for (var by = 0; by < bh; by++)
        {
            for (var bx = 0; bx < bw; bx++)
            {
                values.Clear();
                for (var y = by * block; y < Math.Min(h, (by + 1) * block); y += 2)
                {
                    for (var x = bx * block; x < Math.Min(w, (bx + 1) * block); x += 2)
                    {
                        values.Add(pixels[y * w + x]);
                    }
                }

                values.Sort();
                paper[by * bw + bx] = values[(int)(values.Count * 0.95)]; // brightest 5% = the paper, not the ink
            }
        }

        // Blocks fully covered by ink or a logo: take the brightest neighbour; then smooth.
        paper = Smooth(Dilate(paper, bw, bh), bw, bh);

        double brightest = paper.Max();
        var result = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            var fy = Math.Clamp((y + 0.5) / block - 0.5, 0, bh - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, bh - 1);
            var ty = fy - y0;
            for (var x = 0; x < w; x++)
            {
                var fx = Math.Clamp((x + 0.5) / block - 0.5, 0, bw - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, bw - 1);
                var tx = fx - x0;
                var local = (paper[y0 * bw + x0] * (1 - tx) + paper[y0 * bw + x1] * tx) * (1 - ty) +
                            (paper[y1 * bw + x0] * (1 - tx) + paper[y1 * bw + x1] * tx) * ty;
                var value = pixels[y * w + x];

                // Only paper (lit or shaded) is evened out – the dark desk around the card is not blown up into noise.
                result[y * w + x] = local < brightest * 0.35
                    ? value
                    : (byte)Math.Min(255, value * 255.0 / local);
            }
        }

        return Write(result, w, h);
    }

    private static double[] Dilate(double[] grid, int w, int h)
    {
        var result = new double[grid.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var max = 0d;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < w && ny < h)
                        {
                            max = Math.Max(max, grid[ny * w + nx]);
                        }
                    }
                }

                result[y * w + x] = max;
            }
        }

        return result;
    }

    private static double[] Smooth(double[] grid, int w, int h)
    {
        var result = new double[grid.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                double sum = 0;
                var count = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < w && ny < h)
                        {
                            sum += grid[ny * w + nx];
                            count++;
                        }
                    }
                }

                result[y * w + x] = sum / count;
            }
        }

        return result;
    }

    /// <summary>8-bit image → one byte per pixel, row by row.</summary>
    internal static byte[] Read(Pix gray)
    {
        int w = gray.Width, h = gray.Height;
        var data = gray.GetData();
        var row = new byte[data.WordsPerLine * 4];
        var pixels = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            Marshal.Copy(data.Data + y * row.Length, row, 0, row.Length);
            for (var x = 0; x < w; x++)
            {
                pixels[y * w + x] = row[x ^ 3];
            }
        }

        return pixels;
    }

    private static Pix Write(byte[] pixels, int w, int h)
    {
        var pix = Pix.Create(w, h, 8);
        var data = pix.GetData();
        var row = new byte[data.WordsPerLine * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                row[x ^ 3] = pixels[y * w + x];
            }

            Marshal.Copy(row, 0, data.Data + y * row.Length, row.Length);
        }

        return pix;
    }

    /// <summary>
    /// Copy of a part of an 8-bit image. Tesseract's own "region" option proved unreliable for regions starting at
    /// the left edge (read from the wrong place).
    /// </summary>
    public static Pix Crop(Pix source, Rect region)
    {
        if (source.Depth != 8)
        {
            throw new ArgumentException("8-bit image expected.", nameof(source));
        }

        int x0 = Math.Clamp(region.X1, 0, source.Width - 1), y0 = Math.Clamp(region.Y1, 0, source.Height - 1);
        int width = Math.Min(region.Width, source.Width - x0), height = Math.Min(region.Height, source.Height - y0);
        var target = Pix.Create(width, height, 8);
        var from = source.GetData();
        var to = target.GetData();
        var sourceRow = new byte[from.WordsPerLine * 4];
        var targetRow = new byte[to.WordsPerLine * 4];
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(from.Data + (y0 + y) * sourceRow.Length, sourceRow, 0, sourceRow.Length);

            // Leptonica keeps 4 pixels per 32-bit word, first pixel in the most significant byte – on a little-endian
            // machine pixel x lives at byte x ^ 3.
            for (var x = 0; x < width; x++)
            {
                targetRow[x ^ 3] = sourceRow[(x0 + x) ^ 3];
            }

            Marshal.Copy(targetRow, 0, to.Data + y * targetRow.Length, targetRow.Length);
        }

        return target;
    }

    /// <summary>Continuous ranges of sampled positions where at least 55% of the pixels are colourful.</summary>
    private static IEnumerable<(int Start, int End)> Runs(double[] counts, int samples, int step, int minLength)
    {
        int? start = null;
        for (var i = 0; i <= counts.Length; i += step)
        {
            var colored = i < counts.Length && counts[i] / samples >= 0.55;
            if (colored && start is null)
            {
                start = i;
            }
            else if (!colored && start is not null)
            {
                var end = Math.Min(i - 1, counts.Length - 1);
                if (end - start.Value >= minLength)
                {
                    yield return (start.Value, end);
                }

                start = null;
            }
        }
    }
}
