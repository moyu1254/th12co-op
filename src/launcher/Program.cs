namespace TH12Coop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-test"))
            return SelfTests.Run().GetAwaiter().GetResult();
        static string? Option(string[] arguments, string option)
        {
            var index = Array.IndexOf(arguments, option);
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
        }
        if (args.Contains("--lockstep-test") || args.Contains("--disconnect-test"))
            return SelfTests.Lockstep(Option(args, "--game") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "game")),
                Option(args, "--native"), int.TryParse(Option(args, "--seconds"), out var seconds) ? Math.Clamp(seconds, 20, 300) : 60,
                args.Contains("--disconnect-test"), Option(args, "--guest-game")).GetAwaiter().GetResult();
        if (args.Contains("--startup-guard-test"))
            return SelfTests.StartupGuard(Option(args, "--game") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "game"))).GetAwaiter().GetResult();
        if (args.Contains("--offline-start-test"))
            return SelfTests.OfflineStart(Option(args, "--game") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "game"))).GetAwaiter().GetResult();
        if (args.Contains("--owner-exit-test"))
        {
            var preparedGame = Option(args, "--game");
            if (preparedGame == null) return 2; // Explicit isolated test copy only.
            return SelfTests.OwnerExit(preparedGame, Option(args, "--report")).GetAwaiter().GetResult();
        }
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "TH12 CO-OP", MessageBoxButtons.OK, MessageBoxIcon.Error);
        string? game = null;
        var gameIndex = Array.IndexOf(args, "--game");
        if (gameIndex >= 0 && gameIndex + 1 < args.Length) game = args[gameIndex + 1];
        Application.Run(new LauncherForm(game, args.Contains("--offline")));
        return 0;
    }
}
