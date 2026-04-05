using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using Mafi;
using Mafi.Core.Input;
using COIJointVentures.Runtime;

namespace COIJointVentures.Waypoints;

/// <summary>
/// Queries COI's TerrainManager for the actual terrain surface height at a
/// given world XZ position, via reflection.  Falls back gracefully if any
/// piece of the chain is unavailable.
/// </summary>
internal static class TerrainHeightQuery
{
    private static readonly BindingFlags NonPublicInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;

    private static object? _terrainManager;
    private static MethodInfo? _getHeightMethod;
    private static Type? _tile2fType;
    private static float _tileSizeM = -1f;
    private static bool _initialized;

    /// <summary>
    /// Try to get the terrain surface height at the given Unity world XZ.
    /// Returns null if we can't query the game's terrain system.
    /// </summary>
    public static float? GetHeightAtWorldXZ(float worldX, float worldZ, ManualLogSource? log = null)
    {
        if (!_initialized)
        {
            TryInitialize(log);
            // only mark initialized if we actually found everything
            _initialized = _terrainManager != null && _getHeightMethod != null
                        && _tile2fType != null && _tileSizeM > 0f;
        }

        if (_terrainManager == null || _getHeightMethod == null || _tile2fType == null || _tileSizeM <= 0f)
            return null;

        try
        {
            // convert Unity world coords to tile coords
            var tileX = worldX / _tileSizeM;
            var tileZ = worldZ / _tileSizeM;

            // Tile2f is a struct with Fix32 X, Y fields (Y = Z in world space)
            // construct via Activator — Tile2f(Fix32 x, Fix32 y)
            var fix32X = Fix32.FromFloat(tileX);
            var fix32Z = Fix32.FromFloat(tileZ);
            var tile2f = Activator.CreateInstance(_tile2fType, fix32X, fix32Z);

            // call GetHeight(Tile2f) -> HeightTilesF
            var heightResult = _getHeightMethod.Invoke(_terrainManager, new[] { tile2f });
            if (heightResult == null) return null;

            // HeightTilesF has a .Value property (Fix32) — height in tile units
            var valueProp = heightResult.GetType().GetProperty("Value")
                         ?? heightResult.GetType().GetField("Value")?.DeclaringType?.GetProperty("Value");

            object? rawValue = null;
            var valField = heightResult.GetType().GetField("Value");
            var valProp = heightResult.GetType().GetProperty("Value");
            if (valProp != null)
                rawValue = valProp.GetValue(heightResult);
            else if (valField != null)
                rawValue = valField.GetValue(heightResult);

            if (rawValue == null) return null;

            // rawValue is Fix32 or similar — convert to float
            float heightTiles;
            if (rawValue is Fix32 fix)
            {
                heightTiles = fix.ToFloat();
            }
            else
            {
                // try reflection on ToFloat()
                var toFloat = rawValue.GetType().GetMethod("ToFloat", Type.EmptyTypes);
                if (toFloat != null)
                    heightTiles = (float)toFloat.Invoke(rawValue, null)!;
                else
                    heightTiles = Convert.ToSingle(rawValue);
            }

            // height in tile units * tile size = world height
            return heightTiles * _tileSizeM;
        }
        catch (Exception ex)
        {
            log?.LogWarning($"[TERRAIN] Height query failed: {ex.Message}");
            return null;
        }
    }

