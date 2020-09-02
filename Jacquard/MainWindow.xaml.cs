using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Jacquard
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }
        StringBuilder text = new StringBuilder();

        string currentFileName = "";

        private void OpenImage_Click(object sender, RoutedEventArgs e)
        {
            text = new StringBuilder();
            try
            {
                var ofd = new System.Windows.Forms.OpenFileDialog();
                if (ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    string fileName = ofd.FileName;
                    currentFileName = fileName;
                    DoTheMagic(fileName);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

        }

        private void DoTheMagic(string fileName)
        {
            text = new StringBuilder();
            try
            {
                var SourceImage = new Bitmap(fileName);
                SourceImage = ResizeImage(SourceImage);
                var result = new bool[SourceImage.Height, SourceImage.Width];
                StringBuilder resultText = new StringBuilder();
                ImageCanvas.Children.Clear();
                for (int i = 0; i < SourceImage.Height; i++)
                {
                    var currentStack = MakeStack();
                    ImageCanvas.Children.Add(currentStack);
                    for (int j = 0; j < SourceImage.Width; j++)
                    {
                        var pixel = SourceImage.GetPixel(j, i);
                        var r = Math.Sqrt(pixel.R * pixel.R * .241 + pixel.G * pixel.G * .691 + pixel.B * pixel.B * .068) < 130;
                        result[i, j] = r;
                        resultText.Append(r ? "1" : " ");
                        text.Append((r ? "1" : " ") + "\t");
                        currentStack.Children.Add(MakeRect(r));
                    }
                    resultText.AppendLine();
                    text.AppendLine();
                }
                Output.Text = resultText.ToString();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

        }

        public Bitmap ResizeImage(Bitmap image)
        {
            string value = scall.Text;
            int.TryParse(value, out int width);
            if (width == 0 || width > 640)
            {
                return image;
            }
            else
            {
                int height = (int)Math.Ceiling((image.Height + 0.0) * width / image.Width);
                var destRect = new System.Drawing.Rectangle(0, 0, width, height);
                var destImage = new Bitmap(width, height);

                destImage.SetResolution(image.HorizontalResolution, image.VerticalResolution);

                using (var graphics = Graphics.FromImage(destImage))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.SmoothingMode = SmoothingMode.HighQuality;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                    using (var wrapMode = new System.Drawing.Imaging.ImageAttributes())
                    {
                        wrapMode.SetWrapMode(WrapMode.TileFlipXY);
                        graphics.DrawImage(image, destRect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, wrapMode);
                    }
                }

                return destImage;
            }
            
        }

        private System.Windows.Shapes.Rectangle MakeRect(bool black)
        {
            var rec = new System.Windows.Shapes.Rectangle();
            rec.Fill = black? System.Windows.Media.Brushes.Pink : System.Windows.Media.Brushes.White;
            rec.Margin = new Thickness(0, 0, 0, 0);
            //rec.Stroke = System.Windows.Media.Brushes.Black;
            rec.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            rec.Width = 5;
            rec.VerticalAlignment = VerticalAlignment.Stretch;
            return rec;
        }

        private StackPanel MakeStack()
        {
            StackPanel st = new StackPanel();
            st.Height = 5;
            st.VerticalAlignment = VerticalAlignment.Top;
            st.Orientation = System.Windows.Controls.Orientation.Horizontal;
            return st;
        }

        private void CopyText_Click(object sender, RoutedEventArgs e)
        {
            if (text.Length == 0)
            {
               MessageBox.Show("No text to copy.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                Clipboard.SetText(text.ToString());
            }
        }

        private void IamgeWidth_TextChanged(object sender, TextChangedEventArgs e)
        {
            ((TextBox)sender).Foreground = System.Windows.Media.Brushes.Black;
            ((TextBox)sender).FontStyle = FontStyles.Normal;
        }

        private void IamgeWidth_LostFocus(object sender, RoutedEventArgs e)
        {
            string value = ((TextBox)sender).Text;
            int.TryParse(value, out int width);
            if (width == 0 || width > 640)
            {
                ((TextBox)sender).Text = "auto";
                ((TextBox)sender).Foreground = System.Windows.Media.Brushes.Gray;
                ((TextBox)sender).FontStyle = FontStyles.Italic;
            }
            else
            {
                ((TextBox)sender).Text = width.ToString();
                ((TextBox)sender).Foreground = System.Windows.Media.Brushes.Black;
                ((TextBox)sender).FontStyle = FontStyles.Normal;
            }
            if (!string.IsNullOrEmpty(currentFileName))
                DoTheMagic(currentFileName);
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            currentFileName = "";
            text.Clear();
            Output.Text = "";
            ImageCanvas.Children.Clear();
            scall.Text = "auto";
            IamgeWidth_LostFocus(scall, new RoutedEventArgs());
        }
    }
}
