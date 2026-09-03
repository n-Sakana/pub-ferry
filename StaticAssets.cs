using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Ferry
{
    internal static class StaticAssets
    {
        private static Dictionary<string, Asset> _assets;

        public static void Initialize(string appDirectory)
        {
            if (string.IsNullOrWhiteSpace(appDirectory))
            {
                throw new ArgumentException("Ferry's application folder is missing.", "appDirectory");
            }

            var webDirectory = Path.Combine(Path.GetFullPath(appDirectory), "web");
            _assets = AssetsExist(webDirectory)
                ? LoadFromFiles(webDirectory)
                : LoadFromResources();
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
                && File.Exists(Path.Combine(webDirectory, "icon.svg"));
        }

        private static Dictionary<string, Asset> LoadFromFiles(string webDirectory)
        {
            return new Dictionary<string, Asset>(StringComparer.Ordinal)
            {
                { "/index.html", LoadFile(webDirectory, "index.html", "text/html; charset=utf-8", false) },
                { "/app.css", LoadFile(webDirectory, "app.css", "text/css; charset=utf-8", true) },
                { "/app.js", LoadFile(webDirectory, "app.js", "text/javascript; charset=utf-8", true) },
                { "/icon.svg", LoadFile(webDirectory, "icon.svg", "image/svg+xml", true) }
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
                { "/icon.svg", LoadResource("Ferry.Web.icon.svg", "image/svg+xml", true) }
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
