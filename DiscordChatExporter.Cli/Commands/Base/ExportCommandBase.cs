﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli.Commands.Converters;
using DiscordChatExporter.Cli.Utils.Extensions;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using DiscordChatExporter.Core.Utils;
using DiscordChatExporter.Core.Utils.Extensions;
using Gress;

namespace DiscordChatExporter.Cli.Commands.Base;

public abstract class ExportCommandBase : DiscordCommandBase
{
    private readonly string _outputPath = Directory.GetCurrentDirectory();

    [CommandOption(
        "output",
        'o',
        Description =
            "Output file or directory path. " +
            "If a directory is specified, file names will be generated automatically based on the channel names and other parameters. " +
            "Directory path should end with a slash to avoid ambiguity. " +
            "Supports template tokens, see the documentation for more info."
    )]
    public string OutputPath
    {
        get => _outputPath;
        // Handle ~/ in paths on Unix systems
        // https://github.com/Tyrrrz/DiscordChatExporter/pull/903
        init => _outputPath = Path.GetFullPath(value);
    }

    [CommandOption(
        "format",
        'f',
        Description = "Export format."
    )]
    public ExportFormat ExportFormat { get; init; } = ExportFormat.HtmlDark;

    [CommandOption(
        "after",
        Description = "Only include messages sent after this date or message ID."
    )]
    public Snowflake? After { get; init; }

    [CommandOption(
        "before",
        Description = "Only include messages sent before this date or message ID."
    )]
    public Snowflake? Before { get; init; }

    [CommandOption(
        "partition",
        'p',
        Description =
            "Split output into partitions, each limited to this number of messages (e.g. '100') or file size (e.g. '10mb')."
    )]
    public PartitionLimit PartitionLimit { get; init; } = PartitionLimit.Null;

    [CommandOption(
        "filter",
        Description = "Only include messages that satisfy this filter (e.g. 'from:foo#1234' or 'has:image')."
    )]
    public MessageFilter MessageFilter { get; init; } = MessageFilter.Null;

    [CommandOption(
        "parallel",
        Description = "Limits how many channels can be exported in parallel."
    )]
    public int ParallelLimit { get; init; } = 1;

    [CommandOption(
        "markdown",
        Description = "Process markdown, mentions, and other special tokens."
    )]
    public bool ShouldFormatMarkdown { get; init; } = true;

    [CommandOption(
        "media",
        Description = "Download assets referenced by the export (user avatars, attached files, embedded images, etc.)."
    )]
    public bool ShouldDownloadAssets { get; init; }

    [CommandOption(
        "reuse-media",
        Description = "Reuse previously downloaded assets to avoid redundant requests."
    )]
    public bool ShouldReuseAssets { get; init; }

    private readonly string? _assetsDirPath;

    [CommandOption(
        "media-dir",
        Description = "Download assets to this directory. If not specified, the asset directory path will be derived from the output path."
    )]
    public string? AssetsDirPath
    {
        get => _assetsDirPath;
        // Handle ~/ in paths on Unix systems
        // https://github.com/Tyrrrz/DiscordChatExporter/pull/903
        init => _assetsDirPath = value is not null ? Path.GetFullPath(value) : null;
    }

    [CommandOption(
        "dateformat",
        Description = "Format used when writing dates."
    )]
    public string DateFormat { get; init; } = "MM/dd/yyyy h:mm tt";

    [CommandOption(
        "fuck-russia",
        EnvironmentVariable = "FUCK_RUSSIA",
        Description = "Don't print the Support Ukraine message to the console.",
        Converter = typeof(TruthyBooleanBindingConverter)
    )]
    public bool IsUkraineSupportMessageDisabled { get; init; }

    private ChannelExporter? _channelExporter;
    protected ChannelExporter Exporter => _channelExporter ??= new ChannelExporter(Discord);

