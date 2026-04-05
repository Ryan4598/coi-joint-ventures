using System;
using System.Collections;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Mafi.Core;

namespace COIJointVentures.Integration;

// hooks into Mafi.Unity.Main so we can call LoadGame and grab FileSystemHelper
internal static class MainCapture
{
    private static object? _mainInstance;
    private static MethodInfo? _loadGameMethod;
    private static MethodInfo? _goToMainMenuMethod;
    private static PropertyInfo? _fileSystemHelperProp;
    private static PropertyInfo? _currentSceneProp;
    private static PropertyInfo? _isInitializingSceneProp;
    private static ManualLogSource? _log;

    public static object? MainInstance => _mainInstance;

    public static bool HasMain => _mainInstance != null;

    public static bool IsInGame => Runtime.PluginRuntime.IsSchedulerActive;

    public static bool IsInitializingScene
    {
        get
        {
            if (_mainInstance == null || _isInitializingSceneProp == null)
            {
                return false;
            }

            try
            {
                return (bool)_isInitializingSceneProp.GetValue(_mainInstance);
            }
            catch
            {
                return false;
            }
        }
    }

    // slap a postfix on Main's ctor to grab the instance
    public static bool TryApplyPatch(Harmony harmony, ManualLogSource log)
    {
        _log = log;

        var mainType = FindMainType();
        if (mainType == null)
        {
            log.LogWarning("Could not find Mafi.Unity.Main type. Auto-load will not be available.");
            return false;
        }

        // cache all the reflection stuff up front
        var imainType = mainType.GetInterface("IMain") ?? mainType;
        _loadGameMethod = imainType.GetMethod("LoadGame", BindingFlags.Public | BindingFlags.Instance);
        _goToMainMenuMethod = imainType.GetMethod("GoToMainMenu", BindingFlags.Public | BindingFlags.Instance);
        _fileSystemHelperProp = imainType.GetProperty("FileSystemHelper", BindingFlags.Public | BindingFlags.Instance);
        _currentSceneProp = imainType.GetProperty("CurrentScene", BindingFlags.Public | BindingFlags.Instance);
        _isInitializingSceneProp = imainType.GetProperty("IsInitializingScene", BindingFlags.Public | BindingFlags.Instance);

        if (_loadGameMethod == null)
        {
            log.LogWarning("Could not find IMain.LoadGame method.");
            return false;
        }

        // patch the ctor
        var ctor = mainType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (ctor.Length == 0)
        {
            log.LogWarning("Could not find Main constructor.");
            return false;
        }

        var postfix = typeof(MainCapture).GetMethod(nameof(OnMainConstructed), BindingFlags.Static | BindingFlags.NonPublic);
        harmony.Patch(ctor[0], postfix: new HarmonyMethod(postfix));
        log.LogInfo("Applied Harmony postfix to Main constructor for IMain capture.");

        // also patch LoadGame so we can block clients from loading saves while connected
        var loadGameImpl = mainType.GetMethod("LoadGame", BindingFlags.Public | BindingFlags.Instance);
        if (loadGameImpl != null)
        {
            var loadPrefix = typeof(MainCapture).GetMethod(nameof(OnLoadGamePrefix), BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(loadGameImpl, prefix: new HarmonyMethod(loadPrefix));
            log.LogInfo("Applied Harmony prefix to Main.LoadGame for client load blocking.");
        }

        return true;
    }

    public static string? GetSaveDirectory()
    {
        if (_mainInstance == null || _fileSystemHelperProp == null)
        {
            return null;
        }

        try
        {
            var fsHelper = _fileSystemHelperProp.GetValue(_mainInstance);
            if (fsHelper == null)
            {
                return null;
            }

            // GetDirPath(FileType.GameSave, ensureExists: true, subDir: null)
            var getDirPath = fsHelper.GetType().GetMethod("GetDirPath",
                new[] { typeof(FileType), typeof(bool), typeof(string) });
            if (getDirPath == null)
            {
                return null;
            }

            var path = getDirPath.Invoke(fsHelper, new object[] { FileType.GameSave, true, (string)null! }) as string;
            return path;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"Failed to get save directory from IFileSystemHelper: {ex.Message}");
            return null;
        }
    }

    // try to auto-load a save via IMain.LoadGame
    public static bool TryLoadGame(string saveNameNoExtension, string gameName)
    {
        if (_mainInstance == null || _loadGameMethod == null)
        {
            _log?.LogWarning("Cannot auto-load: IMain not captured.");
            return false;
        }

        try
        {
            // build SaveFileInfo
            var saveFileInfo = CreateSaveFileInfo(saveNameNoExtension, gameName);
            if (saveFileInfo == null)
            {
                _log?.LogWarning("Failed to construct SaveFileInfo.");
                return false;
            }

            // fill in remaining params with null/default, we only care about the save
            var parameters = _loadGameMethod.GetParameters();
            var args = new object[parameters.Length];
            args[0] = saveFileInfo;

            for (int i = 1; i < parameters.Length; i++)
            {
                var paramType = parameters[i].ParameterType;
                if (paramType.IsValueType)
                {
                    args[i] = Activator.CreateInstance(paramType);
                }
                else
                {
                    args[i] = null!;
                }
            }

            _log?.LogInfo($"Calling IMain.LoadGame for '{saveNameNoExtension}' (game: '{gameName}')...");
            _loadGameMethod.Invoke(_mainInstance, args);
            _log?.LogInfo("IMain.LoadGame invoked successfully.");
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogError($"Failed to auto-load game: {ex}");
            return false;
        }
    }

    private static object? CreateSaveFileInfo(string name, string gameName)
    {
        var saveFileInfoType = typeof(SaveFileInfo);

        // just need the (name, gameName) ctor
        var ctor = saveFileInfoType.GetConstructor(new[] { typeof(string), typeof(string) });
        if (ctor != null)
        {
            return ctor.Invoke(new object[] { name, gameName });
        }

        _log?.LogWarning("Could not find SaveFileInfo(string, string) constructor.");
        return null;
    }

    private static Type? FindMainType()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!asm.FullName.StartsWith("Mafi.Unity", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var mainType = asm.GetType("Mafi.Unity.Main");
                if (mainType != null)
                {
                    return mainType;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static void OnMainConstructed(object __instance)
    {
        _mainInstance = __instance;
        _log?.LogInfo($"Captured IMain instance: {__instance.GetType().FullName}");
    }

    // blocks clients from loading a different save while connected to a server
    private static bool OnLoadGamePrefix()
    {
        var session = Runtime.PluginRuntime.Session;
        if (session != null && session.Mode == Session.MultiplayerMode.Client)
        {
            _log?.LogWarning("Blocked save load — you're connected to a multiplayer session. Disconnect first.");
            return false; // skip the original method
        }

        return true;
    }
}
