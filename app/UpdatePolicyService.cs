using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PitLaunch;

internal enum UpdatePolicyState
{
    NotConfigured,
    Satisfied,
    Required,
    Unavailable,
    Invalid
}

internal sealed record UpdatePolicyResult(
    UpdatePolicyState State,
    string? MinimumVersion,
    string Message,
    string DownloadUrl,
    string Diagnostic = "")
{
    public bool IsRequired => State == UpdatePolicyState.Required;
    public bool WasChecked => State is UpdatePolicyState.Satisfied or UpdatePolicyState.Required;
}

internal sealed class UpdatePolicyDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string MinimumVersion { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
}

/// <summary>
/// Reads and evaluates the small minimum-version policy independently of the Velopack release
/// feed. Policy failure is deliberately fail-open: a network outage or malformed file must never
/// strand a working installation. A successfully validated policy remains authoritative even if
/// the release feed is temporarily unavailable.
/// </summary>
internal sealed class UpdatePolicyService
{
    private const int MaxPolicyBytes = 64 * 1024;
    private const int MaxMessageLength = 500;
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 8
    };

    private readonly string? _source;

    public UpdatePolicyService(string? source = null)
    {
        _source = source ?? AppInfo.ResolvedUpdatePolicyUrl;
    }

    public async Task<UpdatePolicyResult> CheckAsync(
        string? currentVersion = null,
        CancellationToken cancellationToken = default)
    {
        string version = string.IsNullOrWhiteSpace(currentVersion) ? AppInfo.Version : currentVersion.Trim();
        if (string.IsNullOrWhiteSpace(_source))
        {
            return new UpdatePolicyResult(
                UpdatePolicyState.NotConfigured,
                null,
                "No minimum-version policy is configured.",
                AppInfo.DownloadPageUrl);
        }

        try
        {
            string json = await ReadPolicyAsync(_source, cancellationToken).ConfigureAwait(false);
            UpdatePolicyResult result = Evaluate(version, json);
            if (result.State == UpdatePolicyState.Invalid)
            {
                AppLog.Error("Update policy was ignored because it is invalid: " + result.Diagnostic);
            }
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            const string detail = "The policy request timed out.";
            AppLog.Error("Update policy could not be checked: " + detail);
            return Unavailable(detail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or
                                       JsonException or NotSupportedException or ArgumentException)
        {
            string detail = DiagnosticText(ex.Message);
            AppLog.Error("Update policy could not be checked: " + detail);
            return Unavailable(detail);
        }
    }

    internal static UpdatePolicyResult Evaluate(string currentVersion, string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Invalid("The document is empty.");
        if (Encoding.UTF8.GetByteCount(json) > MaxPolicyBytes) return Invalid("The document is too large.");

        UpdatePolicyDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<UpdatePolicyDocument>(json.TrimStart('\uFEFF'), Json);
        }
        catch (JsonException ex)
        {
            return Invalid("The document is not valid JSON: " + DiagnosticText(ex.Message));
        }

        if (document is null) return Invalid("The document did not contain a policy.");
        if (document.SchemaVersion != 1) return Invalid($"Schema version {document.SchemaVersion} is not supported.");

        string minimum = document.MinimumVersion?.Trim() ?? string.Empty;
        if (minimum.Length == 0 || minimum.Length > 64)
            return Invalid("minimumVersion is missing or too long.");
        if (!PitLaunchVersion.TryCompare(currentVersion, minimum, out int comparison))
            return Invalid("The current or minimum version is not a supported semantic version.");

        string downloadUrl = NormalizeDownloadUrl(document.DownloadUrl);
        if (downloadUrl.Length == 0)
            return Invalid("downloadUrl must be an HTTPS URL when it is provided.");

        string customMessage = NormalizeMessage(document.Message);
        if (comparison < 0)
        {
            string message = customMessage.Length == 0
                ? $"PitLaunch {minimum} or newer is required. Update before continuing."
                : customMessage;
            return new UpdatePolicyResult(UpdatePolicyState.Required, minimum, message, downloadUrl);
        }

        return new UpdatePolicyResult(
            UpdatePolicyState.Satisfied,
            minimum,
            $"This build meets the minimum supported version ({minimum}).",
            downloadUrl);
    }

    private async Task<string> ReadPolicyAsync(string source, CancellationToken cancellationToken)
    {
        string value = Environment.ExpandEnvironmentVariables(source.Trim().Trim('"'));
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
                throw new NotSupportedException("Remote update policies must use HTTPS.");

            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            using HttpResponseMessage response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxPolicyBytes)
                throw new InvalidDataException("The policy response is too large.");
            await using Stream webStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await ReadLimitedUtf8Async(webStream, cancellationToken).ConfigureAwait(false);
        }

        string path;
        if (Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.IsFile)
        {
            path = uri.LocalPath;
        }
        else
        {
            path = Path.GetFullPath(value);
        }

        if (Directory.Exists(path)) path = Path.Combine(path, "update-policy.json");
        FileInfo file = new(path);
        if (!file.Exists) throw new FileNotFoundException("The policy file does not exist.", path);
        if (file.Length > MaxPolicyBytes) throw new InvalidDataException("The policy file is too large.");
        await using FileStream localStream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return await ReadLimitedUtf8Async(localStream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadLimitedUtf8Async(Stream stream, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaxPolicyBytes)
                throw new InvalidDataException("The policy response is too large.");
            buffer.Write(chunk, 0, read);
        }
        return new UTF8Encoding(false, true).GetString(buffer.ToArray());
    }

    private static UpdatePolicyResult Invalid(string detail) => new(
        UpdatePolicyState.Invalid,
        null,
        "The minimum-version policy was invalid, so it was ignored.",
        AppInfo.DownloadPageUrl,
        detail);

    private static UpdatePolicyResult Unavailable(string detail) => new(
        UpdatePolicyState.Unavailable,
        null,
        "The minimum-version policy could not be checked; normal update checking will continue.",
        AppInfo.DownloadPageUrl,
        detail);

    private static string NormalizeDownloadUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return AppInfo.DownloadPageUrl;
        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : string.Empty;
    }

    private static string NormalizeMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string message = new(value.Where(character => !char.IsControl(character) || character is '\r' or '\n' or '\t').ToArray());
        message = string.Join(" ", message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return message.Length <= MaxMessageLength ? message : message[..MaxMessageLength].TrimEnd();
    }

    private static string DiagnosticText(string value)
    {
        string singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 300 ? singleLine : singleLine[..300];
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"PitLaunch/{AppInfo.Version}");
        return client;
    }
}

