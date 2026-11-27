using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace DeskBox.Services.LayoutSync;

/// <summary>
/// 待办与随手记的逐条同步。两台机器各自有本地库文件，共享一份镜像放在资料库
/// 的 _deskbox\items\ 下，由坚果云搬运；每次读/写本地文件时先和镜像合并。
///
/// 合并规则（用户定：以最后修改的为准）：
/// 1. 同 id 比 updatedAt，晚的整条覆盖早的；
/// 2. 时间相同则 isDeleted=true 的赢（删除不会因为时钟精度复活）；
/// 3. 本机删掉的条目（上次见过、这次没了）补一条墓碑写进镜像，否则另一台会
///    把它当「本机没有」再传回来。
/// 4. 第一次跑只做并集，不补墓碑——否则新机的空列表会把对端全删掉。
/// </summary>
internal static class ItemSyncBridge
{
    private const string StateDirName = "_deskbox";

    private static readonly string[] s_arrayNames = ["items", "recentItems"];

    private static readonly object s_gate = new();

    private static Dictionary<string, HashSet<string>>? s_seen;

    /// <summary>资料库根目录（引擎 /api/status 报的绝对路径）。空 = 同步关闭。</summary>
    public static string LibraryRoot { get; set; } = string.Empty;

    private static string SeenPath => Path.Combine(
        DeskBoxDataPathService.Current.DataDirectory,
        "item-sync-state.json");

    /// <summary>
    /// 读本地文件时调用：并进镜像里的另一台内容，返回本机该看到的内容。
    /// 不补墓碑、不写本地文件（写盘由调用方或 SyncFile 负责）。
    /// </summary>
    public static string MergeText(string key, string localJson)
    {
        try
        {
            MergeResult result = MergeCore(key, localJson, synthesiseDeletions: false);
            if (result.RemoteChanged)
            {
                WriteMirror(key, result.RemoteJson);
            }

            return result.LocalJson;
        }
        catch (Exception ex)
        {
            App.Log("[ItemSync] merge failed: " + ex.Message);
            return localJson;
        }
    }

    /// <summary>
    /// 本地文件刚写完之后调用：再合并一次（拉回对端更新的条目）、给本机删掉
    /// 的条目补墓碑、并把成果写回本地文件和共享镜像。
    /// </summary>
    public static void SyncFile(string key, string path)
    {
        try
        {
            if (string.IsNullOrEmpty(LibraryRoot) || !File.Exists(path))
            {
                return;
            }

            string localJson = File.ReadAllText(path);
            MergeResult result = MergeCore(key, localJson, synthesiseDeletions: true);
            WriteMirror(key, result.RemoteJson);
            RememberSeen(key, result.RemoteJson);
            if (result.LocalChanged)
            {
                File.WriteAllText(path, result.LocalJson);
            }
        }
        catch (Exception ex)
        {
            App.Log("[ItemSync] sync-after-save failed: " + ex.Message);
        }
    }

    private static MergeResult MergeCore(string key, string localJson, bool synthesiseDeletions)
    {
        if (string.IsNullOrEmpty(LibraryRoot))
        {
            return new MergeResult(localJson, localJson, false, false);
        }

        string mirrorPath = MirrorPath(key);
        string? remoteJson = null;
        try
        {
            if (File.Exists(mirrorPath))
            {
                remoteJson = File.ReadAllText(mirrorPath);
            }
        }
        catch (Exception ex)
        {
            App.Log("[ItemSync] read mirror failed: " + ex.Message);
        }

        JsonObject local = ParseObject(localJson);
        JsonObject remote = remoteJson is null ? new JsonObject() : ParseObject(remoteJson);
        bool localChanged = false;
        bool remoteChanged = remoteJson is null;

        foreach (string name in s_arrayNames)
        {
            JsonArray? localArray = local[name] as JsonArray;
            JsonArray? remoteArray = remote[name] as JsonArray;
            if (localArray is null && remoteArray is null)
            {
                continue;
            }

            localArray ??= new JsonArray();
            remoteArray ??= new JsonArray();

            Dictionary<string, JsonNode?> byLocal = Index(localArray);
            Dictionary<string, JsonNode?> byRemote = Index(remoteArray);
            var merged = new List<JsonNode?>();
            var used = new HashSet<string>(StringComparer.Ordinal);

            foreach ((string id, JsonNode? localNode) in byLocal)
            {
                used.Add(id);
                if (!byRemote.TryGetValue(id, out JsonNode? remoteNode))
                {
                    merged.Add(((JsonObject)localNode!).DeepClone());
                    remoteChanged = true;
                    continue;
                }

                int order = CompareUpdated(localNode, remoteNode);
                if (order > 0)
                {
                    merged.Add(((JsonObject)localNode!).DeepClone());
                    remoteChanged = true;
                }
                else if (order < 0)
                {
                    merged.Add(((JsonObject)remoteNode!).DeepClone());
                    localChanged = true;
                }
                else
                {
                    // 同一时刻：删除赢，避免「删了又回来」。
                    bool localDeleted = IsDeleted(localNode);
                    bool remoteDeleted = IsDeleted(remoteNode);
                    if (localDeleted == remoteDeleted)
                    {
                        merged.Add(((JsonObject)localNode!).DeepClone());
                    }
                    else if (localDeleted)
                    {
                        merged.Add(((JsonObject)localNode!).DeepClone());
                        remoteChanged = true;
                    }
                    else
                    {
                        merged.Add(((JsonObject)remoteNode!).DeepClone());
                        localChanged = true;
                    }
                }
            }

            // 远端独有的条目：本机没有就补进来。
            foreach ((string id, JsonNode? remoteNode) in byRemote)
            {
                if (used.Contains(id))
                {
                    continue;
                }

                merged.Add(((JsonObject)remoteNode!).DeepClone());
                localChanged = true;
            }

            // 本机删掉的（上次见过、这次没了）补墓碑，否则对端会把它传回来。
            if (synthesiseDeletions)
            {
                HashSet<string> seen = SeenFor(key);
                if (seen.Count > 0)
                {
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    foreach (string id in seen)
                    {
                        if (byLocal.ContainsKey(id) || byRemote.ContainsKey(id))
                        {
                            continue;
                        }

                        merged.Add(new JsonObject
                        {
                            ["id"] = id,
                            ["isDeleted"] = true,
                            ["updatedAt"] = now.ToString("O", CultureInfo.InvariantCulture)
                        });
                        remoteChanged = true;
                    }
                }
            }

            JsonArray rebuilt = new JsonArray();
            foreach (JsonNode? node in merged)
            {
                rebuilt.Add(node);
            }

            local[name] = rebuilt.DeepClone();
            remote[name] = rebuilt;
        }

        return new MergeResult(
            local.ToJsonString(),
            remote.ToJsonString(),
            localChanged,
            remoteChanged);
    }

