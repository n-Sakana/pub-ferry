using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Ferry
{
    public sealed class MainWindow : Window
    {
        private readonly Uri _appUri;
        private readonly CancellationToken _shutdownToken;
        private readonly Action _requestShutdown;
        private readonly Grid _rootGrid;
        private readonly WebView2 _webView;
        private Border _loadingOverlay;
        private CancellationTokenRegistration _shutdownRegistration;
        private string _pendingActivationMode;
        private int _activationSequence;

        private const int SwRestore = 9;

        private static readonly Color MarkPlate = Color.FromRgb(0, 122, 255);
        private static readonly Color MarkText = Color.FromRgb(255, 255, 255);

        public MainWindow(
            Uri appUri,
            CancellationToken shutdownToken,
            Action requestShutdown)
        {
            _appUri = appUri;
            _shutdownToken = shutdownToken;
            _requestShutdown = requestShutdown;

            Title = "Ferry";
            Icon = CreateWindowIcon();
            Width = 1180;
            Height = 800;
            MinWidth = 900;
            MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(242, 242, 247));

            _rootGrid = new Grid();
            _webView = new WebView2();
            _webView.DefaultBackgroundColor =
                System.Drawing.Color.FromArgb(242, 242, 247);
            _webView.Visibility = Visibility.Hidden;
            _rootGrid.Children.Add(_webView);

            _loadingOverlay = CreateLoadingOverlay();
            _rootGrid.Children.Add(_loadingOverlay);
            Content = _rootGrid;

            Loaded += OnLoaded;
            Closing += OnClosing;
            Closed += OnClosed;
            DesktopActivation.Register(OnActivationRequested);
        }

        public static ImageSource CreateWindowIcon()
        {
            const double size = 64;
            DrawingGroup drawing = new DrawingGroup();
            SolidColorBrush textBrush = new SolidColorBrush(MarkText);
            RectangleGeometry plate = new RectangleGeometry(
                new Rect(1, 1, size - 2, size - 2),
                14,
                14);
            FormattedText text = new FormattedText(
                "F",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(
                    SystemFonts.MessageFontFamily,
                    FontStyles.Normal,
                    FontWeights.Bold,
                    FontStretches.Normal),
                40,
                textBrush,
                1.0);

            drawing.Children.Add(new GeometryDrawing(
                new SolidColorBrush(MarkPlate),
                null,
                plate));
            drawing.Children.Add(new GeometryDrawing(
                textBrush,
                null,
                text.BuildGeometry(new Point(
                    (size - text.Width) / 2,
                    (size - text.Height) / 2))));

            DrawingImage image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }

        private Border CreateLoadingOverlay()
        {
            Border overlay = new Border();
            overlay.Background = new SolidColorBrush(
                Color.FromRgb(242, 242, 247));

            StackPanel stack = new StackPanel();
            stack.HorizontalAlignment = HorizontalAlignment.Center;
            stack.VerticalAlignment = VerticalAlignment.Center;

            Border mark = new Border();
            mark.Width = 46;
            mark.Height = 46;
            mark.CornerRadius = new CornerRadius(12);
            mark.HorizontalAlignment = HorizontalAlignment.Center;
            mark.Background = new SolidColorBrush(MarkPlate);

            TextBlock initial = new TextBlock();
            initial.Text = "F";
            initial.FontSize = 18;
            initial.FontWeight = FontWeights.Bold;
            initial.HorizontalAlignment = HorizontalAlignment.Center;
            initial.VerticalAlignment = VerticalAlignment.Center;
            initial.Foreground = new SolidColorBrush(MarkText);
            mark.Child = initial;
            stack.Children.Add(mark);

            TextBlock name = new TextBlock();
            name.Text = "Ferry";
            name.FontSize = 15;
            name.FontWeight = FontWeights.SemiBold;
            name.Margin = new Thickness(0, 16, 0, 0);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.Foreground = new SolidColorBrush(
                Color.FromRgb(29, 29, 31));
            stack.Children.Add(name);
            stack.Children.Add(CreateLoadingBar());

            overlay.Child = stack;
            return overlay;
        }

        private Border CreateLoadingBar()
        {
            Border track = new Border();
            track.Width = 132;
            track.Height = 3;
            track.Margin = new Thickness(0, 22, 0, 0);
            track.CornerRadius = new CornerRadius(2);
            track.HorizontalAlignment = HorizontalAlignment.Center;
            track.Background = new SolidColorBrush(
                Color.FromRgb(220, 220, 225));
            track.ClipToBounds = true;

            Border pill = new Border();
            pill.Width = 44;
            pill.Height = 3;
            pill.CornerRadius = new CornerRadius(2);
            pill.HorizontalAlignment = HorizontalAlignment.Left;
            pill.Background = new SolidColorBrush(MarkPlate);

            TranslateTransform slide = new TranslateTransform();
            pill.RenderTransform = slide;
            track.Child = pill;

            DoubleAnimation move = new DoubleAnimation();
            move.From = -44;
            move.To = 132;
            move.Duration = new Duration(TimeSpan.FromMilliseconds(1100));
            move.RepeatBehavior = RepeatBehavior.Forever;
            move.EasingFunction = new SineEase();
            slide.BeginAnimation(TranslateTransform.XProperty, move);
            return track;
        }

        private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
        {
            _shutdownRegistration = _shutdownToken.Register(
                OnShutdownRequested);
            if (_shutdownToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "Ferry",
                    "WebView2Cache");
                CoreWebView2Environment environment =
                    await CoreWebView2Environment.CreateAsync(
                        null,
                        userDataFolder,
                        null);
                await _webView.EnsureCoreWebView2Async(environment);

                WebViewSecurity.Apply(
                    _webView.CoreWebView2,
                    _appUri,
                    null);
                _webView.CoreWebView2.NavigationCompleted +=
                    OnFirstNavigationCompleted;
                string pendingMode = _pendingActivationMode;
                _pendingActivationMode = null;
                _webView.Source = string.IsNullOrWhiteSpace(pendingMode)
                    ? _appUri
                    : CreateActivationUri(pendingMode);
            }
            catch (Exception exception)
            {
                Close();
                DesktopApp.ShowStartupMessage(
                    "Ferry の画面を初期化できませんでした。",
                    exception);
            }
        }

        private void OnShutdownRequested()
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate()
                {
                    if (IsLoaded)
                    {
                        Close();
                    }
                }));
            }
            catch
            {
            }
        }

        private void OnClosing(
            object sender,
            System.ComponentModel.CancelEventArgs eventArgs)
        {
            try
            {
                _requestShutdown();
            }
            catch
            {
            }
        }

        private void OnClosed(object sender, EventArgs eventArgs)
        {
            DesktopActivation.Unregister(OnActivationRequested);
            _shutdownRegistration.Dispose();
            if (_webView.CoreWebView2 != null)
            {
                _webView.CoreWebView2.NavigationCompleted -=
                    OnFirstNavigationCompleted;
            }
            _webView.Dispose();
        }

        private void OnActivationRequested(string mode)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                _pendingActivationMode = mode;
                if (_webView.CoreWebView2 != null &&
                    !string.IsNullOrWhiteSpace(mode))
                {
                    _webView.Source = CreateActivationUri(mode);
                    _pendingActivationMode = null;
                }

                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                }
                if (!IsVisible)
                {
                    Show();
                }

                Activate();
                Focus();
                IntPtr handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero)
                {
                    ShowWindow(handle, SwRestore);
                    SetForegroundWindow(handle);
                }
            }));
        }

        private Uri CreateActivationUri(string mode)
        {
            _activationSequence++;
            UriBuilder builder = new UriBuilder(_appUri);
            builder.Query = "activation=" + _activationSequence.ToString(
                CultureInfo.InvariantCulture);
            builder.Fragment = mode;
            return builder.Uri;
        }

        private void OnFirstNavigationCompleted(
            object sender,
            CoreWebView2NavigationCompletedEventArgs eventArgs)
        {
            _webView.CoreWebView2.NavigationCompleted -=
                OnFirstNavigationCompleted;
            Dispatcher.BeginInvoke(new Action(delegate()
            {
                _webView.Visibility = Visibility.Visible;
                if (_loadingOverlay == null)
                {
                    return;
                }

                Border leaving = _loadingOverlay;
                DoubleAnimation fade = new DoubleAnimation();
                fade.From = 1;
                fade.To = 0;
                fade.Duration = new Duration(
                    TimeSpan.FromMilliseconds(220));
                fade.Completed += delegate
                {
                    _rootGrid.Children.Remove(leaving);
                };
                leaving.BeginAnimation(UIElement.OpacityProperty, fade);
                _loadingOverlay = null;
            }));
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);
    }
}
