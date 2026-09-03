using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Ferry
{
    internal static class StaticAssets
    {
        private const string BuildIdPlaceholder = "__FERRY_BUILD_ID__";

        private static Dictionary<string, Asset> _assets;

        public static void Initialize(string appDirectory, string buildId)
        {
            if (string.IsNullOrWhiteSpace(appDirectory))
            {
                throw new ArgumentException("Ferry's application folder is missing.", "appDirectory");
            }

            var webDirectory = Path.Combine(Path.GetFullPath(appDirectory), "web");
            _assets = AssetsExist(webDirectory)
                ? LoadFromFiles(webDirectory)
                : LoadFromResources();
            ApplyBuildId(string.IsNullOrWhiteSpace(buildId)
                ? ComputeAssetBuildId()
                : buildId);
        }

        public static bool TryGet(string path, out Asset asset)
        {
            if (_assets == null)
            {
                throw new InvalidOperationException("Static assets have not been initialized.");
            }

            if (path == "/")
            {
                path = "/index.html";
            }

            return _assets.TryGetValue(path, out asset);
        }

        private static bool AssetsExist(string webDirectory)
        {
            return File.Exists(Path.Combine(webDirectory, "index.html"))
                && File.Exists(Path.Combine(webDirectory, "app.css"))
                && File.Exists(Path.Combine(webDirectory, "app.js"))
                && File.Exists(Path.Combine(webDirectory, "icon.svg"))
                && File.Exists(Path.Combine(webDirectory, "manifest.webmanifest"))
                && File.Exists(Path.Combine(webDirectory, "service-worker.js"));
        }

        private static void ApplyBuildId(string buildId)
        {
            Asset index;
            if (!_assets.TryGetValue("/index.html", out index))
            {
                throw new InvalidOperationException("Ferry's index page is missing.");
            }

            var html = Encoding.UTF8.GetString(index.Bytes);
            if (html.IndexOf(BuildIdPlaceholder, StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException(
                    "Ferry's index page is missing the static asset build identifier.");
            }

            var escapedBuildId = Uri.EscapeDataString(buildId);
            var paths = new List<string>(_assets.Keys);
            foreach (var path in paths)
            {
                var asset = _assets[path];
                if (asset.ContentType.IndexOf("text", StringComparison.OrdinalIgnoreCase) < 0
                    && asset.ContentType.IndexOf("json", StringComparison.OrdinalIgnoreCase) < 0
                    && asset.ContentType.IndexOf("javascript", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                var text = Encoding.UTF8.GetString(asset.Bytes);
                if (text.IndexOf(BuildIdPlaceholder, StringComparison.Ordinal) < 0)
                {
                    continue;
                }
                _assets[path] = new Asset(
                    Encoding.UTF8.GetBytes(text.Replace(BuildIdPlaceholder, escapedBuildId)),
                    asset.ContentType,
                    asset.Cacheable);
            }
        }

        private static string ComputeAssetBuildId()
        {
            using (var contents = new MemoryStream())
            {
                var paths = new List<string>(_assets.Keys);
                paths.Sort(StringComparer.Ordinal);
                foreach (var path in paths)
                {
                    if (path == "/index.html")
                    {
                        continue;
                    }
                    contents.Write(_assets[path].Bytes, 0, _assets[path].Bytes.Length);
                }
                contents.Position = 0;

                using (var hash = SHA256.Create())
                {
                    return BitConverter.ToString(hash.ComputeHash(contents))
                        .Replace("-", string.Empty)
                        .ToLowerInvariant();
                }
            }
        }

        private static Dictionary<string, Asset> LoadFromFiles(string webDirectory)
        {
            return new Dictionary<string, Asset>(StringComparer.Ordinal)
            {
                { "/index.html", LoadFile(webDirectory, "index.html", "text/html; charset=utf-8", false) },
                { "/app.css", LoadFile(webDirectory, "app.css", "text/css; charset=utf-8", true) },
                { "/app.js", LoadFile(webDirectory, "app.js", "text/javascript; charset=utf-8", true) },
                { "/icon.svg", LoadFile(webDirectory, "icon.svg", "image/svg+xml", true) },
                { "/manifest.webmanifest", LoadFile(webDirectory, "manifest.webmanifest", "application/manifest+json; charset=utf-8", true) },
                { "/service-worker.js", LoadFile(webDirectory, "service-worker.js", "text/javascript; charset=utf-8", true) }
            };
        }

        private static Asset LoadFile(
            string webDirectory,
            string fileName,
            string contentType,
            bool cacheable)
        {
            return new Asset(
                File.ReadAllBytes(Path.Combine(webDirectory, fileName)),
                contentType,
                cacheable);
        }

        private static Dictionary<string, Asset> LoadFromResources()
        {
            return new Dictionary<string, Asset>(StringComparer.Ordinal)
            {
                { "/index.html", LoadResource("Ferry.Web.index.html", "text/html; charset=utf-8", false) },
                { "/app.css", LoadResource("Ferry.Web.app.css", "text/css; charset=utf-8", true) },
                { "/app.js", LoadResource("Ferry.Web.app.js", "text/javascript; charset=utf-8", true) },
                { "/icon.svg", LoadResource("Ferry.Web.icon.svg", "image/svg+xml", true) },
                { "/manifest.webmanifest", LoadResource("Ferry.Web.manifest.webmanifest", "application/manifest+json; charset=utf-8", true) },
                { "/service-worker.js", LoadResource("Ferry.Web.service-worker.js", "text/javascript; charset=utf-8", true) }
            };
        }

        private static Asset LoadResource(string resourceName, string contentType, bool cacheable)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException(string.Format(
                        "Web asset not found on disk or in the assembly: {0}",
                        resourceName));
                }

                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    return new Asset(buffer.ToArray(), contentType, cacheable);
                }
            }
        }
    }

    internal sealed class Asset
    {
        public Asset(byte[] bytes, string contentType, bool cacheable)
        {
            Bytes = bytes;
            ContentType = contentType;
            Cacheable = cacheable;
        }

        public byte[] Bytes { get; private set; }
        public string ContentType { get; private set; }
        public bool Cacheable { get; private set; }
    }
}
