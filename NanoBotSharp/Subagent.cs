#nullable enable
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenClawSharp.Bus;
using OpenClawSharp.Config;
using OpenClawSharp.Core;
using OpenClawSharp.Providers;
using OpenClawSharp.Tools;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawSharp.Subagent;

/// <summary>
/// 子代理管理器接口（DI 友好）
/// </summary>
public interface ISubagentManager
{
    /// <summary>
    /// 生成子代理执行后台任务
    /// </summary>
    /// <param name="task">任务描述</param>
    /// <param name="label">任务标签（可选）</param>
    /// <param name="originChannel">结果通知的原通道</param>
    /// <param name="originChatId">结果通知的原聊天ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>任务启动信息</returns>
    Task<string> SpawnAsync(
        string task,
        string? label = null,
        string originChannel = "cli",
        string originChatId = "direct",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取当前运行中的子代理任务数
    /// </summary>
    int GetRunningTaskCount();
}

/// <summary>
/// 子代理管理器实现（适配SK原生类型）
/// </summary>
public class SubagentManager : ISubagentManager
{
    private readonly ILlmProvider _llmProvider;
    private readonly string _workspace;
    private readonly IMessageBus _messageBus;
    private readonly ExecToolConfig _execToolConfig;
    private readonly ILogger<SubagentManager> _logger;
    private readonly ConcurrentDictionary<string, Task> _runningTasks = new();
    private readonly ISkPluginRegistry _pluginRegistry;
    private readonly Kernel _kernel;

    public SubagentManager(
        ILlmProvider llmProvider,
        string workspace,
        IMessageBus messageBus,
        ISkPluginRegistry pluginRegistry,
        ExecToolConfig execToolConfig,
        Kernel kernel,
        ILogger<SubagentManager> logger)
    {
        _llmProvider = llmProvider ?? throw new ArgumentNullException(nameof(llmProvider));
        _workspace = workspace.ExpandUser() ?? throw new ArgumentNullException(nameof(workspace));
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _pluginRegistry = pluginRegistry ?? throw new ArgumentNullException(nameof(pluginRegistry));
        _execToolConfig = execToolConfig ?? throw new ArgumentNullException(nameof(execToolConfig));
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _workspace.EnsureDirectoryExists();
        _logger.LogInformation("子代理管理器初始化完成 | 工作区：{Workspace}", _workspace);
    }

    /// <summary>
    /// 构建子代理的系统提示
    /// </summary>
    private string BuildSubagentPrompt(string task)
    {
        return $"""
# Subagent

You are a subagent spawned by the main agent to complete a specific task.

## Your Task
{task}

## Rules
1. Stay focused - complete only the assigned task, nothing else
2. Your final response will be reported back to the main agent
3. Do not initiate conversations or take on side tasks
4. Be concise but informative in your findings

## What You Can Do
- Read and write files in the workspace
- Execute shell commands
- Complete the task thoroughly

## What You Cannot Do
- Send messages directly to users (no message tool available)
- Spawn other subagents
- Access the main agent's conversation history

## Workspace
Your workspace is at: {_workspace}

When you have completed the task, provide a clear summary of your findings or actions.
""";
    }

