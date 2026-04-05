namespace JointVentures.Launcher;

/// <summary>
/// Always uses our bundled BepInEx. If doorstop files already exist in the
/// game directory, backs them up and restores on cleanup.
/// </summary>
internal sealed class DoorstopInjection(
    string gameDir, string bundledDoorstopDll, string bundledPreloaderDll)
{
    private readonly string _gameDoorstopDll = Path.Combine(gameDir, "winhttp.dll");
    private readonly string _gameDoorstopCfg = Path.Combine(gameDir, "doorstop_config.ini");
    private readonly string _backupDoorstopDll = Path.Combine(gameDir, "winhttp.dll.jv-backup");
    private readonly string _backupDoorstopCfg = Path.Combine(gameDir, "doorstop_config.ini.jv-backup");

    private bool _hadExistingDoorstop;

    public void Install()
    {
        _hadExistingDoorstop = File.Exists(_gameDoorstopDll);

        if (_hadExistingDoorstop)
        {
            File.Copy(_gameDoorstopDll, _backupDoorstopDll, overwrite: true);
            if (File.Exists(_gameDoorstopCfg))
                File.Copy(_gameDoorstopCfg, _backupDoorstopCfg, overwrite: true);
        }

        File.Copy(bundledDoorstopDll, _gameDoorstopDll, overwrite: true);

        var config =
            $"[General]\r\n" +
            $"enabled = true\r\n" +
            $"target_assembly = {bundledPreloaderDll}\r\n" +
            $"redirect_output_log = false\r\n" +
            $"boot_config_override =\r\n" +
            $"ignore_disable_switch = false\r\n" +
            $"\r\n" +
            $"[UnityMono]\r\n" +
            $"dll_search_path_override =\r\n" +
            $"debug_enabled = false\r\n" +
            $"debug_address = 127.0.0.1:10000\r\n" +
            $"debug_suspend = false\r\n";

        File.WriteAllText(_gameDoorstopCfg, config);
    }

    public bool HadExistingDoorstop => _hadExistingDoorstop;

    public void Uninstall()
    {
        RetryFileOp(_gameDoorstopDll, _hadExistingDoorstop ? _backupDoorstopDll : null);
        RetryFileOp(_gameDoorstopCfg, _hadExistingDoorstop ? _backupDoorstopCfg : null);
    }

    /// <summary>
    /// Deletes or restores a file, retrying on lock for up to 10 seconds.
    /// Throws if all retries fail.
    /// </summary>
    private static void RetryFileOp(string path, string? backupPath)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (backupPath is not null && File.Exists(backupPath))
                    File.Move(backupPath, path, overwrite: true);
                else if (File.Exists(path))
                    File.Delete(path);

                return;
            }
            catch (IOException) when (attempt < 19)
            {
                Thread.Sleep(500);
            }
        }

        throw new IOException($"Could not clean up {Path.GetFileName(path)} — file may still be locked.");
    }
}
