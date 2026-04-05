using System;
using System.Reflection;
using BepInEx.Logging;
using Mafi;
using Mafi.Core.Input;
using Mafi.Core.SaveGame;

namespace COIJointVentures.Integration;

internal sealed class SaveManagerBridge
{
    private readonly ManualLogSource _log;
    private ISaveManager? _saveManager;
    private SaveManager? _saveManagerConcrete;
    private Action<SaveResult>? _onSaveDone;

    public SaveManagerBridge(ManualLogSource log)
    {
        _log = log;
    }

    public bool IsResolved => _saveManager != null;
    public bool IsSavePending => _saveManagerConcrete?.IsSavePending ?? false;
    public string? GameName => _saveManager?.GameName;

    public event Action<SaveResult>? SaveCompleted;

    public bool TryResolve(InputScheduler scheduler)
    {
        if (_saveManager != null)
        {
            return true;
        }

        try
        {
            var field = typeof(InputScheduler).GetField("m_resolver", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                return false;
            }

            var resolver = field.GetValue(scheduler) as DependencyResolver;
            if (resolver == null)
            {
                return false;
            }

            if (resolver.TryResolve<ISaveManager>(out var mgr))
            {
                _saveManager = mgr;
                _saveManagerConcrete = mgr as SaveManager;
                _log.LogInfo($"[SaveBridge] Resolved ISaveManager (GameName='{mgr.GameName}').");

                _onSaveDone = result =>
                {
                    _log.LogInfo($"[SaveBridge] Save completed.");
                    SaveCompleted?.Invoke(result);
                };
                _saveManager.OnSaveDone += _onSaveDone;
                return true;
            }

            _log.LogWarning("[SaveBridge] DependencyResolver could not resolve ISaveManager.");
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[SaveBridge] Failed to resolve ISaveManager: {ex.Message}");
        }

        return false;
    }

    public void RequestSave(string saveName)
    {
        if (_saveManager == null)
        {
            _log.LogWarning("[SaveBridge] Cannot request save — ISaveManager not resolved.");
            return;
        }

        _log.LogInfo($"[SaveBridge] Requesting save '{saveName}'...");
        _saveManager.RequestGameSave(saveName);
    }

    public bool IsNonAutosaveInProgress()
    {
        return _saveManager?.IsNonAutosaveInProgress() ?? false;
    }

    public string? GetLastSaveFilePath()
    {
        if (_saveManagerConcrete == null)
        {
            return null;
        }

        var opt = _saveManagerConcrete.LastSaveFilePath;
        try
        {
            var hasValueProp = opt.GetType().GetProperty("HasValue");
            if (hasValueProp != null && (bool)hasValueProp.GetValue(opt)!)
            {
                var valueProp = opt.GetType().GetProperty("Value");
                return valueProp?.GetValue(opt) as string;
            }
        }
        catch
        {
        }

        return null;
    }

    public void Dispose()
    {
        if (_saveManager != null && _onSaveDone != null)
        {
            _saveManager.OnSaveDone -= _onSaveDone;
        }
    }
}
