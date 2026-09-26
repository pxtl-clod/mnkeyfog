using idunno.AtProto;
using Microsoft.Extensions.Logging;
using MnkeyFog.Model;
using MnkeyFog.Model.Template;
using OneOf;
using OneOf.Types;

namespace MnkeyFog.BskyService;

/// <summary>
/// Concrete bot that hosts mnkeyfog games over Bluesky DMs and @mentions.
///
/// DM protocol (per conversation thread):
///   /new [template] [players] — start a game (default: tictactoe, 2 players)
///   /join                     — claim a player slot
///   /board                    — re-render your view of the board
///   /help                     — show help
///   /quit                     — end the game in this thread
///   space name (e.g. "5" or "1A") — play that space
///
/// Public threads: @mention the bot with "space=NN" to play a move on the game
/// keyed to the mention thread's root post; the bot posts the spectator board
/// mentioning all active players.
/// </summary>
public sealed class BskyGameService : BskyBotService {
    public BskyGameService(ILogger logger) : base(logger) { }

    protected internal override Task OnDirectMessageAsync(string conversationId, string senderDid, string message, CancellationToken cancellationToken) {
        return HandleMessageAsync(
            chatKey: $"dm:{conversationId}",
            senderDid: senderDid,
            message: message,
            replyToDm: text => SendDirectMessageAsync(conversationId, text, cancellationToken),
            cancellationToken: cancellationToken
        );
    }

    protected internal override Task OnMentionAsync(AtUri postUri, string authorDid, string message, CancellationToken cancellationToken) {
        return HandleMessageAsync(
            chatKey: $"mention:{postUri}",
            senderDid: authorDid,
            message: message,
            replyToDm: text => SendDirectMessageAsync(authorDid, text, cancellationToken),
            cancellationToken: cancellationToken
        );
    }

    private async Task HandleMessageAsync(
        string chatKey,
        string senderDid,
        string message,
        Func<string, Task> replyToDm,
        CancellationToken cancellationToken
    ) {
        if (ActionParser.ParseCommand(message) is not (var command, var args)) {
            // Not a command — try to parse it as moves in an existing game.
            await TryPlayMovesAsync(chatKey, senderDid, message, replyToDm, cancellationToken);
            return;
        }

        switch (command) {
            case "new":
                await HandleNewAsync(chatKey, senderDid, args, replyToDm, cancellationToken);
                break;
            case "join":
                await HandleJoinAsync(chatKey, senderDid, replyToDm, cancellationToken);
                break;
            case "board":
                await HandleBoardAsync(chatKey, senderDid, replyToDm, cancellationToken);
                break;
            case "help":
                await replyToDm(HelpText);
                break;
            case "quit":
                GameStore.RemoveGame(chatKey);
                await replyToDm("Game ended.");
                break;
            default:
                await replyToDm($"Unknown command '/{command}'. " + HelpText);
                break;
        }
    }

    #region command handlers
    private async Task HandleNewAsync(string chatKey, string creatorDid, string args, Func<string, Task> replyToDm, CancellationToken cancellationToken) {
        var argTokens = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var templateName = argTokens.Length > 0 ? argTokens[0].ToLowerInvariant() : "tictactoe";
        var template = GameTemplates.GetBuiltInGameTemplates()
            .FirstOrDefault(t => string.Equals(t.CommandName, templateName, StringComparison.OrdinalIgnoreCase));

        if (template is null) {
            var known = string.Join(", ", GameTemplates.GetBuiltInGameTemplates().Select(t => t.CommandName));
            await replyToDm($"Unknown game '{templateName}'. Known games: {known}");
            return;
        }

        var playerCount = 2;
        if (argTokens.Length > 1 && int.TryParse(argTokens[1], out var requestedCount)) {
            playerCount = requestedCount;
        }

        var result = GameStore.CreateGame(chatKey, creatorDid, playerCount, template);
        await result.Match(
            session => replyToDm(
                $"New game of {template.CommandName} ({playerCount} players) started.\n"
                + TextBoardRenderer.Render(session.GetViewFor(creatorDid))
                + "\nOther players: /join to claim a slot."
            ),
            invalid => replyToDm(invalid.Message)
        );
    }

    private async Task HandleJoinAsync(string chatKey, string did, Func<string, Task> replyToDm, CancellationToken cancellationToken) {
        if (!GameStore.TryGetGame(chatKey, out var session)) {
            await replyToDm("No game in this thread. Start one with /new [game] [players].");
            return;
        }

        var result = session.Join(did);
        await result.Match(
            player => replyToDm(
                $"You joined as '{player.Value.Mark}'.\n"
                + TextBoardRenderer.Render(session.GetViewFor(did))
            ),
            invalid => replyToDm(invalid.Message)
        );
    }

    private async Task HandleBoardAsync(string chatKey, string did, Func<string, Task> replyToDm, CancellationToken cancellationToken) {
        if (!GameStore.TryGetGame(chatKey, out var session)) {
            await replyToDm("No game in this thread. Start one with /new [game] [players].");
            return;
        }

        await replyToDm(TextBoardRenderer.Render(session.GetViewFor(did)));
    }

    private async Task TryPlayMovesAsync(string chatKey, string senderDid, string message, Func<string, Task> replyToDm, CancellationToken cancellationToken) {
        if (!GameStore.TryGetGame(chatKey, out var session)) {
            return; // no game; stay quiet to avoid replying to every random DM
        }

        var playerIndex = session.GetPlayerIndex(senderDid);
        if (playerIndex is null) {
            await replyToDm("You're not in this game yet. Send /join to claim a slot.");
            return;
        }

        var playerView = session.GetViewFor(senderDid);
        var moves = ActionParser.ParseMoves(playerView, message);
        if (moves.Count == 0) {
            await replyToDm("No valid moves found. Send a space name like '5' (or '1A'), or /help.");
            return;
        }

        var resultTexts = new List<string>();
        foreach (var move in moves) {
            var result = session.Attempt(playerIndex.Value, move);
            resultTexts.Add(result.GetResultText(session.GameState.PlayersState));
            session.GameState.ExecutePendingActions();
        }

        // Advance to the next round once every player has taken their turn.
        if (session.GameState.PlayersState.IsRoundOver) {
            session.GameState.EndRound(out _);
        }

        var boardText = TextBoardRenderer.Render(session.GetViewFor(senderDid));
        await replyToDm(string.Join("\n", resultTexts) + "\n" + boardText);

        if (session.GameState.IsGameOver) {
            GameStore.RemoveGame(chatKey);
        }
    }
    #endregion

    private const string HelpText =
        "Commands:\n"
        + "/new [game] [players] — start a game (games: tictactoe, fog-tictactoe, kriegspiel-tictactoe, gomoku...)\n"
        + "/join — claim a player slot\n"
        + "/board — show the board\n"
        + "/quit — end the game\n"
        + "Reply with a space name (e.g. '5' or '1A') to play a move.";
}
