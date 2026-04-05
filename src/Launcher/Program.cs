using System.Diagnostics;

namespace JointVentures.Launcher;

internal static class Program
{
    private const int SteamAppId = 1594320;

    [STAThread]
    static void Main()
    {
        using var window = new NativeWindow();
        window.Create("COI: Joint Ventures");

        // Run the launch pipeline on a background thread
        var worker = new Thread(() => Run(window)) { IsBackground = true };
        worker.Start();

        // Block on the Win32 message loop (UI thread)
        window.RunMessageLoop();
    }

    private static void Run(NativeWindow window)
    {
        DoorstopInjection? injection = null;
        var cleaned = true;

        try
        {
            // ── Version + update check (non-blocking) ──
            window.Log($"Joint Ventures v{UpdateChecker.CurrentVersion}");
            var updateTask = UpdateChecker.CheckForUpdateAsync();

            // ── Download BepInEx if not cached (may wipe BepInEx/ dir) ──
            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JointVentures");
            var bepinexOk = BepInExDownloader.EnsureDownloadedAsync(cacheDir, window.Log).GetAwaiter().GetResult();
            if (!bepinexOk)
            {
                Pause(window);
                return;
            }

            // ── Extract plugin from embedded resources (after BepInEx so it doesn't get wiped) ──
            window.Log("Extracting plugin...");
            BundleExtractor.EnsureExtracted();
            window.Log("Plugin ready.");

            // Show update result if ready
            CheckUpdateResult(window, updateTask);

            var bundledPreloader = Path.Combine(cacheDir, "BepInEx", "core", "BepInEx.Preloader.dll");
            var bundledDoorstop = Path.Combine(cacheDir, "BepInEx", "doorstop", "winhttp.dll");

            if (!File.Exists(bundledPreloader) || !File.Exists(bundledDoorstop))
            {
                window.Log("ERROR: BepInEx files are incomplete.");
                Pause(window);
                return;
            }

            // ── Find game ──
            window.Log("Searching for Captain of Industry...");
            var gameDir = SteamLocator.FindGameDir();
            if (gameDir is null)
            {
                window.Log("ERROR: Could not find Captain of Industry.");
                window.Log("Make sure the game is installed via Steam.");
                Pause(window);
                return;
            }

            window.Log($"Game found: {gameDir}");

            // ── Inject doorstop ──
            window.Log("Installing doorstop...");
            injection = new DoorstopInjection(gameDir, bundledDoorstop, bundledPreloader);
            injection.Install();
            cleaned = false;

            window.Log(injection.HadExistingDoorstop
                ? "Existing BepInEx backed up. Our doorstop installed."
                : "Doorstop proxy + config installed.");

            // ── Launch ──
            window.Log("Launching Captain of Industry via Steam...");
            Process.Start(new ProcessStartInfo
            {
                FileName = $"steam://rungameid/{SteamAppId}",
                UseShellExecute = true
            });

            // ── Wait for game process ──
            window.Log("Waiting for game process...");
            var gameProcess = WaitForGameProcess(30);

            if (gameProcess is null)
            {
                window.Log("ERROR: Game process did not start within 30 seconds.");
                Cleanup(window, injection, ref cleaned);
                Pause(window);
                return;
            }

            window.Log($"Game running (PID {gameProcess.Id}). Waiting for exit...");

            // ── Wait for exit ──
            try { gameProcess.WaitForExit(); }
            catch (SystemException) { WaitForProcessExit(gameProcess.Id); }

            window.Log("Game exited.");
        }
        catch (Exception ex)
        {
            window.Log($"ERROR: {ex.Message}");
        }

        // ── Cleanup ──
        if (!cleaned && injection is not null)
            Cleanup(window, injection, ref cleaned);

        window.Log("Done. Closing in 3 seconds...");
        Thread.Sleep(3000);
        window.Close();
    }

    private static void Cleanup(NativeWindow window, DoorstopInjection injection, ref bool cleaned)
    {
        window.Log("Cleaning up...");
        try
        {
            injection.Uninstall();
            cleaned = true;
            window.Log(injection.HadExistingDoorstop
                ? "Restored original doorstop files."
                : "Removed doorstop proxy + config.");
        }
        catch (Exception ex)
        {
            window.Log($"ERROR: Cleanup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// On error, wait 10 seconds so the user can read the message, then close.
    /// </summary>
    private static void Pause(NativeWindow window)
    {
        window.Log("Closing in 10 seconds...");
        Thread.Sleep(10000);
        window.Close();
    }

    private static void CheckUpdateResult(NativeWindow window, Task<string?> updateTask)
    {
        try
        {
            // Give it a moment if not done yet, but don't block long
            if (!updateTask.Wait(TimeSpan.FromSeconds(3)))
                return;

            var newVersion = updateTask.Result;
            if (newVersion is not null)
            {
                window.Log($"");
                window.Log($"*** UPDATE AVAILABLE: {newVersion} ***");
                window.Log($"    {UpdateChecker.ReleasesPage}");
                window.Log($"");
            }
        }
        catch { /* network issues — ignore */ }
    }

    private static Process? WaitForGameProcess(int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var procs = Process.GetProcessesByName("Captain of Industry");
                if (procs.Length > 0)
                    return procs[0];
            }
            catch { }

            Thread.Sleep(1000);
        }

        return null;
    }

    private static void WaitForProcessExit(int pid)
    {
        while (true)
        {
            try { Process.GetProcessById(pid); }
            catch (ArgumentException) { return; }
            Thread.Sleep(1000);
        }
    }
}
