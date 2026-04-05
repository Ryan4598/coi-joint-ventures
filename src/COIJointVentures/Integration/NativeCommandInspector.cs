using System;
using System.Reflection;
using System.Text;

namespace COIJointVentures.Integration;

internal static class NativeCommandInspector
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static bool TryInspect(object? command, out NativeCommandInfo info)
    {
        info = new NativeCommandInfo();
        if (command == null)
        {
            return false;
        }

        var type = command.GetType();
        if (!ImplementsInterface(type, "Mafi.Core.Input.IInputCommand"))
        {
            return false;
        }

        info.CommandType = type.FullName ?? type.Name;
        info.AffectsSaveState = ReadBoolProperty(command, type, "AffectsSaveState");
        info.IsVerificationCmd = ReadBoolProperty(command, type, "IsVerificationCmd");
        info.IsProcessed = ReadBoolProperty(command, type, "IsProcessed");
        info.Summary = BuildSummary(command, type);
        return true;
    }

    private static bool ImplementsInterface(Type type, string interfaceName)
    {
        var interfaces = type.GetInterfaces();
        for (var i = 0; i < interfaces.Length; i++)
        {
            if (string.Equals(interfaces[i].FullName, interfaceName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ReadBoolProperty(object instance, Type type, string propertyName)
    {
        var property = type.GetProperty(propertyName, InstanceFlags);
        if (property == null || property.PropertyType != typeof(bool))
        {
            return false;
        }

        try
        {
            return (bool)property.GetValue(instance, null);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildSummary(object instance, Type type)
    {
        var builder = new StringBuilder();
        var fields = type.GetFields(InstanceFlags);
        var appended = 0;

        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            if (!field.IsPublic || appended >= 6)
            {
                continue;
            }

            object? value;
            try
            {
                value = field.GetValue(instance);
            }
            catch
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(", ");
            }

            builder.Append(field.Name);
            builder.Append('=');
            builder.Append(value ?? "null");
            appended++;
        }

        if (builder.Length == 0)
        {
            builder.Append("no-public-fields");
        }

        return builder.ToString();
    }
}

