namespace MnkeyFog.BskyService.Persistence;

/// <summary>
/// Persistence DTO for a <see cref="GameSession"/>: game state plus chat metadata.
/// A list of entries is used instead of a dictionary so that the polymorphic
/// type names stay resolvable by <see cref="ModelTypesBinder"/>.
/// </summary>
internal sealed record PersistedSession(
    string ChatId,
    string CreatorDid,
    DateTimeOffset CreatedAt,
    List<PersistedPlayerDid> PlayerDids,
    GameState GameState
);
