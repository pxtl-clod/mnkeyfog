namespace MnkeyFog.BskyService.Persistence;

/// <summary>One DID-to-player-index mapping within a persisted <see cref="PersistedSession"/>.</summary>
internal sealed record PersistedPlayerDid(string Did, int PlayerIndex);
