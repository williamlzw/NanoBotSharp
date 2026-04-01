#nullable enable
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace OpenClawSharp.Queue;

/// <summary>
/// 异步并发队列接口（解耦队列实现，方便测试替换）
/// </summary>
/// <typeparam name="T">队列元素类型</typeparam>
public interface IAsyncConcurrentQueue<T>
{
    /// <summary>
    /// 队列元素数量（近似值，Channel不保证精确计数）
    /// </summary>
    int Count { get; }

    /// <summary>
    /// 入队元素
    /// </summary>
    void Enqueue(T item);

    /// <summary>
    /// 异步出队（阻塞直到有元素/取消）
    /// </summary>
    Task<T> DequeueAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 基于.NET官方Channel实现的异步并发队列（修复ChannelOptions抽象类实例化问题）
/// </summary>
/// <typeparam name="T">队列元素类型</typeparam>
public class ChannelBasedAsyncQueue<T> : IAsyncConcurrentQueue<T>
{
    private readonly Channel<T> _channel;
    private readonly ILogger<ChannelBasedAsyncQueue<T>> _logger;

    /// <summary>
    /// 队列元素数量（近似值，Channel的Count是估计值）
    /// </summary>
    public int Count => (int)_channel.Reader.Count;

    /// <summary>
    /// 构造函数（注入日志器）
    /// </summary>
    /// <param name="logger">日志器</param>
    /// <param name="boundedCapacity">队列容量（-1表示无界，默认无界）</param>
    public ChannelBasedAsyncQueue(ILogger<ChannelBasedAsyncQueue<T>> logger, int boundedCapacity = -1)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 修复核心：根据队列类型使用具体的ChannelOptions实现类
        if (boundedCapacity > 0)
        {
            // 有界队列：使用BoundedChannelOptions（ChannelOptions的具体实现）
            var boundedOptions = new BoundedChannelOptions(boundedCapacity)
            {
                SingleReader = false, // 允许多个消费者
                SingleWriter = false, // 允许多个生产者
                AllowSynchronousContinuations = false, // 避免同步延续导致的死锁
                FullMode = BoundedChannelFullMode.Wait // 队列满时等待（而非丢弃/失败）
            };
            _channel = Channel.CreateBounded<T>(boundedOptions);
            _logger.LogDebug("有界Channel异步队列初始化完成 | 容量：{Capacity}", boundedCapacity);
        }
        else
        {
            // 无界队列：使用UnboundedChannelOptions（ChannelOptions的具体实现）
            var unboundedOptions = new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            };
            _channel = Channel.CreateUnbounded<T>(unboundedOptions);
            _logger.LogDebug("无界Channel异步队列初始化完成");
        }

        _logger.LogInformation("Channel异步队列初始化完成 | 类型：{Type} | 近似容量：{Capacity}",
            boundedCapacity > 0 ? "有界" : "无界",
            boundedCapacity > 0 ? boundedCapacity.ToString() : "无限制");
    }

    /// <summary>
    /// 入队元素（非阻塞）
    /// </summary>
    /// <param name="item">要入队的元素</param>
    /// <exception cref="InvalidOperationException">有界队列满时触发</exception>
    public void Enqueue(T item)
    {
        // TryWrite是非阻塞的，Channel内置线程安全，无需额外锁
        if (_channel.Writer.TryWrite(item))
        {
            _logger.LogDebug("元素入队成功，队列近似数量：{QueueCount}", _channel.Reader.Count);
        }
        else
        {
            throw new InvalidOperationException($"有界队列已达最大容量（{(_channel.Reader.Count)}），无法入队元素");
        }
    }

    /// <summary>
    /// 异步出队（阻塞直到有元素/取消）
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>队列中的元素</returns>
    /// <exception cref="OperationCanceledException">取消时触发</exception>
    public async Task<T> DequeueAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Channel的ReadAsync内置了完善的取消逻辑，无需手动管理TCS
            var item = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("出队成功，剩余近似数量：{QueueCount}", _channel.Reader.Count);
            return item;
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("出队操作被取消：{Message}", ex.Message);
            return default;
        }
    }
}