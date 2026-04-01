#nullable enable
using Microsoft.Extensions.Logging;
using OpenClawSharp.Bus;
using OpenClawSharp.Config;
using OpenClawSharp.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawSharp.Channels;

/// <summary>
/// 通道核心接口（DI友好，所有通道的统一契约）
/// </summary>
public interface IChannel
{
    /// <summary>
    /// 通道唯一名称（如console/telegram）
    /// </summary>
    string Name { get;  }

    /// <summary>
    /// 通道是否正在运行
    /// </summary>
    bool IsRunning { get;  }

    /// <summary>
    /// 启动通道（连接平台、监听消息、转发到总线）
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止通道（优雅断开、清理资源）
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 发送出站消息到通道（由消息总线分发器调用）
    /// </summary>
    Task SendOutboundMessageAsync(OutboundMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// 通道管理器接口（DI友好，统一管理所有通道的启停、消息路由）
/// </summary>
public interface IChannelManager
{
    /// <summary>
    /// 已注册的通道名称列表
    /// </summary>
    IReadOnlyList<string> RegisteredChannels { get; }

    /// <summary>
    /// 注册通道到管理器
    /// </summary>
    void RegisterChannel(IChannel channel);

    /// <summary>
    /// 根据名称获取通道实例
    /// </summary>
    IChannel? GetChannel(string channelName);

    /// <summary>
    /// 启动所有已注册且启用的通道
    /// </summary>
    Task StartAllChannelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止所有正在运行的通道
    /// </summary>
    Task StopAllChannelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取所有通道的运行状态
    /// </summary>
    Dictionary<string, ChannelStatus> GetAllChannelStatus();
}

/// <summary>
/// 通道运行状态（轻量模型，供状态查询）
/// </summary>
public class ChannelStatus
{
    /// <summary>
    /// 是否启用
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 是否正在运行
    /// </summary>
    public bool IsRunning { get; set; }
}

/// <summary>
/// 通道管理器实现（线程安全，统一管理通道的注册、启停、消息路由）
/// </summary>
public class ChannelManager : IChannelManager
{
    #region 核心字段
    private readonly ConcurrentDictionary<string, IChannel> _registeredChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly IMessageBus _messageBus;
    private readonly ILogger<ChannelManager> _logger;
    #endregion

    #region 构造函数
    public ChannelManager(IMessageBus messageBus, ILogger<ChannelManager> logger)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.LogInformation("通道管理器初始化完成");
    }
    #endregion

    #region 公共属性
    public IReadOnlyList<string> RegisteredChannels => _registeredChannels.Keys.ToList();
    #endregion

    #region 通道注册/获取
    public void RegisterChannel(IChannel channel)
    {
        if (channel == null) throw new ArgumentNullException(nameof(channel));
        if (_registeredChannels.ContainsKey(channel.Name))
            throw new OpenClawException($"通道{channel.Name}已注册，不允许重复注册");

        _registeredChannels.TryAdd(channel.Name, channel);
        _logger.LogInformation("正在为通道 {ChannelName} 注册出站消息订阅...", channel.Name);
        // 自动订阅通道的出站消息（路由到通道的Send方法）
        _messageBus.SubscribeOutbound(channel.Name, async (msg) =>
        {
            _logger.LogDebug("📨 消息总线调用通道 {ChannelName} 的 SendOutboundMessageAsync", channel.Name);
            try
            {
                await channel.SendOutboundMessageAsync(msg);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "通道 {ChannelName} 发送消息失败", channel.Name);
            }
        });
        _logger.LogInformation("通道注册成功 | 名称：{ChannelName}", channel.Name);
    }