    private static void TryInitialize(ManualLogSource? log)
    {
        try
        {
            var terrainMgrType = Type.GetType("Mafi.Core.Terrain.TerrainManager, Mafi.Core");
            if (terrainMgrType == null)
            {
                log?.LogWarning("[TERRAIN] TerrainManager type not found.");
                return;
            }

            // strategy 1: walk DependencyResolver from InputScheduler
            var scheduler = PluginRuntime.Scheduler;
            if (scheduler != null)
            {
                var resolverField = typeof(InputScheduler).GetField("m_resolver", NonPublicInstance);
                var resolver = resolverField?.GetValue(scheduler) as DependencyResolver;
                if (resolver != null)
                    _terrainManager = FindInstanceInResolver(resolver, terrainMgrType, log);
            }

            // strategy 2: Main.CurrentScene -> IGameScene -> DependencyResolver -> TerrainManager
            var mainInst = Integration.MainCapture.MainInstance;
            if (mainInst != null && _terrainManager == null)
            {
                // unwrap Option<IGameScene> from CurrentScene
                var sceneField = mainInst.GetType().GetField("<CurrentScene>k__BackingField", NonPublicInstance);
                var sceneOption = sceneField?.GetValue(mainInst);
                // unwrap nested Option — may need multiple rounds
                var scene = sceneOption;
                for (int i = 0; i < 3 && scene != null; i++)
                {
                    var u = UnwrapOption(scene);
                    if (ReferenceEquals(u, scene)) break; // not an Option, done
                    scene = u;
                }

                if (scene != null)
                {
                    log?.LogInfo($"[TERRAIN] Scene type: {scene.GetType().FullName}");

                    // search the scene for DependencyResolver fields/properties
                    foreach (var field in scene.GetType().GetFields(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        object? val = null;
                        try { val = field.GetValue(scene); } catch { }
                        if (val == null) continue;

                        if (val is DependencyResolver sceneResolver)
                        {
                            log?.LogInfo($"[TERRAIN] Found resolver in Scene.{field.Name} — searching...");
                            _terrainManager = FindInstanceInResolver(sceneResolver, terrainMgrType, log, dump: true);
                            if (_terrainManager != null)
                            {
                                log?.LogInfo($"[TERRAIN] Found TerrainManager via Scene resolver!");
                                break;
                            }
                        }
                    }

                    // also try properties
                    if (_terrainManager == null)
                    {
                        foreach (var prop in scene.GetType().GetProperties(
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            if (prop.GetIndexParameters().Length > 0) continue;
                            object? val = null;
                            try { val = prop.GetValue(scene); } catch { }
                            if (val == null) continue;

                            if (val is DependencyResolver sceneResolver)
                            {
                                log?.LogInfo($"[TERRAIN] Found resolver in Scene.{prop.Name} — searching...");
                                _terrainManager = FindInstanceInResolver(sceneResolver, terrainMgrType, log);
                                if (_terrainManager != null)
                                {
                                    log?.LogInfo($"[TERRAIN] Found TerrainManager via Scene.{prop.Name} resolver!");
                                    break;
                                }
                            }
                        }
                    }

                    // if still not found, dump scene fields for next debug round
                    if (_terrainManager == null)
                    {
                        foreach (var field in scene.GetType().GetFields(
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            object? val = null;
                            try { val = field.GetValue(scene); } catch { }
                            var typeName = val?.GetType().FullName ?? field.FieldType.FullName;
                            log?.LogInfo($"[TERRAIN]   Scene.{field.Name} : {typeName}");
                        }
                    }
                }
                else
                {
                    log?.LogWarning("[TERRAIN] CurrentScene is empty.");
                }
            }

            if (_terrainManager == null)
            {
                log?.LogWarning("[TERRAIN] Could not find TerrainManager anywhere.");
                return;
            }

            // find GetHeight(Tile2f) method
            _tile2fType = Type.GetType("Mafi.Tile2f, Mafi.Core");
            if (_tile2fType == null)
            {
                log?.LogWarning("[TERRAIN] Tile2f type not found.");
                return;
            }

            _getHeightMethod = terrainMgrType.GetMethod("GetHeight",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { _tile2fType }, null);

            if (_getHeightMethod == null)
            {
                log?.LogWarning("[TERRAIN] GetHeight(Tile2f) method not found.");
                return;
            }

            // read TILE_SIZE_M
            var tileType = Type.GetType("Mafi.Core.Terrain.TerrainTile, Mafi.Core");
            if (tileType != null)
            {
                var sizeField = tileType.GetField("TILE_SIZE_M",
                    BindingFlags.Public | BindingFlags.Static);
                if (sizeField != null)
                {
                    var val = sizeField.GetValue(null);
                    _tileSizeM = Convert.ToSingle(val);
                }
            }

            if (_tileSizeM <= 0f)
            {
                // common default in COI is 2 meters per tile
                _tileSizeM = 2f;
                log?.LogInfo("[TERRAIN] TILE_SIZE_M not found, defaulting to 2.");
            }

            log?.LogInfo($"[TERRAIN] Initialized: tileSizeM={_tileSizeM}, terrainMgr={_terrainManager.GetType().Name}, getHeight={_getHeightMethod.Name}");
        }
        catch (Exception ex)
        {
            log?.LogWarning($"[TERRAIN] Init failed: {ex.Message}");
        }
    }

    private static object? FindInObjectGraph(object root, Type targetType, int maxDepth, ManualLogSource? log)
    {
        if (maxDepth <= 0) return null;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return FindInObjectGraphInner(root, targetType, maxDepth, visited);
    }

    private static object? FindInObjectGraphInner(object obj, Type targetType, int depth, HashSet<object> visited)
    {
        if (depth <= 0 || obj == null || !visited.Add(obj)) return null;

        foreach (var field in obj.GetType().GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            object? val;
            try { val = field.GetValue(obj); }
            catch { continue; }
            if (val == null) continue;

            if (targetType.IsAssignableFrom(val.GetType())) return val;

            // unwrap Option<>
            var unwrapped = UnwrapOption(val);
            if (unwrapped != null && targetType.IsAssignableFrom(unwrapped.GetType())) return unwrapped;

            // recurse into DependencyResolver instances
            if (val is DependencyResolver resolver)
            {
                var found = FindInstanceInResolver(resolver, targetType, null);
                if (found != null) return found;
            }

            // recurse into objects from Mafi namespace
            if (val.GetType().Namespace?.StartsWith("Mafi") == true && !val.GetType().IsValueType)
            {
                var found = FindInObjectGraphInner(val, targetType, depth - 1, visited);
                if (found != null) return found;
            }
        }

        return null;
    }

    private sealed class ReferenceEqualityComparer : System.Collections.Generic.IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    private static object? FindInstanceInResolver(DependencyResolver resolver, Type targetType, ManualLogSource? log, bool dump = false)
    {
        // walk every field in DependencyResolver looking for a collection
        // that contains an instance of targetType (or assignable to it)
        foreach (var field in typeof(DependencyResolver).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            object? fieldVal;
            try { fieldVal = field.GetValue(resolver); }
            catch { continue; }

            if (fieldVal == null) continue;

            if (dump)
            {
                log?.LogInfo($"[TERRAIN]   Resolver.{field.Name} : {fieldVal.GetType().FullName}");
            }

            // check if it's a dictionary-like collection (IDictionary or IEnumerable<KVP>)
            // Mafi.Collections.Dict doesn't implement IDictionary, so try both paths
            var searched = TrySearchDict(fieldVal, targetType, field.Name, log, dump);
            if (searched != null) return searched;

            // check if it's an array or list
            if (fieldVal is System.Collections.IList list)
            {
                foreach (var item in list)
                {
                    if (item == null) continue;
                    var unwrapped = UnwrapOption(item);
                    if (unwrapped != null && targetType.IsAssignableFrom(unwrapped.GetType()))
                    {
                        log?.LogInfo($"[TERRAIN] Found TerrainManager in resolver field '{field.Name}'");
                        return unwrapped;
                    }
                }
            }

            // direct field value
            var direct = UnwrapOption(fieldVal);
            if (direct != null && targetType.IsAssignableFrom(direct.GetType()))
            {
                log?.LogInfo($"[TERRAIN] Found TerrainManager in resolver field '{field.Name}'");
                return direct;
            }
        }

        log?.LogWarning("[TERRAIN] Could not find TerrainManager in resolver fields.");
        return null;
    }

    private static object? TrySearchDict(object fieldVal, Type targetType, string fieldName, ManualLogSource? log, bool dump)
    {
        // try IDictionary first
        if (fieldVal is System.Collections.IDictionary dict)
        {
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                if (dump && entry.Key is Type k && k.FullName?.Contains("Terrain") == true)
                    log?.LogInfo($"[TERRAIN]     dict key: {k.FullName} -> {entry.Value?.GetType().FullName}");

                if (entry.Key is Type key && key == targetType && entry.Value != null)
                {
                    var val = UnwrapOption(entry.Value);
                    if (val != null && targetType.IsAssignableFrom(val.GetType()))
                    {
                        log?.LogInfo($"[TERRAIN] Found TerrainManager in resolver field '{fieldName}'");
                        return val;
                    }
                }
            }
            return null;
        }

        // try IEnumerable — Mafi.Collections.Dict implements IEnumerable<KeyValuePair<K,V>>
        if (fieldVal is System.Collections.IEnumerable enumerable)
        {
            var valType = fieldVal.GetType();
            // only search types that look like dictionaries (generic with 2 type args, first is Type)
            if (!valType.IsGenericType) return null;
            var genArgs = valType.GetGenericArguments();
            if (genArgs.Length != 2 || genArgs[0] != typeof(Type)) return null;

            foreach (var item in enumerable)
            {
                if (item == null) continue;
                // item is KeyValuePair<Type, TValue> — get Key and Value via reflection
                var itemType = item.GetType();
                var keyProp = itemType.GetProperty("Key");
                var valProp = itemType.GetProperty("Value");
                if (keyProp == null || valProp == null) break;

                var entryKey = keyProp.GetValue(item) as Type;
                var entryVal = valProp.GetValue(item);

                if (dump && entryKey?.FullName?.Contains("Terrain") == true)
                    log?.LogInfo($"[TERRAIN]     dict key: {entryKey.FullName} -> {entryVal?.GetType().FullName}");

                if (entryKey == targetType && entryVal != null)
                {
                    var unwrapped = UnwrapOption(entryVal);
                    if (unwrapped != null && targetType.IsAssignableFrom(unwrapped.GetType()))
                    {
                        log?.LogInfo($"[TERRAIN] Found TerrainManager in resolver field '{fieldName}'");
                        return unwrapped;
                    }
                }
            }
        }

        return null;
    }

    private static object? UnwrapOption(object? value)
    {
        if (value == null) return null;
        var t = value.GetType();
        if (!t.IsGenericType || !t.Name.StartsWith("Option")) return value;

        // check HasValue / IsPresent / IsSome / IsNone
        var hasProp = t.GetProperty("HasValue") ?? t.GetProperty("IsPresent") ?? t.GetProperty("IsSome");
        if (hasProp != null)
        {
            var has = hasProp.GetValue(value);
            if (has is bool b && !b) return null;
        }
        else
        {
            // Mafi's Option uses IsNone
            var noneProp = t.GetProperty("IsNone");
            if (noneProp != null)
            {
                var isNone = noneProp.GetValue(value);
                if (isNone is bool b && b) return null;
            }
        }

        // try Value property first, then Value field
        var valProp = t.GetProperty("Value");
        if (valProp != null)
        {
            try { return valProp.GetValue(value); }
            catch { }
        }

        var valField = t.GetField("Value");
        if (valField != null)
        {
            try { return valField.GetValue(value); }
            catch { }
        }

        return value;
    }
}
