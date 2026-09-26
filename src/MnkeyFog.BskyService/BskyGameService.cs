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
///   !new [template] [players] — start a game (default: tictactoe, 2 players)
///   !join                     — claim a player slot
///   !board                    — re-render your view of the board
///   !help                     — show help
///   !quit                     — end the game in this thread
///   space name (e.g. "5" or "1A") — play that space
///
/// Public threads: @mention the bot with "space=NN" to play a move on the game
/// keyed to the mention thread's root post; the bot posts the spectator board
/// (no fog leak) mentioning all active players (up to 10 handles).
/// </summary>
public sealed class BskyGameService : BskyBotService {
    /// <summary>Bluesky limits a post to mentioning at most 10 users.</summary>
    private const int MaxMentionsPerPost = 10;

    public BskyGameService(ILogger logger) : base(logger) { }

    protected internal override Task OnDirectMessageAsync(string conversationId, string senderDid, string message, CancellationToken cancellationToken) {
        var channel = new MessageChannel(
            RespondAsync: text => SendDirectMessageAsync(conversationId, text, cancellationToken),
            RenderBoardForAsync: (session, _) => Task.FromResult(
                TextBoardRenderer.Render(session.GetViewFor(senderDid))
            )
        );
        return HandleMessageAsync($"dm:{conversationId}", senderDid, message, channel, cancellationToken);
    }

    protected internal override Task OnMentionAsync(AtUri postUri, string authorDid, string message, CancellationToken cancellationToken) {
        var channel = new MessageChannel(
            RespondAsync: text => PostAsync(text, cancellationToken),
            RenderBoardForAsync: RenderPublicBoardAsync
        );
        return HandleMessageAsync($"mention:{postUri}", authorDid, message, channel, cancellationToken);
    }

    private async Task<string> RenderPublicBoardAsync(GameSession session, CancellationToken cancellationToken) {
        var board = TextBoardRenderer.Render(session.GetSpectatorView());

        // Append @mentions of active players so everyone in the thread sees the call-out.
        var mentions = await GetActivePlayerHandlesAsync(session, cancellationToken);
        return mentions.Count == 0 ? board : $"{board}\n{string.Join(" ", mentions.Select(h => $"@{h}"))}";
    }

    /// <summary>Resolve handles for the DIDs of players who have joined the session.</summary>
    private async Task<IReadOnlyList<string>> GetActivePlayerHandlesAsync(GameSession session, CancellationToken cancellationToken) {
        if (session.PlayerDids.Count == 0) {
            return [];
        }

        var profiles = await Agent.GetProfiles(
            session.PlayerDids.Keys.Select(did => (AtIdentifier)(Did)did).ToList(),
            cancellationToken: cancellationToken
        );

        return profiles.Succeeded && profiles.Result is not null
            ? [.. profiles.Result
                .Where(p => p.Handle is not null)
                .Take(MaxMentionsPerPost)
                .Select(p => (string)p.Handle!)]
            : [];
    }

    private async Task HandleMessageAsync(string chatKey, string senderDid, string message, MessageChannel channel, CancellationToken cancellationToken) {
        var parsedCommand = ActionParser.ParseCommand(message);
        if (parsedCommand.IsT1) {
            // Not a command — try to parse it as moves in an existing game.
            await TryPlayMovesAsync(chatKey, senderDid, message, channel, cancellationToken);
            return;
        }

        var (command, args) = parsedCommand.AsT0;
        switch (command) {
            case "new":
                await HandleNewAsync(chatKey, senderDid, args, channel, cancellationToken);
                break;
            case "join":
                await HandleJoinAsync(chatKey, senderDid, channel, cancellationToken);
                break;
            case "board":
                await HandleBoardAsync(chatKey, senderDid, channel, cancellationToken);
                break;
            case "help":
                await channel.RespondAsync(HelpText);
                break;
            case "quit":
                GameStore.RemoveGame(chatKey);
                await channel.RespondAsync("Game ended.");
                break;
            default:
                await channel.RespondAsync($"Unknown command '!{command}'. " + HelpText);
                break;
        }
    }

