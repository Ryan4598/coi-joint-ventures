using System;
using System.IO;
using System.Linq;
using BepInEx.Logging;

namespace COIJointVentures.Integration;

internal sealed class SaveFileManager
{
    private readonly ManualLogSource _log;
    private string? _saveDirectory;

    public SaveFileManager(ManualLogSource log)
    {
        _log = log;
    }

    public string? SaveDirectory
    {
        get
        {
            if (_saveDirectory != null && Directory.Exists(_saveDirectory))
            {
                return _saveDirectory;
            }

            _saveDirectory = DiscoverSaveDirectory();
            return _saveDirectory;
        }
    }

    // grabs all saves, newest first, returns "GameName/SaveName"
    public string[] ListSaves()
    {
        var dir = SaveDirectory;
        if (dir == null || !Directory.Exists(dir))
        {
            return Array.Empty<string>();
        }

        // saves live in Saves/<GameName>/<SaveName>.save
        return Directory.GetDirectories(dir)
            .SelectMany(gameDir =>
                Directory.GetFiles(gameDir, "*.save")
                    .Select(f => new
                    {
                        RelativePath = Path.GetFileName(gameDir) + "/" + Path.GetFileNameWithoutExtension(f),
                        WriteTime = File.GetLastWriteTimeUtc(f)
                    }))
            .OrderByDescending(x => x.WriteTime)
            .Select(x => x.RelativePath)
            .ToArray();
    }

    public string? FindMostRecentSave()
    {
        var dir = SaveDirectory;
        if (dir == null || !Directory.Exists(dir))
        {
            return null;
        }

        return Directory.GetDirectories(dir)
            .SelectMany(gameDir => Directory.GetFiles(gameDir, "*.save"))
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .FirstOrDefault();
    }

    public byte[]? ReadSaveFile(string savePath)
    {
        try
        {
            if (!File.Exists(savePath))
            {
                _log.LogWarning($"Save file not found: {savePath}");
                return null;
            }

            var bytes = File.ReadAllBytes(savePath);
            _log.LogInfo($"Read save file: {savePath} ({bytes.Length} bytes)");
            return bytes;
        }
        catch (Exception ex)
        {
            _log.LogError($"Failed to read save file '{savePath}': {ex.Message}");
            return null;
        }
    }

    // writes into Saves/gameName/saveName.save
    public string? WriteSaveFile(byte[] saveData, string saveName, string gameName = "Multiplayer")
    {
        var dir = SaveDirectory;
        if (dir == null)
        {
            _log.LogError("Cannot write save: save directory not found.");
            return null;
        }

        try
        {
            var gameDir = Path.Combine(dir, gameName);
            Directory.CreateDirectory(gameDir);
            var fileName = saveName.EndsWith(".save", StringComparison.OrdinalIgnoreCase)
                ? saveName
                : saveName + ".save";
            var path = Path.Combine(gameDir, fileName);
            File.WriteAllBytes(path, saveData);
            _log.LogInfo($"Wrote multiplayer save file: {path} ({saveData.Length} bytes)");
            return path;
        }
        catch (Exception ex)
        {
            _log.LogError($"Failed to write save file: {ex.Message}");
            return null;
        }
    }

    // turns "GameName/SaveName" into a real file path
    public string? ResolveSavePath(string saveEntry)
    {
        var dir = SaveDirectory;
        if (dir == null)
        {
            return null;
        }

        var path = Path.Combine(dir, saveEntry + ".save");
        return File.Exists(path) ? path : null;
    }

    private string? DiscoverSaveDirectory()
    {
        // try the game's own file system helper first
        var fsDir = MainCapture.GetSaveDirectory();
        if (fsDir != null && Directory.Exists(fsDir))
        {
            _log.LogInfo($"Save directory from IFileSystemHelper: {fsDir}");
            return fsDir;
        }

        // fallback: dig around in appdata
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var coiSaves = Path.Combine(appData, "Captain of Industry", "Saves");
            if (Directory.Exists(coiSaves))
            {
                _log.LogInfo($"Found save directory via AppData: {coiSaves}");
                return coiSaves;
            }

            _log.LogWarning($"Save directory not found at {coiSaves}");
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError($"Failed to discover save directory: {ex.Message}");
            return null;
        }
    }
}
