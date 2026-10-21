using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ComponentAce.Compression.Libs.zlib;
using Microsoft.Extensions.Logging;
using SPTarkov.Core.Runtime;
using SPTarkov.Core.SPT;
using SPTarkov.Core.SPT.Responses;

[assembly: InternalsVisibleTo("SessionFixtures")]

namespace SPTarkov.Core.Helpers;

public class HttpHelper
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpHelper> _logger;
    private readonly StateHelper _stateHelper;

    public HttpHelper(ILogger<HttpHelper> logger, StateHelper stateHelper)
        : this(logger, stateHelper, CreateHttpClient()) { }

    // Internal injection keeps the public DI constructor unchanged and allows real wire-format fixtures without sockets.
    internal HttpHelper(ILogger<HttpHelper> logger, StateHelper stateHelper, HttpClient httpClient)
    {
        _logger = logger;
        _stateHelper = stateHelper;
        _httpClient = httpClient;
    }

    private static readonly HttpRequestOptionsKey<string> CertificatePin = new("ServerCertificateSha256");

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler();
        handler.AllowAutoRedirect = false;
        handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
        {
            request.Options.TryGetValue(CertificatePin, out var pin);
            return ValidateServerCertificate(certificate, errors, pin);
        };
        handler.UseCookies = false;

        return new HttpClient(handler)
        {
            DefaultRequestVersion = new Version(3, 0),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    internal static bool ValidateServerCertificate(X509Certificate2? certificate, SslPolicyErrors errors, string? pin) =>
        pin is null
            ? certificate is not null && errors == SslPolicyErrors.None
            : certificate is not null
                && ServerEndpoint.IsFingerprint(pin)
                && Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)) == pin;

    private HttpRequestMessage GameRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, BuildGameUrl(url));
        if (_stateHelper.VerifiedBackendTlsSha256 is { } pin)
            request.Options.Set(CertificatePin, pin);
        return request;
    }

    private string BuildGameUrl(string url)
    {
        var origin = ServerEndpoint.Normalize(_stateHelper.SelectedServer?.IpAddress ?? "");
        if (!url.StartsWith('/') || url.StartsWith("//") || url.Contains('\\') || url.Any(char.IsControl))
            throw new InvalidOperationException("The server returned an invalid request path.");
        var target = new Uri(new Uri(origin), url);
        if (ServerEndpoint.Normalize(target.GetLeftPart(UriPartial.Authority)) != origin)
            throw new InvalidOperationException("The request must stay on the selected server.");
        return target.AbsoluteUri;
    }

    public async Task<T?> GameServerGet<T>(string url, CancellationToken token)
    {
        _logger.LogDebug("Get: {Url}", url);

        using var request = GameRequest(HttpMethod.Get, url);
        using var task = await _httpClient.SendAsync(request, token);
        return JsonSerializer.Deserialize<T>(ReadJson(await task.Content.ReadAsByteArrayAsync(token)));
    }

    /// <summary>
    /// Pings a specific server address, independent of the currently-selected server. Any failure (unreachable, bad response,
    /// cancellation) reads as offline.
    /// </summary>
    public async Task<bool> PingServerAsync(string ipAddress, CancellationToken token, string? certificateSha256 = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ServerEndpoint.Normalize(ipAddress) + Urls.Ping);
            if (
                certificateSha256 is null
                && _stateHelper.SelectedServer is { } selected
                && ServerEndpoint.Normalize(selected.IpAddress) == ServerEndpoint.Normalize(ipAddress)
            )
                certificateSha256 = _stateHelper.VerifiedBackendTlsSha256;
            if (certificateSha256 is not null)
                request.Options.Set(CertificatePin, certificateSha256);
            using var response = await _httpClient.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var ping = JsonSerializer.Deserialize<SPTPingResponse>(ReadJson(await response.Content.ReadAsByteArrayAsync(token)));
            return ping?.Response == "Pong!";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PingServerAsync failed for {IpAddress}", ipAddress);
            return false;
        }
    }

    /// <summary>
    /// Streams a file straight to disk. Unlike <see cref="GameServerGet{T}"/> the body is not zlib-wrapped,
    /// so it must not be run through SimpleZlib.
    /// </summary>
    /// <param name="url">Server-relative path to fetch</param>
    /// <param name="destinationPath">Final path to write to</param>
    /// <param name="onProgress">Called with (bytesWritten, totalBytes); totalBytes is -1 when unknown</param>
    /// <param name="token">Cancels the download and removes the partial file</param>
    public async Task DownloadFileAsync(string url, string destinationPath, Action<long, long>? onProgress, CancellationToken token)
    {
        _logger.LogDebug("Download: {Url}", url);

        var directory = Path.GetDirectoryName(destinationPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write to a sibling temp file and move on success, so an interrupted download can never be
        // mistaken for a complete bundle
        var tempPath = destinationPath + ".part";

        try
        {
            using var request = GameRequest(HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;

            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (
                var destination = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true)
            )
            {
                var buffer = new byte[81920];
                long written = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), token);
                    written += read;
                    onProgress?.Invoke(written, totalBytes);
                }
            }

            File.Move(tempPath, destinationPath, true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    public async Task<T?> GameServerPut<T>(string url, object request, CancellationToken token)
    {
        _logger.LogDebug("Put: {Url}", url);

        var content = new ByteArrayContent(SimpleZlib.CompressToBytes(JsonSerializer.Serialize(request), zlibConst.Z_BEST_COMPRESSION));

        using var message = GameRequest(HttpMethod.Put, url);
        message.Content = content;
        using var task = await _httpClient.SendAsync(message, token);

        return JsonSerializer.Deserialize<T>(ReadJson(await task.Content.ReadAsByteArrayAsync(token)));
    }

    /// <summary>
    /// Servers up to 4.1 zlib the launcher responses, 5.0 sends them plain. The zlib header decides.
    /// </summary>
    private static string ReadJson(byte[] body)
    {
        var zlib = body.Length >= 2 && (body[0] & 0x0F) == 8 && ((body[0] << 8) | body[1]) % 31 == 0;
        return zlib ? SimpleZlib.Decompress(body) : Encoding.UTF8.GetString(body);
    }
}