    #region command handlers
    private async Task HandleNewAsync(string chatKey, string creatorDid, string args, MessageChannel channel, CancellationToken cancellationToken) {
        var argTokens = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var templateName = argTokens.Length > 0 ? argTokens[0].ToLowerInvariant() : "tictactoe";
        var template = GameTemplates.GetBuiltInGameTemplates()
            .FirstOrDefault(t => string.Equals(t.CommandName, templateName, StringComparison.OrdinalIgnoreCase));

        if (template is null) {
            var known = string.Join(", ", GameTemplates.GetBuiltInGameTemplates().Select(t => t.CommandName));
            await channel.RespondAsync($"Unknown game '{templateName}'. Known games: {known}");
            return;
        }

        var playerCount = 2;
        if (argTokens.Length > 1 && int.TryParse(argTokens[1], out var requestedCount)) {
            playerCount = requestedCount;
        }

        var result = GameStore.CreateGame(chatKey, creatorDid, playerCount, template);
        await result.Match(
            async session => await channel.RespondAsync(
                $"New game of {template.CommandName} ({playerCount} players) started.\n"
                + await channel.RenderBoardForAsync(session, cancellationToken)
                + "\nOther players: !join to claim a slot."
            ),
            error => channel.RespondAsync(error.Message)
        );
    }

    private async Task HandleJoinAsync(string chatKey, string did, MessageChannel channel, CancellationToken cancellationToken) {
        if (!GameStore.TryGetGame(chatKey, out var session)) {
            await channel.RespondAsync("No game in this thread. Start one with !new [game] [players].");
            return;
        }

        var result = session.Join(did);
        await result.Match(
            async player => {
                GameStore.Save(session);
                await channel.RespondAsync(
                    $"You joined as '{player.Value.Mark}'.\n"
                    + await channel.RenderBoardForAsync(session, cancellationToken)
                );
            },
            error => channel.RespondAsync(error.Message)
        );
    }

    private async Task HandleBoardAsync(string chatKey, string did, MessageChannel channel, CancellationToken cancellationToken) {
        if (!GameStore.TryGetGame(chatKey, out var session)) {
            await channel.RespondAsync("No game in this thread. Start one with !new [game] [players].");
            return;
        }

        await channel.RespondAsync(await channel.RenderBoardForAsync(session, cancellationToken));
    }

    private async Task TryPlayMovesAsync(string chatKey, string senderDid, string message, MessageChannel channel, CancellationToken cancellationToken) {
        if (!GameStore.TryGetGame(chatKey, out var session)) {
            return; // no game; stay quiet to avoid replying to every random DM
        }

        var playerIndex = session.GetPlayerIndex(senderDid);
        if (playerIndex is null) {
            await channel.RespondAsync("You're not in this game yet. Send !join to claim a slot.");
            return;
        }

        var playerView = session.GetViewFor(senderDid);
        var moves = ActionParser.ParseMoves(playerView, message);
        if (moves.Count == 0) {
            await channel.RespondAsync("No valid moves found. Send a space name like '5' (or '1A'), or !help.");
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

        await channel.RespondAsync(string.Join("\n", resultTexts) + "\n" + await channel.RenderBoardForAsync(session, cancellationToken));

        if (session.GameState.IsGameOver) {
            GameStore.RemoveGame(chatKey);
        } else {
            GameStore.Save(session);
        }
    }
    #endregion

    /// <summary>How the game service talks back: DM reply vs public post, and which board view to render.</summary>
    private sealed record MessageChannel(
        Func<string, Task> RespondAsync,
        Func<GameSession, CancellationToken, Task<string>> RenderBoardForAsync
    );

    private const string HelpText =
        "Commands:\n"
        + "!new [game] [players] — start a game (games: tictactoe, fog-tictactoe, kriegspiel-tictactoe, gomoku...)\n"
        + "!join — claim a player slot\n"
        + "!board — show the board\n"
        + "!quit — end the game\n"
        + "Reply with a space name (e.g. '5' or '1A') to play a move.";
}
