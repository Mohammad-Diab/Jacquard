using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Jacquard
{
    public partial class MainWindow : Window
    {
        // A square is this many units wide at 100% zoom.
        private const double CellSize = 8;
        private const double MinZoom = 0.05, MaxZoom = 8, ZoomStep = 1.25;
        private const string Hint = "Wheel or pinch: zoom · Drag or two-finger scroll: move · Double-click or double-tap: fit";

        private SourceImage source;
        private Pattern pattern;
        // Brightness of the image resized to brightnessWidth; a threshold change reuses it.
        private byte[] brightness;
        private int brightnessWidth;
        private CancellationTokenSource work;
        private readonly DispatcherTimer widthTimer;
        private bool suppressWidthChange;
        private bool fitAfterBuild;
        private double zoom = 1;
        private Point? dragStart;
        private Point dragOffset;

        public MainWindow()
        {
            InitializeComponent();
            widthTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            widthTimer.Tick += (s, e) =>
            {
                widthTimer.Stop();
                _ = BuildAsync();
            };
            GridOverlay.Fill = MakeGridBrush();
            StatusText.Text = Hint;

            // "Jacquard.exe picture.png" (or Open with) opens the picture right away.
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1)
            {
                Loaded += (s, e) => _ = OpenAsync(args[1]);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            work?.Cancel();
            source?.Dispose();
            base.OnClosed(e);
        }

        #region Open

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Open an image",
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
            };
            if (dialog.ShowDialog(this) == true)
            {
                _ = OpenAsync(dialog.FileName);
            }
        }

        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_PreviewDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                e.Handled = true;
                _ = OpenAsync(files[0]);
            }
        }

        private async Task OpenAsync(string fileName)
        {
            work?.Cancel();
            SetBusy($"Opening {Path.GetFileName(fileName)}…", indeterminate: true);
            SourceImage image;
            try
            {
                image = await Task.Run(() => SourceImage.Load(fileName));
            }
            catch (Exception ex)
            {
                SetIdle();
                ShowMessage(ex.Message, error: true);
                return;
            }

            source?.Dispose();
            source = image;
            brightness = null;
            brightnessWidth = 0;
            Title = $"Jacquard - {Path.GetFileName(fileName)}";

            int suggested = source.SuggestedWidth;
            SuggestionRun.Text = $"Suggested: {suggested} × {source.HeightFor(suggested)}";
            SuggestionText.Visibility = Visibility.Visible;

            // A new picture starts from the suggested width.
            suppressWidthChange = true;
            WidthBox.Text = "";
            suppressWidthChange = false;
            fitAfterBuild = true;
            await BuildAsync();
        }

        #endregion

        #region Width, threshold and building

        private void WidthBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (WidthPlaceholder != null)
            {
                WidthPlaceholder.Visibility = WidthBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            if (!IsLoaded || suppressWidthChange) return;
            // Wait until the user stops typing.
            widthTimer.Stop();
            widthTimer.Start();
        }

        private void UseSuggested_Click(object sender, RoutedEventArgs e)
        {
            if (source == null) return;
            suppressWidthChange = true;
            WidthBox.Text = source.SuggestedWidth.ToString();
            suppressWidthChange = false;
            _ = BuildAsync();
        }

        private void ThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;
            ThresholdText.Text = ((int)e.NewValue).ToString();
            _ = BuildAsync();
        }

        private void InvertCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            ThresholdSlider.ToolTip = InvertCheck.IsChecked == true
                ? "Pixels lighter than this become filled squares. Move it left to fill more."
                : "Pixels darker than this become filled squares. Move it right to fill more.";
            _ = BuildAsync();
        }

        /// <summary>
        /// The width to build, or null when the box holds a value that can't be used (the message bar says why).
        /// An empty box means the suggested width.
        /// </summary>
        private int? ReadWidth()
        {
            if (source == null) return null;
            string text = WidthBox.Text.Trim();
            int max = source.MaxAllowedWidth;
            int suggested = source.SuggestedWidth;

            if (text.Length == 0)
            {
                HideMessage();
                return suggested;
            }
            if (!int.TryParse(text, out int width) || width < 1)
            {
                return Invalid($"Width must be a whole number from 1 to {max}. Leave it empty for the suggested width ({suggested}).");
            }
            if (width > SourceImage.MaxWidth)
            {
                return Invalid($"Width can be at most {SourceImage.MaxWidth} squares. The suggested width for this image is {suggested}.");
            }
            if (width > source.Width)
            {
                return Invalid($"The image is only {source.Width} px wide, so the pattern can't be wider than {source.Width} squares. The suggested width is {suggested}.");
            }
            if (width > max)
            {
                long squares = (long)width * source.HeightFor(width);
                return Invalid($"{width} across would make {width} × {source.HeightFor(width)} = {squares:N0} squares. This image allows up to {max} across; the suggested width is {suggested}.");
            }

            long total = (long)width * source.HeightFor(width);
            if (width > suggested * 2)
            {
                ShowMessage($"Large pattern: {width} × {source.HeightFor(width)} = {total:N0} squares. It works, but it's slow to follow; the suggested width for this image is {suggested}.", error: false);
            }
            else
            {
                HideMessage();
            }
            return width;
        }

        private int? Invalid(string message)
        {
            HeightText.Text = "squares";
            ShowMessage(message, error: true);
            return null;
        }

        private async Task BuildAsync()
        {
            int? maybeWidth = ReadWidth();
            if (maybeWidth == null) return;
            int width = maybeWidth.Value;
            int height = source.HeightFor(width);
            int threshold = (int)ThresholdSlider.Value;
            bool invert = InvertCheck.IsChecked == true;
            HeightText.Text = $"× {height} squares";

            work?.Cancel();
            var cts = new CancellationTokenSource();
            work = cts;
            var token = cts.Token;
            var image = source;
            byte[] cached = brightnessWidth == width ? brightness : null;
            var progress = new Progress<int>(p =>
            {
                if (work == cts) Progress.Value = p;
            });
            SetBusy(cached == null ? $"Building a {width} × {height} pattern…" : "Updating the squares…", indeterminate: false);

            try
            {
                var result = await Task.Run(() =>
                {
                    byte[] values = cached ?? image.Brightness(width, progress, token);
                    token.ThrowIfCancellationRequested();
                    var built = Pattern.Build(values, width, height, threshold, invert);
                    return (Brightness: values, Pattern: built, Bitmap: built.ToBitmap(), Text: built.ToText());
                }, token);

                if (work != cts) return;
                // A new size is shown whole; a threshold change keeps the current view.
                if (pattern == null || pattern.Width != width) fitAfterBuild = true;
                brightness = result.Brightness;
                brightnessWidth = width;
                Show(result.Pattern, result.Bitmap, result.Text);
                SetIdle();
            }
            catch (OperationCanceledException)
            {
                // A newer build replaced this one.
            }
            catch (Exception ex)
            {
                if (work != cts) return;
                SetIdle();
                ShowMessage($"Couldn't build the pattern: {ex.Message}", error: true);
            }
        }

        private void Show(Pattern result, BitmapSource bitmap, string text)
        {
            pattern = result;
            PatternImage.Source = bitmap;
            PatternImage.Width = GridOverlay.Width = result.Width * CellSize;
            PatternImage.Height = GridOverlay.Height = result.Height * CellSize;
            PreviewHost.Visibility = Visibility.Visible;
            EmptyText.Visibility = Visibility.Collapsed;
            OutputText.Text = text;
            CopyButton.IsEnabled = true;
            InfoText.Text = $"Image {source.Width} × {source.Height} px  ·  Pattern {result.Width} × {result.Height}  ·  {result.Filled:N0} filled";

            if (fitAfterBuild)
            {
                fitAfterBuild = false;
                Dispatcher.BeginInvoke(new Action(FitToWindow), DispatcherPriority.Loaded);
            }
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (pattern == null) return;
            try
            {
                Clipboard.SetText(pattern.ToClipboardText());
                StatusText.Text = $"Copied the {pattern.Width} × {pattern.Height} pattern. Paste it into Excel: one square per cell.";
            }
            catch (Exception ex)
            {
                ShowMessage($"Couldn't copy to the clipboard: {ex.Message}", error: true);
            }
        }

        #endregion

        #region Status and messages

        private void SetBusy(string status, bool indeterminate)
        {
            StatusText.Text = status;
            Progress.IsIndeterminate = indeterminate;
            Progress.Value = 0;
            Progress.Visibility = Visibility.Visible;
        }

        private void SetIdle()
        {
            Progress.Visibility = Visibility.Collapsed;
            StatusText.Text = Hint;
        }

        private void ShowMessage(string message, bool error)
        {
            MessageText.Text = message;
            MessageText.Foreground = new SolidColorBrush(error ? Color.FromRgb(0x8A, 0x1C, 0x14) : Color.FromRgb(0x7A, 0x4A, 0x00));
            MessageBar.Background = new SolidColorBrush(error ? Color.FromRgb(0xFD, 0xEC, 0xEA) : Color.FromRgb(0xFF, 0xF4, 0xE5));
            MessageBar.BorderBrush = new SolidColorBrush(error ? Color.FromRgb(0xF5, 0xC2, 0xC0) : Color.FromRgb(0xF5, 0xD9, 0xA8));
            MessageBar.Visibility = Visibility.Visible;
        }

        private void HideMessage() => MessageBar.Visibility = Visibility.Collapsed;

        #endregion

        #region Zoom and pan

        // On screen, a point of the pattern sits at pattern point × zoom + (PreviewMove.X, PreviewMove.Y).
        // Part of the pattern always stays this many units inside the view, so it can't be lost off screen.
        private const double KeepVisible = 48;

        private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(zoom * ZoomStep);

        private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(zoom / ZoomStep);

        private void Fit_Click(object sender, RoutedEventArgs e) => FitToWindow();

        private void GridCheck_Changed(object sender, RoutedEventArgs e) => UpdateGrid();

        private void PreviewArea_SizeChanged(object sender, SizeChangedEventArgs e) => MoveBy(0, 0);

        /// <summary>Zooms keeping the point under <paramref name="anchor"/> (the view's centre by default) in place.</summary>
        private void SetZoom(double value, Point? anchor = null)
        {
            if (pattern == null) return;
            value = Math.Max(MinZoom, Math.Min(MaxZoom, value));
            Point at = anchor ?? new Point(PreviewArea.ActualWidth / 2, PreviewArea.ActualHeight / 2);
            // The pattern point under the anchor stays under it.
            double x = (at.X - PreviewMove.X) / zoom, y = (at.Y - PreviewMove.Y) / zoom;

            zoom = value;
            PreviewScale.ScaleX = PreviewScale.ScaleY = zoom;
            ZoomText.Text = $"{zoom * 100:0}%";
            UpdateGrid();
            MoveTo(at.X - x * zoom, at.Y - y * zoom);
        }

        private void MoveBy(double dx, double dy) => MoveTo(PreviewMove.X + dx, PreviewMove.Y + dy);

        private void MoveTo(double x, double y)
        {
            if (pattern == null) return;
            double width = pattern.Width * CellSize * zoom, height = pattern.Height * CellSize * zoom;
            double keepX = Math.Min(KeepVisible, width), keepY = Math.Min(KeepVisible, height);
            PreviewMove.X = Math.Max(keepX - width, Math.Min(PreviewArea.ActualWidth - keepX, x));
            PreviewMove.Y = Math.Max(keepY - height, Math.Min(PreviewArea.ActualHeight - keepY, y));
        }

        /// <summary>Shows the whole pattern, centred.</summary>
        private void FitToWindow()
        {
            if (pattern == null) return;
            double areaWidth = PreviewArea.ActualWidth, areaHeight = PreviewArea.ActualHeight;
            if (areaWidth <= 40 || areaHeight <= 40) return;
            double width = pattern.Width * CellSize, height = pattern.Height * CellSize;
            zoom = Math.Max(MinZoom, Math.Min(MaxZoom, Math.Min((areaWidth - 40) / width, (areaHeight - 40) / height)));
            PreviewScale.ScaleX = PreviewScale.ScaleY = zoom;
            ZoomText.Text = $"{zoom * 100:0}%";
            UpdateGrid();
            MoveTo((areaWidth - width * zoom) / 2, (areaHeight - height * zoom) / 2);
        }

        /// <summary>Grid lines only when squares are big enough on screen to tell apart.</summary>
        private void UpdateGrid()
        {
            bool show = GridCheck.IsChecked == true && zoom * CellSize >= 5;
            GridOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private static Brush MakeGridBrush()
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 0.4);
            var cell = new Rect(0, 0, CellSize, CellSize);
            var brush = new DrawingBrush(new GeometryDrawing(null, pen, new RectangleGeometry(cell)))
            {
                TileMode = TileMode.Tile,
                Viewport = cell,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = cell,
                ViewboxUnits = BrushMappingMode.Absolute,
            };
            brush.Freeze();
            return brush;
        }

        #region Mouse and touchpad

        /// <summary>
        /// A mouse wheel moves in whole notches (120) and zooms. A touchpad sends small steps: two-finger scroll moves
        /// the pattern, and pinch arrives as Ctrl+wheel, which zooms. Shift+wheel moves sideways.
        /// </summary>
        private void Preview_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (pattern == null) return;
            e.Handled = true;
            ModifierKeys modifiers = Keyboard.Modifiers;
            if (modifiers.HasFlag(ModifierKeys.Shift))
            {
                MoveBy(e.Delta, 0);
            }
            else if (modifiers.HasFlag(ModifierKeys.Control) || e.Delta % Mouse.MouseWheelDeltaForOneLine == 0)
            {
                SetZoom(zoom * Math.Pow(ZoomStep, (double)e.Delta / Mouse.MouseWheelDeltaForOneLine), e.GetPosition(PreviewArea));
            }
            else
            {
                MoveBy(0, e.Delta);
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            // WPF on .NET Framework has no event for the horizontal wheel (touchpad two-finger scroll sideways).
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_MOUSEHWHEEL = 0x020E;
            if (message == WM_MOUSEHWHEEL && pattern != null && PreviewArea.IsMouseOver)
            {
                int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                MoveBy(-delta, 0);
                handled = true;
                return (IntPtr)1;
            }
            return IntPtr.Zero;
        }

        private void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Touch also arrives as mouse clicks; the touch handlers below deal with it.
            if (e.StylusDevice != null || pattern == null) return;
            e.Handled = true;
            if (e.ClickCount == 2)
            {
                FitToWindow();
                return;
            }
            dragStart = e.GetPosition(PreviewArea);
            dragOffset = new Point(PreviewMove.X, PreviewMove.Y);
            PreviewArea.CaptureMouse();
            PreviewArea.Cursor = Cursors.SizeAll;
        }

        private void Preview_MouseMove(object sender, MouseEventArgs e)
        {
            if (dragStart == null) return;
            Point now = e.GetPosition(PreviewArea);
            MoveTo(dragOffset.X + now.X - dragStart.Value.X, dragOffset.Y + now.Y - dragStart.Value.Y);
        }

        private void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (dragStart == null) return;
            dragStart = null;
            PreviewArea.ReleaseMouseCapture();
            PreviewArea.Cursor = null;
        }

        #endregion

        #region Touch

        private Point touchDownPoint;
        private DateTime lastTapTime;
        private Point lastTapPoint;

        private void Preview_ManipulationStarting(object sender, ManipulationStartingEventArgs e)
        {
            if (pattern == null)
            {
                e.Cancel();
                return;
            }
            e.ManipulationContainer = PreviewArea;
            e.Mode = ManipulationModes.Translate | ManipulationModes.Scale;
            e.Handled = true;
        }

        /// <summary>Pinch zooms around the fingers; one or two fingers move the pattern.</summary>
        private void Preview_ManipulationDelta(object sender, ManipulationDeltaEventArgs e)
        {
            e.Handled = true;
            if (pattern == null) return;
            ManipulationDelta delta = e.DeltaManipulation;
            double scale = (delta.Scale.X + delta.Scale.Y) / 2;
            // Zoom follows the fingers only; after they lift, only the movement glides on.
            if (!e.IsInertial && Math.Abs(scale - 1) > 0.001)
            {
                SetZoom(zoom * scale, e.ManipulationOrigin);
            }
            MoveBy(delta.Translation.X, delta.Translation.Y);
        }

        /// <summary>A flick keeps the pattern moving for a moment, then it slows down.</summary>
        private void Preview_ManipulationInertiaStarting(object sender, ManipulationInertiaStartingEventArgs e)
        {
            e.TranslationBehavior.DesiredDeceleration = 10.0 * 96.0 / (1000.0 * 1000.0);
            e.Handled = true;
        }

        private void Preview_TouchDown(object sender, TouchEventArgs e)
        {
            touchDownPoint = e.GetTouchPoint(PreviewArea).Position;
        }

        /// <summary>Two quick taps in the same place fit the pattern, like a double-click.</summary>
        private void Preview_TouchUp(object sender, TouchEventArgs e)
        {
            Point point = e.GetTouchPoint(PreviewArea).Position;
            if ((point - touchDownPoint).Length > 20) return; // a drag, not a tap
            if ((DateTime.Now - lastTapTime).TotalMilliseconds < 400 && (point - lastTapPoint).Length < 40)
            {
                FitToWindow();
                lastTapTime = DateTime.MinValue;
            }
            else
            {
                lastTapTime = DateTime.Now;
                lastTapPoint = point;
            }
        }

        #endregion

        #endregion

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            switch (e.Key)
            {
                case Key.O:
                    Open_Click(this, null);
                    break;
                case Key.OemPlus:
                case Key.Add:
                    SetZoom(zoom * ZoomStep);
                    break;
                case Key.OemMinus:
                case Key.Subtract:
                    SetZoom(zoom / ZoomStep);
                    break;
                case Key.D0:
                case Key.NumPad0:
                    FitToWindow();
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }
    }
}
