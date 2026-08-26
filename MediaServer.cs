using System.Net;
using System.Net.Sockets;
using System.Text;

#if ANDROID
using Android.Content;
using Android.Provider;
#endif

namespace SLVZ.LocalMediaServer;

/// <summary>
/// A minimal local HTTP server (loopback-only) that streams files identified by a
/// platform-specific source identifier — an Android content:// Uri string on Android,
/// or a plain absolute file path on Windows — to a local client (typically a WebView)
/// over TCP, with HTTP range support for video/audio seeking.
/// </summary>
public static partial class MediaServer
{
    private static TcpListener? _listener;
    private static int _port;
    private static bool _isRunning;

    private const string PathPrefix = "slvz/";

    /// <summary>
    /// True while the server is actively listening for connections.
    /// This automatically flips to false whenever the accept loop exits for ANY reason —
    /// an explicit Stop() call, or an unexpected exception tearing the listener down —
    /// so callers can safely trust this value instead of assuming Start() guarantees uptime.
    /// </summary>
    public static bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            StatusChanged?.Invoke(value);
        }
    }

    /// <summary>
    /// Fires whenever the server transitions between running and stopped.
    /// Subscribe to this if the UI needs to react live (e.g. show a "server disconnected"
    /// badge) instead of polling IsRunning.
    /// </summary>
    public static event Action<bool>? StatusChanged;

    public static string Url => IsRunning ? $"http://127.0.0.1:{_port}/{PathPrefix}" : "";

    public static void Start()
    {
        if (IsRunning) return;

        _port = FreePort();
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        IsRunning = true;

        _ = Task.Run(AcceptLoop);
    }

    public static void Stop()
    {
        // Set this first so the accept loop's while-condition drops out even if
        // Stop() races with a pending AcceptTcpClientAsync() call.
        IsRunning = false;
        _listener?.Stop();
    }

    private static async Task AcceptLoop()
    {
        try
        {
            while (IsRunning)
            {
                TcpClient client;
                try
                {
                    client = await _listener!.AcceptTcpClientAsync();
                }
                catch (ObjectDisposedException)
                {
                    // Listener was disposed by Stop() — normal shutdown, exit quietly.
                    break;
                }
                catch (SocketException)
                {
                    // Listener was stopped mid-accept — normal shutdown, exit quietly.
                    break;
                }

                _ = HandleClient(client);
            }
        }
        catch (Exception)
        {
            // Any other unexpected failure tears the server down. The finally block
            // below makes sure IsRunning reflects that instead of leaving stale state.
        }
        finally
        {
            // Whatever ended the loop — a deliberate Stop() or a crash — the server is
            // no longer actually serving, so make that visible to callers/subscribers.
            IsRunning = false;
        }
    }

    private static async Task HandleClient(TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

            var requestLine = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(requestLine)) return;

            // Expected format: "GET /slvz/<url-encoded-source-id> HTTP/1.1"
            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return;

            var path = parts[1].TrimStart('/');
            var sourceId = ExtractSourceId(path);
            if (sourceId == null)
            {
                await WriteNotFound(stream);
                return;
            }

            // Drain the remaining request headers, keeping only the one we care about.
            string? rangeHeader = null;
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
            {
                if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                    rangeHeader = line;
            }

            var fileLength = GetFileSize(sourceId);
            var fileName = GetFileName(sourceId);

            using var source = OpenSource(sourceId);
            if (source == null || fileLength < 0)
            {
                // Source is missing, was deleted, or permission was revoked since the
                // caller last had access to it (common with SAF/content Uris).
                await WriteNotFound(stream);
                return;
            }

            long start = 0;
            long end = fileLength - 1;
            bool isPartial = false;

            if (rangeHeader != null && TryParseRange(rangeHeader, fileLength, out var parsedStart, out var parsedEnd))
            {
                start = parsedStart;
                end = parsedEnd;
                isPartial = true;
            }

            long length = end - start + 1;
            string status = isPartial ? "HTTP/1.1 206 Partial Content" : "HTTP/1.1 200 OK";

            // No caching: filenames can be reused with different content (e.g. edited
            // photos), so the client (WebView) must always re-request instead of serving
            // a stale cached copy.
            string headers =
                $"{status}\r\n" +
                $"Content-Type: {ContentTypeHelper.GetContentType(fileName)}\r\n" +
                "Accept-Ranges: bytes\r\n" +
                $"Content-Length: {length}\r\n" +
                (isPartial ? $"Content-Range: bytes {start}-{end}/{fileLength}\r\n" : "") +
                "\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));

            int bufferSize = Environment.ProcessorCount > 4 ? 128 * 1024 : 64 * 1024;
            source.Seek(start, SeekOrigin.Begin);

            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(bufferSize);
            long remaining = length;
            try
            {
                int read;
                while (remaining > 0 &&
                       (read = await source.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining))) > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, read));
                    remaining -= read;
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (IOException)
        {
            // Client disconnected mid-stream (e.g. user closed the page while an
            // image/video was loading) — expected, nothing to do.
        }
        catch (SocketException)
        {
            // Same as above, at the socket level.
        }
        finally
        {
            client.Close();
        }
    }

    private static int FreePort()
    {
        // Bind to port 0 to let the OS hand back a currently-free ephemeral port,
        // then release it immediately so the real listener below can bind to it.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Strips the "slvz/" path prefix and URL-decodes the remainder to recover the
    /// original source identifier (an Android content:// Uri string, or a Windows file
    /// path). Returns null if the request path doesn't match the expected prefix.
    /// </summary>
    private static string? ExtractSourceId(string path)
    {
        if (!path.StartsWith(PathPrefix, StringComparison.Ordinal))
            return null;

        var encoded = path[PathPrefix.Length..];
        return Uri.UnescapeDataString(encoded);
    }

    private static async Task WriteNotFound(NetworkStream stream)
    {
        var msg = "HTTP/1.1 404 Not Found\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(msg));
    }

    /// <summary>
    /// Parses a "Range: bytes=start-end" header, including open-ended ("bytes=1000-")
    /// and suffix ("bytes=-500", meaning "last 500 bytes") forms.
    /// Returns false (and the caller falls back to a full 200 response) if the header
    /// is malformed instead of throwing.
    /// </summary>
    private static bool TryParseRange(string rangeHeader, long fileLength, out long start, out long end)
    {
        start = 0;
        end = fileLength - 1;

        var eqIndex = rangeHeader.IndexOf('=');
        if (eqIndex < 0) return false;

        var value = rangeHeader[(eqIndex + 1)..].Trim();
        var rangeParts = value.Split('-');
        if (rangeParts.Length != 2) return false;

        var startStr = rangeParts[0].Trim();
        var endStr = rangeParts[1].Trim();

        if (string.IsNullOrEmpty(startStr))
        {
            // Suffix range: "bytes=-500" => last 500 bytes.
            if (!long.TryParse(endStr, out var suffixLength) || suffixLength <= 0)
                return false;

            start = Math.Max(0, fileLength - suffixLength);
            end = fileLength - 1;
            return true;
        }

        if (!long.TryParse(startStr, out start))
            return false;

        if (!string.IsNullOrEmpty(endStr))
        {
            if (!long.TryParse(endStr, out end))
                return false;
        }
        else
        {
            end = fileLength - 1;
        }

        if (start < 0 || end < start || start >= fileLength)
            return false;

        if (end >= fileLength)
            end = fileLength - 1;

        return true;
    }

    // ========================= Platform-specific source access =========================
    // Everything above this line (TCP handling, range parsing, status tracking) is fully
    // shared between Android and Windows. Only opening/inspecting the underlying file
    // differs per platform, so that's the only part behind #if blocks below.

#if ANDROID
    // Checked on the raw string, before any Uri.Parse — a plain absolute path can
    // parse into a Uri with a misleading/empty Scheme, so a direct prefix check here
    // is the reliable way to tell "content://..." apart from a filesystem path.
    private static bool IsContentUri(string sourceId) =>
        sourceId.StartsWith("content://", StringComparison.OrdinalIgnoreCase);

    private static Stream? OpenSource(string sourceId)
    {
        if (IsContentUri(sourceId))
        {
            var uri = Android.Net.Uri.Parse(sourceId);
            try
            {
                return Platform.AppContext.ContentResolver?.OpenInputStream(uri);
            }
            catch (Java.IO.FileNotFoundException)
            {
                // Uri no longer resolves — file deleted or permission revoked since the
                // caller obtained the Uri.
                return null;
            }
        }

        // Plain absolute filesystem path (e.g. a file the app owns directly, not
        // obtained through SAF/MediaStore) — no ContentResolver involved.
        return File.Exists(sourceId) ? File.OpenRead(sourceId) : null;
    }

    private static long GetFileSize(string sourceId)
    {
        if (IsContentUri(sourceId))
        {
            var uri = Android.Net.Uri.Parse(sourceId);
            var resolver = Platform.AppContext.ContentResolver;

            using var cursor = resolver?.Query(uri, new[] { OpenableColumns.Size }, null, null, null);
            if (cursor != null && cursor.MoveToFirst())
            {
                int sizeIndex = cursor.GetColumnIndex(OpenableColumns.Size);
                if (sizeIndex >= 0 && !cursor.IsNull(sizeIndex))
                    return cursor.GetLong(sizeIndex);
            }

            return -1; // unknown / not found
        }

        var info = new FileInfo(sourceId);
        return info.Exists ? info.Length : -1;
    }

    private static string GetFileName(string sourceId)
    {
        if (IsContentUri(sourceId))
        {
            var uri = Android.Net.Uri.Parse(sourceId);
            var resolver = Platform.AppContext.ContentResolver;

            using var cursor = resolver?.Query(uri, null, null, null, null);
            if (cursor != null && cursor.MoveToFirst())
            {
                int index = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
                if (index >= 0)
                {
                    var name = cursor.GetString(index);
                    if (name != null) return name;
                }
            }

            return Path.GetFileName(uri.Path) ?? "file";
        }

        return Path.GetFileName(sourceId);
    }
#elif WINDOWS
    private static Stream? OpenSource(string sourceId)
    {
        // On Windows the source identifier is a plain absolute file path, already
        // URL-decoded by ExtractSourceId — no content-resolver step is needed.
        return File.Exists(sourceId) ? File.OpenRead(sourceId) : null;
    }

    private static long GetFileSize(string sourceId)
    {
        var info = new FileInfo(sourceId);
        return info.Exists ? info.Length : -1;
    }

    private static string GetFileName(string sourceId)
    {
        return Path.GetFileName(sourceId);
    }
#endif
}


public static partial class MediaServer
{
    /// <summary>
    /// Like Path.Combine, but always joins with '/' regardless of OS —
    /// useful for building URL paths where Path.Combine's OS-dependent
    /// separator (e.g. '\' on Windows) would break things.
    /// </summary>
    public static string Combine(params string[] segments)
    {
        var parts = segments
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s.Trim('/'));

        string _url = string.Join('/', parts);

        return Url + Uri.EscapeDataString(_url);
    }
}