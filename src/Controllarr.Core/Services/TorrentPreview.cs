using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Controllarr.Core.Services;

/// <summary>A bounded, temporary loopback stream with an unguessable capability URL, not a public media server.</summary>
public sealed class TorrentPreview : IAsyncDisposable
{
    private readonly WebApplication _server;
    private readonly Func<Task> _onClosed;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private int _closed;
    public string Url { get; private set; } = "";
    public string FileName { get; }

    private TorrentPreview(WebApplication server, string fileName, Func<Task> onClosed)
    { _server = server; FileName = fileName; _onClosed = onClosed; }

    public static async Task<TorrentPreview> StartAsync(string fileName, Func<CancellationToken, Task<Stream>> openStream, Func<Task> onClosed)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); // Never log the capability URL.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Limits.MaxConcurrentConnections = 4;
            options.Limits.MaxRequestBodySize = 0;
        });
        var server = builder.Build();
        var preview = new TorrentPreview(server, Path.GetFileName(fileName), onClosed);
        string route = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) + "/stream";
        server.MapMethods(route, new[] { "GET", "HEAD" }, async (HttpContext context) =>
        {
            if (context.Connection.RemoteIpAddress == null || !IPAddress.IsLoopback(context.Connection.RemoteIpAddress))
            { context.Response.StatusCode = 403; return; }
            if (!await preview._readGate.WaitAsync(0, context.RequestAborted))
            { context.Response.StatusCode = 409; return; }
            try
            {
                context.Response.Headers.CacheControl = "no-store";
                var stream = await openStream(context.RequestAborted);
                await Results.Stream(stream, ContentType(fileName), enableRangeProcessing: true).ExecuteAsync(context);
            }
            catch (InvalidOperationException) when (!context.Response.HasStarted) { context.Response.StatusCode = 409; }
            catch (OperationCanceledException) { }
            finally { preview._readGate.Release(); }
        });
        try
        {
            await server.StartAsync();
            preview.Url = server.Urls.Single().TrimEnd('/') + route;
            return preview;
        }
        catch { await server.DisposeAsync(); throw; }
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4", ".webm" => "video/webm", ".mkv" => "video/x-matroska",
        ".mp3" => "audio/mpeg", ".ogg" => "audio/ogg", ".flac" => "audio/flac", ".wav" => "audio/wav",
        _ => "application/octet-stream"
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _server.StopAsync(timeout.Token);
        }
        finally
        {
            await _server.DisposeAsync();
            await _onClosed();
        }
    }
}
