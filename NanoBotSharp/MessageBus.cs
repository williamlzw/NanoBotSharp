#nullable enable
using OpenClawSharp.Bus;
using OpenClawSharp.Core;
using OpenClawSharp.Queue;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawSharp.Bus;

/// <summary>
/// 出站消息回调委托（定义订阅者处理逻辑）
/// </summary>
/// <param name="message">出站消息</param>
/// <returns>异步任务</returns>
public delegate Task OutboundMessageCallback(OutboundMessage message);

/// <summary>
/// 消息总线核心接口（DI友好，解耦通道/代理/子代理的消息通信）
/// 核心能力：入站/出站消息的发布/消费、出站消息订阅/取消订阅、分发器启停
/// </summary>
public interface IMessageBus
{
    /// <summary>
    /// 入站队列待处理消息数（近似值，基于Channel的估计值）
    /// </summary>
    int InboundQueueCount { get; }

    /// <summary>
    /// 出站队列待处理消息数（近似值，基于Channel的估计值）
    /// </summary>
    int OutboundQueueCount { get; }

    #region 消息总线启动/停止
    /// <summary>
    /// 启动消息总线（初始化分发器）
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止消息总线（停止分发器）
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);
    #endregion

    #region 入站消息（通道→代理）
    /// <summary>
    /// 发布入站消息到总线
    /// </summary>
    Task PublishInboundAsync(InboundMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// 消费入站消息（阻塞直到有消息/取消）
    /// </summary>
    Task<InboundMessage> ConsumeInboundAsync(CancellationToken cancellationToken = default);
    #endregion

    #region 出站消息（代理→通道）
    /// <summary>
    /// 发布出站消息到总线
    /// </summary>
    Task PublishOutboundAsync(OutboundMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// 消费出站消息（阻塞直到有消息/取消，供分发器使用）
    /// </summary>
    Task<OutboundMessage> ConsumeOutboundAsync(CancellationToken cancellationToken = default);
    #endregion

    #region 出站消息订阅
    /// <summary>
    /// 订阅指定通道的出站消息
    /// </summary>
    void SubscribeOutbound(string channel, OutboundMessageCallback callback);

    /// <summary>
    /// 取消指定通道的出站消息订阅
    /// </summary>
    void UnsubscribeOutbound(string channel, OutboundMessageCallback callback);
    #endregion

    #region 分发器启停
    /// <summary>
    /// 启动出站消息分发器（后台长期任务，按通道路由消息到订阅者）
    /// </summary>
    void StartOutboundDispatcher();

    /// <summary>
    /// 停止出站消息分发器（优雅取消，清理资源）
    /// </summary>
    Task StopOutboundDispatcherAsync();
    #endregion
}

/// <summary>
/// 消息总线实现（适配ChannelBasedAsyncQueue，线程安全，复刻Python原逻辑）
/// </summary>
public class MessageBus : IMessageBus
{
    #region 核心依赖与字段
    private readonly IAsyncConcurrentQueue<InboundMessage> _inboundQueue;
    private readonly IAsyncConcurrentQueue<OutboundMessage> _outboundQueue;
    private readonly ConcurrentDictionary<string, List<OutboundMessageCallback>> _outboundSubscribers = new();
    private readonly ILogger<MessageBus> _logger;
    private CancellationTokenSource? _dispatcherCts;
    private Task? _dispatcherTask;
    private readonly object _dispatcherLock = new();

    // 定义消费超时常量（替代OpenClawConstants，避免未定义错误）
    private const int ConsumeTimeoutSeconds = 1;
    #endregion

    #region 构造函数（依赖注入，适配新队列）
    public MessageBus(
        IAsyncConcurrentQueue<InboundMessage> inboundQueue,
        IAsyncConcurrentQueue<OutboundMessage> outboundQueue,
        ILogger<MessageBus> logger)
    {
        _inboundQueue = inboundQueue ?? throw new ArgumentNullException(nameof(inboundQueue));
        _outboundQueue = outboundQueue ?? throw new ArgumentNullException(nameof(outboundQueue));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.LogInformation("消息总线初始化完成 | 入站队列：{InboundType} | 出站队列：{OutboundType}",
            inboundQueue.GetType().Name, outboundQueue.GetType().Name);
        _logger.LogDebug("队列计数说明：入站/出站队列Count为近似值（Channel特性）");
    }
    #endregion

    #region 队列状态属性（适配Channel近似计数）
    public int InboundQueueCount => _inboundQueue.Count;
    public int OutboundQueueCount => _outboundQueue.Count;
    #endregion

    #region 消息总线启动/停止
    /// <summary>
    /// 启动消息总线（初始化分发器）
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        StartOutboundDispatcher();
        _logger.LogInformation("✅ 消息总线已启动，出站消息分发器正在运行");
        await Task.CompletedTask;
    }

    /// <summary>
    /// 停止消息总线（停止分发器）
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await StopOutboundDispatcherAsync().ConfigureAwait(false);
        _logger.LogInformation("✅ 消息总线已停止");
    }
    #endregion

    #region 入站消息实现（适配新队列取消逻辑）
    public async Task PublishInboundAsync(InboundMessage message, CancellationToken cancellationToken = default)
    {
        if (message == null) throw new ArgumentNullException(nameof(message));
        cancellationToken.ThrowIfCancellationRequested();

        _inboundQueue.Enqueue(message);
        // 日志中注明Count是近似值
        _logger.LogDebug("入站消息发布成功 | 通道：{Channel} | 入站队列待处理（近似）：{Count}",
            message.Channel, _inboundQueue.Count);
        await Task.CompletedTask;
    }

    public async Task<InboundMessage> ConsumeInboundAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var message = await _inboundQueue.DequeueAsync(cancellationToken).ConfigureAwait(false);
            if(message == null )
            {
                return null;
            }
            _logger.LogDebug("成功消费入站消息 | 通道：{Channel} | 剩余队列数（近似）：{Count}",
                message.Channel, _inboundQueue.Count);
            return message;
        }
        catch (OperationCanceledException)
        {
            //_logger.LogDebug("消费入站消息被取消（正常取消逻辑）");
            // throw; // 重新抛出，让上层处理
            return null;
        }
    }
    #endregion

    #region 出站消息实现（适配新队列+空值安全）
    public async Task PublishOutboundAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        if (message == null) throw new ArgumentNullException(nameof(message));
        cancellationToken.ThrowIfCancellationRequested();

        _outboundQueue.Enqueue(message);

        // 空值安全处理：避免message.Content为null时的索引错误
        var contentPreview = message.Content == null
            ? "[空内容]"
            : message.Content.Length > 50
                ? message.Content[..50] + "..."
                : message.Content;

        _logger.LogInformation("📤 出站消息发布到队列 | 通道：{Channel} | 聊天ID：{ChatId} | 内容片段：{ContentPreview}",
            message.Channel, message.ChatId, contentPreview);
        _logger.LogDebug("出站队列待处理（近似）：{Count}", _outboundQueue.Count);
        await Task.CompletedTask;
    }

    public async Task<OutboundMessage> ConsumeOutboundAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var message = await _outboundQueue.DequeueAsync(cancellationToken).ConfigureAwait(false);
            if(message == null)
            {
                return null;
            }
            _logger.LogInformation("分发器消费出站消息成功 | 通道：{Channel} | 出站队列待处理（近似）：{Count}",
                message.Channel, _outboundQueue.Count);
            return message;
        }
        catch (OperationCanceledException)
        {
            // _logger.LogDebug("消费出站消息被取消（正常取消逻辑）");
            //throw;
            return null;
        }
    }
    #endregion

    #region 出站消息订阅实现（线程安全）
    public void SubscribeOutbound(string channel, OutboundMessageCallback callback)
    {
        if (string.IsNullOrWhiteSpace(channel)) throw new ArgumentException("通道名不能为空", nameof(channel));
        if (callback == null) throw new ArgumentNullException(nameof(callback));

        var subscriberCount = 0;

        _outboundSubscribers.AddOrUpdate(
            key: channel,
            addValueFactory: _ =>
            {
                var list = new List<OutboundMessageCallback> { callback };
                subscriberCount = 1;
                return list;
            },
            updateValueFactory: (_, list) =>
            {
                lock (list)
                {
                    if (!list.Contains(callback))
                    {
                        list.Add(callback);
                    }
                    subscriberCount = list.Count;
                }
                return list;
            });

        _logger.LogInformation("📡 出站消息订阅成功 | 通道：{Channel} | 订阅者数：{Count} | 回调：{Callback}",
            channel, subscriberCount, callback.Method.Name);
    }

    public void UnsubscribeOutbound(string channel, OutboundMessageCallback callback)
    {
        if (string.IsNullOrWhiteSpace(channel)) throw new ArgumentException("通道名不能为空", nameof(channel));
        if (callback == null) throw new ArgumentNullException(nameof(callback));

        if (_outboundSubscribers.TryGetValue(channel, out var list))
        {
            lock (list)
            {
                list.Remove(callback);
            }
            _logger.LogInformation("出站消息取消订阅成功 | 通道：{Channel} | 剩余订阅者数：{Count}",
                channel, list.Count);

            // 无订阅者时移除通道，释放内存
            if (list.Count == 0)
            {
                _outboundSubscribers.TryRemove(channel, out _);
                _logger.LogDebug("通道无订阅者，移除订阅记录 | 通道：{Channel}", channel);
            }
        }
        else
        {
            _logger.LogWarning("取消订阅失败：通道无订阅记录 | 通道：{Channel}", channel);
        }
    }
    #endregion

    #region 分发器核心逻辑（适配新队列取消逻辑+空值安全）
    public void StartOutboundDispatcher()
    {
        lock (_dispatcherLock)
        {
            if (_dispatcherTask != null && !_dispatcherTask.IsCompleted)
            {
                _logger.LogWarning("出站消息分发器已在运行，无需重复启动");
                return;
            }

            _dispatcherCts = new CancellationTokenSource();
            _dispatcherTask = DispatchOutboundMessagesAsync(_dispatcherCts.Token);
            _logger.LogInformation("✅ 出站消息分发器已启动，Task ID: {TaskId}", _dispatcherTask.Id);
        }
    }

    public async Task StopOutboundDispatcherAsync()
    {
        lock (_dispatcherLock)
        {
            if (_dispatcherCts == null || _dispatcherTask == null || _dispatcherTask.IsCompleted)
            {
                _logger.LogWarning("出站消息分发器未运行，无需停止");
                return;
            }

            if (!_dispatcherCts.IsCancellationRequested)
            {
                _dispatcherCts.Cancel();
                _logger.LogInformation("📤 已发送分发器停止信号，等待优雅退出");
            }
        }

        try
        {
            await _dispatcherTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("📤 出站消息分发器已被取消（正常退出）");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "📤 停止出站消息分发器时发生异常");
        }
        finally
        {
            _dispatcherCts?.Dispose();
            _dispatcherTask = null;
            _dispatcherCts = null;
            _logger.LogInformation("📤 出站消息分发器已完全停止，资源已清理");
        }
    }

    /// <summary>
    /// 出站消息分发核心循环（适配新队列，长期运行，按通道路由到订阅者）
    /// </summary>
    private async Task DispatchOutboundMessagesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // 直接等待消费消息（无超时，直到有消息或主取消）
                var message = await _outboundQueue.DequeueAsync(cancellationToken).ConfigureAwait(false);

                if (message == null)
                {
                    // 这通常意味着队列被关闭或发生其他问题
                    _logger.LogWarning("出站队列返回空消息，继续等待...");
                    continue;
                }

                _logger.LogDebug("📨 分发器收到出站消息 | 通道：{Channel} | 聊天ID：{ChatId}",
                    message.Channel, message.ChatId);

                // 按通道获取订阅者
                if (!_outboundSubscribers.TryGetValue(message.Channel, out var subscribers) || !subscribers.Any())
                {
                    // 空值安全处理：避免Content为null时的Substring错误
                    var contentPreview = message.Content == null
                        ? "[空内容]"
                        : message.Content[..Math.Min(message.Content.Length, 50)];

                    _logger.LogWarning("⚠️ 出站消息无订阅者，已丢弃 | 通道：{Channel} | 内容：{Content}",
                        message.Channel, contentPreview);
                    continue;
                }

                _logger.LogDebug("👥 找到 {Count} 个订阅者 | 通道：{Channel}", subscribers.Count, message.Channel);

                // 并行执行所有订阅者回调（线程安全）
                var callbackTasks = new List<Task>();
                lock (subscribers) // 锁定订阅者列表，避免遍历中修改
                {
                    callbackTasks = subscribers.Select(async (callback, index) =>
                    {
                        _logger.LogTrace("调用订阅者 #{Index} | 通道：{Channel}", index + 1, message.Channel);
                        try
                        {
                            await callback(message).ConfigureAwait(false);
                            _logger.LogTrace("订阅者 #{Index} 回调成功 | 通道：{Channel}", index + 1, message.Channel);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "❌ 订阅者 #{Index} 回调执行失败 | 通道：{Channel}", index + 1, message.Channel);
                        }
                    }).ToList();
                }

                await Task.WhenAll(callbackTasks).ConfigureAwait(false);

            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 主取消令牌触发，正常退出
                _logger.LogInformation("🛑 出站消息分发器接收到取消信号，准备退出");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "出站消息分发过程中发生异常，等待后继续...");
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false); // 错误后短暂等待
            }
        }
    }
    #endregion
}