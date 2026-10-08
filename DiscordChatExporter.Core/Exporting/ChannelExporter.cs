using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;
using Gress;

namespace DiscordChatExporter.Core.Exporting;

public class ChannelExporter(DiscordClient discord)
{
    public async ValueTask ExportChannelAsync(
        ExportRequest request,
        IProgress<Percentage>? progress = null,
        Action<string>? notice = null,
        CancellationToken cancellationToken = default
    )
    {
        // Forum channels don't have messages, they are just a list of threads
        if (request.Channel.Kind == ChannelKind.GuildForum)
        {
            throw new DiscordChatExporterException(
                $"Channel '{request.Channel.Name}' "
                    + $"of guild '{request.Guild.Name}' "
                    + $"is a forum and cannot be exported directly. "
                    + "You need to pull its threads and export them individually."
            );
        }

        // Build context
        var context = new ExportContext(discord, request);
        await context.PopulateChannelsAndRolesAsync(cancellationToken);

        // Initialize the exporter before further checks to ensure the file is created even if
        // an exception is thrown after this point.
        await using var messageExporter = new MessageExporter(context);

        // Check if the channel is empty
        if (request.Channel.IsEmpty)
        {
            throw new ChannelEmptyException(
                $"Channel '{request.Channel.Name}' "
                    + $"of guild '{request.Guild.Name}' "
                    + $"does not contain any messages; an empty file will be created."
            );
        }

        // Check if the 'before' and 'after' boundaries are valid
        if (
            (
                request.Before is not null
                && !request.Channel.MayHaveMessagesBefore(request.Before.Value)
            )
            || (
                request.After is not null
                && !request.Channel.MayHaveMessagesAfter(request.After.Value)
            )
        )
        {
            throw new ChannelEmptyException(
                $"Channel '{request.Channel.Name}' "
                    + $"of guild '{request.Guild.Name}' "
                    + $"does not contain any messages within the specified period; an empty file will be created."
            );
        }

        async ValueTask ExportMessageAsync(Message message)
        {
            try
            {
                // Resolve members for referenced users
                foreach (var user in message.GetReferencedUsers())
                    await context.PopulateMemberAsync(user, cancellationToken);

                // Export the message
                if (request.MessageFilter.IsMatch(message))
                    await messageExporter.ExportMessageAsync(message, cancellationToken);
            }
            catch (Exception ex)
            {
                // Provide more context to the exception, to simplify debugging based on error messages
                throw new DiscordChatExporterException(
                    $"Failed to export message #{message.Id} "
                        + $"in channel '{request.Channel.Name}' (#{request.Channel.Id}) "
                        + $"of guild '{request.Guild.Name} (#{request.Guild.Id})'.",
                    ex is not DiscordChatExporterException dex || dex.IsFatal,
                    ex
                );
            }
        }

        // Replay messages from a previous export of the same channel instead of downloading them
        var reuseSource = await TryLoadReuseSourceAsync(request, notice, cancellationToken);

        if (reuseSource is not null)
        {
            await foreach (var rawMessage in reuseSource.EnumerateMessagesAsync(cancellationToken))
            {
                await messageExporter.ExportRawMessageAsync(rawMessage, cancellationToken);
            }

            // Everything up to the last message of the source export has already been replayed,
            // so only the messages beyond it still need to be fetched.
            var effectiveAfter =
                request.After is { } after && after > reuseSource.LastMessageId
                    ? after
                    : reuseSource.LastMessageId;

            var hasMessagesToFetch =
                request.Channel.MayHaveMessagesAfter(effectiveAfter)
                && (request.Before is not { } before || effectiveAfter < before);

            if (hasMessagesToFetch)
            {
                await foreach (
                    var message in discord.GetMessagesAsync(
                        request.Channel.Id,
                        effectiveAfter,
                        request.Before,
                        progress,
                        cancellationToken
                    )
                )
                {
                    await ExportMessageAsync(message);
                }
            }

            return;
        }

        var messages = !request.IsReverseMessageOrder
            ? discord.GetMessagesAsync(
                request.Channel.Id,
                request.After,
                request.Before,
                progress,
                cancellationToken
            )
            : discord.GetMessagesInReverseAsync(
                request.Channel.Id,
                request.After,
                request.Before,
                progress,
                cancellationToken
            );

        await foreach (var message in messages)
        {
            await ExportMessageAsync(message);
        }
    }

    // Returns the messages available in the given directory, or null if there's nothing to reuse.
    // Falling back to a regular export is preferred over failing, because a partial or missing
    // source export is an ordinary situation rather than a user error.
    private async ValueTask<ExportReuseSource?> TryLoadReuseSourceAsync(
        ExportRequest request,
        Action<string>? notice,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request.ReuseExportDirPath))
            return null;

        var source = await ExportReuseSource.TryLoadAsync(request, cancellationToken);
        if (source is null)
        {
            var reason = !Directory.Exists(request.ReuseExportDirPath)
                ? "the directory does not exist"
                : $"no exported messages for channel #{request.Channel.Id} were found";

            notice?.Invoke(
                $"Unable to reuse previous exports from '{request.ReuseExportDirPath}' "
                    + $"because {reason}; exporting from scratch instead."
            );

            return null;
        }

        // The source export may have been limited to a date range that doesn't start where the
        // current request does, in which case replaying it would silently omit older messages.
        if (
            source.ExportedAfter is { } exportedAfter
            && (request.After is null || exportedAfter > request.After.Value.ToDate())
        )
        {
            notice?.Invoke(
                $"The previous export in '{request.ReuseExportDirPath}' starts at "
                    + $"'{exportedAfter:u}', which is later than the requested range; "
                    + "exporting from scratch instead."
            );

            return null;
        }

        return source;
    }
}