    /// <summary>
    /// 向主代理发布子代理执行结果
    /// </summary>
    private async Task AnnounceResultAsync(
        string taskId,
        string label,
        string task,
        string result,
        string originChannel,
        string originChatId,
        string status,
        CancellationToken cancellationToken = default)
    {
        var statusText = status == "ok" ? "completed successfully" : "failed";
        var announceContent = $"""
[Subagent '{label}' {statusText}]

Task: {task}

Result:
{result}

Summarize this naturally for the user. Keep it brief (1-2 sentences). Do not mention technical details like "subagent" or task IDs.
""";

        // 构造系统入站消息，发布到消息总线
        var inboundMsg = new InboundMessage
        {
            Channel = OpenClawConstants.SystemChannelName,
            SenderId = "subagent",
            ChatId = $"{originChannel}:{originChatId}",
            Content = announceContent
        };

        await _messageBus.PublishInboundAsync(inboundMsg, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("子代理执行结果已发布 | {TaskId} | {OriginChannel}:{OriginChatId}",
            taskId, originChannel, originChatId);
    }

    /// <summary>
    /// 子代理核心执行逻辑（适配SK原生类型）
    /// </summary>
    private async Task RunSubagentAsync(
        string taskId,
        string task,
        string label,
        string originChannel,
        string originChatId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("子代理开始执行 | {TaskId} | {Label}", taskId, label);
        try
        {
            // 构建子代理的SK插件列表（排除消息/子代理插件）
            var tools = new List<KernelFunction>();
            tools.AddRange(_kernel.Plugins["ReadFile"].Select(f => f));
            tools.AddRange(_kernel.Plugins["WriteFile"].Select(f => f));
            tools.AddRange(_kernel.Plugins["EditFile"].Select(f => f));
            tools.AddRange(_kernel.Plugins["ListDir"].Select(f => f));
            tools.AddRange(_kernel.Plugins["Exec"].Select(f => f));

            // 构建初始SK ChatHistory
            var chatHistory = new ChatHistory();
            chatHistory.AddSystemMessage(BuildSubagentPrompt(task));
            chatHistory.AddUserMessage(task);

            string? finalResult = null;
            var iteration = 0;

            // 子代理核心循环
            while (iteration < OpenClawConstants.SubagentMaxIterations && !cancellationToken.IsCancellationRequested)
            {
                iteration++;
                _logger.LogDebug("子代理执行迭代 | {TaskId} | {Iteration}/{Max}",
                    taskId, iteration, OpenClawConstants.SubagentMaxIterations);

                // 调用LLM（SK原生类型）
                var llmResponse = await _llmProvider.ChatAsync(
                    chatHistory,
                    tools,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                // 提取工具调用
                var toolCalls = FunctionCallContent.GetFunctionCalls(llmResponse).ToArray();

                if (toolCalls.Any())
                {
                    // 添加助手消息到上下文
                    chatHistory.Add(llmResponse);

                    // 执行所有工具调用
                    foreach (var toolCall in toolCalls)
                    {
                        _logger.LogDebug("子代理执行插件 | {TaskId} | {PluginName}.{FunctionName}",
                            taskId, toolCall.PluginName, toolCall.FunctionName);

                        try
                        {
                            var toolResult = await _pluginRegistry.ExecuteFunctionAsync(
                                toolCall.PluginName,
                                toolCall.FunctionName,
                                toolCall.Arguments.ToDictionary(kv => kv.Key, kv => kv.Value),
                                cancellationToken).ConfigureAwait(false);

                            // 添加工具执行结果到上下文
                            chatHistory.AddUserMessage($"工具执行结果 [{toolCall.FunctionName}]: {toolResult}");
                        }
                        catch (ToolExecutionException ex)
                        {
                            _logger.LogError(ex, "子代理插件执行失败 | {TaskId} | {PluginName}.{FunctionName}",
                                taskId, toolCall.PluginName, toolCall.FunctionName);

                            chatHistory.AddUserMessage($"工具执行失败 [{toolCall.FunctionName}]: {ex.Message}");
                        }
                    }
                }
                else
                {
                    // 无工具调用，结束循环
                    finalResult = llmResponse.Content?.Trim() ?? string.Empty;
                    break;
                }
            }

            // 处理无结果的情况
            finalResult ??= "Task completed but no final response was generated.";
            _logger.LogInformation("子代理执行成功 | {TaskId}", taskId);
            await AnnounceResultAsync(taskId, label, task, finalResult, originChannel, originChatId, "ok", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var errorMsg = $"执行失败：{ex.Message}";
            _logger.LogError(ex, "子代理执行异常 | {TaskId}", taskId);
            await AnnounceResultAsync(taskId, label, task, errorMsg, originChannel, originChatId, "error", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // 任务完成后从运行中移除
            _runningTasks.TryRemove(taskId, out _);
        }
    }

    public async Task<string> SpawnAsync(
        string task,
        string? label = null,
        string originChannel = "cli",
        string originChatId = "direct",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(task))
            throw new ArgumentException("任务描述不能为空", nameof(task));

        // 生成8位唯一任务ID
        var taskId = Guid.NewGuid().ToString("N")[..8];
        label ??= task.Length > 30 ? $"{task[..30]}..." : task;

        // 创建后台任务
        var backgroundTask = RunSubagentAsync(taskId, task, label, originChannel, originChatId, cancellationToken);
        _runningTasks[taskId] = backgroundTask;

        // 任务异常处理（不阻塞主线程）
        _ = backgroundTask.ContinueWith(t =>
        {
            if (t.Exception != null)
                _logger.LogError(t.Exception, "子代理后台任务异常 | {TaskId}", taskId);
        }, TaskContinuationOptions.OnlyOnFaulted);

        _logger.LogInformation("子代理已启动 | {TaskId} | {Label}", taskId, label);
        return $"Subagent [{label}] started (id: {taskId}). I'll notify you when it completes.";
    }

    public int GetRunningTaskCount() => _runningTasks.Count;
}