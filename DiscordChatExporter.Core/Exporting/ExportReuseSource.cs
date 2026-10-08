using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting;

// Reads messages back from a previous export so that they can be replayed into a new export
// instead of being downloaded from Discord again.
//
// Messages are replayed as raw JSON, which means no member lookups, no reaction requests,
// no markdown re-formatting and no asset resolution happen for the replayed portion.
internal sealed class ExportReuseSource
{
    private readonly ExportRequest _request;

    // Partitions of the source export, ordered from the oldest to the newest message
    private readonly IReadOnlyList<SourcePartition> _partitions;

    private ExportReuseSource(
        ExportRequest request,
        IReadOnlyList<SourcePartition> partitions,
        Snowflake lastMessageId,
        long messageCount,
        DateTimeOffset? exportedAfter
    )
    {
        _request = request;
        _partitions = partitions;
        LastMessageId = lastMessageId;
        MessageCount = messageCount;
        ExportedAfter = exportedAfter;
    }

    // The newest message available in the source export; everything beyond it must be fetched
    public Snowflake LastMessageId { get; }

    public long MessageCount { get; }

    // The 'after' boundary that the source export was created with, if any
    public DateTimeOffset? ExportedAfter { get; }

    public async IAsyncEnumerable<JsonElement> EnumerateMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        foreach (var partition in _partitions)
        {
            var document = await ParseAsync(partition.FilePath, cancellationToken);

            try
            {
                if (
                    !document.RootElement.TryGetProperty("messages", out var messages)
                    || messages.ValueKind != JsonValueKind.Array
                )
                {
                    continue;
                }

                var length = messages.GetArrayLength();

                // Exports created with --reverse store their partitions from newest to oldest,
                // so the direction has to be undone for the replay to stay in order
                if (partition.IsDescending)
                {
                    for (var i = length - 1; i >= 0; i--)
                    {
                        if (IsWithinRequestedRange(messages[i]))
                            yield return messages[i];
                    }
                }
                else
                {
                    for (var i = 0; i < length; i++)
                    {
                        if (IsWithinRequestedRange(messages[i]))
                            yield return messages[i];
                    }
                }
            }
            finally
            {
                document.Dispose();
            }
        }
    }

    private bool IsWithinRequestedRange(JsonElement message)
    {
        if (!TryReadDate(message, "timestamp", out var timestamp))
            return true;

        if (_request.After is { } after && timestamp <= after.ToDate())
            return false;

        if (_request.Before is { } before && timestamp >= before.ToDate())
            return false;

        return true;
    }

    // Returns null when there's nothing to replay from the given directory
    public static async ValueTask<ExportReuseSource?> TryLoadAsync(
        ExportRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var dirPath = request.ReuseExportDirPath;
        if (string.IsNullOrWhiteSpace(dirPath) || !Directory.Exists(dirPath))
            return null;

        var allFilePaths = Directory.EnumerateFiles(dirPath, "*.json").ToArray();
        if (allFilePaths.Length <= 0)
            return null;

        // Exports created with default file names carry the channel ID, which spares us from
        // parsing every other export in the directory. Fall back to parsing them all when the
        // output was written to a custom file name.
        var taggedFilePaths = allFilePaths
            .Where(f =>
                Path.GetFileName(f).Contains($"[{request.Channel.Id}]", StringComparison.Ordinal)
            )
            .ToArray();

        var candidateFilePaths = taggedFilePaths.Length > 0 ? taggedFilePaths : allFilePaths;

        var partitions = new List<SourcePartition>(candidateFilePaths.Length);
        var lastMessageId = Snowflake.Zero;
        long messageCount = 0;
        DateTimeOffset? exportedAfter = null;

        foreach (var filePath in candidateFilePaths)
        {
            JsonDocument document;

            try
            {
                document = await ParseAsync(filePath, cancellationToken);
            }
            // The channel an unreadable file belongs to cannot be determined, and silently
            // skipping it could leave a hole in the middle of the export
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                return null;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    continue;

                if (
                    !root.TryGetProperty("channel", out var channel)
                    || channel.ValueKind != JsonValueKind.Object
                    || !channel.TryGetProperty("id", out var channelIdJson)
                    || Snowflake.TryParse(channelIdJson.GetString()) != request.Channel.Id
                )
                {
                    continue;
                }

                if (exportedAfter is null && TryReadDate(root, "dateRange", "after", out var range))
                    exportedAfter = range;

                if (
                    !root.TryGetProperty("messages", out var messages)
                    || messages.ValueKind != JsonValueKind.Array
                )
                {
                    continue;
                }

                var length = messages.GetArrayLength();
                if (length <= 0)
                    continue;

                var firstId = ReadMessageId(messages[0]);
                var lastId = ReadMessageId(messages[length - 1]);
                if (firstId is null || lastId is null)
                    continue;

                // Messages within a partition are always contiguous and ordered, so the first
                // and the last message are enough to establish its range
                var isDescending = firstId.Value.CompareTo(lastId.Value) > 0;
                var minMessageId = isDescending ? lastId.Value : firstId.Value;
                var maxMessageId = isDescending ? firstId.Value : lastId.Value;

                partitions.Add(new SourcePartition(filePath, minMessageId, isDescending));

                messageCount += length;

                if (maxMessageId.CompareTo(lastMessageId) > 0)
                    lastMessageId = maxMessageId;
            }
        }

        if (partitions.Count <= 0 || messageCount <= 0)
            return null;

        return new ExportReuseSource(
            request,
            partitions.OrderBy(p => p.MinMessageId).ToArray(),
            lastMessageId,
            messageCount,
            exportedAfter
        );
    }

    private static async ValueTask<JsonDocument> ParseAsync(
        string filePath,
        CancellationToken cancellationToken
    )
    {
        await using var stream = File.OpenRead(filePath);
        return await JsonDocument.ParseAsync(stream, default, cancellationToken);
    }

    private static Snowflake? ReadMessageId(JsonElement message) =>
        message.TryGetProperty("id", out var idJson)
            ? Snowflake.TryParse(idJson.GetString())
            : null;

    private static bool TryReadDate(
        JsonElement root,
        string objectKey,
        string valueKey,
        out DateTimeOffset value
    )
    {
        value = default;

        return root.TryGetProperty(objectKey, out var obj)
            && obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(valueKey, out var dateJson)
            && dateJson.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(dateJson.GetString(), out value);
    }

    private static bool TryReadDate(JsonElement element, string key, out DateTimeOffset value)
    {
        value = default;

        return element.TryGetProperty(key, out var dateJson)
            && dateJson.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(dateJson.GetString(), out value);
    }

    private sealed record SourcePartition(
        string FilePath,
        Snowflake MinMessageId,
        bool IsDescending
    );
}
