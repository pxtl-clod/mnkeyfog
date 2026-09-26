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
///   !play <space> [<space>]   — play a move, e.g. !play 5 or !play 1A 2B
///   !board                    — re-render your view of the board
///   !help                     — show help
///   !quit                     — end the game in this thread
///
/// In DMs the '!' prefix is optional ("join", "play 5" and a bare space name
/// like "5" all work). In public chats the '!' prefix is required and other
/// plain text is ignored — the bot never infers a command from it.
///
/// Secret games (Kriegspiel or synchronous play) refuse !play in public
/// threads, warning the player to move via DM instead.
///
/// Public threads: @mention the bot with "!play space=NN" to play a move on the
/// game keyed to the mention thread's root post; the bot posts the spectator
/// board (no fog leak) mentioning all active players (up to 10 handles).
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
            ),
            IsPublic: false
        );
        return HandleMessageAsync($"dm:{conversationId}", senderDid, message, channel, cancellationToken, isPrefixOptional: true);
    }

    protected internal override Task OnMentionAsync(AtUri postUri, string authorDid, string message, CancellationToken cancellationToken) {
        var channel = new MessageChannel(
            RespondAsync: text => PostAsync(text, cancellationToken),
            RenderBoardForAsync: RenderPublicBoardAsync,
            IsPublic: true
        );
        return HandleMessageAsync($"mention:{postUri}", authorDid, message, channel, cancellationToken, isPrefixOptional: false);
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

    /// <summary>
    /// Dispatch a single inbound message (DM or @mention) to its handler.
    /// </summary>
    /// <param name="chatKey">
    /// Identifier of the chat thread the message arrived in, used to key the
    /// game session: one game per DM conversation ("dm:{conversationId}") or
    /// per public mention thread ("mention:{root post URI}").
    /// </param>
    /// <param name="senderDid">The Bluesky DID of the message's author.</param>
    /// <param name="message">The raw text of the message.</param>
    /// <param name="channel">How to respond to the sender (DM vs public post).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="isPrefixOptional">
    /// True for DMs, where the '!' command prefix is optional and plain text
    /// that names spaces is treated as moves. False for public chats, where the
    /// prefix is required and plain text is ignored.
    /// </param>
    private Task HandleMessageAsync(
        string chatKey,
        string senderDid,
        string message,
        MessageChannel channel,
        CancellationToken cancellationToken,
        bool isPrefixOptional
    )
    => ActionParser.ParseCommand(message, isPrefixOptional).Match(
        command => HandleCommandAsync(chatKey, senderDid, command, channel, cancellationToken),
        // Not a command. In DMs plain text may still be moves; in public chats
        // it is ignored.
        _ => isPrefixOptional
            ? PlayPlainMovesAsync(chatKey, senderDid, message, channel, cancellationToken)
            : Task.CompletedTask);

    private async Task HandleCommandAsync(string chatKey, string senderDid, CommandToken command, MessageChannel channel, CancellationToken cancellationToken) {
        switch (command.Command) {
            case "new":
                await HandleNewAsync(chatKey, senderDid, command.Arg, channel, cancellationToken);
                break;
            case "join":
                await HandleJoinAsync(chatKey, senderDid, channel, cancellationToken);
                break;
            case "play":
                await HandlePlayAsync(chatKey, senderDid, command.Arg, channel, cancellationToken);
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
                await channel.RespondAsync($"Unknown command '!{command.Command}'. " + HelpText);
                break;
        }
    }

    #region command handlers
    private async Task HandleNewAsync(string chatKey, string creatorDid, IReadOnlyList<string> commandArgs, MessageChannel channel, CancellationToken cancellationToken) {
        var templateName = commandArgs.Count > 0 ? commandArgs[0].ToLowerInvariant() : "tictactoe";
        var template = GameTemplates.GetBuiltInGameTemplates()
            .FirstOrDefault(t => string.Equals(t.CommandName, templateName, StringComparison.OrdinalIgnoreCase));

        if (template is null) {
            var known = string.Join(", ", GameTemplates.GetBuiltInGameTemplates().Select(t => t.CommandName));
            await channel.RespondAsync($"Unknown game '{templateName}'. Known games: {known}");
            return;
        }

        var playerCount = 2;
        if (commandArgs.Count > 1 && int.TryParse(commandArgs[1], out var requestedCount)) {
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

    /// <summary>Handle the <c>!play</c> command: play the moves named by its arguments.</summary>
    private async Task HandlePlayAsync(
        string chatKey,
        string senderDid,
        IReadOnlyList<string> commandArgs,
        MessageChannel channel,
        CancellationToken cancellationToken
    ) {
        if (!GameStore.TryGetGame(chatKey, out var session)) {
            await channel.RespondAsync("No game in this thread. Start one with !new [game] [players].");
            return;
        }

        // Secret games must not be played in public threads — the attempt and
        // its result would reveal fogged information to everyone reading.
        if (channel.IsPublic && session.IsSecret) {
            await channel.RespondAsync(
                "WARNING: this game is secret (Kriegspiel or synchronous play) — do not make these "
                + "moves publicly! DM me and play there instead.");
            return;
        }

        var playerIndex = session.GetPlayerIndex(senderDid);
        if (playerIndex is null) {
            await channel.RespondAsync("You're not in this game yet. Send !join to claim a slot.");
            return;
        }

        var playerView = session.GetViewFor(senderDid);
        var moves = ActionParser.ParsePlay(playerView, commandArgs);
        if (moves.Count == 0) {
            await channel.RespondAsync("No valid spaces in !play. Usage: !play <space> — e.g. '!play 5' or '!play 1A'.");
            return;
        }

        await ExecuteMovesAsync(chatKey, session, playerIndex.Value, moves, channel, cancellationToken);
    }

    /// <summary>
    /// DM fallback for the optional '!' prefix: plain text that names spaces is
    /// played as moves. Stays quiet unless the text actually contains moves, so
    /// ordinary chat is not nagged.
    /// </summary>
    private async Task PlayPlainMovesAsync(
        string chatKey,
        string senderDid,
        string message,
        MessageChannel channel,
        CancellationToken cancellationToken
    ) {
        if (!GameStore.TryGetGame(chatKey, out var session)) {
            return; // no game; stay quiet to avoid replying to every random DM
        }

        var moves = ActionParser.ParsePlay(session.GetViewFor(senderDid), ActionParser.Tokenize(message));
        if (moves.Count == 0) {
            return; // not move-like text; stay quiet
        }

        var playerIndex = session.GetPlayerIndex(senderDid);
        if (playerIndex is null) {
            await channel.RespondAsync("You're not in this game yet. Send !join to claim a slot.");
            return;
        }

        await ExecuteMovesAsync(chatKey, session, playerIndex.Value, moves, channel, cancellationToken);
    }

    /// <summary>
    /// Attempt the given moves, advance the round if it is over, report the
    /// results with the board, and persist or clean up the game.
    /// </summary>
    private async Task ExecuteMovesAsync(
        string chatKey,
        GameSession session,
        int playerIndex,
        IReadOnlyList<GameAction> moves,
        MessageChannel channel,
        CancellationToken cancellationToken
    ) {
        var resultTexts = new List<string>();
        foreach (var move in moves) {
            var result = session.Attempt(playerIndex, move);
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

    private const string HelpText =
        "Commands:\n"
        + "!new [game] [players] — start a game (games: tictactoe, fog-tictactoe, kriegspiel-tictactoe, gomoku...)\n"
        + "!join — claim a player slot\n"
        + "!play <space> [<space>] — play a move, e.g. '!play 5' or '!play 1A'\n"
        + "!board — show the board\n"
        + "!quit — end the game\n"
        + "In DMs the '!' is optional (plain text naming spaces also plays moves). "
        + "In public chats '!' is required. Secret games (Kriegspiel/synchronous) "
        + "must be played in DMs only.";
}
