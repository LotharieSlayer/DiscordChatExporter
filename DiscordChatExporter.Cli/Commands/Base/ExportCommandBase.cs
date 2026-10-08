using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli.Commands.Converters;
using DiscordChatExporter.Cli.Commands.Shared;
using DiscordChatExporter.Cli.Utils.Extensions;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using Gress;
using Spectre.Console;

namespace DiscordChatExporter.Cli.Commands.Base;

public abstract class ExportCommandBase : DiscordCommandBase
{
    [CommandOption(
        "output",
        'o',
        Description = "Output file or directory path. "
            + "If a directory is specified, file names will be generated automatically based on the channel names and export parameters. "
            + "Directory paths must end with a slash to avoid ambiguity. "
            + "Supports template tokens, see the documentation for more info."
    )]
    public string OutputPath
    {
        get;
        // Handle ~/ in paths on Unix systems
        // https://github.com/Tyrrrz/DiscordChatExporter/pull/903
        set => field = Path.GetFullPath(value);
    } = Directory.GetCurrentDirectory();

    [CommandOption("format", 'f', Description = "Export format.")]
    public ExportFormat ExportFormat { get; set; } = ExportFormat.HtmlDark;

    [CommandOption(
        "after",
        Description = "Only include messages sent after this date or message ID."
    )]
    public Snowflake? After { get; set; }

    [CommandOption(
        "before",
        Description = "Only include messages sent before this date or message ID."
    )]
    public Snowflake? Before { get; set; }

    [CommandOption(
        "partition",
        'p',
        Description = "Split the output into partitions, each limited to the specified "
            + "number of messages (e.g., '100') or file size (e.g., '10mb')."
    )]
    public PartitionLimit PartitionLimit { get; set; } = PartitionLimit.Null;

    [CommandOption(
        "include-threads",
        Description = "Which types of threads should be included.",
        Converter = typeof(ThreadInclusionModeInputConverter)
    )]
    public ThreadInclusionMode ThreadInclusionMode { get; set; } = ThreadInclusionMode.None;

    [CommandOption(
        "filter",
        Description = "Only include messages that satisfy this filter. "
            + "See the documentation for more info."
    )]
    public MessageFilter MessageFilter { get; set; } = MessageFilter.Null;

    [CommandOption(
        "parallel",
        Description = "Limits how many channels can be exported in parallel."
    )]
    public int ParallelLimit { get; set; } = 1;

    [CommandOption(
        "reverse",
        Description = "Export messages in reverse chronological order (newest first)."
    )]
    public bool IsReverseMessageOrder { get; set; }

    [CommandOption(
        "markdown",
        Description = "Process markdown, mentions, and other special tokens."
    )]
    public bool ShouldFormatMarkdown { get; set; } = true;

    [CommandOption(
        "media",
        Description = "Download assets referenced by the export (user avatars, attached files, embedded images, etc.)."
    )]
    public bool ShouldDownloadAssets { get; set; }

    [CommandOption(
        "reuse-media",
        Description = "Reuse previously downloaded assets to avoid redundant requests."
    )]
    public bool ShouldReuseAssets { get; set; } = false;

    [CommandOption(
        "media-dir",
        Description = "Download assets to this directory. "
            + "If not specified, the asset directory path will be derived from the output path."
    )]
    public string? AssetsDirPath
    {
        get;
        // Handle ~/ in paths on Unix systems
        // https://github.com/Tyrrrz/DiscordChatExporter/pull/903
        set => field = value is not null ? Path.GetFullPath(value) : null;
    }

    [CommandOption(
        "cache-dir",
        Description = "Cache API responses in this directory and reuse them on subsequent runs. "
            + "If not specified, no caching is performed."
    )]
    public string? CacheDirPath
    {
        get;
        // Handle ~/ in paths on Unix systems
        // https://github.com/Tyrrrz/DiscordChatExporter/pull/903
        set => field = value is not null ? Path.GetFullPath(value) : null;
    }

    protected override string? RequestCacheDirPath => CacheDirPath;

    [CommandOption(
        "reuse-export",
        Description = "Reuse messages from a previously exported directory instead of fetching them again. "
            + "Only messages newer than the source export will be downloaded. "
            + "Can only be used with the JSON format, and cannot be combined with --filter or --reverse. "
            + "The output directory must be different from this directory."
    )]
    public string? ReuseExportDirPath
    {
        get;
        // Handle ~/ in paths on Unix systems
        // https://github.com/Tyrrrz/DiscordChatExporter/pull/903
        set => field = value is not null ? Path.GetFullPath(value) : null;
    }

