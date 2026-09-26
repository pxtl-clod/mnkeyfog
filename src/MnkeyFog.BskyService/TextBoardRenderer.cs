using System.Text;

namespace MnkeyFog.BskyService;

/// <summary>
/// Renders a game board as compact text, using the same pipe-drawing characters as
/// the CommandLine renderer's <c>FullPipes</c> configuration but with minimal chrome
/// to fit the Bluesky 300-character post limit (DMs get a larger budget).
///
/// Empty cells show their own space name (e.g. "5" or "2B") so players can simply
/// reply with the name of the space they want to play.
/// </summary>
public static class TextBoardRenderer {
    private const int CellWidth = 3;
    private const char ColumnSeparator = '│';
    private const char RowSeparator = '─';
    private const char RowIntersection = '┼';

    /// <summary>
    /// Render the board from the given view, with turn/score info, budgeted to
    /// <paramref name="maxChars"/> characters.
    /// </summary>
    public static string Render(GameView gameView, int maxChars = 300) {
        var sb = new StringBuilder(256);

        // Header: turn (or winners when the game is over).
        if (gameView.IsGameOver) {
            var winners = gameView.ScoreCard.Highest.AsPlayerInfos(gameView.PlayersState).ToList();
            sb.Append(winners.Count == 0
                ? "Game over. Draw."
                : $"Game over. Winner: {string.Join(", ", winners.Select(w => w.Mark))}");
        } else {
            var turnMarks = string.Join(
                ",",
                gameView.PlayersState.PlayersAvailableForTurn.Select(p => p.Mark)
            );
            sb.Append($"Turn: {turnMarks}");
        }

        sb.AppendLine();

        // Board(s).
        var showBoardNames = gameView.BoardsCount > 1;
        for (sbyte boardIndex = 0; boardIndex < gameView.BoardsCount; boardIndex++) {
            var board = gameView.GetBoardViewByIndex(boardIndex);
            if (showBoardNames) {
                sb.Append($"Board {board.BoardName}:");
                sb.AppendLine();
            }

            RenderBoard(sb, gameView, board);
        }

        // Footer: scores.
        var scoreSummary = RenderScores(gameView);
        if (scoreSummary.Length > 0) {
            sb.AppendLine();
            sb.Append(scoreSummary);
        }

        var result = sb.ToString();
        return result.Length <= maxChars ? result : result[..maxChars];
    }

    /// <summary>
    /// Render a single board. Empty spaces show their space name; taken spaces show
    /// the occupying mark (from the perspective baked into the view — fog for players).
    /// </summary>
    private static void RenderBoard(StringBuilder sb, GameView gameView, BoardView board) {
        for (sbyte row = 0; row < board.RowCount; row++) {
            if (row > 0) {
                sb.Append(RowSeparator, CellWidth);
                for (sbyte col = 1; col < board.ColumnCount; col++) {
                    sb.Append(RowIntersection);
                    sb.Append(RowSeparator, CellWidth);
                }
                sb.AppendLine();
            }

            for (sbyte col = 0; col < board.ColumnCount; col++) {
                if (col > 0) {
                    sb.Append(ColumnSeparator);
                }

                var space = board.GetSpaceView(col, row);
                var cell = space.MarkIndex.HasValue
                    ? gameView.PlayersState.GetMark(space.MarkIndex)
                    : board.GetSpaceName(gameView, col, row);
                sb.Append(PadCell(cell));
            }

            sb.AppendLine();
        }
    }

    private static string PadCell(string cell) {
        return cell.Length >= CellWidth ? cell : cell.PadLeft((CellWidth + cell.Length + 1) / 2).PadRight(CellWidth);
    }

    private static string RenderScores(GameView gameView) {
        var parts = gameView.ScoreCard.PlayerScores
            .Select(ps => $"{gameView.PlayersState.GetMark(ps.PlayerIndex)}:{ps.Score}");
        var joined = string.Join(" ", parts);
        return joined.Length == 0 ? "" : $"Scores: {joined}";
    }
}
