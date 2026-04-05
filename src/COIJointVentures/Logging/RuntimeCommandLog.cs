using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;

namespace COIJointVentures.Logging;

internal sealed class RuntimeCommandLog
{
    private readonly ManualLogSource _log;
    private readonly string _outputPath;
    private readonly HashSet<string> _seenTypes = new HashSet<string>(StringComparer.Ordinal);
    private readonly object _gate = new object();

    public RuntimeCommandLog(ManualLogSource log, string outputPath)
    {
        _log = log;
        _outputPath = outputPath;

        var directory = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public void RecordObservedCommand(string commandType, string details)
    {
        lock (_gate)
        {
            if (!_seenTypes.Add(commandType))
            {
                return;
            }

            File.AppendAllText(_outputPath, $"{DateTime.UtcNow:O} {commandType} {details}{Environment.NewLine}");
        }

        _log.LogInfo($"Observed native COI command '{commandType}'.");
    }
}

