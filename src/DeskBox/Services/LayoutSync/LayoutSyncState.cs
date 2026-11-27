using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Services;

namespace DeskBox.Services.LayoutSync;

/// <summary>
/// 本机的「上次同步到哪」记录。放在 data 目录，不参与同步：它记的是这台
/// 机器自己的指纹与时间戳，另一台看到只会打架。
/// </summary>
internal sealed class LayoutSyncState
{
    public sealed class WidgetState
    {
        public string Key { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public Dictionary<string, WidgetState> Widgets { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, DateTimeOffset> DeadKeys { get; } = new(StringComparer.Ordinal);

    public string ShellSignature { get; set; } = string.Empty;
    public DateTimeOffset ShellAt { get; set; }

    public string DrawerSignature { get; set; } = string.Empty;
    public DateTimeOffset DrawerAt { get; set; }

    public string CapsuleSignature { get; set; } = string.Empty;
    public DateTimeOffset CapsuleAt { get; set; }

    public long RemoteRev { get; set; }

    private static string StatePath => Path.Combine(
        DeskBoxDataPathService.Current.DataDirectory,
        "layout-sync-state.json");

    public static LayoutSyncState Load()
    {
        var state = new LayoutSyncState();
        try
        {
            if (!File.Exists(StatePath))
            {
                return state;
            }

            JsonNode? root = JsonNode.Parse(File.ReadAllText(StatePath));
            if (root is not JsonObject obj)
            {
                return state;
            }

            state.RemoteRev = ReadLong(obj["remoteRev"]);
            if (obj["widgets"] is JsonObject widgets)
            {
                foreach ((string id, JsonNode? node) in widgets)
                {
                    if (node is not JsonObject w)
                    {
                        continue;
                    }

                    state.Widgets[id] = new WidgetState
                    {
                        Key = ReadString(w["key"]),
                        Signature = ReadString(w["sig"]),
                        UpdatedAt = ReadTime(w["at"])
                    };
                }
            }

            if (obj["dead"] is JsonObject dead)
            {
                foreach ((string key, JsonNode? node) in dead)
                {
                    state.DeadKeys[key] = ReadTime(node);
                }
            }

            state.ShellSignature = ReadString(obj["shellSig"]);
            state.ShellAt = ReadTime(obj["shellAt"]);
            state.DrawerSignature = ReadString(obj["drawerSig"]);
            state.DrawerAt = ReadTime(obj["drawerAt"]);
            state.CapsuleSignature = ReadString(obj["capsuleSig"]);
            state.CapsuleAt = ReadTime(obj["capsuleAt"]);
        }
        catch (Exception ex)
        {
            App.Log("[LayoutSync] state load failed: " + ex.Message);
        }

        return state;
    }

    public void Save()
    {
        try
        {
            var widgets = new JsonObject();
            foreach ((string id, WidgetState w) in Widgets)
            {
                widgets[id] = new JsonObject
                {
                    ["key"] = w.Key,
                    ["sig"] = w.Signature,
                    ["at"] = w.UpdatedAt
                };
            }

            var dead = new JsonObject();
            foreach ((string key, DateTimeOffset at) in DeadKeys)
            {
                dead[key] = at;
            }

            var root = new JsonObject
            {
                ["remoteRev"] = RemoteRev,
                ["widgets"] = widgets,
                ["dead"] = dead,
                ["shellSig"] = ShellSignature,
                ["shellAt"] = ShellAt,
                ["drawerSig"] = DrawerSignature,
                ["drawerAt"] = DrawerAt,
                ["capsuleSig"] = CapsuleSignature,
                ["capsuleAt"] = CapsuleAt
            };

            string dir = Path.GetDirectoryName(StatePath) ?? string.Empty;
            if (dir.Length > 0)
            {
                Directory.CreateDirectory(dir);
            }

            // 本地状态文件损坏的代价只是多推一次布局，不需要原子替换；
            // File.Move/Delete 属于受控的文件变更域（DeskBox.Platform /
            // Core.Persistence），这里刻意不用它们。
            string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StatePath, json);
        }
        catch (Exception ex)
        {
            App.Log("[LayoutSync] state save failed: " + ex.Message);
        }
    }

    private static string ReadString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? s) ? s ?? string.Empty : string.Empty;

    private static long ReadLong(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out long n) ? n : 0;

    private static DateTimeOffset ReadTime(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue(out string? s) &&
            DateTimeOffset.TryParse(s, out DateTimeOffset parsed))
        {
            return parsed;
        }

        return default;
    }
}
