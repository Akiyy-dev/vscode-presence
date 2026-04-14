using System.Globalization;
using System.Text.Json;
using Steamworks;

namespace VSCodePresenceSteamClient;

internal sealed class RichPresenceManager
{
    private readonly string _presenceDisplayToken;

    // 扩展字段时，优先把 VS Code 插件真实发送的字段名放在 aliases 的第一个位置。
    // 后续 aliases 可用于兼容旧字段名、测试数据或手动构造的数据，例如 filename -> currentFileName。
    // Steam Rich Presence 的输出 key 保持使用规范化后的字段名，确保与 selectedStatusItems 完全一致。
    private static readonly PresenceFieldMapping[] FieldMappings =
    [
        new("currentFileName", ["currentFileName", "filename"], FormatText),
        new("languageId", ["languageId", "language"], FormatText),
        new("gitBranch", ["gitBranch", "branch"], FormatText),
        new("isDebugging", ["isDebugging"], FormatBooleanLower),
        new("workspaceName", ["workspaceName", "workspace"], FormatText),
        new("lineNumber", ["lineNumber", "line"], FormatNumberLike),
        new("columnNumber", ["columnNumber", "column"], FormatNumberLike),
        new("isDirty", ["isDirty"], FormatDirtyState),
        new("debugType", ["debugType"], FormatText),
        new("openEditorsCount", ["openEditorsCount"], FormatNumberLike),
        new("vscodeVersion", ["vscodeVersion"], FormatText),
        new("themeName", ["themeName"], FormatText),
        new("editingTimeInMinutes", ["editingTimeInMinutes"], FormatNumberLike)
    ];

    public RichPresenceManager(string? presenceDisplayToken)
    {
        _presenceDisplayToken = string.IsNullOrWhiteSpace(presenceDisplayToken)
            ? "#VSCodeStatus"
            : presenceDisplayToken;
    }

    public void UpdatePresence(Dictionary<string, object?> payload)
    {
        // 插件可能只发送用户在 selectedStatusItems 中勾选的字段。
        // 每次更新前先清空旧状态，避免上一次存在、本次未发送的字段残留在 Steam 中。
        SteamFriends.ClearRichPresence();
        SteamFriends.SetRichPresence("steam_display", _presenceDisplayToken);

        if (payload is null || payload.Count == 0)
        {
            SetDefaultStatus();
            SteamAPI.RunCallbacks();
            return;
        }

        var normalizedPayload = new Dictionary<string, object?>(payload, StringComparer.OrdinalIgnoreCase);
        var appliedCount = 0;

        foreach (var mapping in FieldMappings)
        {
            if (!TryResolveValue(normalizedPayload, mapping.Aliases, out var rawValue))
            {
                continue;
            }

            var formattedValue = mapping.Formatter(rawValue);
            if (string.IsNullOrWhiteSpace(formattedValue))
            {
                continue;
            }

            if (SteamFriends.SetRichPresence(mapping.SteamKey, formattedValue))
            {
                appliedCount++;
            }
        }

        // 如果 payload 存在但都为空、false 或不可用，则给出一个稳定的默认状态。
        if (appliedCount == 0)
        {
            SetDefaultStatus();
        }

        SteamAPI.RunCallbacks();
    }

    private void SetDefaultStatus()
    {
        SteamFriends.SetRichPresence("status", "正在使用 VSCode");
    }

    private static bool TryResolveValue(
        IReadOnlyDictionary<string, object?> payload,
        IReadOnlyList<string> aliases,
        out object? value)
    {
        foreach (var alias in aliases)
        {
            if (payload.TryGetValue(alias, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string? FormatText(object? value)
    {
        var text = CoerceToString(value);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? FormatNumberLike(object? value)
    {
        return value switch
        {
            byte number => number.ToString(CultureInfo.InvariantCulture),
            short number => number.ToString(CultureInfo.InvariantCulture),
            int number => number.ToString(CultureInfo.InvariantCulture),
            long number => number.ToString(CultureInfo.InvariantCulture),
            float number => number.ToString(CultureInfo.InvariantCulture),
            double number => number.ToString(CultureInfo.InvariantCulture),
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            JsonElement element when element.ValueKind == JsonValueKind.Number => element.ToString(),
            _ => FormatText(value)
        };
    }

    private static string? FormatBooleanLower(object? value)
    {
        if (TryGetBoolean(value, out var booleanValue))
        {
            return booleanValue ? "true" : "false";
        }

        var text = CoerceToString(value);
        return string.IsNullOrWhiteSpace(text) ? null : text.ToLowerInvariant();
    }

    private static string? FormatDirtyState(object? value)
    {
        if (!TryGetBoolean(value, out var isDirty))
        {
            return null;
        }

        return isDirty ? "未保存" : null;
    }

    private static string? CoerceToString(object? value)
    {
        return value switch
        {
            null => null,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            JsonElement element when element.ValueKind == JsonValueKind.True => "true",
            JsonElement element when element.ValueKind == JsonValueKind.False => "false",
            JsonElement element when element.ValueKind == JsonValueKind.Number => element.ToString(),
            JsonElement element when element.ValueKind == JsonValueKind.Null => null,
            _ => value.ToString()
        };
    }

    private static bool TryGetBoolean(object? value, out bool booleanValue)
    {
        switch (value)
        {
            case bool directBoolean:
                booleanValue = directBoolean;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.True:
                booleanValue = true;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.False:
                booleanValue = false;
                return true;
            case string text when bool.TryParse(text, out var parsedBoolean):
                booleanValue = parsedBoolean;
                return true;
            default:
                booleanValue = false;
                return false;
        }
    }

    private sealed record PresenceFieldMapping(
        string SteamKey,
        IReadOnlyList<string> Aliases,
        Func<object?, string?> Formatter);
}