    [CommandOption(
        "dateformat",
        Description = "This option doesn't do anything. Kept for backwards compatibility."
    )]
    public string DateFormat { get; set; } = "MM/dd/yyyy h:mm tt";

    [CommandOption(
        "locale",
        Description = "Locale to use when formatting dates and numbers. "
            + "If not specified, the default system locale will be used."
    )]
    public string? Locale { get; set; }

    [CommandOption("utc", Description = "Normalize all timestamps to UTC+0.")]
    public bool IsUtcNormalizationEnabled { get; set; } = false;

    [CommandOption(
        "fuck-russia",
        EnvironmentVariable = "FUCK_RUSSIA",
        Description = "Don't print the Support Ukraine message to the console.",
        // Use a converter to accept '1' as 'true' to reuse the existing environment variable
        Converter = typeof(TruthyBooleanInputConverter)
    )]
    public bool IsUkraineSupportMessageDisabled { get; set; } = false;

    [field: AllowNull, MaybeNull]
    protected ChannelExporter Exporter => field ??= new ChannelExporter(Discord);

    // Overridden by the commands that let the user exclude channels from the export
    protected virtual IReadOnlyList<Snowflake> ExcludedChannelIds => [];

    protected async ValueTask ExportAsync(IConsole console, IReadOnlyList<Channel> channels)
    {
        var cancellationToken = console.RegisterCancellationHandler();

        var excludedChannelIds = new HashSet<Snowflake>(ExcludedChannelIds);
        var matchedExcludedChannelIds = new HashSet<Snowflake>();

        // A channel is excluded if its own ID, or the ID of one of its ancestors, is listed.
        // Ancestors cover categories for regular channels and parent channels for threads.
        bool IsChannelExcluded(Channel channel)
        {
            var isExcluded = false;

            foreach (var id in channel.GetParents().Select(p => p.Id).Append(channel.Id))
            {
                if (excludedChannelIds.Contains(id))
                {
                    matchedExcludedChannelIds.Add(id);
                    isExcluded = true;
                }
            }

            return isExcluded;
        }

        // Apply the exclusions before unwrapping threads, so that excluded channels
        // don't have their threads fetched either
        var filteredChannels =
            excludedChannelIds.Count > 0
                ? channels.Where(c => !IsChannelExcluded(c)).ToArray()
                : channels.ToArray();

        // Asset reuse can only be enabled if the download assets option is set
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/425
        if (ShouldReuseAssets && !ShouldDownloadAssets)
        {
            throw new CommandException("Option --reuse-media cannot be used without --media.");
        }

        // Assets directory can only be specified if the download assets option is set
        if (!string.IsNullOrWhiteSpace(AssetsDirPath) && !ShouldDownloadAssets)
        {
            throw new CommandException("Option --media-dir cannot be used without --media.");
        }

        // Make sure the user does not try to export multiple channels into one file.
        // Output path must either be a directory or contain template tokens for this to work.
        // Validate this up-front, before fetching threads, because thread fetching can take a
        // long time and it's frustrating to fail only after it completes.
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/799
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/917
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/1549
        var mayExportMultipleChannels =
            // Multiple channels were provided explicitly
            filteredChannels.Length > 1
            // Thread inclusion can add more channels to the export
            || ThreadInclusionMode != ThreadInclusionMode.None;

        var isValidOutputPath =
            // Anything is valid when exporting a single channel
            !mayExportMultipleChannels
            // When using template tokens, assume the user knows what they're doing
            || OutputPath.Contains('%')
            // Otherwise, require an existing directory or an unambiguous directory path
            || Directory.Exists(OutputPath)
            || Path.EndsInDirectorySeparator(OutputPath);

        if (!isValidOutputPath)
        {
            throw new CommandException(
                "Attempted to export multiple channels, but the output path is neither a directory nor a template. "
                    + "If the provided output path is meant to be treated as a directory, make sure it ends with a slash. "
                    + $"Provided output path: '{OutputPath}'."
            );
        }

        var unwrappedChannels = new List<Channel>(filteredChannels);

        // Unwrap threads
        if (ThreadInclusionMode != ThreadInclusionMode.None)
        {
            await console.Output.WriteLineAsync("Fetching threads...");

            var fetchedThreadsCount = 0;
            await console
                .CreateStatusTicker()
                .StartAsync(
                    "...",
                    async ctx =>
                    {
                        await foreach (
                            var thread in Discord.GetChannelThreadsAsync(
                                filteredChannels,
                                ThreadInclusionMode == ThreadInclusionMode.All,
                                Before,
                                After,
                                cancellationToken
                            )
                        )
                        {
                            // Threads may be excluded individually, or through their parent channel
                            if (IsChannelExcluded(thread))
                                continue;

                            unwrappedChannels.Add(thread);

                            ctx.Status(Markup.Escape($"Fetched '{thread.GetHierarchicalName()}'."));

                            fetchedThreadsCount++;
                        }
                    }
                );

            // Remove forums, as they cannot be exported directly and their constituent threads
            // have already been fetched.
            unwrappedChannels.RemoveAll(channel => channel.Kind == ChannelKind.GuildForum);

            await console.Output.WriteLineAsync($"Fetched {fetchedThreadsCount} thread(s).");
        }

        // Export
        var errorsByChannel = new ConcurrentDictionary<Channel, string>();
        var warningsByChannel = new ConcurrentDictionary<Channel, string>();
        var noticesByChannel = new ConcurrentDictionary<Channel, string>();

        await console.Output.WriteLineAsync($"Exporting {unwrappedChannels.Count} channel(s)...");
        await console
            .CreateProgressTicker()
            .HideCompleted(
                // When exporting multiple channels in parallel, hide the completed tasks
                // because it gets hard to visually parse them as they complete out of order.
                // https://github.com/Tyrrrz/DiscordChatExporter/issues/1124
                ParallelLimit > 1
            )
            .StartAsync(async ctx =>
            {
                await Parallel.ForEachAsync(
                    unwrappedChannels,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Max(1, ParallelLimit),
                        CancellationToken = cancellationToken,
                    },
                    async (channel, innerCancellationToken) =>
                    {
                        try
                        {
                            await ctx.StartTaskAsync(
                                Markup.Escape(channel.GetHierarchicalName()),
                                async progress =>
                                {
                                    var guild = await Discord.GetGuildAsync(
                                        channel.GuildId,
                                        innerCancellationToken
                                    );

                                    var request = new ExportRequest(
                                        guild,
                                        channel,
                                        OutputPath,
                                        AssetsDirPath,
                                        ExportFormat,
                                        After,
                                        Before,
                                        PartitionLimit,
                                        MessageFilter,
                                        IsReverseMessageOrder,
                                        ShouldFormatMarkdown,
                                        ShouldDownloadAssets,
                                        ShouldReuseAssets,
                                        Locale,
                                        IsUtcNormalizationEnabled,
                                        ReuseExportDirPath
                                    );

                                    await Exporter.ExportChannelAsync(
                                        request,
                                        progress.ToPercentageBased(),
                                        message => noticesByChannel[channel] = message,
                                        innerCancellationToken
                                    );
                                }
                            );
                        }
                        catch (ChannelEmptyException ex)
                        {
                            warningsByChannel[channel] = ex.Message;
                        }
                        catch (DiscordChatExporterException ex) when (!ex.IsFatal)
                        {
                            errorsByChannel[channel] = ex.Message;
                        }
                    }
                );
            });

