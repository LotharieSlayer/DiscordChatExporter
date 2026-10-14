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
        DateTimeOffset? exportedAfter,
        bool wasRecovered
    )
    {
        _request = request;
        _partitions = partitions;
        LastMessageId = lastMessageId;
        MessageCount = messageCount;
        ExportedAfter = exportedAfter;
        WasRecovered = wasRecovered;
    }

    // The newest message available in the source export; everything beyond it must be fetched
    public Snowflake LastMessageId { get; }

    public long MessageCount { get; }

    // The 'after' boundary that the source export was created with, if any
    public DateTimeOffset? ExportedAfter { get; }

    // Whether any of the source files were only partially written (e.g. because a previous
    // export was interrupted) and had to be recovered
    public bool WasRecovered { get; }

    public async IAsyncEnumerable<JsonElement> EnumerateMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        foreach (var partition in _partitions)
        {
            var (document, _) = await ParseTolerantAsync(partition.FilePath, cancellationToken);
            if (document is null)
                continue;

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

        var allFilePaths = EnumerateExportFiles(dirPath);
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
        var wasRecovered = false;

        foreach (var filePath in candidateFilePaths)
        {
            JsonDocument? document;
            bool recovered;

            try
            {
                (document, recovered) = await ParseTolerantAsync(filePath, cancellationToken);
            }
            // The channel an unreadable file belongs to cannot be determined, and silently
            // skipping it could leave a hole in the middle of the export
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            // A file that can't be interpreted as an export at all is treated the same way,
            // for the same reason
            if (document is null)
                return null;

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

                wasRecovered |= recovered;
            }
        }

        if (partitions.Count <= 0 || messageCount <= 0)
            return null;

        return new ExportReuseSource(
            request,
            partitions.OrderBy(p => p.MinMessageId).ToArray(),
            lastMessageId,
            messageCount,
            exportedAfter,
            wasRecovered
        );
    }

    // Considers both complete exports and in-progress ones (written with the '.partial' suffix).
    // When both exist for the same file, the in-progress one represents the newer attempt and
    // therefore supersedes the complete one.
    private static string[] EnumerateExportFiles(string dirPath)
    {
        var filePaths = Directory
            .EnumerateFiles(dirPath)
            .Where(f =>
                f.EndsWith(".json", StringComparison.Ordinal)
                || f.EndsWith(
                    ".json" + MessageExporter.PartialFileExtension,
                    StringComparison.Ordinal
                )
            )
            .ToArray();

        var partialBasePaths = filePaths
            .Where(f => f.EndsWith(MessageExporter.PartialFileExtension, StringComparison.Ordinal))
            .Select(f => f[..^MessageExporter.PartialFileExtension.Length])
            .ToHashSet(StringComparer.Ordinal);

        return filePaths.Where(f => !partialBasePaths.Contains(f)).ToArray();
    }

    // Reads an export file that may have been cut off halfway (e.g. because the process was
    // terminated). Messages that were written in full are preserved and the incomplete tail is
    // discarded. Returns a null document if the file can't be interpreted as an export at all.
    private static async ValueTask<(JsonDocument? Document, bool WasRecovered)> ParseTolerantAsync(
        string filePath,
        CancellationToken cancellationToken
    )
    {
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);

        // Fast path: the file was written in full
        try
        {
            return (JsonDocument.Parse(bytes), false);
        }
        catch (JsonException)
        {
            // Fall through to recovery
        }

        // Rewind to just past the last message that was written in full, then close the
        // message array and the root object to form valid JSON again
        if (TryRecoverTruncatedJson(bytes) is not { } recoveredBytes)
            return (null, false);

        try
        {
            return (JsonDocument.Parse(recoveredBytes), true);
        }
        catch (JsonException)
        {
            return (null, false);
        }
    }

    // Rewinds a truncated export file to just past the last message that was written in full,
    // then closes the message array and the root object so that the result is valid JSON.
    // Returns null if the input can't be recovered (e.g. it is not a DiscordChatExporter
    // payload, or it did not contain a single complete message).
    internal static byte[]? TryRecoverTruncatedJson(byte[] bytes)
    {
        if (FindLastCompleteMessageEnd(bytes) is not { } lastMessageEnd)
            return null;

        var recoveredBytes = new byte[lastMessageEnd + 2];
        bytes.AsSpan(0, lastMessageEnd).CopyTo(recoveredBytes);
        recoveredBytes[^2] = (byte)']';
        recoveredBytes[^1] = (byte)'}';

        return recoveredBytes;
    }

    // Scans the 'messages' array and returns the byte offset just past the last message element
    // that was written in full, or null if the array could not be reached or contained no
    // complete message. The input may be truncated: scanning stops at the first structural error.
    private static int? FindLastCompleteMessageEnd(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, isFinalBlock: true, default);

        var depth = 0;
        var isInMessagesArray = false;
        string? currentPropertyName = null;
        int? lastMessageEnd = null;

        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        currentPropertyName = reader.GetString();
                        break;

                    case JsonTokenType.StartObject:
                        depth++;
                        currentPropertyName = null;
                        break;

                    case JsonTokenType.StartArray:
                        depth++;
                        // The 'messages' array sits directly inside the root object
                        if (currentPropertyName == "messages" && depth == 2)
                            isInMessagesArray = true;
                        currentPropertyName = null;
                        break;

                    case JsonTokenType.EndObject:
                        depth--;
                        // Each element of the 'messages' array is an object, so seeing the depth
                        // drop back to that of the array marks a fully written message
                        if (isInMessagesArray && depth == 2)
                            lastMessageEnd = (int)reader.BytesConsumed;
                        currentPropertyName = null;
                        break;

                    case JsonTokenType.EndArray:
                        depth--;
                        if (isInMessagesArray && depth < 2)
                            isInMessagesArray = false;
                        currentPropertyName = null;
                        break;

                    default:
                        currentPropertyName = null;
                        break;
                }
            }
        }
        // The truncation point was reached; whatever was recorded up to here is valid
        catch (JsonException) { }

        return lastMessageEnd;
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
