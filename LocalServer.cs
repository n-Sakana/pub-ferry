using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ferry
{
    internal sealed class LocalServer : IDisposable
    {
        private const long MaximumRequestBytes = 64 * 1024;

        private readonly HttpListener _listener = new HttpListener();
        private readonly AppState _state;
        private readonly int _port;
        private readonly string _initialMode;

        public LocalServer(int port, AppState state, string initialMode)
        {
            _port = port;
            _state = state;
            _initialMode = initialMode;
        }

        public Uri Start()
        {
            _listener.IgnoreWriteExceptions = true;
            _listener.Prefixes.Add(string.Format("http://127.0.0.1:{0}/", _port));
            _listener.Prefixes.Add(string.Format("http://localhost:{0}/", _port));
            _listener.Start();
            return new Uri(string.Format("http://localhost:{0}/", _port));
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using (cancellationToken.Register(delegate
            {
                try
                {
                    _listener.Close();
                }
                catch (ObjectDisposedException)
                {
                }
            }))
            {
                while (!cancellationToken.IsCancellationRequested && _listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (HttpListenerException)
                    {
                        if (cancellationToken.IsCancellationRequested || !_listener.IsListening)
                        {
                            break;
                        }

                        throw;
                    }
                    catch (ObjectDisposedException)
                    {
                        if (cancellationToken.IsCancellationRequested || !_listener.IsListening)
                        {
                            break;
                        }

                        throw;
                    }

                    await HandleSafelyAsync(context);
                }
            }
        }

        public void Dispose()
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }

            _listener.Close();
        }

        private async Task HandleSafelyAsync(HttpListenerContext context)
        {
            AddSecurityHeaders(context.Response);
            ErrorBody error = null;
            var errorStatus = HttpStatusCode.InternalServerError;

            try
            {
                await HandleAsync(context);
            }
            catch (DirectoryNotFoundException exception)
            {
                errorStatus = HttpStatusCode.BadRequest;
                error = new ErrorBody(exception.Message);
            }
            catch (ArgumentException exception)
            {
                errorStatus = HttpStatusCode.BadRequest;
                error = new ErrorBody(exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                errorStatus = HttpStatusCode.Forbidden;
                error = new ErrorBody(string.Format(
                    "このフォルダは開けません: {0}",
                    exception.Message));
            }
            catch (IOException exception)
            {
                errorStatus = HttpStatusCode.BadRequest;
                error = new ErrorBody(string.Format(
                    "フォルダを読めません: {0}",
                    exception.Message));
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    "{0:O} {1} {2}: {3}",
                    DateTimeOffset.Now,
                    context.Request.HttpMethod,
                    context.Request.RawUrl,
                    exception);
                error = new ErrorBody("Ferry の処理中にエラーが起きました。");
            }

            if (error != null)
            {
                try
                {
                    if (context.Response.OutputStream.CanWrite)
                    {
                        await WriteJsonAsync(context, errorStatus, error);
                    }
                }
                catch (ObjectDisposedException)
                {
                    // The client disconnected while Ferry was reporting the error.
                }
            }

            try
            {
                context.Response.OutputStream.Close();
            }
            catch (ObjectDisposedException)
            {
                // The client disconnected after the response was written.
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var path = request.Url == null ? "/" : request.Url.AbsolutePath;
            var role = GetRequestRole(request);

            if (request.HttpMethod == "GET" && path == "/api/status")
            {
                await WriteJsonAsync(context, HttpStatusCode.OK, BuildStatus(role));
                return;
            }

            if (request.HttpMethod == "GET" && path == "/api/folder")
            {
                await WriteJsonAsync(context, HttpStatusCode.OK, _state.ReadFolder());
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/folder")
            {
                if (role == "remote")
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.Forbidden,
                        new ErrorBody("フォルダは PC 側で選んでください。"));
                    return;
                }

                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                object pathValue;
                var selectedPath = body.TryGetValue("path", out pathValue) ? pathValue as string : null;
                if (string.IsNullOrWhiteSpace(selectedPath))
                {
                    throw new ArgumentException("選ぶフォルダが指定されていません。");
                }

                await WriteJsonAsync(
                    context,
                    HttpStatusCode.OK,
                    _state.SelectFolder(selectedPath));
                return;
            }

            if (request.HttpMethod == "GET" && path == "/api/directories")
            {
                if (role == "remote")
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.Forbidden,
                        new ErrorBody("フォルダは PC 側で選んでください。"));
                    return;
                }

                var requestedPath = request.QueryString["path"];
                await WriteJsonAsync(
                    context,
                    HttpStatusCode.OK,
                    FolderCatalog.Browse(requestedPath));
                return;
            }

            Asset asset;
            if ((request.HttpMethod == "GET" || request.HttpMethod == "HEAD")
                && StaticAssets.TryGet(path, out asset))
            {
                await WriteAssetAsync(context, asset, request.HttpMethod == "HEAD");
                return;
            }

            await WriteJsonAsync(
                context,
                HttpStatusCode.NotFound,
                new ErrorBody("Not found"));
        }

        private object BuildStatus(string role)
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return new
            {
                Device = Environment.MachineName,
                Platform = PlatformInfo.Name,
                Role = role,
                Version = version == null ? "0.0.0" : version.ToString(3),
                InitialMode = _initialMode,
                Capabilities = new
                {
                    Word = PlatformInfo.IsWindows && WordIsRegistered(),
                    WindowsOcr = PlatformInfo.IsWindows && Environment.OSVersion.Version.Major >= 10,
                    Camera = "unchecked"
                }
            };
        }

        private static string GetRequestRole(HttpListenerRequest request)
        {
            var host = request.Headers["Host"];
            if (string.IsNullOrWhiteSpace(host))
            {
                return "remote";
            }

            host = host.Trim();
            if (host.Length > 0 && host[0] == '[')
            {
                var closingBracket = host.IndexOf(']');
                if (closingBracket > 0)
                {
                    host = host.Substring(1, closingBracket - 1);
                }
            }
            else
            {
                var colon = host.LastIndexOf(':');
                if (colon > 0 && host.IndexOf(':') == colon)
                {
                    host = host.Substring(0, colon);
                }
            }

            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
                ? "local"
                : "remote";
        }

        private static bool WordIsRegistered()
        {
            if (!PlatformInfo.IsWindows)
            {
                return false;
            }

            try
            {
                return Type.GetTypeFromProgID("Word.Application", false) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static async Task<byte[]> ReadBodyAsync(HttpListenerRequest request)
        {
            if (request.ContentLength64 > MaximumRequestBytes)
            {
                throw new ArgumentException("Request body is too large.");
            }

            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[4096];
                while (true)
                {
                    var read = await request.InputStream.ReadAsync(chunk, 0, chunk.Length);
                    if (read == 0)
                    {
                        return buffer.ToArray();
                    }

                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > MaximumRequestBytes)
                    {
                        throw new ArgumentException("Request body is too large.");
                    }
                }
            }
        }

        private static async Task WriteAssetAsync(
            HttpListenerContext context,
            Asset asset,
            bool headOnly)
        {
            var response = context.Response;
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = asset.ContentType;
            response.ContentLength64 = asset.Bytes.LongLength;
            response.Headers[HttpResponseHeader.CacheControl] = asset.Cacheable
                ? "public, max-age=3600"
                : "no-cache";

            if (!headOnly)
            {
                await response.OutputStream.WriteAsync(asset.Bytes, 0, asset.Bytes.Length);
            }
        }

        private static async Task WriteJsonAsync(
            HttpListenerContext context,
            HttpStatusCode status,
            object body)
        {
            var bytes = JsonCodec.Serialize(body);
            var response = context.Response;
            response.StatusCode = (int)status;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentEncoding = Encoding.UTF8;
            response.ContentLength64 = bytes.LongLength;
            response.Headers[HttpResponseHeader.CacheControl] = "no-store";
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }

        private static void AddSecurityHeaders(HttpListenerResponse response)
        {
            response.Headers["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
                "media-src 'self' blob:; connect-src 'self'; frame-ancestors 'none'; " +
                "base-uri 'none'; form-action 'self'";
            response.Headers["Permissions-Policy"] =
                "camera=(self), microphone=(), geolocation=()";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["X-Frame-Options"] = "DENY";
        }

        private sealed class ErrorBody
        {
            public ErrorBody(string error)
            {
                Error = error;
            }

            public string Error { get; private set; }
        }
    }
}
