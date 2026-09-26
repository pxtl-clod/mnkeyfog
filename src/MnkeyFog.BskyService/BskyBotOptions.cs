namespace MnkeyFog.BskyService;

/// <summary>
/// Configuration for connecting a bot to Bluesky.
/// </summary>
public record BskyBotOptions {
    /// <summary>The bot account's handle, e.g. "mybot.bsky.social".</summary>
    public required string Handle { get; init; }

    /// <summary>The bot account's app-password.</summary>
    public required string AppPassword { get; init; }

    /// <summary>Bluesky PDS service URI. Defaults to https://bsky.social.</summary>
    public Uri? Service { get; init; }

    /// <summary>Jetstream endpoint. Defaults to wss://jetstream1.us-west.bsky.network.</summary>
    public Uri? JetstreamUri { get; init; }

    /// <summary>How often to poll DM conversation logs. Defaults to 10 seconds.</summary>
    public TimeSpan? DirectMessagePollInterval { get; init; }

    /// <summary>
    /// Optional directory for game-state persistence. When set, active games are
    /// mirrored to JSON files there and reloaded on startup (crash recovery).
    /// </summary>
    public string? GamePersistenceDirectory { get; init; }
}