    protected async ValueTask ExecuteAsync(IConsole console, IReadOnlyList<Channel> channels)
    {
        // Reuse assets option should only be used when the download assets option is set.
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/425
        if (ShouldReuseAssets && !ShouldDownloadAssets)
        {
            throw new CommandException(
                "Option --reuse-media cannot be used without --media."
            );
        }

        // Assets directory should only be specified when the download assets option is set
        if (!string.IsNullOrWhiteSpace(AssetsDirPath) && !ShouldDownloadAssets)
        {
            throw new CommandException(
                "Option --media-dir cannot be used without --media."
            );
        }

        // Make sure the user does not try to export all channels into a single file.
        // Output path must either be a directory, or contain template tokens.
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/799
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/917
        var isValidOutputPath =
            // Anything is valid when exporting a single channel
            channels.Count <= 1 ||
            // When using template tokens, assume the user knows what they're doing
            OutputPath.Contains('%') ||
            // Otherwise, require an existing directory or an unambiguous directory path
            Directory.Exists(OutputPath) || PathEx.IsDirectoryPath(OutputPath);

        if (!isValidOutputPath)
        {
            throw new CommandException(
                "Attempted to export multiple channels, but the output path is neither a directory nor a template. " +
                "If the provided output path is meant to be treated as a directory, make sure it ends with a slash."
            );
        }

        var cancellationToken = console.RegisterCancellationHandler();
        var errors = new ConcurrentDictionary<Channel, string>();

        // Export
        await console.Output.WriteLineAsync($"Exporting {channels.Count} channel(s)...");
        await console.CreateProgressTicker().StartAsync(async progressContext =>
        {
            await Parallel.ForEachAsync(
                channels,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, ParallelLimit),
                    CancellationToken = cancellationToken
                },
                async (channel, innerCancellationToken) =>
                {
                    try
                    {
                        await progressContext.StartTaskAsync(
                            $"{channel.Category.Name} / {channel.Name}",
                            async progress =>
                            {
                                var guild = await Discord.GetGuildAsync(channel.GuildId, innerCancellationToken);

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
                                    ShouldFormatMarkdown,
                                    ShouldDownloadAssets,
                                    ShouldReuseAssets,
                                    DateFormat
                                );

                                await Exporter.ExportChannelAsync(
                                    request,
                                    progress.ToPercentageBased(),
                                    innerCancellationToken
                                );
                            }
                        );
                    }
                    catch (DiscordChatExporterException ex) when (!ex.IsFatal)
                    {
                        errors[channel] = ex.Message;
                    }
                }
            );
        });

        // Print result
        using (console.WithForegroundColor(ConsoleColor.White))
        {
            await console.Output.WriteLineAsync(
                $"Successfully exported {channels.Count - errors.Count} channel(s)."
            );
        }

        // Print errors
        if (errors.Any())
        {
            await console.Output.WriteLineAsync();

            using (console.WithForegroundColor(ConsoleColor.Red))
            {
                await console.Output.WriteLineAsync(
                    $"Failed to export {errors.Count} channel(s):"
                );
            }

            foreach (var (channel, error) in errors)
            {
                await console.Output.WriteAsync($"{channel.Category.Name} / {channel.Name}: ");

                using (console.WithForegroundColor(ConsoleColor.Red))
                    await console.Output.WriteLineAsync(error);
            }

            await console.Output.WriteLineAsync();
        }

        // Fail the command only if ALL channels failed to export.
        // Having some of the channels fail to export is expected.
        if (errors.Count >= channels.Count)
        {
            throw new CommandException("Export failed.");
        }
    }

    protected async ValueTask ExecuteAsync(IConsole console, IReadOnlyList<Snowflake> channelIds)
    {
        var cancellationToken = console.RegisterCancellationHandler();

        await console.Output.WriteLineAsync("Resolving channel(s)...");

        var channels = new List<Channel>();
        var channelsByGuild = new Dictionary<Snowflake, IReadOnlyList<Channel>>();

        foreach (var channelId in channelIds)
        {
            var channel = await Discord.GetChannelAsync(channelId, cancellationToken);

            // Unwrap categories
            if (channel.Kind == ChannelKind.GuildCategory)
            {
                var guildChannels =
                    channelsByGuild.GetValueOrDefault(channel.GuildId) ??
                    await Discord.GetGuildChannelsAsync(channel.GuildId, cancellationToken);

                foreach (var guildChannel in guildChannels)
                {
                    if (guildChannel.Category.Id == channel.Id)
                        channels.Add(guildChannel);
                }

                // Cache the guild channels to avoid redundant work
                channelsByGuild[channel.GuildId] = guildChannels;
            }
            else
            {
                channels.Add(channel);
            }
        }

        await ExecuteAsync(console, channels);
    }

    public override ValueTask ExecuteAsync(IConsole console)
    {
        // Support Ukraine callout
        if (!IsUkraineSupportMessageDisabled)
        {
            console.Output.WriteLine("┌────────────────────────────────────────────────────────────────────┐");
            console.Output.WriteLine("│   Thank you for supporting Ukraine <3                              │");
            console.Output.WriteLine("│                                                                    │");
            console.Output.WriteLine("│   As Russia wages a genocidal war against my country,              │");
            console.Output.WriteLine("│   I'm grateful to everyone who continues to                        │");
            console.Output.WriteLine("│   stand with Ukraine in our fight for freedom.                     │");
            console.Output.WriteLine("│                                                                    │");
            console.Output.WriteLine("│   Learn more: https://tyrrrz.me/ukraine                            │");
            console.Output.WriteLine("└────────────────────────────────────────────────────────────────────┘");
            console.Output.WriteLine("");
        }

        return default;
    }
}