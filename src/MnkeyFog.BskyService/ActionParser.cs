using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using MnkeyFog.Model.Template;

namespace MnkeyFog.BskyService;

/// <summary>
/// Parses bot-directed text messages into commands and game actions.
///
/// Messages are whitespace-tokenized. Tokens starting with '!' are commands.
/// Any other token that names a valid space (e.g. "5", "1A", "2B") is a move.
/// </summary>
public static partial class ActionParser {
    [GeneratedRegex(@"^space\s*[=:]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex SpaceAssignmentPrefixRegex();

    /// <summary>
    /// Parse a command token. Returns a <see cref="CommandToken"/> with the
    /// lowercased command name (without the '!') and the remainder of the
    /// message, or <see cref="None"/> if the message is not a command.
    /// </summary>
    public static OneOf<CommandToken, None> ParseCommand(string message) {
        var trimmed = message.Trim();
        if (!trimmed.StartsWith('!')) {
            return new None();
        }

        var spaceIndex = trimmed.IndexOf(' ');
        var command = (spaceIndex < 0 ? trimmed : trimmed[..spaceIndex])[1..].ToLowerInvariant();
        var args = spaceIndex < 0 ? "" : trimmed[(spaceIndex + 1)..].Trim();
        return new CommandToken(command, args);
    }

    /// <summary>
    /// Parse all space-move tokens in a message into game actions. The player's
    /// view is used to resolve space names (board prefixes etc.); fog is not a
    /// factor — fogged spaces remain playable, per Kriegspiel rules.
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
            if (rawToken.StartsWith('!')) {
                continue; // command tokens are not moves
            }

            var token = SpaceAssignmentPrefixRegex().Replace(rawToken, "");
            if (TryCreateMove(factory, playerView, token, out var move)) {
                moves.Add(move);
            }
        }

        return moves;
    }

    /// <summary>
    /// Resolve a space name to a move action. Returns false if the token is not
    /// a valid space name. Whether the move is allowed is decided by the model
    /// at attempt time (e.g. Known-occupied spaces return PositionAlreadyPlayed).
    /// The model's lookup throws on invalid board-name prefixes (multi-board
    /// games), so this is guarded.
    /// </summary>
    private static bool TryCreateMove(
        GameActionFactoryForSpace factory,
        GameView playerView,
        string token,
        [NotNullWhen(true)] out GameAction? move
    ) {
        move = null;
        try {
            if (!playerView.TryGetCoordinatesFromSpaceName(token, out sbyte boardIndex, out sbyte col, out sbyte row)) {
                return false;
            }
            move = factory.Create(boardIndex, col, row);
            return true;
        } catch (ArgumentException) {
            return false;
        }
    }
}

/// <summary>A parsed bot command: the lowercased command name and its argument string.</summary>
public sealed record CommandToken(string Command, string Args);
