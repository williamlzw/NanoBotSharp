#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace OpenClawSharp.Bus;

/// <summary>
/// 入站消息（通道→代理）
/// </summary>
public class InboundMessage
{
    /// <summary>
    /// 所属通道名
    /// </summary>
    public string Channel { get; set; }

    /// <summary>
    /// 发送者唯一标识
    /// </summary>
    public string SenderId { get; set; }

    /// <summary>
    /// 聊天/频道唯一标识
    /// </summary>
    public string ChatId { get; set; }

    /// <summary>
    /// 消息文本内容
    /// </summary>
    public string Content { get; set; }

    /// <summary>
    /// 消息时间戳（默认当前时间）
    /// </summary>
    public DateTime TimeStamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 媒体资源URL列表
    /// </summary>
    public List<string> Media { get; set; } = new();

    /// <summary>
    /// 通道特定元数据
    /// </summary>
    public ConcurrentDictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// 会话唯一标识（计算属性，替代原方法）
    /// </summary>
    public string SessionKey => $"{Channel}:{ChatId}";
}

/// <summary>
/// 出站消息（代理→通道）
/// </summary>
public class OutboundMessage
{
    /// <summary>
    /// 目标通道名
    /// </summary>
    public string Channel { get; set; }

    /// <summary>
    /// 目标聊天/频道唯一标识
    /// </summary>
    public string ChatId { get; set; }

    /// <summary>
    /// 消息文本内容
    /// </summary>
    public string Content { get; set; }

    /// <summary>
    /// 回复的原消息ID
    /// </summary>
    public string ReplyTo { get; set; } = string.Empty;

    /// <summary>
    /// 媒体资源URL列表
    /// </summary>
    public List<string> Media { get; set; } = new();

    /// <summary>
    /// 通道特定元数据
    /// </summary>
    public ConcurrentDictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// 构造函数（强类型验证）
    /// </summary>
    public OutboundMessage(string channel, string chatId, string content)
    {
        Channel = string.IsNullOrWhiteSpace(channel) ? throw new ArgumentNullException(nameof(channel)) : channel;
        ChatId = string.IsNullOrWhiteSpace(chatId) ? throw new ArgumentNullException(nameof(chatId)) : chatId;
        Content = string.IsNullOrWhiteSpace(content) ? throw new ArgumentNullException(nameof(content)) : content;
    }
}
