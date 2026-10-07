using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Jacquard
{
    /// <summary>
    /// A picture loaded once and kept in memory, so changing the width or the threshold doesn't re-read the file.
    /// </summary>
    internal sealed class SourceImage : IDisposable
    {
        public const int MaxWidth = 640;
        public const int MaxSquares = MaxWidth * MaxWidth;
        // The suggested width keeps the pattern near this many squares: easy to read, quick to build.
        private const int SuggestedSquares = 20000;

        private readonly Bitmap bitmap;
        private readonly object gate = new object();

        public string FileName { get; }
        public int Width { get; }
        public int Height { get; }

        private SourceImage(string fileName, Bitmap bitmap)
        {
            FileName = fileName;
            this.bitmap = bitmap;
            Width = bitmap.Width;
            Height = bitmap.Height;
        }

        public static SourceImage Load(string fileName)
        {
            try
            {
                // Copy into memory so the file isn't kept locked.
                using (var stream = File.OpenRead(fileName))
                using (var image = Image.FromStream(stream))
                {
                    return new SourceImage(fileName, new Bitmap(image));
                }
            }
            catch (ArgumentException)
            {
                throw new InvalidDataException("This file isn't an image Windows can read. Use a PNG, JPG, BMP or GIF.");
            }
            catch (OutOfMemoryException)
            {
                throw new InvalidDataException("This image is too large or damaged to open.");
            }
        }

        /// <summary>Rows of the pattern for a given width, keeping the image's proportions.</summary>
        public int HeightFor(int width) => Math.Max(1, (int)Math.Ceiling((double)Height * width / Width));

        /// <summary>Widest pattern allowed: no wider than the image, 640 squares or MaxSquares in total.</summary>
        public int MaxAllowedWidth
        {
            get
            {
                int max = Math.Min(MaxWidth, Width);
                while (max > 1 && (long)max * HeightFor(max) > MaxSquares) max--;
                return max;
            }
        }

        public int SuggestedWidth
        {
            get
            {
                int suggested = (int)Math.Round(Math.Sqrt(SuggestedSquares * (double)Width / Height));
                return Math.Max(Math.Min(10, MaxAllowedWidth), Math.Min(suggested, MaxAllowedWidth));
            }
        }

        /// <summary>
        /// Resizes the image to <paramref name="width"/> and returns the brightness (0-255) of each pixel, row by row.
        /// </summary>
        public byte[] Brightness(int width, IProgress<int> progress, CancellationToken token)
        {
            int height = HeightFor(width);
            var pixels = new int[width * height];

            lock (gate)
            {
                using (var resized = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    using (var graphics = Graphics.FromImage(resized))
                    using (var wrapMode = new ImageAttributes())
                    {
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.CompositingQuality = CompositingQuality.HighQuality;
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.SmoothingMode = SmoothingMode.HighQuality;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        wrapMode.SetWrapMode(WrapMode.TileFlipXY);
                        graphics.DrawImage(bitmap, new Rectangle(0, 0, width, height), 0, 0, Width, Height, GraphicsUnit.Pixel, wrapMode);
                    }

                    var data = resized.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        for (int y = 0; y < height; y++)
                        {
                            Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * width, width);
                        }
                    }
                    finally
                    {
                        resized.UnlockBits(data);
                    }
                }
            }

            var brightness = new byte[pixels.Length];
            for (int y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    int argb = pixels[y * width + x];
                    double a = ((argb >> 24) & 0xFF) / 255.0;
                    // Transparent parts count as white paper, not black.
                    double r = ((argb >> 16) & 0xFF) * a + 255 * (1 - a);
                    double g = ((argb >> 8) & 0xFF) * a + 255 * (1 - a);
                    double b = (argb & 0xFF) * a + 255 * (1 - a);
                    // Perceived brightness, the same formula as the 2020 version.
                    brightness[y * width + x] = (byte)Math.Min(255, Math.Sqrt(r * r * .241 + g * g * .691 + b * b * .068));
                }
                progress?.Report((y + 1) * 100 / height);
            }
            return brightness;
        }

        public void Dispose()
        {
            lock (gate)
            {
                bitmap.Dispose();
            }
        }
    }

    /// <summary>The jacquard grid: each square is filled (darker than the threshold) or empty.</summary>
    internal sealed class Pattern
    {
        public static readonly Color FilledColor = Color.FromRgb(0xE9, 0x1E, 0x63);
        public static readonly Color EmptyColor = Colors.White;

        public int Width { get; }
        public int Height { get; }
        public int Filled { get; }
        private readonly bool[] cells;

        private Pattern(int width, int height, bool[] cells, int filled)
        {
            Width = width;
            Height = height;
            this.cells = cells;
            Filled = filled;
        }

        /// <param name="invert">Fill the light parts instead of the dark ones.</param>
        public static Pattern Build(byte[] brightness, int width, int height, int threshold, bool invert)
        {
            var cells = new bool[width * height];
            int filled = 0;
            for (int i = 0; i < cells.Length; i++)
            {
                if ((brightness[i] < threshold) != invert)
                {
                    cells[i] = true;
                    filled++;
                }
            }
            return new Pattern(width, height, cells, filled);
        }

        /// <summary>One pixel per square; the window scales it up with sharp edges.</summary>
        public BitmapSource ToBitmap()
        {
            int filled = ToBgra(FilledColor), empty = ToBgra(EmptyColor);
            var pixels = new int[cells.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                pixels[i] = cells[i] ? filled : empty;
            }
            var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, pixels, Width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>"1" for a filled square and "·" for an empty one, a line per row, for reading on screen.</summary>
        public string ToText() => Join('·', "");

        /// <summary>"1" or a space, with a tab after every square so it pastes into Excel cell by cell (as in 2020).</summary>
        public string ToClipboardText() => Join(' ', "\t");

        private string Join(char empty, string separator)
        {
            var text = new StringBuilder(Height * (Width * (1 + separator.Length) + 2));
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    text.Append(cells[y * Width + x] ? '1' : empty).Append(separator);
                }
                text.AppendLine();
            }
            return text.ToString();
        }

        private static int ToBgra(Color color) => (color.A << 24) | (color.R << 16) | (color.G << 8) | color.B;
    }
}
