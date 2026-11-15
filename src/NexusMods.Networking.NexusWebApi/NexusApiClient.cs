using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.DTOs;
using NexusMods.Abstractions.NexusWebApi.DTOs.Interfaces;
using NexusMods.Abstractions.NexusWebApi.DTOs.OAuth;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Networking.NexusWebApi;

/// <summary>
/// Provides an easy to use access point for the Nexus API; start your journey here.
/// </summary>
public class NexusApiClient : INexusApiClient
{
    private readonly ILogger<NexusApiClient> _logger;
    private readonly IHttpMessageFactory _factory;
    private readonly HttpClient _httpClient;
    private readonly IGraphQlClient _graphQlClient;

    // Dedicated client for www.nexusmods.com website endpoints.
    // Created with no default headers so _httpClient's User-Agent doesn't bleed through.
    private static readonly HttpClient _websiteHttpClient = new();

    // Serialize curl calls to the Nexus Mods GenerateDownloadUrl endpoint.
    // Sending many parallel requests triggers Cloudflare's bot detection (HTML response instead of JSON).
    private static readonly SemaphoreSlim _curlSemaphore = new(initialCount: 1, maxCount: 1);

    /// <summary>
    /// Constructor.
    /// </summary>
    public NexusApiClient(
        ILogger<NexusApiClient> logger,
        IHttpMessageFactory factory,
        HttpClient httpClient,
        IGraphQlClient graphQlClient)
    {
        _logger = logger;
        _factory = factory;
        _httpClient = httpClient;
        _graphQlClient = graphQlClient;
    }

    /// <summary>
    /// Retrieves the current user information when logged in via APIKEY
    /// </summary>
    /// <param name="token">Can be used to cancel this task.</param>
    public async Task<Response<ValidateInfo>> Validate(CancellationToken token = default)
    {
        var msg = await _factory.Create(HttpMethod.Get, new Uri($"{ClientConfig.LegacyApiEndpoint}/users/validate.json"));
        return await SendAsync<ValidateInfo>(msg, token);
    }

    /// <summary>
    /// Retrieves information about the current user when logged in via OAuth.
    /// </summary>
    public async Task<Response<OAuthUserInfo>> GetOAuthUserInfo(CancellationToken cancellationToken = default)
    {
        var msg = await _factory.Create(HttpMethod.Get, new Uri($"{ClientConfig.UsersUrl}/oauth/userinfo"));
        return await SendAsync<OAuthUserInfo>(msg, cancellationToken);
    }

    /// <summary>
    /// Generates download links for a given game.
    /// [Premium only endpoint, use other overload for free users].
    /// </summary>
    /// <param name="domain">
    ///     Unique, human friendly name for the game used in URLs. e.g. 'skyrim'
    ///     You can find this in <see cref="GameInfo.DomainName"/>.
    /// </param>
    /// <param name="modId">
    ///    An individual identifier for the mod. Unique per game.
    /// </param>
    /// <param name="fileId">
    ///    Unique ID for a game file hosted on a mod page; unique per game.
    /// </param>
    /// <param name="token">Token used to cancel the task.</param>
    /// <returns> List of available download links. </returns>
    /// <remarks>
    ///    Currently available for Premium users only; with some minor exceptions [nxm links].
    /// </remarks>
    public async Task<Response<DownloadLink[]>> DownloadLinksAsync(string domain, ModId modId, FileId fileId, CancellationToken token = default)
    {
        var msg = await _factory.Create(HttpMethod.Get, new Uri(
            $"{ClientConfig.LegacyApiEndpoint}/games/{domain}/mods/{modId}/files/{fileId}/download_link.json"));

        return await SendAsyncArray<DownloadLink>(msg, token);
    }

    /// <summary>
    /// Generates download links for a given game.
    /// </summary>
    /// <param name="domain">
    ///     Unique, human friendly name for the game used in URLs. e.g. 'skyrim'
    ///     You can find this in <see cref="GameInfo.DomainName"/>.
    /// </param>
    /// <param name="modId">
    ///    An individual identifier for the mod. Unique per game.
    /// </param>
    /// <param name="fileId">
    ///    Unique ID for a game file hosted on a mod page; unique per game.
    /// </param>
    /// <param name="expireTime">Time before key expires.</param>
    /// <param name="token">Token used to cancel the task.</param>
    /// <param name="key">Key required for free user to download from the site.</param>
    /// <returns> List of available download links. </returns>
    /// <remarks>
    ///    Currently available for Premium users only; with some minor exceptions [nxm links].
    /// </remarks>
    public async Task<Response<DownloadLink[]>> DownloadLinksAsync(string domain, ModId modId, FileId fileId, NXMKey key, DateTime expireTime, CancellationToken token = default)
    {
        var msg = await _factory.Create(HttpMethod.Get, new Uri($"{ClientConfig.LegacyApiEndpoint}/games/{domain}/mods/{modId}/files/{fileId}/download_link.json?key={key}&expires={new DateTimeOffset(expireTime).ToUnixTimeSeconds()}"));
        return await SendAsyncArray<DownloadLink>(msg, token);
    }

