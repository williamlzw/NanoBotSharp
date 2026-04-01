#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenClawSharp.Agent;
using OpenClawSharp.Bus;
using OpenClawSharp.Config;
using OpenClawSharp.Context;
using OpenClawSharp.Core;
using OpenClawSharp.Providers;
using OpenClawSharp.Session;
using OpenClawSharp.Tools;
using OpenClawSharp.Tools.Impl;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawSharp.Agent;

/// <summary>
/// 代理核心循环接口（适配SK原生类型）
/// </summary>
public interface IAgentLoop
{
    /// <summary>
    /// 代理是否正在运行
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// 启动代理核心循环（长期运行，消费消息总线入站消息并处理）
    /// </summary>
    Task RunAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止代理核心循环（优雅取消）
    /// </summary>
    void Stop();

    /// <summary>
    /// 直接处理CLI消息（无通道依赖，供控制台直接调用）
    /// </summary>
    Task<string> ProcessDirectMessageAsync(string content, string sessionKey = OpenClawConstants.DefaultSessionKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// 代理核心循环实现（适配SK原生类型）
/// </summary>
public class AgentLoop : IAgentLoop
{
    #region 核心依赖与配置
    private readonly IMessageBus _messageBus;
    private readonly ILlmProvider _llmProvider;
    private readonly IContextBuilder _contextBuilder;
    private readonly ISessionManager _sessionManager;
    private readonly ISkPluginRegistry _pluginRegistry;
    private readonly AgentConfig _agentConfig;
    private readonly string _workspace;
    private readonly ILogger<AgentLoop> _logger;
    private readonly Kernel _kernel;
    private readonly IServiceProvider _serviceProvider;
    #endregion

    #region 运行状态与工具上下文
    private volatile bool _isRunning;
    private readonly object _runningLock = new();
    #endregion

    #region 构造函数（全依赖注入，无硬编码依赖）
    public AgentLoop(
        IMessageBus messageBus,
        ILlmProvider llmProvider,
        IContextBuilder contextBuilder,
        ISessionManager sessionManager,
        ISkPluginRegistry pluginRegistry,
        IOptions<OpenClawConfig> configOptions,
        Kernel kernel,
        IServiceProvider serviceProvider,
        ILogger<AgentLoop> logger)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _llmProvider = llmProvider ?? throw new ArgumentNullException(nameof(llmProvider));
        _contextBuilder = contextBuilder ?? throw new ArgumentNullException(nameof(contextBuilder));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _pluginRegistry = pluginRegistry ?? throw new ArgumentNullException(nameof(pluginRegistry));
        var config = configOptions.Value ?? throw new ArgumentNullException(nameof(configOptions), "OpenClaw配置未绑定");
        _agentConfig = config.Agent ?? throw new ArgumentNullException(nameof(config.Agent));
        _workspace = config.Workspace.ExpandUser() ?? throw new ArgumentNullException(nameof(config.Workspace));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _workspace.EnsureDirectoryExists();
        RegisterDefaultPlugins(); // 注册默认SK插件
        _logger.LogInformation("代理核心循环初始化完成 | 模型：{Model} | 最大迭代：{MaxIter} | 工作区：{Workspace}",
            _llmProvider.GetDefaultModel(), _agentConfig.MaxIterations, _workspace);
    }
    #endregion

    #region 公共状态属性
    public bool IsRunning => _isRunning;
    #endregion

    #region 核心插件注册（替换原ToolRegistry）
    /// <summary>
    /// 注册默认SK插件
    /// </summary>
    private void RegisterDefaultPlugins()
    {
        _logger.LogInformation("开始注册默认SK插件集");

        //// 从主DI容器获取插件实例（而非Kernel的独立容器）
        var readFilePlugin = _serviceProvider.GetRequiredService<ReadFilePlugin>();
        var writeFilePlugin = _serviceProvider.GetRequiredService<WriteFilePlugin>();
        var editFilePlugin = _serviceProvider.GetRequiredService<EditFilePlugin>();
        var listDirPlugin = _serviceProvider.GetRequiredService<ListDirPlugin>();
        var execPlugin = _serviceProvider.GetRequiredService<ExecPlugin>();
        var messagePlugin = _serviceProvider.GetRequiredService<MessagePlugin>();


        // 注册到SkPluginRegistry
        _pluginRegistry.RegisterPlugin("ReadFile", readFilePlugin);
        _pluginRegistry.RegisterPlugin("WriteFile", writeFilePlugin);
        _pluginRegistry.RegisterPlugin("EditFile", editFilePlugin);
        _pluginRegistry.RegisterPlugin("ListDir", listDirPlugin);
        _pluginRegistry.RegisterPlugin("Exec", execPlugin);
        _pluginRegistry.RegisterPlugin("Message", messagePlugin);


        _logger.LogInformation("默认SK插件集注册完成 | 已注册插件数：{Count}",
            _pluginRegistry.GetPluginFunctions("ReadFile").Count +
            _pluginRegistry.GetPluginFunctions("WriteFile").Count +
            _pluginRegistry.GetPluginFunctions("EditFile").Count +
            _pluginRegistry.GetPluginFunctions("ListDir").Count +
            _pluginRegistry.GetPluginFunctions("Exec").Count +
            _pluginRegistry.GetPluginFunctions("Message").Count);
    }
    #endregion

    #region 代理核心循环（启动/停止）
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        lock (_runningLock)
        {
            if (_isRunning)
            {
                _logger.LogWarning("代理核心循环已在运行，无需重复启动");
                return;
            }
            _isRunning = true;
        }

        _logger.LogInformation("✅ 代理核心循环已启动，开始监听消息总线入站队列");
        try
        {
            while (_isRunning && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // 直接等待，无超时
                    var inboundMessage = await _messageBus.ConsumeInboundAsync(cancellationToken).ConfigureAwait(false);

                    if (inboundMessage == null)
                    {
                        continue;
                    }

                    _logger.LogDebug("收到入站消息，开始处理 | 通道：{Channel}", inboundMessage.Channel);

                    // 异步处理消息（不阻塞主循环）
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ProcessInboundMessageWithErrorHandlingAsync(inboundMessage, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "处理入站消息时发生未捕获异常");
                        }
                    }, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // 主取消信号，正常退出
                    _logger.LogInformation("🛑 代理核心循环接收到取消信号，准备退出");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "代理循环发生异常，继续运行...");
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "代理核心循环发生未处理异常");
        }
        finally
        {
            lock (_runningLock)
            {
                _isRunning = false;
            }
            _logger.LogInformation("🛑 代理核心循环已停止");
        }
    }

    public void Stop()
    {
        lock (_runningLock)
        {
            if (!_isRunning)
            {
                _logger.LogWarning("代理核心循环未运行，无需停止");
                return;
            }
            _isRunning = false;
            _logger.LogInformation("🛑 已触发代理核心循环停止指令");
        }
    }
    #endregion

    #region 消息处理核心逻辑（适配SK类型）
    /// <summary>
    /// 入站消息处理（带全局异常处理）
    /// </summary>
    private async Task ProcessInboundMessageWithErrorHandlingAsync(InboundMessage inboundMessage, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("开始处理入站消息 | 通道：{Channel} | 发送者：{Sender} | 会话：{Session}",
                inboundMessage.Channel, inboundMessage.SenderId, inboundMessage.SessionKey);

            // 处理消息并生成出站消息
            var outboundMessage = await ProcessInboundMessageAsync(inboundMessage, cancellationToken).ConfigureAwait(false);
            if (outboundMessage == null)
            {
                _logger.LogWarning("消息处理无结果，未生成出站消息 | 通道：{Channel}", inboundMessage.Channel);
                return;
            }

            // 发布出站消息到总线
            await _messageBus.PublishOutboundAsync(outboundMessage, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("出站消息已发布到总线 | 通道：{Channel} | 会话：{Session}",
                outboundMessage.Channel, inboundMessage.SessionKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理入站消息失败 | 通道：{Channel} | 发送者：{Sender} | 异常详情：{Message}",
           inboundMessage.Channel, inboundMessage.SenderId, ex.ToString());

            // 构建错误响应并发布
            var errorMessage = new OutboundMessage(
                inboundMessage.Channel,
                inboundMessage.ChatId,
                $"处理消息时发生错误：{ex.Message}");
            await _messageBus.PublishOutboundAsync(errorMessage, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 处理单条入站消息核心逻辑
    /// </summary>
    private async Task<OutboundMessage?> ProcessInboundMessageAsync(InboundMessage inboundMessage, CancellationToken cancellationToken)
    {
        _logger.LogInformation("🔧 开始处理入站消息 | 通道：{Channel} | 发送者：{Sender}",
        inboundMessage.Channel, inboundMessage.SenderId);

        // 处理系统消息（子代理结果通知）
        if (inboundMessage.Channel.Equals(OpenClawConstants.SystemChannelName, StringComparison.OrdinalIgnoreCase))
        {
            return await ProcessSystemMessageAsync(inboundMessage, cancellationToken).ConfigureAwait(false);
        }

        // 处理普通用户消息
        var session = await _sessionManager.GetOrCreateAsync(inboundMessage.SessionKey, cancellationToken).ConfigureAwait(false);
        var sessionHistory = session.GetHistory();

        // 构建SK ChatHistory
        var chatHistory = await _contextBuilder.BuildChatHistoryAsync(
            sessionHistory,
            inboundMessage.Content,
            media: inboundMessage.Media,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("📝 SK ChatHistory构建完成 | 消息数：{Count}", chatHistory.Count);
        var allPluginFunctions = _kernel.Plugins
        .SelectMany(p => p) // 遍历所有插件的所有函数
        .ToList();
        

        // LLM核心循环调用
        var llmFinalResponse = await RunLlmLoopAsync(chatHistory, allPluginFunctions, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("🤖 LLM处理完成 | 响应长度：{Length}", llmFinalResponse?.Length ?? 0);

        // 保存会话消息
        session.AddMessage(new ChatMessageContent(AuthorRole.User, inboundMessage.Content));
        session.AddMessage(new ChatMessageContent(AuthorRole.Assistant, llmFinalResponse));
        await _sessionManager.SaveAsync(session, cancellationToken).ConfigureAwait(false);

        // 构建出站消息
        var outboundMessage = new OutboundMessage(
            inboundMessage.Channel,
            inboundMessage.ChatId,
            llmFinalResponse ?? "处理完成，无有效响应");

        _logger.LogInformation("📦 出站消息构建完成 | 通道：{Channel} | 聊天ID：{ChatId}",
            outboundMessage.Channel, outboundMessage.ChatId);

        return outboundMessage;
    }

    /// <summary>
    /// 处理系统消息（子代理执行结果通知）
    /// </summary>
    private async Task<OutboundMessage?> ProcessSystemMessageAsync(InboundMessage systemMessage, CancellationToken cancellationToken)
    {
        _logger.LogInformation("开始处理系统消息 | 发送者：{Sender} | ChatId：{ChatId}",
            systemMessage.SenderId, systemMessage.ChatId);

        // 解析原通道和聊天ID（格式：channel:chat_id）
        var (originChannel, originChatId) = ParseOriginFromChatId(systemMessage.ChatId);
        var sessionKey = $"{originChannel}:{originChatId}";
        var session = await _sessionManager.GetOrCreateAsync(sessionKey, cancellationToken).ConfigureAwait(false);
        var sessionHistory = session.GetHistory();

        // 构建SK ChatHistory
        var chatHistory = await _contextBuilder.BuildChatHistoryAsync(
            sessionHistory,
            systemMessage.Content,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // 获取所有SK插件函数
        var allPluginFunctions = _kernel.Plugins
        .SelectMany(p => p)
        .ToList();


        // LLM核心循环调用
        var llmFinalResponse = await RunLlmLoopAsync(chatHistory, allPluginFunctions, cancellationToken).ConfigureAwait(false);

        // 保存系统消息到会话
        session.AddMessage(new ChatMessageContent(AuthorRole.User, $"[System: {systemMessage.SenderId}] {systemMessage.Content}"));
        session.AddMessage(new ChatMessageContent(AuthorRole.Assistant, llmFinalResponse));
        await _sessionManager.SaveAsync(session, cancellationToken).ConfigureAwait(false);

        // 构建出站消息，路由到原通道
        return new OutboundMessage(originChannel, originChatId, llmFinalResponse ?? "处理完成，无有效响应");
    }

    /// <summary>
    /// LLM核心循环调用（适配SK类型）
    /// </summary>
    private async Task<string> RunLlmLoopAsync(ChatHistory chatHistory, List<KernelFunction> tools, CancellationToken cancellationToken)
    {
        var currentChatHistory = chatHistory;
        var iteration = 0;
        string? finalResponse = null;

        _logger.LogInformation("开始LLM核心循环调用 | 最大迭代：{MaxIter}", _agentConfig.MaxIterations);
        while (iteration < _agentConfig.MaxIterations && _isRunning && !cancellationToken.IsCancellationRequested)
        {
            iteration++;
            _logger.LogDebug("LLM调用迭代 {Iter}/{Max}", iteration, _agentConfig.MaxIterations);

            // 调用LLM获取响应（SK原生类型）
            var llmResponse = await _llmProvider.ChatAsync(
                currentChatHistory,
                tools,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // 提取工具调用
            var toolCalls = FunctionCallContent.GetFunctionCalls(llmResponse).ToArray();

            // 无工具调用，结束循环
            if (!toolCalls.Any())
            {
                finalResponse = llmResponse.Content?.Trim() ?? string.Empty;
                _logger.LogInformation("LLM循环结束（无工具调用） | 迭代：{Iter}", iteration);
                break;
            }

            // 有工具调用，添加助手消息到上下文
            currentChatHistory = _contextBuilder.AddAssistantMessage(currentChatHistory, llmResponse);
            _logger.LogDebug("LLM请求工具调用 | 迭代：{Iter} | 工具数：{ToolCount}", iteration, toolCalls.Length);

            // 执行所有工具调用
            foreach (var toolCall in toolCalls)
            {
                _logger.LogInformation("开始执行SK插件调用 | 插件：{PluginName} | 函数：{FunctionName} | 参数：{Params}",
                    toolCall.PluginName, toolCall.FunctionName, string.Join(", ", toolCall.Arguments.Select(kv => $"{kv.Key}={kv.Value}")));

                try
                {
                    //// 执行SK插件函数
                    var retlist = toolCall.FunctionName.Split("_");
                    string pluginName = retlist[0];
                    string funName = retlist[1];
                    var toolResult = await _pluginRegistry.ExecuteFunctionAsync(
                        pluginName,
                        funName,
                        toolCall.Arguments.ToDictionary(kv => kv.Key, kv => kv.Value),
                        cancellationToken).ConfigureAwait(false);

                    // 添加工具执行结果到上下文
                    currentChatHistory = _contextBuilder.AddToolResult(currentChatHistory, toolCall, toolResult);
                    _logger.LogDebug("插件执行成功 | 插件：{PluginName} | 函数：{FunctionName} | 结果长度：{Length}",
                        toolCall.PluginName, toolCall.FunctionName, toolResult.Length);
                }
                catch (ToolExecutionException ex)
                {
                    _logger.LogError(ex, "插件执行失败 | 插件：{PluginName} | 函数：{FunctionName}",
                        toolCall.PluginName, toolCall.FunctionName);

                    // 工具执行失败也添加到上下文
                    currentChatHistory = _contextBuilder.AddToolResult(currentChatHistory, toolCall, $"插件执行失败：{ex.Message}");
                }
            }
        }

        // 处理达到最大迭代仍无结果的情况
        if (finalResponse == null || string.IsNullOrWhiteSpace(finalResponse))
        {
            finalResponse = iteration >= _agentConfig.MaxIterations
                ? $"已达到最大迭代次数（{_agentConfig.MaxIterations}次），处理终止"
                : "处理完成，无有效响应";
            _logger.LogWarning("LLM循环无有效响应 | 迭代：{Iter} | 原因：{Reason}", iteration, finalResponse);
        }

        return finalResponse;
    }
    #endregion

    #region CLI直接消息处理（无通道依赖）
    public async Task<string> ProcessDirectMessageAsync(string content, string sessionKey = OpenClawConstants.DefaultSessionKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("消息内容不能为空", nameof(content));
        if (!_isRunning)
            throw new OpenClawException("代理核心循环未运行，无法处理直接消息");

        _logger.LogInformation("开始处理CLI直接消息 | 会话：{Session} | 内容：{Content}",
            sessionKey, content[..Math.Min(content.Length, 50)]);

        // 构造虚拟入站消息
        var inboundMessage = new InboundMessage
        {
            Channel = OpenClawConstants.ConsoleChannelName,
            SenderId = "console_user",
            ChatId = "console_direct",
            Content = content
        };

        // 处理消息并返回结果
        var outboundMessage = await ProcessInboundMessageAsync(inboundMessage, cancellationToken).ConfigureAwait(false);
        var result = outboundMessage?.Content ?? "处理失败，无响应";
        _logger.LogInformation("CLI直接消息处理完成 | 会话：{Session} | 结果：{Result}",
            sessionKey, result[..Math.Min(result.Length, 50)]);

        return result;
    }
    #endregion

    #region 辅助方法
    /// <summary>
    /// 从ChatId解析原通道和聊天ID（格式：channel:chat_id）
    /// </summary>
    private (string originChannel, string originChatId) ParseOriginFromChatId(string chatId)
    {
        if (string.IsNullOrWhiteSpace(chatId) || !chatId.Contains(":"))
        {
            _logger.LogWarning("无效的系统消息ChatId，使用默认值 | ChatId：{ChatId}", chatId);
            return (OpenClawConstants.ConsoleChannelName, chatId);
        }

        var parts = chatId.Split(":", 2);
        return (parts[0].Trim(), parts[1].Trim());
    }
    #endregion
}