/// <summary>Small SemVer comparator kept independent from Velopack so policy JSON is easy to test.</summary>
internal static class PitLaunchVersion
{
    public static bool IsAtLeast(string candidate, string minimum) =>
        TryCompare(candidate, minimum, out int comparison) && comparison >= 0;

    public static bool TryCompare(string left, string right, out int comparison)
    {
        comparison = 0;
        if (!TryParse(left, out ParsedVersion leftVersion) || !TryParse(right, out ParsedVersion rightVersion))
            return false;

        for (int index = 0; index < leftVersion.Core.Length; index++)
        {
            comparison = leftVersion.Core[index].CompareTo(rightVersion.Core[index]);
            if (comparison != 0) return true;
        }

        if (leftVersion.Prerelease.Length == 0 && rightVersion.Prerelease.Length == 0) return true;
        if (leftVersion.Prerelease.Length == 0) { comparison = 1; return true; }
        if (rightVersion.Prerelease.Length == 0) { comparison = -1; return true; }

        int count = Math.Max(leftVersion.Prerelease.Length, rightVersion.Prerelease.Length);
        for (int index = 0; index < count; index++)
        {
            if (index >= leftVersion.Prerelease.Length) { comparison = -1; return true; }
            if (index >= rightVersion.Prerelease.Length) { comparison = 1; return true; }
            comparison = CompareIdentifier(leftVersion.Prerelease[index], rightVersion.Prerelease[index]);
            if (comparison != 0) return true;
        }
        return true;
    }

    private static bool TryParse(string value, out ParsedVersion parsed)
    {
        parsed = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        if (text.Length == 0 || text.Length > 64 || text.Any(char.IsWhiteSpace)) return false;

        int buildIndex = text.IndexOf('+');
        if (buildIndex >= 0) text = text[..buildIndex];
        string[] releaseParts = text.Split('-', 2);
        string[] coreParts = releaseParts[0].Split('.');
        if (coreParts.Length is < 1 or > 4) return false;

        int[] core = new int[4];
        for (int index = 0; index < coreParts.Length; index++)
        {
            string part = coreParts[index];
            if (part.Length == 0 || !part.All(char.IsDigit) || !int.TryParse(part, out core[index])) return false;
        }

        string[] prerelease = releaseParts.Length == 1 ? [] : releaseParts[1].Split('.');
        if (prerelease.Any(identifier => identifier.Length == 0 ||
            identifier.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')))
        {
            return false;
        }

        parsed = new ParsedVersion(core, prerelease);
        return true;
    }

    private static int CompareIdentifier(string left, string right)
    {
        bool leftNumeric = left.All(char.IsDigit);
        bool rightNumeric = right.All(char.IsDigit);
        if (leftNumeric && !rightNumeric) return -1;
        if (!leftNumeric && rightNumeric) return 1;
        if (!leftNumeric) return string.Compare(left, right, StringComparison.Ordinal);

        string normalizedLeft = left.TrimStart('0');
        string normalizedRight = right.TrimStart('0');
        if (normalizedLeft.Length == 0) normalizedLeft = "0";
        if (normalizedRight.Length == 0) normalizedRight = "0";
        int length = normalizedLeft.Length.CompareTo(normalizedRight.Length);
        return length != 0 ? length : string.Compare(normalizedLeft, normalizedRight, StringComparison.Ordinal);
    }

    private sealed record ParsedVersion(int[] Core, string[] Prerelease);
}
