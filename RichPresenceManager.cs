using Steamworks;

namespace VSCodePresenceSteamClient;

internal sealed class RichPresenceManager
{
    public void UpdatePresence(Dictionary<string, object?> payload)
    {
        // 每次收到新的状态快照时，先清空之前的字段，避免本次 payload 未包含的旧字段继续残留在 Steam 上。
        SteamFriends.ClearRichPresence();
        SteamFriends.SetRichPresence("steam_display", "正在使用 VSCode");
        SteamFriends.SetRichPresence("status", "正在使用 VSCode");

        if (payload is null || payload.Count == 0)
        {
            Console.WriteLine("[RichPresence] payload 为空，已回退到默认状态。");
            SteamAPI.RunCallbacks();
            return;
        }

        var normalizedPayload = new Dictionary<string, object?>(payload, StringComparer.OrdinalIgnoreCase);
        var currentFileName = SetIfPresent(normalizedPayload, "currentFileName");
        var languageId = SetIfPresent(normalizedPayload, "languageId");
        var gitBranch = SetIfPresent(normalizedPayload, "gitBranch");
        var workspaceName = SetIfPresent(normalizedPayload, "workspaceName");

        // 这些字段可以直接透传给 Steam Rich Presence，键名保持与 VS Code 扩展发送值一致。
        // 后续如果 bridge 增加了新的 selectedStatusItems，可继续在这里按同样模式追加。
        SetIfPresent(normalizedPayload, "lineNumber");
        SetIfPresent(normalizedPayload, "columnNumber");
        SetIfPresent(normalizedPayload, "debugType");
        SetIfPresent(normalizedPayload, "openEditorsCount");
        SetIfPresent(normalizedPayload, "vscodeVersion");
        SetIfPresent(normalizedPayload, "themeName");
        SetIfPresent(normalizedPayload, "editingTimeInMinutes");

        if (TryGetBoolean(normalizedPayload, "isDirty", out var isDirty))
        {
            var dirtyText = isDirty ? "未保存" : string.Empty;
            SteamFriends.SetRichPresence("isDirty", dirtyText);
        }

        if (TryGetBoolean(normalizedPayload, "isDebugging", out var isDebugging))
        {
            SteamFriends.SetRichPresence("isDebugging", isDebugging ? "true" : "false");

            if (isDebugging)
            {
                SteamFriends.SetRichPresence("status", "正在调试代码");
            }
        }
        else if (!string.IsNullOrWhiteSpace(currentFileName))
        {
            SteamFriends.SetRichPresence("status", $"正在编辑 {currentFileName}");
        }

        if (string.IsNullOrWhiteSpace(currentFileName) &&
            string.IsNullOrWhiteSpace(languageId) &&
            string.IsNullOrWhiteSpace(gitBranch) &&
            string.IsNullOrWhiteSpace(workspaceName) &&
            !TryGetBoolean(normalizedPayload, "isDebugging", out _))
        {
            SteamFriends.SetRichPresence("status", "正在使用 VSCode");
        }

        Console.WriteLine($"[RichPresence] 更新成功 | 文件: {currentFileName ?? "-"} | 语言: {languageId ?? "-"} | 分支: {gitBranch ?? "-"} | 工作区: {workspaceName ?? "-"}");
        SteamAPI.RunCallbacks();
    }

    private static string? SetIfPresent(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value))
        {
            return null;
        }

        var text = value?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        SteamFriends.SetRichPresence(key, text);
        return text;
    }

    private static bool TryGetBoolean(IReadOnlyDictionary<string, object?> payload, string key, out bool booleanValue)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
        {
            booleanValue = false;
            return false;
        }

        switch (value)
        {
            case bool directBoolean:
                booleanValue = directBoolean;
                return true;
            case string text when bool.TryParse(text, out var parsedBoolean):
                booleanValue = parsedBoolean;
                return true;
            default:
                booleanValue = false;
                return false;
        }
    }
}