    public IChannel? GetChannel(string channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName)) return null;
        _registeredChannels.TryGetValue(channelName, out var channel);
        return channel;
    }
    #endregion

    #region 通道批量启停
    public async Task StartAllChannelsAsync(CancellationToken cancellationToken = default)
    {
        if (_registeredChannels.IsEmpty)
        {
            _logger.LogWarning("无已注册通道，跳过启动");
            return;
        }

        _logger.LogInformation("开始启动所有已注册通道 | 通道数：{Count}", _registeredChannels.Count);
        var startTasks = new List<Task>();
        foreach (var (name, channel) in _registeredChannels)
        {
            startTasks.Add(StartChannelWithErrorHandlingAsync(channel, cancellationToken));
        }

        await Task.WhenAll(startTasks).ConfigureAwait(false);
        _logger.LogInformation("所有通道启动流程完成 | 已启动通道数：{RunningCount}",
            _registeredChannels.Values.Count(c => c.IsRunning));
    }

    public async Task StopAllChannelsAsync(CancellationToken cancellationToken = default)
    {
        if (_registeredChannels.IsEmpty)
        {
            _logger.LogWarning("无已注册通道，跳过停止");
            return;
        }

        _logger.LogInformation("开始停止所有正在运行的通道 | 通道数：{Count}", _registeredChannels.Count);
        var stopTasks = new List<Task>();
        foreach (var (name, channel) in _registeredChannels)
        {
            if (channel.IsRunning)
            {
                stopTasks.Add(StopChannelWithErrorHandlingAsync(channel, cancellationToken));
            }
        }

        await Task.WhenAll(stopTasks).ConfigureAwait(false);
        _logger.LogInformation("所有通道停止流程完成");
    }
    #endregion

    #region 通道状态查询
    public Dictionary<string, ChannelStatus> GetAllChannelStatus()
    {
        var statusDict = new Dictionary<string, ChannelStatus>();
        foreach (var (name, channel) in _registeredChannels)
        {
            // 通道配置从IChannelConfig派生，通过类型判断获取启用状态
            var enabled = channel is IConfigurableChannel configurableChannel ? configurableChannel.Config.Enabled : true;
            statusDict[name] = new ChannelStatus
            {
                Enabled = enabled,
                IsRunning = channel.IsRunning
            };
        }
        return statusDict;
    }
    #endregion

    #region 私有辅助方法（启停带异常处理，不影响其他通道）
    /// <summary>
    /// 启动通道并捕获异常（单个通道失败不影响其他）
    /// </summary>
    private async Task StartChannelWithErrorHandlingAsync(IChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("正在启动通道 | 名称：{ChannelName}", channel.Name);
            await channel.StartAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("通道启动成功 | 名称：{ChannelName}", channel.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "通道启动失败 | 名称：{ChannelName}", channel.Name);
        }
    }

    /// <summary>
    /// 停止通道并捕获异常（单个通道失败不影响其他）
    /// </summary>
    private async Task StopChannelWithErrorHandlingAsync(IChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("正在停止通道 | 名称：{ChannelName}", channel.Name);
            await channel.StopAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("通道停止成功 | 名称：{ChannelName}", channel.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "通道停止失败 | 名称：{ChannelName}", channel.Name);
        }
    }
    #endregion
}

/// <summary>
/// 可配置通道接口（标记通道支持配置，获取通道配置）
/// </summary>
/// <typeparam name="TConfig">通道配置类型</typeparam>
public interface IConfigurableChannel<out TConfig> where TConfig : ChannelConfig
{
    /// <summary>
    /// 通道专属配置
    /// </summary>
    TConfig Config { get; }
}

/// <summary>
/// 泛型标记接口（简化类型判断）
/// </summary>
public interface IConfigurableChannel
{
    /// <summary>
    /// 通道基础配置
    /// </summary>
    ChannelConfig Config { get; }
}

