namespace MnkeyFog.BskyService;

/// <summary>How the game service talks back: DM reply vs public post, and which board view to render.</summary>
internal sealed record MessageChannel(
    Func<string, Task> RespondAsync,
    Func<GameSession, CancellationToken, Task<string>> RenderBoardForAsync
);
