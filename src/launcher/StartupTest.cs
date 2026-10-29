using System.Text.Json;

namespace TH12Coop;

internal static partial class SelfTests
{
    private static GameSession? ownerExitSession;
    // An external runner must wait for this launcher and then verify the PID
    // exits. This report proves only startup; it deliberately does not claim
    // the post-exit assertion, which this process cannot observe itself.
    public static async Task<int> OwnerExit(string gameDirectory, string? reportPath)
    {
        reportPath = Path.GetFullPath(reportPath ?? Path.Combine(AppContext.BaseDirectory, "owner-exit-test-startup.json"));
        GameSession? session = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            session = await GameSession.Start(gameDirectory, false, timeout.Token);
            if (session.Network != null || session.HasExited || session.Status != 1)
                throw new IOException("Owner-exit test did not reach native offline ready.");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                started = true, launcherPid = Environment.ProcessId, gamePid = session.Process.Id,
                gameStartedUtc = session.Process.StartTime.ToUniversalTime(), gameDirectory,
                nativeStatus = session.Status, exitWithoutDispose = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            // Keep the SafeHandle alive until OS process teardown, without
            // calling StopGame, Dispose, or running a finally-based cleanup.
            ownerExitSession = session;
            GC.KeepAlive(ownerExitSession);
            Environment.Exit(0);
            return 0;
        }
        catch (Exception error)
        {
            ownerExitSession = null;
            if (session != null)
            {
                try { await session.StopGameAsync(); }
                finally { session.Dispose(); }
            }
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { started = false, error = error.ToString() },
                new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }

    public static async Task<int> OfflineStart(string gameDirectory)
    {
        GameSession? session = null;
        var passed = false;
        int? pid = null;
        string? error = null;
        uint status = 0;
        var closed = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            session = await GameSession.Start(gameDirectory, false, timeout.Token);
            pid = session.Process.Id;
            await Task.Delay(1500, timeout.Token);
            status = session.Status;
            if (session.Network != null || session.HasExited || status != 1)
                throw new IOException("Offline startup did not reach native ready without a network barrier.");
            // Exercise the existing-process guard against this owned instance.
            if (await StartupGuard(gameDirectory) != 0)
                throw new IOException("Startup guard did not preserve the existing owned instance.");
            passed = true;
        }
        catch (Exception e) { error = e.ToString(); }
        finally
        {
            if (session != null)
            {
                await session.StopGameAsync();
                closed = session.HasExited;
                session.Dispose();
            }
        }
        passed &= closed;
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "offline-start-results.json"),
            JsonSerializer.Serialize(new { passed, gameDirectory, pid, status, closed, error }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }

    // Run while one TH12 is already open. This exercises the real Start entry
    // point and verifies rejection leaves both the process set and cfg intact.
    public static async Task<int> StartupGuard(string gameDirectory)
    {
        var before = GameSession.RunningGameIds().Order().ToArray();
        if (before.Length == 0)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-guard-results.json"),
                "{\"passed\":false,\"reason\":\"Requires an already-running TH12; no game was launched.\"}");
            return 2;
        }
        var path = Path.Combine(gameDirectory, "th12.cfg");
        var configuration = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var rejected = false;
        string? message = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var unexpected = await GameSession.Start(gameDirectory, false, timeout.Token);
            await unexpected.StopGameAsync();
            message = "Unexpectedly started a second game";
        }
        catch (GameSession.AlreadyRunningException e) { rejected = true; message = e.Message; }
        catch (Exception e) { message = e.ToString(); }
        var after = GameSession.RunningGameIds().Order().ToArray();
        var current = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var configurationUnchanged = configuration == null ? current == null : current != null && current.AsSpan().SequenceEqual(configuration);
        var passed = rejected && before.SequenceEqual(after) && configurationUnchanged;
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-guard-results.json"),
            JsonSerializer.Serialize(new { passed, rejected, message, before, after, configurationUnchanged }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
}