/// <summary>
/// 通道抽象基类（泛型强类型，复刻Python原BaseChannel逻辑，所有通道需继承）
/// 核心能力：权限校验、消息转发到总线、运行状态管理
/// </summary>
/// <typeparam name="TConfig">通道专属配置类型</typeparam>
public abstract class BaseChannel<TConfig> : IChannel, IConfigurableChannel<TConfig>, IConfigurableChannel
    where TConfig : ChannelConfig
{
    #region 核心依赖与配置
    protected readonly TConfig _config;
    protected readonly IMessageBus _messageBus;
    protected readonly ILogger<BaseChannel<TConfig>> _logger;
    private volatile bool _isRunning;
    private readonly object _runningLock = new();
    #endregion

    #region 构造函数（强类型配置，依赖注入）
    protected BaseChannel(TConfig config, IMessageBus messageBus, ILogger<BaseChannel<TConfig>> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config), "通道配置不能为空");
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus), "消息总线不能为空");
        _logger = logger ?? throw new ArgumentNullException(nameof(logger), "日志器不能为空");
        _logger.LogDebug("通道基类初始化 | 通道名称：{ChannelName} | 启用状态：{Enabled}",
            Name, _config.Enabled);
    }
    #endregion

    #region 抽象属性与方法（子类必须实现）
    /// <summary>
    /// 通道唯一名称（子类重写）
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// 启动通道（子类实现：连接平台、监听消息）
    /// </summary>
    public abstract Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止通道（子类实现：断开连接、清理资源）
    /// </summary>
    public abstract Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 发送出站消息（子类实现：适配平台的消息发送逻辑）
    /// </summary>
    public abstract Task SendOutboundMessageAsync(OutboundMessage message, CancellationToken cancellationToken = default);
    #endregion

    #region 公共属性与实现
    /// <summary>
    /// 通道运行状态
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// 泛型配置（强类型）
    /// </summary>
    public TConfig Config => _config;

    /// <summary>
    /// 基础配置（非泛型，供管理器统一查询）
    /// </summary>
    ChannelConfig IConfigurableChannel.Config => _config;
    #endregion

    #region 运行状态管理（线程安全）
    /// <summary>
    /// 设置通道运行状态（子类调用）
    /// </summary>
    protected void SetRunningState(bool isRunning)
    {
        lock (_runningLock)
        {
            _isRunning = isRunning;
        }
        _logger.LogInformation("通道运行状态变更 | 名称：{ChannelName} | 状态：{State}",
            Name, isRunning ? "运行中" : "已停止");
    }
    #endregion

    #region 核心工具方法（权限校验、消息转发，复刻Python原逻辑）
    /// <summary>
    /// 校验发送者是否有权限使用通道（白名单机制，复刻Python is_allowed）
    /// </summary>
    protected bool IsSenderAllowed(string senderId)
    {
        // 白名单为空则允许所有发送者
        if (_config.AllowFrom == null || !_config.AllowFrom.Any())
        {
            _logger.LogTrace("通道白名单为空，允许所有发送者 | 通道：{ChannelName} | 发送者：{SenderId}",
                Name, senderId);
            return true;
        }

        var sender = senderId?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(sender))
        {
            _logger.LogWarning("发送者ID为空，权限校验失败 | 通道：{ChannelName}", Name);
            return false;
        }

        // 发送者ID直接在白名单中
        if (_config.AllowFrom.Contains(sender, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogTrace("发送者在白名单中，权限校验通过 | 通道：{ChannelName} | 发送者：{SenderId}",
                Name, senderId);
            return true;
        }

        // 发送者ID包含|，分割后任意一个在白名单即允许
        if (sender.Contains("|"))
        {
            foreach (var part in sender.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                if (_config.AllowFrom.Contains(part.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    _logger.LogTrace("发送者子ID在白名单中，权限校验通过 | 通道：{ChannelName} | 发送者：{SenderId}",
                        Name, senderId);
                    return true;
                }
            }
        }

        _logger.LogWarning("发送者权限校验失败 | 通道：{ChannelName} | 发送者：{SenderId} | 白名单：{AllowFrom}",
            Name, senderId, string.Join(",", _config.AllowFrom));
        return false;
    }

    /// <summary>
    /// 处理通道入站消息（权限校验→封装为InboundMessage→转发到消息总线）
    /// 子类监听到底层平台消息后，调用此方法转发
    /// </summary>
    protected async Task HandleInboundMessageAsync(
        string senderId,
        string chatId,
        string content,
        List<string>? media = null,
        ConcurrentDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        // 入参非空校验
        if (string.IsNullOrWhiteSpace(senderId)) throw new ArgumentException("发送者ID不能为空", nameof(senderId));
        if (string.IsNullOrWhiteSpace(chatId)) throw new ArgumentException("聊天ID不能为空", nameof(chatId));
        if (string.IsNullOrWhiteSpace(content) && (media == null || !media.Any()))
            throw new ArgumentException("消息内容和媒体不能同时为空", nameof(content));

        // 权限校验
        if (!IsSenderAllowed(senderId)) return;

        // 封装入站消息
        var inboundMessage = new InboundMessage
        {
            Channel = Name,
            SenderId = senderId.Trim(),
            ChatId = chatId.Trim(),
            Content = content?.Trim() ?? string.Empty,
            Media = media ?? new List<string>(),
            Metadata = metadata ?? new ConcurrentDictionary<string, object>()
        };

        // 转发到消息总线
        await _messageBus.PublishInboundAsync(inboundMessage, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("通道入站消息已转发到总线 | 通道：{ChannelName} | 发送者：{SenderId} | 内容：{Content}",
            Name, senderId, content?.Trim()?[..Math.Min(50, content.Length)] ?? "无文本内容");
    }
    #endregion
}

/// <summary>
/// 控制台通道实现（强类型，复刻Python原ConsoleChannel逻辑，适配C#控制台操作）
/// 入站：控制台输入→转发到总线；出站：接收总线消息→控制台打印
/// </summary>
public class ConsoleChannel : BaseChannel<ConsoleChannelConfig>
{
    #region 固定常量
    private const string ConsoleSenderId = "console_user";
    private const string ConsoleChatId = "console_chat";
    #endregion

    #region 取消令牌（用于优雅停止输入监听）
    private CancellationTokenSource? _inputCts;
    #endregion

    #region 构造函数
    public ConsoleChannel(ConsoleChannelConfig config, IMessageBus messageBus, ILogger<ConsoleChannel> logger)
        : base(config, messageBus, logger)
    {
        _logger.LogInformation("控制台通道初始化完成 | 白名单：{AllowFrom}",
            _config.AllowFrom.Any() ? string.Join(",", _config.AllowFrom) : "无（允许所有）");
    }
    #endregion

    #region 通道核心属性实现
    public override string Name => OpenClawSharp.Core.OpenClawConstants.ConsoleChannelName;
    #endregion

    #region 通道启停实现
    public override async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            _logger.LogWarning("控制台通道已在运行，无需重复启动");
            await Task.CompletedTask;
            return;
        }

        if (!_config.Enabled)
        {
            _logger.LogWarning("控制台通道已禁用，跳过启动");
            await Task.CompletedTask;
            return;
        }

        SetRunningState(true);
        _inputCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inputToken = _inputCts.Token;

        // 启动控制台输入监听循环
        _logger.LogInformation("=====================================");
        _logger.LogInformation("📌 控制台通道已启动，开始监听输入");
        _logger.LogInformation($"💡 提示：输入任意内容调用LLM，输入「{string.Join("|", OpenClawConstants.ExitCommands)}」退出");
        _logger.LogInformation("=====================================\n");
        Console.Write("> "); // 输入提示符

        try
        {
            await ListenConsoleInputAsync(inputToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("控制台通道输入监听被取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "控制台通道输入监听发生异常");
        }
        finally
        {
            SetRunningState(false);
            _inputCts?.Dispose();
            Console.WriteLine("\n📌 控制台通道输入监听已停止");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            _logger.LogWarning("控制台通道未运行，无需停止");
            await Task.CompletedTask;
            return;
        }

        // 取消输入监听令牌
        if (_inputCts != null && !_inputCts.IsCancellationRequested)
        {
            _inputCts.Cancel();
        }

        SetRunningState(false);
        await Task.CompletedTask;
    }
    #endregion

    #region 出站消息发送实现（控制台打印）
    public override async Task SendOutboundMessageAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            _logger.LogWarning("控制台通道未运行，无法发送出站消息");
            await Task.CompletedTask;
            return;
        }

        if (message == null) throw new ArgumentNullException(nameof(message));
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation("🖨️ 控制台通道开始发送出站消息 | 通道：{Channel} | 聊天ID：{ChatId} | 内容长度：{Length}",
        message.Channel, message.ChatId, message.Content.Length);
        // 格式化打印LLM回复
        var sb = new StringBuilder();
        sb.AppendLine("\n-------------------------------------");
        sb.AppendLine("🤖 【LLM 回 复】");
        sb.AppendLine($"⏰ 时间：{DateTime.Now:HH:mm:ss}");
        sb.AppendLine($"💬 内容：{message.Content}");
        sb.AppendLine("-------------------------------------");

        Console.WriteLine(sb.ToString());
        Console.Write("> "); // 重新显示输入提示符
        _logger.LogInformation("控制台通道已打印出站消息 | 内容长度：{Length}", message.Content.Length);
        await Task.CompletedTask;
    }
    #endregion

    #region 控制台输入监听核心逻辑
    /// <summary>
    /// 监听控制台输入（同步读取，稳定捕获，支持退出指令和取消）
    /// </summary>
    private async Task ListenConsoleInputAsync(CancellationToken cancellationToken)
    {
        while (IsRunning && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                // 非阻塞检测控制台输入，避免ReadLine阻塞时无法响应取消
                while (!Console.KeyAvailable && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }

                if (cancellationToken.IsCancellationRequested) break;

                // 读取控制台输入
                var input = Console.ReadLine()?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(input))
                {
                    Console.WriteLine("⚠️  请输入有效内容，输入「exit/quit/退出」可退出");
                    Console.Write("> ");
                    continue;
                }

                // 退出指令检测
                if (OpenClawConstants.ExitCommands.Contains(input))
                {
                    _logger.LogInformation("接收到退出指令，停止控制台通道输入监听");
                    await StopAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }

                // 转发输入到消息总线
                await HandleInboundMessageAsync(
                    senderId: ConsoleSenderId,
                    chatId: ConsoleChatId,
                    content: input,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
               
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "控制台输入处理异常，继续监听");
                Console.WriteLine($"⚠️  输入处理异常：{ex.Message}\n> ");
            }
        }
    }
    #endregion
}