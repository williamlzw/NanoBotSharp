#nullable enable
using OpenClawSharp.Core;
using System;
using System.Collections.Generic;

namespace OpenClawSharp.Config;

/// <summary>
/// 根配置（应用全局配置）
/// </summary>
public sealed class OpenClawConfig
{
    public string Workspace { get; set; } = "./workspace".ExpandUser();
    public LlmConfig Llm { get; set; } = new LlmConfig();
    public ChannelConfigCollection Channels { get; set; } = new ChannelConfigCollection();
    public ExecToolConfig ExecTool { get; set; } = new ExecToolConfig();
    public AgentConfig Agent { get; set; } = new AgentConfig();

    public OpenClawConfig() { }

    public static OpenClawConfig CreateDefault(string workspace = "./workspace")
    {
        workspace = workspace.ExpandUser();
        workspace.EnsureDirectoryExists();

        return new OpenClawConfig
        {
            Workspace = workspace,
            Llm = new LlmConfig
            {
                ApiBase = OpenClawConstants.DefaultOllamaApiBase,
                DefaultModel = OpenClawConstants.DefaultOllamaModel,
                MaxTokens = OpenClawConstants.DefaultLlmMaxTokens,
                Temperature = OpenClawConstants.DefaultLlmTemperature
            },
            Channels = new ChannelConfigCollection
            {
                Console = new ConsoleChannelConfig
                {
                    Enabled = true,
                    AllowFrom = new List<string> { "console_user" }
                }
            },
            ExecTool = new ExecToolConfig
            {
                Timeout = 30000,
                RestrictToWorkspace = true
            },
            Agent = new AgentConfig
            {
                MaxIterations = OpenClawConstants.DefaultMaxIterations
            }
        };
    }
}

/// <summary>
/// LLM 配置
/// </summary>
public sealed class LlmConfig
{
    public string ApiBase { get; set; } = OpenClawConstants.DefaultOllamaApiBase;
    public string DefaultModel { get; set; } = OpenClawConstants.DefaultOllamaModel;
    public string Endpoint { get; set; } = OpenClawConstants.DefaultOllamaApiBase;
    public int MaxTokens { get; set; } = OpenClawConstants.DefaultLlmMaxTokens;
    public float Temperature { get; set; } = OpenClawConstants.DefaultLlmTemperature;

    public LlmConfig() { }
}

/// <summary>
/// 代理核心配置
/// </summary>
public sealed class AgentConfig
{
    public int MaxIterations { get; set; } = OpenClawConstants.DefaultMaxIterations;
    public string? BraveApiKey { get; set; }

    public AgentConfig() { }
}

/// <summary>
/// 执行工具配置
/// </summary>
public sealed class ExecToolConfig
{
    public int Timeout { get; set; } = 30000;
    public bool RestrictToWorkspace { get; set; } = true;

    public ExecToolConfig() { }
}

/// <summary>
/// 通道配置基类
/// </summary>
public abstract class ChannelConfig
{
    public bool Enabled { get; set; } = true;
    public List<string> AllowFrom { get; set; } = new List<string>();
}

/// <summary>
/// 通道配置集合
/// </summary>
public sealed class ChannelConfigCollection
{
    public ConsoleChannelConfig Console { get; set; } = new ConsoleChannelConfig();

    public ChannelConfigCollection() { }
}

/// <summary>
/// 控制台通道配置
/// </summary>
public sealed class ConsoleChannelConfig : ChannelConfig
{
    public ConsoleChannelConfig() { }
}