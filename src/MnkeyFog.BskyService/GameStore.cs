using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using MnkeyFog.BskyService.Persistence;
using MnkeyFog.Model.Template;
using Newtonsoft.Json;

namespace MnkeyFog.BskyService;

/// <summary>
/// Thread-safe in-memory store of active games keyed by a chat/thread identifier
/// (Bluesky DM conversation ID for private games, root-post URI for public threads).
///
/// When a persistence directory is configured, every change is mirrored to a JSON
/// file (one per game) and reloaded on construction for crash recovery.
/// </summary>
public sealed class GameStore {
    private readonly ConcurrentDictionary<string, GameSession> _games = new();
    private readonly string? _persistDirectory;

    /// <summary>Default marks used for players when starting a game.</summary>
    public static readonly string[] DefaultMarks = ["X", "O", "A", "B", "C", "D", "E", "F"];

    private static readonly JsonSerializerSettings _persistenceSettings = new() {
        TypeNameHandling = TypeNameHandling.Objects,
        SerializationBinder = ModelTypesBinder.Instance,
    };

    #region constructors
    /// <summary>
    /// Create a store. When <paramref name="persistDirectory"/> is provided, previously
    /// persisted games are loaded from it and changes are saved back to it.
    /// </summary>
    public GameStore(string? persistDirectory = null) {
        _persistDirectory = persistDirectory;

        if (persistDirectory is not null) {
            LoadPersistedGames(persistDirectory);
        }
    }
    #endregion

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

        PersistSession(created);
        return created;
    }

    /// <summary>Try to retrieve a game session.</summary>
    public bool TryGetGame(string chatId, [NotNullWhen(true)] out GameSession? session) {
        return _games.TryGetValue(chatId, out session);
    }

    /// <summary>Remove a game from the store (e.g. when finished or resigned).</summary>
    public bool RemoveGame(string chatId) {
        var removed = _games.TryRemove(chatId, out _);
        if (removed) {
            DeletePersistedGame(chatId);
        }

        return removed;
    }

    /// <summary>List all active game sessions.</summary>
    public IReadOnlyList<GameSession> GetActiveGames() {
        return _games.Values.ToList();
    }

    /// <summary>Persist the current state of a game (e.g. after a move).</summary>
    public void Save(GameSession session) {
        ArgumentNullException.ThrowIfNull(session);
        PersistSession(session);
    }
    #endregion

    #region persistence
    private void LoadPersistedGames(string directory) {
        if (!Directory.Exists(directory)) {
            return;
        }

        foreach (var file in Directory.GetFiles(directory, "*.json")) {
            GameSession? session;
            try {
                var persisted = JsonConvert.DeserializeObject<PersistedSession>(File.ReadAllText(file), _persistenceSettings);
                session = persisted is null ? null : new GameSession(
                    persisted.ChatId,
                    persisted.CreatorDid,
                    persisted.CreatedAt,
                    persisted.PlayerDids.ToDictionary(p => p.Did, p => p.PlayerIndex),
                    persisted.GameState
                );
            } catch (Exception ex) when (ex is Newtonsoft.Json.JsonException or System.Text.Json.JsonException or IOException) {
                continue; // corrupted file: skip it rather than fail startup
            }

            if (session is not null && _games.TryAdd(session.ChatId, session)) {
                // loaded
            }
        }
    }

    private void PersistSession(GameSession session) {
        if (_persistDirectory is null) {
            return;
        }

        try {
            Directory.CreateDirectory(_persistDirectory);
            var persisted = new PersistedSession(
                session.ChatId,
                session.CreatorDid,
                session.CreatedAt,
                [.. session.PlayerDids.Select(kvp => new PersistedPlayerDid(kvp.Key, kvp.Value))],
                session.GameState
            );
            var json = JsonConvert.SerializeObject(persisted, _persistenceSettings);
            File.WriteAllText(GetPersistPath(session.ChatId), json);
        } catch (IOException) {
            // best-effort persistence: a failed save must not break gameplay
        }
    }

    private void DeletePersistedGame(string chatId) {
        if (_persistDirectory is null) {
            return;
        }

        try {
            File.Delete(GetPersistPath(chatId));
        } catch (IOException) {
            // best-effort persistence
        }
    }

    private string GetPersistPath(string chatId) {
        var directory = _persistDirectory!;
        // Hash the chat ID into a filesystem-safe file name.
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(chatId)))[..16];
        return Path.Combine(directory, $"{hash}.json");
    }
    #endregion
}
