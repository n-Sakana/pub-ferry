using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace PubFerry
{
    // The window, and the bridge between the page and the host.
    //
    // Two channels, on purpose:
    //
    //   WEB MESSAGES carry decisions — pick something, save the settings, open
    //     the folder. Small JSON, request/reply by id.
    //
    //   INTERCEPTED REQUESTS carry bytes. A 16 MB folder as a base64 string in
    //     a web message would be a 21 MB string built, copied, parsed and held
    //     several times over. `fetch()` against a host-answered origin moves
    //     the same bytes once, as bytes.
    public class MainWindow : Window
    {
        private readonly Grid rootGrid;
        private readonly WebView2 webView;
        private Border loadingOverlay;
        private HostServices services;
        private ReaderInput reader;
        private string appRoot;

        private static readonly Color MarkPlate = Color.FromRgb(22, 98, 143);
        private static readonly Color MarkInk = Color.FromRgb(255, 255, 255);
        private static readonly Color Shell = Color.FromRgb(16, 20, 26);

        public MainWindow()
        {
            Title = App.ProductName;
            Icon = CreateWindowIcon();
            // Wide rather than tall: the sending screen puts a large square
            // code beside a narrow rail, and the receiving screen puts a camera
            // beside what is arriving. Both want width.
            Width = 1180;
            Height = 800;
            // Below this the two-column screens fold to one and the code gets
            // too few pixels per module to be read reliably from a phone.
            MinWidth = 880;
            MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Shell);

            rootGrid = new Grid();
            webView = new WebView2();
            webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 243, 245, 248);
            webView.Visibility = Visibility.Hidden;
            rootGrid.Children.Add(webView);
            loadingOverlay = CreateLoadingOverlay();
            rootGrid.Children.Add(loadingOverlay);
            Content = rootGrid;

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        public static ImageSource CreateWindowIcon()
        {
            const double Size = 64;
            DrawingGroup drawing = new DrawingGroup();
            RectangleGeometry plate = new RectangleGeometry(new Rect(1, 1, Size - 2, Size - 2), 14, 14);
            FormattedText text = new FormattedText(
                "PF",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(
                    SystemFonts.MessageFontFamily,
                    FontStyles.Normal,
                    FontWeights.Bold,
                    FontStretches.Normal),
                34,
                new SolidColorBrush(MarkInk),
                1.0);
            drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(MarkPlate), null, plate));
            drawing.Children.Add(new GeometryDrawing(
                new SolidColorBrush(MarkInk),
                null,
                text.BuildGeometry(new Point((Size - text.Width) / 2, (Size - text.Height) / 2))));
            DrawingImage image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }

        // Small and quiet: the mark, the name, and one bar that keeps moving.
        // No percentage is claimed, because the wait is not measurable.
        private Border CreateLoadingOverlay()
        {
            Border overlay = new Border();
            overlay.Background = new SolidColorBrush(Shell);
            StackPanel stack = new StackPanel();
            stack.HorizontalAlignment = HorizontalAlignment.Center;
            stack.VerticalAlignment = VerticalAlignment.Center;

            Border mark = new Border();
            mark.Width = 46;
            mark.Height = 46;
            mark.CornerRadius = new CornerRadius(12);
            mark.HorizontalAlignment = HorizontalAlignment.Center;
            mark.Background = new SolidColorBrush(MarkPlate);
            TextBlock initials = new TextBlock();
            initials.Text = "PF";
            initials.FontSize = 16;
            initials.FontWeight = FontWeights.Bold;
            initials.HorizontalAlignment = HorizontalAlignment.Center;
            initials.VerticalAlignment = VerticalAlignment.Center;
            initials.Foreground = new SolidColorBrush(MarkInk);
            mark.Child = initials;
            stack.Children.Add(mark);

            TextBlock name = new TextBlock();
            name.Text = App.ProductName;
            name.FontSize = 15;
            name.FontWeight = FontWeights.SemiBold;
            name.Margin = new Thickness(0, 16, 0, 0);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.Foreground = new SolidColorBrush(Color.FromRgb(232, 237, 243));
            stack.Children.Add(name);

            Border track = new Border();
            track.Width = 132;
            track.Height = 3;
            track.Margin = new Thickness(0, 22, 0, 0);
            track.CornerRadius = new CornerRadius(2);
            track.HorizontalAlignment = HorizontalAlignment.Center;
            track.Background = new SolidColorBrush(Color.FromRgb(34, 41, 50));
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
            stack.Children.Add(track);

            overlay.Child = stack;
            return overlay;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                string userData = Path.Combine(HostServices.DataDirectory(), "WebView2");
                Directory.CreateDirectory(userData);

                // A debugging port is opened only when pub-ferry.ps1 was asked
                // for one. It is how the automated screen tests drive the REAL
                // window rather than a copy of the page in a browser, and it is
                // absent from an ordinary run.
                CoreWebView2EnvironmentOptions options = new CoreWebView2EnvironmentOptions();
                string debugPort = Environment.GetEnvironmentVariable("PUB_FERRY_DEBUG_PORT");
                testMode = !string.IsNullOrEmpty(debugPort);
                string extra = "--disable-features=msWebOOUI,msPdfOOUI";
                if (!string.IsNullOrEmpty(debugPort))
                {
                    extra += " --remote-debugging-port=" + debugPort;
                    // Only the loopback tool that opened the port, not "any
                    // origin at all" — a wildcard here lets any page the user
                    // happens to have open attach to the product and run
                    // script inside it.
                    extra += " --remote-allow-origins=http://127.0.0.1:" + debugPort;
                }
                // A fake camera, again only when asked for: this desktop has no
                // camera, so the only way to exercise the receiving path on it
                // is to hand the runtime a recorded one.
                string fakeVideo = Environment.GetEnvironmentVariable("PUB_FERRY_FAKE_VIDEO");
                if (!string.IsNullOrEmpty(fakeVideo))
                {
                    extra += " --use-fake-ui-for-media-stream";
                    extra += " --use-fake-device-for-media-capture";
                    extra += " --use-file-for-fake-video-capture=\"" + fakeVideo + "\"";
                }
                options.AdditionalBrowserArguments = extra;

                CoreWebView2Environment environment =
                    await CoreWebView2Environment.CreateAsync(null, userData, options);
                await webView.EnsureCoreWebView2Async(environment);

                CoreWebView2 core = webView.CoreWebView2;
                // Deliberately NOT SetVirtualHostNameToFolderMapping.
                //
                // A mapped virtual host is resolved below the WebResourceRequested
                // layer, so requests to it never reach the handler — which was
                // measured, not assumed: with the mapping in place, a fetch to
                // a path on that host produced "Failed to fetch" and the handler
                // logged nothing at all. Serving the page from the host means
                // every byte the page loads goes through one place, the page and
                // the host services share one origin (so nothing is cross-origin
                // and no CORS header can be got wrong), and the response headers
                // — the content security policy in particular — are ours.
                appRoot = Path.Combine(App.BaseDir, "app", "dist");

                services = new HostServices(this);
                reader = new ReaderInput(OnReaderLine, OnReaderStatus);

                // The boundary is set before anything is loaded, so no page
                // ever runs under looser rules than the product's.
                WebViewSecurity.Apply(core, WriteSecurityLog);
                core.WebMessageReceived += OnWebMessageReceived;
                core.AddWebResourceRequestedFilter(
                    WebViewSecurity.ResourceFilter,
                    CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += OnWebResourceRequested;
                core.NavigationCompleted += OnFirstNavigationCompleted;

                services.WriteLog("INFO", "startup");
                core.Navigate(WebViewSecurity.StartPage);
            }
            catch (Exception error)
            {
                Close();
                App.ShowStartupFailure("画面を開けませんでした。", "ログを見てください。", error);
            }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            if (reader != null)
            {
                reader.Dispose();
            }
            if (services != null)
            {
                services.WriteLog("INFO", "shutdown");
            }
            webView.Dispose();
        }

        private void OnFirstNavigationCompleted(
            object sender,
            CoreWebView2NavigationCompletedEventArgs e)
        {
            webView.CoreWebView2.NavigationCompleted -= OnFirstNavigationCompleted;
            Dispatcher.BeginInvoke(new Action(delegate()
            {
                webView.Visibility = Visibility.Visible;
                if (loadingOverlay == null)
                {
                    return;
                }
                Border leaving = loadingOverlay;
                DoubleAnimation fade = new DoubleAnimation();
                fade.From = 1;
                fade.To = 0;
                fade.Duration = new Duration(TimeSpan.FromMilliseconds(200));
                fade.Completed += delegate(object s, EventArgs a)
                {
                    rootGrid.Children.Remove(leaving);
                };
                leaving.BeginAnimation(UIElement.OpacityProperty, fade);
                loadingOverlay = null;
            }));
        }

        private void WriteSecurityLog(string message)
        {
            if (services != null)
            {
                services.WriteLog("WARN", message);
            }
        }

        // ---- byte channel ----------------------------------------------------

        private void OnWebResourceRequested(
            object sender,
            CoreWebView2WebResourceRequestedEventArgs e)
        {
            // Requests to the service origin are answered here and nowhere
            // else — nothing on the network is ever reached for them.
            CoreWebView2WebResourceRequest request = e.Request;
            if (testMode && request.Uri.IndexOf(WebViewSecurity.ServicePath, StringComparison.Ordinal) >= 0)
            {
                // Host actions only. Logging every stylesheet the page asks for
                // would bury the three lines that matter.
                services.WriteLog("INFO", "host " + request.Method + " " + WebViewSecurity.Describe(request.Uri).Split('?')[0]);
            }
            Uri uri;
            if (!Uri.TryCreate(request.Uri, UriKind.Absolute, out uri))
            {
                e.Response = Reply(400, "{\"error\":\"bad-request\"}", "application/json");
                return;
            }
            try
            {
                if (uri.AbsolutePath == WebViewSecurity.ServicePath + "pick" && request.Method == "GET")
                {
                    string token = ParseQuery(uri.Query, "token");
                    byte[] bytes = services.ReadStaged(token);
                    e.Response = Binary(bytes);
                    return;
                }
                if (uri.AbsolutePath == WebViewSecurity.ServicePath + "save" && request.Method == "POST")
                {
                    byte[] block = ReadAll(request.Content);
                    Dictionary<string, object> result = services.Receive(block);
                    e.Response = Reply(200, Json.Write(result), "application/json");
                    return;
                }
                e.Response = ServeFile(uri.AbsolutePath);
            }
            catch (HostActionException error)
            {
                Dictionary<string, object> body = new Dictionary<string, object>();
                body["error"] = error.Code;
                body["message"] = error.Message;
                e.Response = Reply(422, Json.Write(body), "application/json");
            }
            catch (Exception error)
            {
                if (services != null)
                {
                    services.WriteLog("ERROR", "resource: " + error.GetType().Name);
                }
                Dictionary<string, object> body = new Dictionary<string, object>();
                body["error"] = "internal";
                body["message"] = "処理できませんでした。";
                e.Response = Reply(500, Json.Write(body), "application/json");
            }
        }

        // The product's own files, and only those. The path is resolved and
        // then checked to be inside the folder: a request for
        // /../../../windows/win.ini is a string that looks fine until it is
        // joined onto something.
        private CoreWebView2WebResourceResponse ServeFile(string absolutePath)
        {
            string relative = Uri.UnescapeDataString(absolutePath).TrimStart('/');
            if (relative.Length == 0)
            {
                relative = "index.html";
            }
            string target;
            try
            {
                target = Path.GetFullPath(Path.Combine(appRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch
            {
                return Reply(400, "{\"error\":\"bad-path\"}", "application/json");
            }
            if (!SafeWriter.IsInside(appRoot, target) || !File.Exists(target))
            {
                return Reply(404, "{\"error\":\"not-found\"}", "application/json");
            }

            string type = MimeFor(Path.GetExtension(target));
            // The page is the product; nothing it loads comes from anywhere
            // else, and nothing may frame it. 'wasm-unsafe-eval' is what the QR
            // decoder's WebAssembly module needs and is not eval().
            string headers =
                "Content-Type: " + type + "\r\n" +
                "Cache-Control: no-cache\r\n" +
                "X-Content-Type-Options: nosniff\r\n" +
                "Content-Security-Policy: default-src 'self'; script-src 'self' 'wasm-unsafe-eval' 'unsafe-inline'; " +
                "style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; " +
                "connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
            return webView.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(File.ReadAllBytes(target)),
                200,
                "OK",
                headers);
        }

        private static string MimeFor(string extension)
        {
            switch (extension.ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": return "text/javascript; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".ico": return "image/x-icon";
                case ".wasm": return "application/wasm";
                case ".woff2": return "font/woff2";
                default: return "application/octet-stream";
            }
        }

        private static string ParseQuery(string query, string key)
        {
            if (string.IsNullOrEmpty(query))
            {
                return null;
            }
            foreach (string pair in query.TrimStart('?').Split('&'))
            {
                string[] parts = pair.Split(new char[] { '=' }, 2);
                if (parts.Length == 2 && parts[0] == key)
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
            }
            return null;
        }

        private static byte[] ReadAll(Stream stream)
        {
            if (stream == null)
            {
                return new byte[0];
            }
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] chunk = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    total += read;
                    if (total > SafeWriter.MaxTotalBytes + 4 * 1024 * 1024)
                    {
                        throw new HostActionException("too-large", "受信データが上限を超えました。");
                    }
                    buffer.Write(chunk, 0, read);
                }
                return buffer.ToArray();
            }
        }

        private CoreWebView2WebResourceResponse Reply(int status, string body, string type)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            return webView.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(bytes),
                status,
                status == 200 ? "OK" : "Error",
                "Content-Type: " + type + "; charset=utf-8\r\nCache-Control: no-store");
        }

        private CoreWebView2WebResourceResponse Binary(byte[] bytes)
        {
            return webView.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(bytes),
                200,
                "OK",
                "Content-Type: application/octet-stream\r\nCache-Control: no-store");
        }

        // ---- decision channel -------------------------------------------------

        private void OnWebMessageReceived(
            object sender,
            CoreWebView2WebMessageReceivedEventArgs e)
        {
            object id = null;
            try
            {
                // Before the message is read: only the product page may ask
                // the host to do work.
                if (!WebViewSecurity.IsTrustedSource(e.Source))
                {
                    WriteSecurityLog("message refused from " + WebViewSecurity.Describe(e.Source));
                    return;
                }
                Json.Node message = Json.Parse(e.WebMessageAsJson);
                id = message.Get("id").Raw;
                string action = message.String("action");
                Json.Node parameters = message.Get("params");
                object data = Dispatch(action, parameters);
                Reply(id, data, null);
            }
            catch (HostActionException error)
            {
                Reply(id, null, error);
            }
            catch (Exception error)
            {
                if (services != null)
                {
                    services.WriteLog("ERROR", "action failed: " + error.GetType().Name);
                }
                Reply(id, null, new HostActionException("internal", "処理できませんでした。"));
            }
        }

        private object Dispatch(string action, Json.Node parameters)
        {
            switch (action)
            {
                case "getStartup":
                    {
                        Dictionary<string, object> startup = new Dictionary<string, object>();
                        startup["settings"] = services.LoadSettings();
                        startup["serialPorts"] = ReaderInput.AvailablePorts();
                        startup["appVersion"] = ReadVersion();
                        return startup;
                    }
                case "pickFiles":
                    return services.PickFiles();
                case "pickFolder":
                    return services.PickFolder();
                case "chooseDestination":
                    return services.ChooseDestination();
                case "saveSettings":
                    return services.SaveSettings(parameters);
                case "openFolder":
                    services.OpenFolder(parameters.String("which"));
                    return new Dictionary<string, object>();
                case "listSerialPorts":
                    {
                        Dictionary<string, object> ports = new Dictionary<string, object>();
                        ports["ports"] = ReaderInput.AvailablePorts();
                        return ports;
                    }
                case "openReader":
                    {
                        double baud = parameters.Number("baud");
                        reader.Open(parameters.String("port"), baud >= 300 ? (int)baud : 9600);
                        Dictionary<string, object> opened = new Dictionary<string, object>();
                        opened["port"] = reader.PortName;
                        return opened;
                    }
                case "closeReader":
                    reader.Close();
                    return new Dictionary<string, object>();
                case "stageForTest":
                    RequireTestMode();
                    return services.StageForTest(parameters.String("path"));
                case "emulateReaderLine":
                    // The path a real reader's bytes take, driven by a test. It
                    // goes through the same parser the serial port feeds, so
                    // what the test exercises is what the port exercises.
                    RequireTestMode();
                    reader.Feed(parameters.String("text") + "\r");
                    return new Dictionary<string, object>();
                default:
                    throw new HostActionException("unknown-action", "不明な要求です。");
            }
        }

        /// Whether this run was started for testing. Read ONCE, at startup,
        /// from the environment pub-ferry.ps1 set for this process — not on
        /// demand. `$env:` persists across commands in one PowerShell session,
        /// so a second, ordinary run in the same window would otherwise
        /// inherit the flag and quietly re-open the test-only actions.
        private static bool testMode;

        /// Actions that exist for the screen tests are unreachable in an
        /// ordinary run.
        private static void RequireTestMode()
        {
            if (!testMode)
            {
                throw new HostActionException("not-available", "この操作は試験用の起動時のみ使えます。");
            }
        }

        private static string ReadVersion()
        {
            try
            {
                string path = Path.Combine(App.BaseDir, "app", "dist", "version.txt");
                if (File.Exists(path))
                {
                    return File.ReadAllText(path).Trim();
                }
            }
            catch
            {
            }
            return "";
        }

        private void Reply(object id, object data, HostActionException error)
        {
            if (id == null)
            {
                return;
            }
            Dictionary<string, object> message = new Dictionary<string, object>();
            message["id"] = id;
            if (error == null)
            {
                message["ok"] = true;
                message["data"] = data;
            }
            else
            {
                message["ok"] = false;
                message["error"] = error.Code;
                message["message"] = error.Message;
            }
            Post(message);
        }

        private void OnReaderLine(string line)
        {
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["text"] = line;
            PushEvent("readerLine", data);
        }

        private void OnReaderStatus(string state, string message)
        {
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["state"] = state;
            data["message"] = message;
            PushEvent("readerStatus", data);
        }

        private void PushEvent(string name, object data)
        {
            Dictionary<string, object> message = new Dictionary<string, object>();
            message["event"] = name;
            message["data"] = data;
            Post(message);
        }

        private void Post(Dictionary<string, object> message)
        {
            // Serial data arrives on a port thread; the WebView is a UI object.
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(delegate() { Post(message); }));
                return;
            }
            try
            {
                if (webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.PostWebMessageAsJson(Json.Write(message));
                }
            }
            catch
            {
            }
        }
    }
}
