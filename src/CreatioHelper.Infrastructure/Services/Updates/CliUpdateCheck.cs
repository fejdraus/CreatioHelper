using System.Net.Http;
using System.Reflection;
using System.Text.Json.Nodes;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Enums;
using NuGet.Versioning;

namespace CreatioHelper.Infrastructure.Services.Updates;

public class CliUpdateCheck : ICliUpdateCheck
{
    private const string TagPrefix = "cli-v";
    private const string RepoOwner = "fejdraus";
    private const string RepoName = "CreatioHelper";
    private const string UserAgent = "CreatioHelper-Cli";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private readonly IHttpClientFactory _httpFactory;

    public CliUpdateCheck(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
        CurrentVersion = ReadCurrentVersion();
    }

    public string CurrentVersion { get; }

    public async Task<string?> GetNewerVersionAsync(UpdateChannel channel, CancellationToken cancellationToken = default)
    {
        if (!NuGetVersion.TryParse(CurrentVersion, out var current) || current is null)
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            var latest = await FetchLatestAsync(channel, timeout.Token).ConfigureAwait(false);
            if (latest is null || latest <= current)
            {
                return null;
            }

            return latest.ToNormalizedString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<NuGetVersion?> FetchLatestAsync(UpdateChannel channel, CancellationToken cancellationToken)
    {
        var http = _httpFactory.CreateClient(nameof(CliUpdateCheck));
        http.Timeout = RequestTimeout;
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=20";
        var json = await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);

        if (JsonNode.Parse(json) is not JsonArray releases)
        {
            return null;
        }

        NuGetVersion? best = null;

        foreach (var node in releases)
        {
            if (node is not JsonObject release)
            {
                continue;
            }

            if (release["draft"]?.GetValue<bool>() == true)
            {
                continue;
            }

            var isPrerelease = release["prerelease"]?.GetValue<bool>() == true;
            if (isPrerelease && channel != UpdateChannel.Beta)
            {
                continue;
            }

            var tag = release["tag_name"]?.GetValue<string>();
            if (!TryParseCliVersion(tag, out var version))
            {
                continue;
            }

            if (version.IsPrerelease && channel != UpdateChannel.Beta)
            {
                continue;
            }

            if (best is null || version > best)
            {
                best = version;
            }
        }

        return best;
    }

    public static bool TryParseCliVersion(string? tag, out NuGetVersion version)
    {
        version = null!;

        if (string.IsNullOrEmpty(tag) || !tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (NuGetVersion.TryParse(tag[TagPrefix.Length..], out var parsed) && parsed is not null)
        {
            version = parsed;
            return true;
        }

        return false;
    }

    private static string ReadCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