        // Print the result
        using (console.WithForegroundColor(ConsoleColor.White))
        {
            await console.Output.WriteLineAsync(
                $"Successfully exported {unwrappedChannels.Count - errorsByChannel.Count} channel(s)."
            );
        }

        // Print notices
        if (noticesByChannel.Any())
        {
            await console.Output.WriteLineAsync();

            using (console.WithForegroundColor(ConsoleColor.Cyan))
            {
                await console.Output.WriteLineAsync("Notes for the following channel(s):");
            }

            foreach (var (channel, message) in noticesByChannel)
            {
                await console.Output.WriteAsync($"{channel.GetHierarchicalName()}: ");
                using (console.WithForegroundColor(ConsoleColor.Cyan))
                    await console.Output.WriteLineAsync(message);
            }

            await console.Output.WriteLineAsync();
        }

        // Warn about excluded channel IDs that didn't match anything, which usually
        // indicates a typo or a channel that isn't part of the current export
        var unmatchedExcludedChannelIds = excludedChannelIds
            .Except(matchedExcludedChannelIds)
            .ToArray();
        if (unmatchedExcludedChannelIds.Length > 0)
        {
            await console.Output.WriteLineAsync();

            using (console.WithForegroundColor(ConsoleColor.Cyan))
            {
                await console.Output.WriteLineAsync(
                    "Note: The following excluded channel ID(s) did not match any channel: "
                        + string.Join(", ", unmatchedExcludedChannelIds.Select(id => id.ToString()))
                        + "."
                );
            }

            await console.Output.WriteLineAsync();
        }

