using System;
using System.IO;
using BepInEx.Logging;

namespace COIJointVentures.Logging;

internal sealed class RuntimeReplicationLog
{
    private readonly ManualLogSource _log;
    private readonly string _outputPath;
    private readonly object _gate = new object();

    public RuntimeReplicationLog(ManualLogSource log, string outputPath)
    {
        _log = log;
        _outputPath = outputPath;

        var directory = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public void RecordSerializedCommand(string commandType, int payloadBytes)
    {
        lock (_gate)
        {
            File.AppendAllText(_outputPath, $"{DateTime.UtcNow:O} serialized {commandType} bytes={payloadBytes}{Environment.NewLine}");
        }

        _log.LogInfo($"Serialized native command '{commandType}' ({payloadBytes} bytes).");
    }
}

