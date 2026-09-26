using System.Diagnostics.CodeAnalysis;
using MnkeyFog.Model.Template;

namespace MnkeyFog.BskyService;

/// <summary>
/// Thread-safe in-memory store of active games keyed by a chat/thread identifier
/// (Bluesky DM conversation ID for private games, root-post URI for public threads).
/// </summary>
public sealed class GameStore {
    private readonly ConcurrentDictionary<string, GameSession> _games = new();

    /// <summary>Default marks used for players when starting a game.</summary>
    public static readonly string[] DefaultMarks = ["X", "O", "A", "B", "C", "D", "E", "F"];

    #region commands
    /// <summary>
    /// Create a new game in the given chat. Fails if a game already exists there.
    /// </summary>
    public OneOf<GameSession, CommandError> CreateGame(string chatId, string creatorDid, int playerCount, IGameTemplate template) {
        if (!template.LegalPlayerCounts.Contains(playerCount)) {
            return new CommandError(
                $"Game '{template.CommandName}' supports player-counts: {string.Join(", ", template.LegalPlayerCounts)}."
            );
        }

        var players = DefaultMarks
            .Take(playerCount)
            .Select(mark => new PlayerInfo(mark))
            .ToArray();

        GameSession? created;
        try {
            created = new GameSession(chatId, creatorDid, players, template);
        } catch (ApplicationException ex) {
            return new CommandError(ex.Message);
        }

        if (!_games.TryAdd(chatId, created)) {
            return new CommandError($"A game is already running in this thread. Use /quit to end it first.");
        }

        return created;
    }

    /// <summary>Try to retrieve a game session.</summary>
    public bool TryGetGame(string chatId, [NotNullWhen(true)] out GameSession? session) {
        return _games.TryGetValue(chatId, out session);
    }

    /// <summary>Remove a game from the store (e.g. when finished or resigned).</summary>
    public bool RemoveGame(string chatId) {
        return _games.TryRemove(chatId, out _);
    }

    /// <summary>List all active game sessions.</summary>
    public IReadOnlyList<GameSession> GetActiveGames() {
        return _games.Values.ToList();
    }
    #endregion
}
