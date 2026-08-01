// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using AIMemory.Core.Models;

namespace AIMemory.Core.Services;

public static class AgentIntegrationStateService
{
    public static AgentIntegrationStatus ApplyConfigurationState(
        AgentIntegrationStatus detected,
        bool hasAIMemoryConfiguration)
    {
        if (!detected.IsDetected)
        {
            return detected with
            {
                IsIntegrated = false,
                State = AgentIntegrationState.Missing,
                Detail = hasAIMemoryConfiguration
                    ? "本机未安装；发现此前保留的 AI Memory 配置，当前不会启动。"
                    : "本机未安装，默认不启用。",
            };
        }
        if (!hasAIMemoryConfiguration)
        {
            return detected with
            {
                IsIntegrated = false,
                State = AgentIntegrationState.Detected,
            };
        }
        return detected with
        {
            IsIntegrated = true,
            State = AgentIntegrationState.Integrated,
            Detail = "AI Memory MCP 已启用。",
        };
    }
}
