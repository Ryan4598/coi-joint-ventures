using System;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using COIJointVentures.Integration;

namespace COIJointVentures.Chat;

internal static class ActionDescriptionBuilder
{
    private static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

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
            // dig through the command to find what building it is
            var configDataField = command.GetType().GetField("ConfigData", Flags);
            if (configDataField == null)
            {
                return null;
            }

            var configData = configDataField.GetValue(command);
            if (configData == null)
            {
                return null;
            }

            // grab the first element if there is one
            var lengthProp = configData.GetType().GetProperty("Length");
            if (lengthProp == null || (int)lengthProp.GetValue(configData)! == 0)
            {
                return null;
            }

            var itemProp = configData.GetType().GetProperty("Item", new[] { typeof(int) });
            if (itemProp == null)
            {
                return null;
            }

            var firstConfig = itemProp.GetValue(configData, new object[] { 0 });
            if (firstConfig == null)
            {
                return null;
            }

            // get the prototype to extract the name
            var protoField = firstConfig.GetType().GetField("Prototype", Flags);
            if (protoField == null)
            {
                return null;
            }

            var protoOption = protoField.GetValue(firstConfig);
            if (protoOption == null)
            {
                return null;
            }

            var hasValueProp = protoOption.GetType().GetProperty("HasValue");
            if (hasValueProp == null || !(bool)hasValueProp.GetValue(protoOption)!)
            {
                return null;
            }

            var valueProp = protoOption.GetType().GetProperty("Value");
            var proto = valueProp?.GetValue(protoOption);
            if (proto == null)
            {
                return null;
            }

            var idProp = proto.GetType().GetProperty("Id");
            var id = idProp?.GetValue(proto);
            if (id == null)
            {
                return null;
            }

            var valueField = id.GetType().GetField("Value");
            var idString = valueField?.GetValue(id) as string;

            if (string.IsNullOrEmpty(idString))
            {
                return null;
            }

            return FormatProtoId(idString!);
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
