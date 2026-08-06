using System;
using Microsoft.Web.WebView2.Core;

namespace Decimen
{
    // The edge between the host and the page.
    //
    // The page is part of the product. It is served from one virtual host over
    // https — which is not decoration: `crypto.subtle` and `getUserMedia` do
    // not exist at all on a non-secure origin, so a page loaded from file://
    // could neither hash a transfer nor open a camera. Every other origin is
    // refused here rather than half-trusted further in.
    public static class WebViewSecurity
    {
        // The page.
        public const string AppHost = "decimen.app.local";
        public const string Scheme = "https";

        // Host services live under a path on the PAGE'S OWN ORIGIN rather than
        // on a second virtual host. A second host name would make every byte
        // the page moves a cross-origin request — which browsers answer with a
        // CORS preflight and, absent the right headers, with "Failed to fetch"
        // and nothing else to go on. Same origin means no preflight, no
        // headers to get wrong, and one fewer name in the security boundary.
        public const string ServicePath = "/__host/";

        public static string AppOrigin { get { return Scheme + "://" + AppHost; } }
        public static string StartPage { get { return AppOrigin + "/index.html"; } }
        /// Everything on this origin is answered by the host — the page's own
        /// files as well as the host services. See MainWindow.ServeFile.
        public static string ResourceFilter { get { return AppOrigin + "/*"; } }

        // Scheme, host and port must all match. A prefix test would accept
        // https://decimen.app.local.example.com/ and https://decimen.app.local:8443/,
        // which are different origins anybody could stand up.
        public static bool IsTrustedSource(string source)
        {
            Uri uri;
            if (string.IsNullOrEmpty(source))
            {
                return false;
            }
            if (!Uri.TryCreate(source, UriKind.Absolute, out uri))
            {
                return false;
            }
            if (!string.Equals(uri.Scheme, Scheme, StringComparison.Ordinal))
            {
                return false;
            }
            if (!string.Equals(uri.Host, AppHost, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (!uri.IsDefaultPort)
            {
                return false;
            }
            // An origin never carries credentials. Something that does is not
            // the product page.
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                return false;
            }
            return true;
        }

        // Locks the window down and installs the refusals. `log` records what
        // was refused and may be null.
        public static void Apply(CoreWebView2 core, Action<string> log)
        {
            if (core == null)
            {
                throw new ArgumentNullException("core");
            }

            CoreWebView2Settings settings = core.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.IsStatusBarEnabled = false;
            settings.IsWebMessageEnabled = true;
            settings.IsSwipeNavigationEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;

            core.NavigationStarting += delegate(
                object sender,
                CoreWebView2NavigationStartingEventArgs e)
            {
                if (IsTrustedSource(e.Uri))
                {
                    return;
                }
                e.Cancel = true;
                Report(log, "navigation refused: " + Describe(e.Uri));
            };

            core.FrameNavigationStarting += delegate(
                object sender,
                CoreWebView2NavigationStartingEventArgs e)
            {
                // The app has no frames. Anything appearing in one is not part
                // of the product.
                if (IsTrustedSource(e.Uri))
                {
                    return;
                }
                e.Cancel = true;
                Report(log, "frame navigation refused: " + Describe(e.Uri));
            };

            core.NewWindowRequested += delegate(
                object sender,
                CoreWebView2NewWindowRequestedEventArgs e)
            {
                // Handled with no window created and nothing handed to the
                // system browser: nothing in this app opens a second window.
                e.Handled = true;
                Report(log, "new window refused: " + Describe(e.Uri));
            };

            core.PermissionRequested += delegate(
                object sender,
                CoreWebView2PermissionRequestedEventArgs e)
            {
                // The camera is the one permission this product uses, and only
                // the product page may have it. Everything else — microphone,
                // location, notifications, clipboard reads — is denied without
                // a prompt, because nothing here asks for them and a prompt for
                // something the user did not initiate is how people learn to
                // click Allow without reading.
                //
                // Deciding here rather than leaving WebView2's default also
                // makes the answer the same on every machine: the default is
                // "ask" on current runtimes and "deny" on older ones.
                bool isCamera = e.PermissionKind == CoreWebView2PermissionKind.Camera;
                bool fromProduct = IsTrustedSource(e.Uri);
                if (isCamera && fromProduct)
                {
                    e.State = CoreWebView2PermissionState.Allow;
                    Report(log, "camera allowed for the product page");
                }
                else
                {
                    e.State = CoreWebView2PermissionState.Deny;
                    Report(
                        log,
                        "permission denied: " + e.PermissionKind.ToString() + " from " + Describe(e.Uri));
                }
                e.Handled = true;
            };

            core.DownloadStarting += delegate(
                object sender,
                CoreWebView2DownloadStartingEventArgs e)
            {
                // Received files are written by the host, into a folder the
                // user registered, after verification. A browser download would
                // route around all of that.
                e.Cancel = true;
                Report(log, "download refused");
            };
        }

        // Refused targets are recorded, but a hostile URI is not copied into
        // the log at full length.
        public static string Describe(string uri)
        {
            if (string.IsNullOrEmpty(uri))
            {
                return "(empty)";
            }
            if (uri.Length > 200)
            {
                return uri.Substring(0, 200) + "...";
            }
            return uri;
        }

        private static void Report(Action<string> log, string message)
        {
            if (log == null)
            {
                return;
            }
            try
            {
                log(message);
            }
            catch (Exception)
            {
                // Refusing is the point; failing to write it down must not
                // undo the refusal.
            }
        }
    }
}
