using System;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using COIJointVentures.Integration;

namespace COIJointVentures.Chat;

internal static class ActionDescriptionBuilder
{
    private static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    public static bool IsSimControl(NativeCommandInfo info)
    {
        var type = info.CommandType;
        return type.Contains("SetSimPauseStateCmd") || type.Contains("GameSpeedChangeCmd");
    }

    public static string? Describe(NativeCommandInfo info, object? commandObj = null)
    {
        var type = info.CommandType;

        // sim control
        if (type.Contains("SetSimPauseStateCmd"))
        {
            var isPaused = ReadField<bool>(commandObj, "IsPaused");
            return isPaused == true ? "paused the game" : "unpaused the game";
        }

        if (type.Contains("GameSpeedChangeCmd"))
        {
            return "changed game speed";
        }

        // building stuff
        if (type.Contains("BatchCreateStaticEntitiesCmd"))
        {
            var buildingName = ExtractBuildingName(commandObj);
            return buildingName != null
                ? $"placed {buildingName}"
                : "placed a building";
        }

        if (type.Contains("DestroyEntitiesCmd") || type.Contains("RemoveStaticEntityCmd"))
        {
            return "removed a building";
        }

        // research
        if (type.Contains("ResearchStartCmd"))
        {
            return "started research";
        }

        if (type.Contains("ResearchStopCmd"))
        {
            return "stopped research";
        }

        // terrain / trees
        if (type.Contains("DesignateHarvestedTreesCmd"))
        {
            return "designated tree harvesting";
        }

        if (type.Contains("DesignateTerraformCmd") || type.Contains("TerraformingCmd"))
        {
            return "designated terraforming";
        }

        // vehicles
        if (type.Contains("AssignVehicle"))
        {
            return "assigned vehicles";
        }

        if (type.Contains("AddVehicleToBuildQueueCmd"))
        {
            return "queued vehicle production";
        }

        // transport / logistics
        if (type.Contains("BuildTransportCmd") || type.Contains("CreateBridgeFromPlanCmd"))
        {
            return "built transport";
        }

        // trains
        if (type.Contains("CreateTrainTrackFromPlanCmd"))
        {
            return "built train track";
        }

        // shipyard / fleet
        if (type.Contains("ShipyardToggle") || type.Contains("FleetToggle"))
        {
            return "toggled a setting";
        }

        if (type.Contains("FleetLoadCrewCmd") || type.Contains("FleetUnloadCrewCmd"))
        {
            return "managed fleet crew";
        }

        // everything else is noise, skip it
        return null;
    }

    private static string? ExtractBuildingName(object? command)
    {
        if (command == null)
        {
            return null;
        }

        try
        {
            var configDataField = command.GetType().GetField("ConfigData", Flags);
            if (configDataField == null) return null;

            var configData = configDataField.GetValue(command);
            if (configData == null) return null;

            var lengthProp = configData.GetType().GetProperty("Length");
            if (lengthProp == null || (int)lengthProp.GetValue(configData)! == 0) return null;

            object? firstConfig = null;
            var itemProp = configData.GetType().GetProperty("Item", new[] { typeof(int) });
            if (itemProp != null)
                firstConfig = itemProp.GetValue(configData, new object[] { 0 });
            else if (configData is IEnumerable enumerable)
                foreach (var item in enumerable) { firstConfig = item; break; }

            if (firstConfig == null) return null;

            // Prototype field is Option<Proto> — its ToString() gives "Some: SmokeStack (MachineProto)"
            // just parse the name out of that
            var protoField = firstConfig.GetType().GetField("Prototype", Flags);
            if (protoField != null)
            {
                var protoOption = protoField.GetValue(firstConfig);
                if (protoOption != null)
                {
                    var str = protoOption.ToString();
                    // parse "Some: SmokeStack (MachineProto)" → "SmokeStack"
                    if (str != null && str.StartsWith("Some: "))
                    {
                        var name = str.Substring(6); // strip "Some: "
                        var parenIdx = name.IndexOf(" (");
                        if (parenIdx > 0)
                            name = name.Substring(0, parenIdx);
                        return FormatProtoId(name);
                    }
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    // "StorageFluidT2" -> "Storage Fluid T2"
    private static string FormatProtoId(string protoId)
    {
        // jam spaces into camelCase
        var spaced = Regex.Replace(protoId, @"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        spaced = spaced.Replace("_", " ");
        return spaced;
    }

    private static T? ReadField<T>(object? obj, string fieldName) where T : struct
    {
        if (obj == null)
        {
            return null;
        }

        try
        {
            var field = obj.GetType().GetField(fieldName, Flags);
            if (field != null)
            {
                return (T)field.GetValue(obj);
            }
        }
        catch
        {
        }

        return null;
    }
}