    /// <summary>
    /// Get the download links for a collection.
    /// </summary>
    public async Task<Response<CollectionDownloadLinks>> CollectionDownloadLinksAsync(CollectionSlug slug, RevisionNumber revision, bool viewAdultContent = true, CancellationToken token = default)
    {
        var result = await _graphQlClient.QueryCollectionRevisionDownloadLink(slug, revision, cancellationToken: token);
        var link = result.AssertHasData();

        var msg = await _factory.Create(HttpMethod.Get, new Uri($"{ClientConfig.ApiUrl}{link}"));
        return await SendAsync<CollectionDownloadLinks>(msg, token);
    }

    /// <summary>
    /// Retrieves a list of all recently updated mods within a specified time period.
    /// </summary>
    /// <param name="domain">
    ///     Unique, human friendly name for the game used in URLs. e.g. 'skyrim'
    ///     You can find this in <see cref="GameInfo.DomainName"/>.
    /// </param>
    /// <param name="time">Time-frame within which to search for updates.</param>
    /// <param name="token">Token used to cancel the task.</param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public async Task<Response<ModUpdate[]>> ModUpdatesAsync(string domain, PastTime time, CancellationToken token = default)
    {
        var timeString = time switch
        {
            PastTime.Day => "1d",
            PastTime.Week => "1w",
            PastTime.Month => "1m",
            _ => throw new ArgumentOutOfRangeException(nameof(time), time, null)
        };

        var msg = await _factory.Create(HttpMethod.Get, new Uri($"{ClientConfig.LegacyApiEndpoint}/games/{domain}/mods/updated.json?period={timeString}"));
        return await SendAsyncArray<ModUpdate>(msg, token: token);
    }

    private async Task<Response<T>> SendAsync<T>(HttpRequestMessage message,
        CancellationToken token = default) where T : IJsonSerializable<T>
    {
        return await SendAsync(message, T.GetTypeInfo(), token);
    }

    private async Task<Response<T[]>> SendAsyncArray<T>(HttpRequestMessage message,
        CancellationToken token = default) where T : IJsonArraySerializable<T>
    {
        return await SendAsync(message, T.GetArrayTypeInfo(), token);
    }