        // Print warnings
        if (warningsByChannel.Any())
        {
            await console.Output.WriteLineAsync();

            using (console.WithForegroundColor(ConsoleColor.Yellow))
            {
                await console.Error.WriteLineAsync(
                    "Warnings reported for the following channel(s):"
                );
            }

            foreach (var (channel, message) in warningsByChannel)
            {
                await console.Error.WriteAsync($"{channel.GetHierarchicalName()}: ");
                using (console.WithForegroundColor(ConsoleColor.Yellow))
                    await console.Error.WriteLineAsync(message);
            }

            await console.Error.WriteLineAsync();
        }

        // Print errors
        if (errorsByChannel.Any())
        {
            await console.Output.WriteLineAsync();

            using (console.WithForegroundColor(ConsoleColor.Red))
            {
                await console.Error.WriteLineAsync("Failed to export the following channel(s):");
            }

            foreach (var (channel, message) in errorsByChannel)
            {
                await console.Error.WriteAsync($"{channel.GetHierarchicalName()}: ");
                using (console.WithForegroundColor(ConsoleColor.Red))
                    await console.Error.WriteLineAsync(message);
            }

            await console.Error.WriteLineAsync();
        }

        // Fail the command only if ALL channels failed to export.
        // If only some channels failed to export, it's okay.
        if (errorsByChannel.Count >= unwrappedChannels.Count)
            throw new CommandException("Export failed.");
    }

    // Replaying previously exported messages only makes sense under a narrow set of conditions
    private void ValidateReuseExportOptions()
    {
        if (string.IsNullOrWhiteSpace(ReuseExportDirPath))
            return;

        // Only the JSON schema round-trips, which is what allows messages to be written back
        // without reconstructing them
        if (ExportFormat is not ExportFormat.Json)
        {
            throw new CommandException(
                "Option --reuse-export can only be used with the JSON export format, "
                    + $"but the export format is '{ExportFormat}'."
            );
        }

        // Replayed messages are written as raw JSON, so there's no Message object to filter on
        if (!ReferenceEquals(MessageFilter, MessageFilter.Null))
        {
            throw new CommandException(
                "Option --reuse-export cannot be combined with --filter, "
                    + "because messages replayed from a previous export cannot be filtered again."
            );
        }

        // The source export may have been created in reverse, and mixing the two directions
        // would produce an incoherent message order
        if (IsReverseMessageOrder)
        {
            throw new CommandException("Option --reuse-export cannot be combined with --reverse.");
        }

        // Writing into the directory being replayed from would overwrite the source files
        if (
            (
                Directory.Exists(OutputPath)
                || string.IsNullOrWhiteSpace(Path.GetExtension(OutputPath))
            )
            && string.Equals(
                NormalizeDirPath(OutputPath),
                NormalizeDirPath(ReuseExportDirPath),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            )
        )
        {
            throw new CommandException(
                "Option --reuse-export cannot point to the same directory as the output path, "
                    + "because the export would overwrite the messages it is replaying. "
                    + $"Provided output path: '{OutputPath}'."
            );
        }
    }

    private static string NormalizeDirPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public override async ValueTask ExecuteAsync(IConsole console)
    {
        ValidateReuseExportOptions();

        // Support Ukraine callout
        if (!IsUkraineSupportMessageDisabled)
        {
            console.Output.WriteLine(
                "┌────────────────────────────────────────────────────────────────────┐"
            );
            console.Output.WriteLine(
                "│   Thank you for supporting Ukraine <3                              │"
            );
            console.Output.WriteLine(
                "│                                                                    │"
            );
            console.Output.WriteLine(
                "│   As Russia wages a genocidal war against my country,              │"
            );
            console.Output.WriteLine(
                "│   I'm grateful to everyone who continues to                        │"
            );
            console.Output.WriteLine(
                "│   stand with Ukraine in our fight for freedom.                     │"
            );
            console.Output.WriteLine(
                "│                                                                    │"
            );
            console.Output.WriteLine(
                "│   Learn more: https://tyrrrz.me/ukraine                            │"
            );
            console.Output.WriteLine(
                "└────────────────────────────────────────────────────────────────────┘"
            );
            console.Output.WriteLine("");
        }

        await base.ExecuteAsync(console);
    }
}
