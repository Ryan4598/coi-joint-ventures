using System;
using System.IO;
using System.Reflection;
using Mafi;
using Mafi.Core.Input;
using Mafi.Serialization;

namespace COIJointVentures.Integration;

internal sealed class NativeCommandCodec
{
    private static readonly BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

    public string SerializeToBase64(IInputCommand command)
    {
        if (command == null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        var sanitized = command.ShallowCloneWithoutResult();
        using (var writer = new MemoryBlobWriter())
        {
            writer.WriteGeneric(sanitized);
            writer.FinalizeSerialization();
            return Convert.ToBase64String(writer.ToArray());
        }
    }

    public IInputCommand DeserializeFromBase64(string base64Payload)
    {
        if (string.IsNullOrWhiteSpace(base64Payload))
        {
            throw new ArgumentException("Payload is empty.", nameof(base64Payload));
        }

        var resolver = GetResolverFromScheduler();

        var bytes = Convert.FromBase64String(base64Payload);
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            var reader = new BlobReader(stream, SaveVersion.CURRENT_SAVE_VERSION);
            try
            {
                var command = reader.ReadGenericAs<IInputCommand>();
                reader.FinalizeLoading(resolver);
                return command;
            }
            finally
            {
                reader.Destroy(ignoreRemainingData: false);
            }
        }
    }

    private static Option<DependencyResolver> GetResolverFromScheduler()
    {
        var scheduler = Runtime.PluginRuntime.Scheduler;
        if (scheduler == null)
        {
            return Option<DependencyResolver>.None;
        }

        try
        {
            var field = typeof(InputScheduler).GetField("m_resolver", InstanceNonPublic);
            if (field != null)
            {
                var resolver = field.GetValue(scheduler) as DependencyResolver;
                if (resolver != null)
                {
                    return Option.Some(resolver);
                }
            }
        }
        catch
        {
        }

        return Option<DependencyResolver>.None;
    }
}
