#nullable enable
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using OllamaSharp;
using OpenClawSharp.Config;
using OpenClawSharp.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawSharp.Providers.Ollama;

/// <summary>
/// Ollama LLM提供者实现（完全适配SK原生类型，无自定义消息/工具类型）
/// </summary>
public class OllamaLlmProvider : ILlmProvider
{
    #region 核心依赖与配置
    private readonly IChatCompletionService _chatCompletionService;
    public readonly Kernel _kernel;
    private readonly LlmConfig _llmConfig;
    private readonly ILogger<OllamaLlmProvider> _logger;
    #endregion

    #region 构造函数（依赖注入ILlmConfig，解耦配置）
    public OllamaLlmProvider(LlmConfig llmConfig, Kernel kernel, ILogger<OllamaLlmProvider> logger)
    {
        _llmConfig = llmConfig ?? throw new ArgumentNullException(nameof(llmConfig));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 验证配置
        if (string.IsNullOrWhiteSpace(_llmConfig.ApiBase))
            throw new ArgumentException("Ollama ApiBase不能为空", nameof(llmConfig));
        if (string.IsNullOrWhiteSpace(_llmConfig.DefaultModel))
            throw new ArgumentException("Ollama 默认模型不能为空", nameof(llmConfig));


        // 手动创建OllamaApiClient并配置AOT兼容的序列化选项
   
        _kernel = kernel;
        _chatCompletionService = _kernel.GetRequiredService<IChatCompletionService>();

        _logger.LogInformation("Ollama LLM提供者初始化完成 | 地址：{ApiBase} | 默认模型：{Model} | 最大令牌：{MaxTokens} | 温度：{Temperature}",
            _llmConfig.ApiBase, _llmConfig.DefaultModel, _llmConfig.MaxTokens, _llmConfig.Temperature);
    }
    #endregion

    #region ILlmProvider接口实现（完全适配SK类型）
    public string GetDefaultModel() => _llmConfig.DefaultModel;

    public async Task<ChatMessageContent> ChatAsync(
        ChatHistory chatHistory,
        IEnumerable<KernelFunction> tools,
        string? model = null,
        int maxTokens = OpenClawConstants.DefaultLlmMaxTokens,
        float temperature = OpenClawConstants.DefaultLlmTemperature,
        CancellationToken cancellationToken = default)
    {
        // 入参校验
        if (chatHistory == null) throw new ArgumentNullException(nameof(chatHistory));
        if (!chatHistory.Any()) throw new LlmCallException(model ?? _llmConfig.DefaultModel, "LLM聊天消息不能为空");
        tools ??= Enumerable.Empty<KernelFunction>();

        try
        {
            var targetModel = model ?? _llmConfig.DefaultModel;
            maxTokens = maxTokens <= 0 ? _llmConfig.MaxTokens : maxTokens;
            temperature = temperature is < 0 or > 1 ? _llmConfig.Temperature : temperature;

            _logger.LogInformation("开始调用Ollama LLM | 模型：{Model} | 消息数：{MsgCount} | 工具数：{ToolCount} | 最大令牌：{MaxTokens} | 温度：{Temperature}",
                targetModel, chatHistory.Count, tools.Count(), maxTokens, temperature);

            // 初始化Ollama执行配置
            var executionSettings = new OllamaPromptExecutionSettings
            {
                ModelId = targetModel,
                NumPredict = maxTokens,
                Temperature = temperature,
                TopP = 0.95f,
                // 自动选择工具（有工具则启用，无则禁用）
                FunctionChoiceBehavior =  FunctionChoiceBehavior.Auto(autoInvoke: false)
                    
            };

            // 核心调用：SK原生ChatCompletion
            var skResponse = await _chatCompletionService.GetChatMessageContentAsync(
                chatHistory: chatHistory,
                executionSettings: executionSettings,
                kernel: _kernel,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            
            _logger.LogInformation("Ollama LLM调用成功 | 模型：{Model} | 有工具调用：{HasToolCalls} | 响应内容长度：{ContentLength}",
                targetModel, Microsoft.SemanticKernel.FunctionCallContent.GetFunctionCalls(skResponse).Any(), skResponse.Content?.Length ?? 0);

            return skResponse;
        }
        catch (OperationCanceledException ex)
        {
            var targetModel = model ?? _llmConfig.DefaultModel;
            _logger.LogWarning(ex, "Ollama LLM调用被取消 | 模型：{Model}", targetModel);
            throw new LlmCallException(targetModel, "LLM调用被用户/系统取消", ex);
        }
        catch (Exception ex)
        {
            var targetModel = model ?? _llmConfig.DefaultModel;
            _logger.LogError(ex, "Ollama LLM调用发生异常 | 模型：{Model}", targetModel);
            throw new LlmCallException(targetModel, "LLM调用失败：" + ex.Message, ex);
        }
    }
    #endregion
}

/// <summary>
/// LLM调用异常（保留，适配业务异常体系）
/// </summary>
public class LlmCallException : OpenClawException
{
    public string Model { get; }
    public LlmCallException(string model, string message) : base(message) => Model = model;
    public LlmCallException(string model, string message, Exception innerException) : base(message, innerException) => Model = model;
}