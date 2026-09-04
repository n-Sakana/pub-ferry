using System;
using Microsoft.Web.WebView2.Core;

namespace Ferry
{
    internal static class WebViewSecurity
    {
        public static bool IsTrustedSource(string source, Uri trustedOrigin)
        {
            Uri uri;
            if (trustedOrigin == null || string.IsNullOrEmpty(source))
            {
                return false;
            }
            if (!Uri.TryCreate(source, UriKind.Absolute, out uri))
            {
                return false;
            }
            if (!string.Equals(
                uri.Scheme,
                trustedOrigin.Scheme,
                StringComparison.Ordinal))
            {
                return false;
            }
            if (!string.Equals(
                uri.Host,
                trustedOrigin.Host,
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (uri.Port != trustedOrigin.Port)
            {
                return false;
            }
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                return false;
            }
            return true;
        }

        public static void Apply(
            CoreWebView2 core,
            Uri trustedOrigin,
            Action<string> log)
        {
            if (core == null)
            {
                throw new ArgumentNullException("core");
            }
            if (trustedOrigin == null)
            {
                throw new ArgumentNullException("trustedOrigin");
            }

            CoreWebView2Settings settings = core.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.IsStatusBarEnabled = false;
            settings.IsWebMessageEnabled = false;

            core.NavigationStarting += delegate(
                object sender,
                CoreWebView2NavigationStartingEventArgs eventArgs)
            {
                if (IsTrustedSource(eventArgs.Uri, trustedOrigin))
                {
                    return;
                }
                eventArgs.Cancel = true;
                Report(
                    log,
                    "navigation refused: " + Describe(eventArgs.Uri));
            };

            core.FrameNavigationStarting += delegate(
                object sender,
                CoreWebView2NavigationStartingEventArgs eventArgs)
            {
                if (IsTrustedSource(eventArgs.Uri, trustedOrigin))
                {
                    return;
                }
                eventArgs.Cancel = true;
                Report(
                    log,
                    "frame navigation refused: " + Describe(eventArgs.Uri));
            };

            core.NewWindowRequested += delegate(
                object sender,
                CoreWebView2NewWindowRequestedEventArgs eventArgs)
            {
                eventArgs.Handled = true;
                Report(
                    log,
                    "new window refused: " + Describe(eventArgs.Uri));
            };
        }

        public static string Describe(string uri)
        {
            if (string.IsNullOrEmpty(uri))
            {
                return "(empty)";
            }
            return uri.Length > 200 ? uri.Substring(0, 200) + "..." : uri;
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
            catch
            {
            }
        }
    }
}
