using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Kartova.App.Services;

public enum UpdateStatus
{
    UpToDate,
    UpdateAvailable,
    NoReleases,
    Offline,
    RateLimited,
    Failed,
}

/// <summary>What a check found, or why it could not find out.</summary>
public sealed record UpdateCheckResult(UpdateStatus Status, string? LatestVersion = null, string? Url = null)
{
    public static UpdateCheckResult Of(UpdateStatus status) => new(status);
}

/// <summary>
/// Asks GitHub whether a newer release exists.
/// </summary>
/// <remarks>
/// <para>
/// Only ever runs when someone presses the button. Kartova does not phone home, does not check
/// on start-up, and does not download or install anything - a check reveals the machine's IP
/// address to GitHub, and that should be a decision rather than a side effect of opening a
/// dialog. The result is a sentence and a link; what to do about it stays with the user.
/// </para>
/// <para>
/// Responses are read with <see cref="JsonDocument"/> rather than deserialized into a type,
/// because the app publishes trimmed and reflection-based binding is exactly what trimming
/// breaks. Two string fields do not justify a source generator.
/// </para>
/// </remarks>
public static class UpdateChecker
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!TrySplitRepository(AppInfo.RepositoryUrl, out var owner, out var repo))
            return UpdateCheckResult.Of(UpdateStatus.Failed);

        try
        {
            // A single manual press per session does not warrant a long-lived static client,
            // and disposing it keeps no sockets or DNS entries alive for an app that may never
            // check again.
            using var http = new HttpClient { Timeout = Timeout };

            // GitHub rejects requests without a User-Agent outright.
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.Name, AppInfo.Version));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await http.GetAsync(
                $"https://api.github.com/repos/{owner}/{repo}/releases/latest", cancellationToken);

            // A repository with no published release answers 404. That is not a failure.
            if (response.StatusCode == HttpStatusCode.NotFound)
                return UpdateCheckResult.Of(UpdateStatus.NoReleases);

            // Unauthenticated callers get 60 requests an hour, shared by IP address.
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return UpdateCheckResult.Of(UpdateStatus.RateLimited);

            if (!response.IsSuccessStatusCode)
                return UpdateCheckResult.Of(UpdateStatus.Failed);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var root = json.RootElement;
            if (!root.TryGetProperty("tag_name", out var tagElement)) return UpdateCheckResult.Of(UpdateStatus.Failed);

            var tag = tagElement.GetString();
            if (string.IsNullOrWhiteSpace(tag)) return UpdateCheckResult.Of(UpdateStatus.Failed);

            var url = root.TryGetProperty("html_url", out var urlElement)
                ? urlElement.GetString()
                : null;

            var status = CompareVersions(tag, AppInfo.Version);
            return new UpdateCheckResult(status, NormalizeTag(tag), url ?? AppInfo.ReleasesUrl);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // No network, DNS failure, or the ten seconds ran out.
            return UpdateCheckResult.Of(UpdateStatus.Offline);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return UpdateCheckResult.Of(UpdateStatus.Failed);
        }
    }

    /// <summary>
    /// Decides whether a release tag is newer than the running version.
    /// </summary>
    /// <remarks>
    /// Pure and public so it can be tested without a network. The failure direction matters:
    /// a tag that cannot be read reports <see cref="UpdateStatus.Failed"/> rather than either
    /// certainty, because "you are up to date" is a claim, not a default.
    /// </remarks>
    public static UpdateStatus CompareVersions(string latestTag, string currentVersion)
    {
        if (!Version.TryParse(NormalizeTag(latestTag), out var latest) ||
            !Version.TryParse(NormalizeTag(currentVersion), out var current))
        {
            return UpdateStatus.Failed;
        }

        return latest > current ? UpdateStatus.UpdateAvailable : UpdateStatus.UpToDate;
    }

    /// <summary>Turns <c>v1.2.3</c> or <c>1.2.3-beta.1</c> into something Version can parse.</summary>
    private static string NormalizeTag(string tag)
    {
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        var suffix = text.IndexOfAny(['-', '+']);
        if (suffix >= 0) text = text[..suffix];

        return text;
    }

    /// <summary>Pulls owner and repository out of the project URL, so a rename carries over.</summary>
    private static bool TrySplitRepository(string repositoryUrl, out string owner, out string repo)
    {
        owner = repo = string.Empty;

        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri)) return false;

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return false;

        owner = segments[0];
        repo = segments[1];
        return true;
    }
}
