#nullable enable
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenClawSharp.Core;
using OpenClawSharp.Memory;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawSharp.Context;

/// <summary>
/// 上下文构建器接口（输出SK原生ChatHistory）
/// </summary>
public interface IContextBuilder
{
    /// <summary>
    /// 构建系统提示（核心身份+引导文件+内存）
    /// </summary>
    Task<string> BuildSystemPromptAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 构建完整的SK ChatHistory（替代自定义ChatMessage列表）
    /// </summary>
    Task<ChatHistory> BuildChatHistoryAsync(
        List<ChatMessageContent> sessionHistory,
        string userMessage,
        List<string>? media = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 向ChatHistory添加工具执行结果
    /// </summary>
    ChatHistory AddToolResult(ChatHistory chatHistory, FunctionCallContent toolCall, string result);

    /// <summary>
    /// 向ChatHistory添加助手消息（支持工具调用）
    /// </summary>
    ChatHistory AddAssistantMessage(ChatHistory chatHistory, ChatMessageContent assistantMessage);
}

/// <summary>
/// 上下文构建器实现（输出SK ChatHistory，移除Skills加载）
/// </summary>
public class WorkspaceContextBuilder : IContextBuilder
{
    private readonly string[] _bootstrapFiles = { "AGENTS.md", "SOUL.md", "USER.md", "IDENTITY.md" };
    private readonly string _workspace;
    private readonly IMemoryStore _memoryStore;
    private readonly ILogger<WorkspaceContextBuilder> _logger;

    /// <summary>
    /// 构造函数（依赖注入所有核心服务）
    /// </summary>
    public WorkspaceContextBuilder(
        string workspace,
        IMemoryStore memoryStore,
        ILogger<WorkspaceContextBuilder> logger)
    {
        _workspace = workspace.ExpandUser() ?? throw new ArgumentNullException(nameof(workspace));
        _memoryStore = memoryStore ?? throw new ArgumentNullException(nameof(memoryStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.LogInformation("上下文构建器初始化完成 | 工作区：{Workspace}", _workspace);
    }

    /// <summary>
    /// 加载工作区中的引导文件（AGENTS.md等）
    /// </summary>
    private async Task<string> LoadBootstrapFilesAsync(CancellationToken cancellationToken = default)
    {
        var bootstrapContent = new StringBuilder();
        foreach (var fileName in _bootstrapFiles)
        {
            var filePath = Path.Combine(_workspace, fileName);
            if (File.Exists(filePath))
            {
                var content = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                bootstrapContent.AppendLine($"## {fileName}");
                bootstrapContent.AppendLine();
                bootstrapContent.AppendLine(content);
                bootstrapContent.AppendLine();
            }
        }
        return bootstrapContent.ToString().Trim();
    }

    /// <summary>
    /// 构建Agent核心身份提示
    /// </summary>
    private string BuildIdentityPrompt()
    {
        var currentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm (dddd)");
        var memoryFile = Path.Combine(_workspace, OpenClawConstants.MemoryDirName, OpenClawConstants.LongTermMemoryFileName);
        var dailyNotesPath = Path.Combine(_workspace, OpenClawConstants.MemoryDirName, "YYYY-MM-DD.md");

        return $"""
# nanobot 🐈

You are nanobot, a helpful AI assistant. You have access to tools that allow you to:
- Read, write, and edit files
- Execute shell commands
- Send messages to users on chat channels
- Spawn subagents for complex background tasks

## Current Time
{currentTime}

## Workspace
Your workspace is at: {_workspace}
- Memory files: {memoryFile}
- Daily notes: {dailyNotesPath}

IMPORTANT: When responding to direct questions or conversations, reply directly with your text response.
Only use the 'message' tool when you need to send a message to a specific chat channel (like WhatsApp).
For normal conversation, just respond with text - do not call the message tool.

Always be helpful, accurate, and concise. When using tools, explain what you're doing.
When remembering something, write to {memoryFile}
""";
    }

    public async Task<string> BuildSystemPromptAsync(CancellationToken cancellationToken = default)
    {
        var prompt = new StringBuilder();
        // 1. 核心身份
        prompt.AppendLine(BuildIdentityPrompt());
        prompt.AppendLine("---");
        prompt.AppendLine();

        // 2. 加载引导文件
        var bootstrap = await LoadBootstrapFilesAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(bootstrap))
        {
            prompt.AppendLine(bootstrap);
            prompt.AppendLine("---");
            prompt.AppendLine();
        }

        // 3. 加载内存上下文（保留MEMORY.md）
        var memoryContext = await _memoryStore.GetMemoryContextAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(memoryContext))
        {
            prompt.AppendLine("# Memory");
            prompt.AppendLine();
            prompt.AppendLine(memoryContext);
            prompt.AppendLine("---");
            prompt.AppendLine();
        }

        var finalPrompt = prompt.ToString().Trim();
        _logger.LogDebug("系统提示构建完成 | 长度：{Length}字符", finalPrompt.Length);
        return finalPrompt;
    }

    public async Task<ChatHistory> BuildChatHistoryAsync(
        List<ChatMessageContent> sessionHistory,
        string userMessage,
        List<string>? media = null,
        CancellationToken cancellationToken = default)
    {
        if (sessionHistory == null) throw new ArgumentNullException(nameof(sessionHistory));
        if (string.IsNullOrWhiteSpace(userMessage)) throw new ArgumentException("用户消息不能为空", nameof(userMessage));

        var chatHistory = new ChatHistory();

        // 1. 添加系统提示
        var systemPrompt = await BuildSystemPromptAsync(cancellationToken).ConfigureAwait(false);
        chatHistory.AddSystemMessage(systemPrompt);

        // 2. 添加会话历史
        foreach (var msg in sessionHistory)
        {
            chatHistory.Add(msg);
        }

        // 3. 添加用户消息（支持媒体）
        chatHistory.AddUserMessage(userMessage);

        _logger.LogDebug("SK ChatHistory构建完成 | 总消息数：{Count}", chatHistory.Count);
        return chatHistory;
    }

    public ChatHistory AddToolResult(ChatHistory chatHistory, FunctionCallContent toolCall, string result)
    {
        if (chatHistory == null) throw new ArgumentNullException(nameof(chatHistory));
        if (toolCall == null) throw new ArgumentNullException(nameof(toolCall));
        if (string.IsNullOrWhiteSpace(result)) result = "无执行结果";

        // 工具执行结果以用户消息形式添加（SK无Tool角色，用用户消息模拟）
        chatHistory.Add(new FunctionResultContent(toolCall.FunctionName, result).ToChatMessage());
        return chatHistory;
    }

    public ChatHistory AddAssistantMessage(ChatHistory chatHistory, ChatMessageContent assistantMessage)
    {
        if (chatHistory == null) throw new ArgumentNullException(nameof(chatHistory));
        if (assistantMessage == null) throw new ArgumentNullException(nameof(assistantMessage));

        chatHistory.AddAssistantMessage(assistantMessage.Content);
        return chatHistory;
    }
}