using System.Text.RegularExpressions;
using MnkeyFog.Model.Template;

namespace MnkeyFog.BskyService;

/// <summary>
/// Parses bot-directed text messages into commands and game actions.
///
/// Messages are whitespace-tokenized. Tokens starting with '/' are commands.
/// Any other token that names a valid space (e.g. "5", "1A", "2B") is a move.
/// </summary>
public static partial class ActionParser {
    [GeneratedRegex(@"^space\s*[=:]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex SpaceAssignmentPrefixRegex();

    /// <summary>
    /// Parse a command token. Returns the lowercased command name (without the '/')
    /// and the remainder of the message, or null if the message is not a command.
    /// </summary>
    public static (string Command, string Args)? ParseCommand(string message) {
        var trimmed = message.Trim();
        if (!trimmed.StartsWith('/')) {
            return null;
        }

        var spaceIndex = trimmed.IndexOf(' ');
        var command = (spaceIndex < 0 ? trimmed : trimmed[..spaceIndex])[1..].ToLowerInvariant();
        var args = spaceIndex < 0 ? "" : trimmed[(spaceIndex + 1)..].Trim();
        return (command, args);
    }

    /// <summary>
    /// Parse all space-move tokens in a message into game actions, using the given
    /// player's view (so fogged spaces are not playable) to resolve space names.
    /// </summary>
    public static IReadOnlyList<GameAction> ParseMoves(GameView playerView, string message) {
        var moves = new List<GameAction>();
        GameActionFactoryForSpace? factory = playerView.AvailableActions
            .OfType<GameActionFactoryForSpace>()
            .FirstOrDefault();

        if (factory is null) {
            return moves;
        }

        foreach (var rawToken in message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) {
            if (rawToken.StartsWith('/')) {
                continue; // command tokens are not moves
            }

            var token = SpaceAssignmentPrefixRegex().Replace(rawToken, "");
            if (TryCreateMove(factory, playerView, token) is { } move) {
                moves.Add(move);
            }
        }

        return moves;
    }

    /// <summary>
    /// Resolve a space name to a move action, or null if the token is not a valid,
    /// currently-playable space. The model's lookup throws on invalid board-name
    /// prefixes (multi-board games), so this is guarded.
    /// </summary>
    private static GameAction? TryCreateMove(GameActionFactoryForSpace factory, GameView playerView, string token) {
        try {
            return playerView.TryGetCoordinatesFromSpaceName(token, out sbyte boardIndex, out sbyte col, out sbyte row)
                ? factory.Create(boardIndex, col, row)
                : null;
        } catch (ArgumentException) {
            return null;
        }
    }
}
