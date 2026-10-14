using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Discord;

// Disk-backed cache of API responses, which allows subsequent runs to skip
// requests that have already been made.
internal partial class DiscordRequestCache(string dirPath)
{
    private static readonly AsyncKeyedLocker<string> Locker = new();

    public async ValueTask<HttpResponseMessage?> TryGetResponseAsync(
        string url,
        CancellationToken cancellationToken = default
    )
    {
        var filePath = GetFilePath(dirPath, url);
        if (!File.Exists(filePath))
            return null;

        using var _ = await Locker.LockAsync(filePath, cancellationToken);

        try
        {
            var content = await File.ReadAllTextAsync(filePath, cancellationToken);

            // Older versions could cache a partially received response body, which would then
            // fail every subsequent run until the entry was cleared. Such an entry is discarded
            // and treated as a cache miss, so that the response is fetched from the server again.
            if (!IsValidJson(content))
            {
                TryDeleteFile(filePath);
                return null;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            };
        }
        // A cache entry that can't be read is no reason to fail the request, so it is
        // treated as a miss and the response is fetched from the server instead.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async ValueTask SetResponseAsync(
        string url,
        string content,
        CancellationToken cancellationToken = default
    )
    {
        // A partially received response body must never be cached, because the corrupt entry
        // would fail every subsequent run until it's cleared.
        if (!IsValidJson(content))
            return;

        var filePath = GetFilePath(dirPath, url);
        string? tempFilePath = null;

        using var _ = await Locker.LockAsync(filePath, cancellationToken);

        try
        {
            Directory.CreateDirectory(dirPath);

            // Write the entry to a temporary file first and only then move it into place, so
            // that a cache entry is never observed in a partially written state. The name of
            // the temporary file is unique because the directory may be shared by several
            // processes, and the lock above only applies within this one.
            tempFilePath = $"{filePath}.{Guid.NewGuid():N}.tmp";

            await File.WriteAllTextAsync(tempFilePath, content, cancellationToken);
            File.Move(tempFilePath, filePath, true);

            tempFilePath = null;
        }
        // Failing to write to the cache should never break the export, so the error is
        // swallowed and the response is simply left uncached.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ignored
        }
        finally
        {
            if (tempFilePath is not null)
                TryDeleteFile(tempFilePath);
        }
    }
}

internal partial class DiscordRequestCache
{
    private static bool IsValidJson(string content)
    {
        try
        {
            using var _ = JsonDocument.Parse(content);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string GetFilePath(string dirPath, string url)
    {
        var fileName =
            // 16 chars = 64 bits, reaches 1% collision probability at ~609 million entries
            SHA256
                .HashData(Encoding.UTF8.GetBytes(url))
                .Pipe(Convert.ToHexStringLower)
                .Truncate(16) + ".json";

        return Path.Combine(dirPath, fileName);
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ignored
        }
    }
}