    private static Dictionary<string, JsonNode?> Index(JsonArray array)
    {
        var map = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (JsonNode? node in array)
        {
            if (node is not JsonObject obj)
            {
                continue;
            }

            string? id = obj["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            map[id] = obj;
        }

        return map;
    }

    private static int CompareUpdated(JsonNode? left, JsonNode? right)
    {
        DateTimeOffset a = ReadUpdated(left);
        DateTimeOffset b = ReadUpdated(right);
        return a.CompareTo(b);
    }

    private static DateTimeOffset ReadUpdated(JsonNode? node)
    {
        if (node is JsonObject obj &&
            obj["updatedAt"] is JsonValue value &&
            value.TryGetValue(out string? raw) &&
            DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed))
        {
            return parsed;
        }

        return DateTimeOffset.MinValue;
    }

    private static bool IsDeleted(JsonNode? node)
    {
        if (node is JsonObject obj && obj["isDeleted"] is JsonValue value &&
            value.TryGetValue(out bool deleted))
        {
            return deleted;
        }

        return false;
    }

    private static JsonObject ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static string MirrorPath(string key) => Path.Combine(
        LibraryRoot,
        StateDirName,
        "items",
        key + ".json");

    private static void WriteMirror(string key, string json)
    {
        string path = MirrorPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private static void RememberSeen(string key, string json)
    {
        lock (s_gate)
        {
            s_seen ??= LoadSeen();
            s_seen[key] = new HashSet<string>(IdsOf(json), StringComparer.Ordinal);
            SaveSeen(s_seen);
        }
    }

    private static HashSet<string> SeenFor(string key)
    {
        lock (s_gate)
        {
            s_seen ??= LoadSeen();
            return s_seen.TryGetValue(key, out HashSet<string>? ids)
                ? ids
                : new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static IEnumerable<string> IdsOf(string json)
    {
        JsonObject root = ParseObject(json);
        foreach (string name in s_arrayNames)
        {
            if (root[name] is not JsonArray array)
            {
                continue;
            }

            foreach (string id in Index(array).Keys)
            {
                yield return id;
            }
        }
    }

    private static Dictionary<string, HashSet<string>> LoadSeen()
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(SeenPath))
            {
                return result;
            }

            JsonObject root = ParseObject(File.ReadAllText(SeenPath));
            foreach ((string key, JsonNode? node) in root)
            {
                if (node is not JsonArray array)
                {
                    continue;
                }

                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonNode? item in array)
                {
                    if (item is JsonValue value && value.TryGetValue(out string? id) &&
                        !string.IsNullOrEmpty(id))
                    {
                        ids.Add(id);
                    }
                }

                result[key] = ids;
            }
        }
        catch (Exception ex)
        {
            App.Log("[ItemSync] read seen-state failed: " + ex.Message);
        }

        return result;
    }

    private static void SaveSeen(Dictionary<string, HashSet<string>> seen)
    {
        try
        {
            var root = new JsonObject();
            foreach ((string key, HashSet<string> ids) in seen)
            {
                var array = new JsonArray();
                foreach (string id in ids.OrderBy(v => v, StringComparer.Ordinal))
                {
                    array.Add((JsonNode?)JsonValue.Create(id));
                }

                root[key] = array;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(SeenPath)!);
            File.WriteAllText(SeenPath, root.ToJsonString());
        }
        catch (Exception ex)
        {
            App.Log("[ItemSync] write seen-state failed: " + ex.Message);
        }
    }

    private sealed record MergeResult(
        string LocalJson,
        string RemoteJson,
        bool LocalChanged,
        bool RemoteChanged);
}