    private async Task<Response<T>> SendAsync<T>(HttpRequestMessage message, JsonTypeInfo<T> typeInfo,
        CancellationToken token = default)
    {
        using var response = await _httpClient.SendAsync(message, token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(response.ReasonPhrase, null, response.StatusCode);

        var data = await response.Content.ReadFromJsonAsync(typeInfo, token);
        return new Response<T>
        {
            Data = data!,
            Metadata = ParseHeaders(response),
            StatusCode = response.StatusCode,
        };
    }

    private ResponseMetadata ParseHeaders(HttpResponseMessage result)
    {
        var metaData = ResponseMetadata.FromHttpHeaders(result);

        // A missing x-rl-* header parses as 0, so only log a limit the server actually sent
        var endpoint = result.RequestMessage?.RequestUri?.GetLeftPart(UriPartial.Path);
        if (result.Headers.Contains("x-rl-daily-remaining") || result.Headers.Contains("x-rl-hourly-remaining"))
            _logger.LogInformation("Nexus API call finished: {Endpoint} {Runtime} - Remaining Limit: {RemainingLimit}",
                endpoint, metaData.Runtime, Math.Max(metaData.DailyRemaining, metaData.HourlyRemaining));
        else
            _logger.LogInformation("Nexus API call finished: {Endpoint} {Runtime} (no rate limit headers)", endpoint, metaData.Runtime);

        return metaData;
    }

    /// <inheritdoc/>
    public async Task<Uri?> GenerateDirectDownloadUrlAsync(FileId fileId, NexusModsGameId gameId, CancellationToken cancellationToken = default)
    {
        // POST https://www.nexusmods.com/Core/Libs/Common/Managers/Downloads?GenerateDownloadUrl
        // This endpoint uses session cookies (not Bearer token) to authenticate free/supporter users.
        // We read the cookies from the Firefox profile on Linux.
        var cookies = FirefoxCookieReader.TryGetNexusModsCookieHeader(_logger);
        if (cookies is null)
        {
            _logger.LogWarning("No hay cookies de nexusmods.com en Firefox (archivo {FileId}): iniciá sesión en nexusmods.com en Firefox", fileId);
            throw new NexusWebsiteSessionRequiredException();
        }

        // NOTE: .NET's HttpClient has a different TLS fingerprint than Firefox, and Cloudflare's bot detection
        // on nexusmods.com returns a fake "successful" response with an empty CDN path (cf-files.nexusmods.com/cdn///)
        // when it detects non-browser TLS fingerprints. Using curl (which has a different, allowed fingerprint) works reliably.
        var jsonResponse = await CallCurlGenerateDownloadUrlAsync(cookies, fileId.Value, gameId.Value, cancellationToken);
        if (jsonResponse is null)
            return null;

        _logger.LogDebug("Website GenerateDownloadUrl response for file {FileId}: {Response}", fileId, jsonResponse);

        try
        {
            var uri = ParseGenerateDownloadUrlResponse(jsonResponse);
            if (uri is null)
                _logger.LogWarning("No se pudo sacar el link de descarga del archivo {FileId} (página de desafío de Cloudflare u otra respuesta)", fileId);
            return uri;
        }
        catch (NexusWebsiteSessionRequiredException)
        {
            _logger.LogWarning("Nexus Mods devolvió una lista vacía para el archivo {FileId}: la sesión de nexusmods.com en Firefox no está iniciada o venció", fileId);
            throw;
        }
    }

    /// <summary>
    /// Reads the CDN URL out of a GenerateDownloadUrl response: <c>{"url":"..."}</c> for free/supporter users,
    /// a mirror list <c>[{"name":"...","URI":"..."}]</c> for premium. <c>[]</c> is what an anonymous session gets
    /// and throws <see cref="NexusWebsiteSessionRequiredException"/>; anything else (Cloudflare's challenge page) is null.
    /// </summary>
    internal static Uri? ParseGenerateDownloadUrlResponse(string json)
    {
        System.Text.Json.JsonDocument doc;
        try
        {
            doc = System.Text.Json.JsonDocument.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0) throw new NexusWebsiteSessionRequiredException();
                root = root[0];
            }

            if (root.ValueKind == System.Text.Json.JsonValueKind.Object
                && (root.TryGetProperty("URI", out var urlEl) || root.TryGetProperty("url", out urlEl))
                && urlEl.ValueKind == System.Text.Json.JsonValueKind.String
                && Uri.TryCreate(urlEl.GetString(), UriKind.Absolute, out var cdnUri))
                return cdnUri;

            return null;
        }
    }

    /// <summary>
    /// Calls the Nexus Mods GenerateDownloadUrl endpoint via curl to avoid .NET TLS fingerprint detection.
    /// Returns the raw response body (JSON, or Cloudflare's challenge page once the retries run out), or null on failure.
    /// </summary>
    private async Task<string?> CallCurlGenerateDownloadUrlAsync(string cookies, uint fileId, uint gameId, CancellationToken cancellationToken)
    {
        // Serialize all curl calls to avoid Cloudflare rate-limiting (parallel requests → HTML challenge page);
        // the throttle also spaces them out and retries the challenge page.
        await _curlSemaphore.WaitAsync(cancellationToken);
        try
        {
            return await GenerateDownloadUrlThrottle.Default.RunAsync(ct => RunCurlOnceAsync(cookies, fileId, gameId, ct), cancellationToken);
        }
        finally
        {
            _curlSemaphore.Release();
        }
    }

    private async Task<string?> RunCurlOnceAsync(string cookies, uint fileId, uint gameId, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "curl",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            var args = process.StartInfo.ArgumentList;
            args.Add("-s");
            args.Add("--max-time"); args.Add("30");
            args.Add("-X"); args.Add("POST");
            args.Add("https://www.nexusmods.com/Core/Libs/Common/Managers/Downloads?GenerateDownloadUrl");
            // The session cookie goes through stdin (`-H @-`, curl 7.55+): in argv any local user sees it with `ps`
            args.Add("-H"); args.Add("@-");
            args.Add("-H"); args.Add("User-Agent: Mozilla/5.0 (X11; Linux x86_64; rv:135.0) Gecko/20100101 Firefox/135.0");
            args.Add("-H"); args.Add("X-Requested-With: XMLHttpRequest");
            args.Add("--data"); args.Add($"fid={fileId}&game_id={gameId}");

            process.Start();
            await process.StandardInput.WriteLineAsync($"Cookie: {cookies}".AsMemory(), cancellationToken);
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                var err = await process.StandardError.ReadToEndAsync(cancellationToken);
                _logger.LogDebug("curl failed with exit code {Code} for file {FileId}: {Error}", process.ExitCode, fileId, err);
                return null;
            }

            return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to call curl for GenerateDownloadUrl (file {FileId})", fileId);
            return null;
        }
    }
}
