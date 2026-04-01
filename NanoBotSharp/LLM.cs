#nullable enable
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenClawSharp.Core;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawSharp.Providers;

/// <summary>
/// LLM提供者接口（完全适配SK原生类型）
/// </summary>
public interface ILlmProvider
{
    /// <summary>
    /// 获取默认模型名
    /// </summary>
    string GetDefaultModel();

    /// <summary>
    /// 调用LLM获取聊天响应（完全使用SK原生类型）
    /// </summary>
    /// <param name="chatHistory">SK聊天历史（替代自定义ChatMessage列表）</param>
    /// <param name="tools">SK内核函数列表（替代自定义FunctionSchema）</param>
    /// <param name="model">模型名（可选，覆盖默认）</param>
    /// <param name="maxTokens">最大令牌数</param>
    /// <param name="temperature">采样温度</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task<ChatMessageContent> ChatAsync(
        ChatHistory chatHistory,
        IEnumerable<KernelFunction> tools,
        string? model = null,
        int maxTokens = OpenClawConstants.DefaultLlmMaxTokens,
        float temperature = OpenClawConstants.DefaultLlmTemperature,
        CancellationToken cancellationToken = default);
}