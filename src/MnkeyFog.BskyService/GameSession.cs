using MnkeyFog.Model.Template;

namespace MnkeyFog.BskyService;

/// <summary>
/// Wraps a <see cref="GameState"/> with Bluesky metadata: which chat the game runs in,
/// who created it, and a mapping from player DIDs to player indices.
/// </summary>
public sealed class GameSession {
    #region data members
    public string ChatId { get; }
    public GameState GameState { get; }
    public string CreatorDid { get; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Maps Bluesky DIDs to player indices in the game.</summary>
    public IReadOnlyDictionary<string, int> PlayerDids => _playerByDid;
    private readonly ConcurrentDictionary<string, int> _playerByDid = new();
    #endregion

    public GameSession(string chatId, string creatorDid, IReadOnlyList<PlayerInfo> playerMarks, IGameTemplate template) {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        ArgumentException.ThrowIfNullOrWhiteSpace(creatorDid);

        ChatId = chatId;
        CreatorDid = creatorDid;
        GameState = new GameState(playerMarks.ToArray(), template, isRandomPlayerOrder: false);

        // Creator claims the first player slot.
        _playerByDid[creatorDid] = 0;
    }

    #region player management
    /// <summary>
    /// Have the DID behind <paramref name="did"/> claim the next unclaimed player slot.
    /// </summary>
    public OneOf<Result<PlayerInfo>, CommandError> Join(string did) {
        ArgumentException.ThrowIfNullOrWhiteSpace(did);

        lock (_playerByDid) {
            if (_playerByDid.TryGetValue(did, out var existingIndex)) {
                return new Result<PlayerInfo>(GameState.PlayersState.PlayerInfos[existingIndex]);
            }

            var claimedIndices = new HashSet<int>(_playerByDid.Values);
            for (var i = 0; i < GameState.PlayersState.PlayerInfos.Count; i++) {
                if (!claimedIndices.Contains(i)) {
                    _playerByDid[did] = i;
                    return new Result<PlayerInfo>(GameState.PlayersState.PlayerInfos[i]);
                }
            }

            return new CommandError($"Game in this thread is full ({GameState.PlayersState.PlayerInfos.Count} players).");
        }
    }

    /// <summary>Get the player index for a Bluesky DID, or null if the DID is not playing.</summary>
    public int? GetPlayerIndex(string did) {
        return PlayerDids.TryGetValue(did, out var index) ? index : null;
    }
    #endregion

    #region game views
    /// <summary>Get the player-private (fogged) view for the given DID.</summary>
    public GameView GetViewFor(string did) {
        return GameState.GetView(GetPlayerIndex(did));
    }

    /// <summary>Get the fully-fogged spectator view (hides everything from everyone).</summary>
    public GameView GetSpectatorView() {
        return GameState.GetSpectatorView();
    }
    #endregion

    #region commands
    /// <summary>Attempt a move for the given player. Returns the play result.</summary>
    public IPlayActionResult Attempt(int playerIndex, GameAction action) {
        return GameState.Attempt(action.GetPlayerAction(playerIndex));
    }
    #endregion
}
