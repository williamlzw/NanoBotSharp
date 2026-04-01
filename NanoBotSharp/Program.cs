#nullable enable
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using OpenClawSharp.Agent;
using OpenClawSharp.Bus;
using OpenClawSharp.Channels;
using OpenClawSharp.Config;
using OpenClawSharp.Context;
using OpenClawSharp.Core;
using OpenClawSharp.Memory;
using OpenClawSharp.Providers;
using OpenClawSharp.Providers.Ollama;
using OpenClawSharp.Queue;
using OpenClawSharp.Session;
using OpenClawSharp.Subagent;
using OpenClawSharp.Tools;
using OpenClawSharp.Tools.Impl;
using System.Text;
using System;
using System.Threading.Tasks;

Console.OutputEncoding = Encoding.UTF8;
var host = Host.CreateDefaultBuilder(args)
    // 配置文件（Host.CreateDefaultBuilder已默认添加，手动追加不冲突，保留你的逻辑）
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        config.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true);
        Console.WriteLine($"[调试] 配置文件基础路径（ContentRootPath）：{context.HostingEnvironment.ContentRootPath}");
    })
    // 服务注册（核心修复区）
    .ConfigureServices((context, services) =>
    {
        #region 1. 根配置 + 嵌套子配置注入（核心修复：解决IConsoleChannelConfig解析失败）
        // 第一步：注册根配置IOpenClawConfig（从配置文件绑定+默认配置兜底，贴合你的设计）
        services.AddSingleton<OpenClawConfig>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            // 1. 从配置文件绑定根配置（匹配appsettings.json的OpenClaw节点）
            var openClawConfig = new OpenClawConfig();
            config.GetSection("OpenClaw").Bind(openClawConfig);

            // 2. 关键：如果配置文件中未指定工作区/核心配置，使用默认配置兜底（复用你的CreateDefault）
            if (string.IsNullOrWhiteSpace(openClawConfig.Workspace))
            {
                var defaultConfig = OpenClawConfig.CreateDefault();
                openClawConfig.Workspace = defaultConfig.Workspace;
                openClawConfig.Llm = defaultConfig.Llm;
                openClawConfig.Channels = defaultConfig.Channels;
                openClawConfig.ExecTool = defaultConfig.ExecTool;
                openClawConfig.Agent = defaultConfig.Agent;
            }
            // 确保工作区目录存在
            openClawConfig.Workspace.ExpandUser().EnsureDirectoryExists();
            return openClawConfig;
        });

        // 注册所有嵌套子配置接口（从根配置解析，供各组件直接注入）
        services.AddSingleton<ConsoleChannelConfig>(sp => sp.GetRequiredService<OpenClawConfig>().Channels.Console);
        services.AddSingleton<LlmConfig>(sp => sp.GetRequiredService<OpenClawConfig>().Llm);
        services.AddSingleton<ExecToolConfig>(sp => sp.GetRequiredService<OpenClawConfig>().ExecTool);
        services.AddSingleton<AgentConfig>(sp => sp.GetRequiredService<OpenClawConfig>().Agent);
        #endregion


        #region 2. 核心基础服务
        // 泛型并发队列单例（你的注册方式正确，保留）
        services.AddSingleton(typeof(IAsyncConcurrentQueue<>), typeof(ChannelBasedAsyncQueue<>));
        // 日志配置（优化级别，避免调试日志过多）
        services.AddLogging(builder =>
        {
            builder.AddConsole(options =>
            {
                // 输出日志时间和类别，方便定位
                options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
            });
            builder.AddDebug();
            // 生产环境可改为Information，开发环境用Debug
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddFilter("OpenClawSharp", LogLevel.Debug);
        });
        #endregion

        

        #region 3. 工具集注册（修复+优化：初始化工作目录+工具注册）
        services.AddSingleton<ISkPluginRegistry, SkPluginRegistry>();
        // 注册所有工具为单例
        services.AddSingleton<ReadFilePlugin>();

        services.AddSingleton<WriteFilePlugin>();

        services.AddSingleton<MessagePlugin>();
        services.AddSingleton<EditFilePlugin>();

        services.AddSingleton<ListDirPlugin>();

        // 修复：ExecTool注册（IOptions获取配置+初始化工作目录）
        services.AddSingleton<ExecPlugin>(sp =>
        {
            var execConfig = sp.GetRequiredService<ExecToolConfig>(); // 从根配置解析的配置接口
            var workspace = sp.GetRequiredService<OpenClawConfig>().Workspace; // 全局唯一工作区
            var pluginRegistry = sp.GetRequiredService<ISkPluginRegistry>();
            var pluginInstance = new ExecPlugin(workspace, execConfig.Timeout, execConfig.RestrictToWorkspace);
            return pluginInstance;
        });
        #endregion

       

        #region 5. 核心业务组件（保留你的设计，无错误）
        services.AddSingleton<IMessageBus, MessageBus>();
        services.AddSingleton<Func<OutboundMessage, Task>>(sp =>
        {
            var messageBus = sp.GetRequiredService<IMessageBus>();
            var logger = sp.GetRequiredService<ILogger<MessagePlugin>>();
            return async (outboundMessage) =>
            {
                try
                {
                    await messageBus.PublishOutboundAsync(outboundMessage, CancellationToken.None);
                    logger.LogDebug("出站消息发送成功：{Content}", outboundMessage.Content);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "发送出站消息失败");
                    throw;
                }
            };
        });
        services.AddSingleton<IChannel, ConsoleChannel>();

        services.AddSingleton<IChannelManager, ChannelManager>();
        services.AddSingleton<ISessionManager, FileSystemSessionManager>(sp =>
        {
            var workspace = sp.GetRequiredService<OpenClawConfig>().Workspace;
            var logger = sp.GetRequiredService<ILogger<FileSystemSessionManager>>();
            return new FileSystemSessionManager(workspace, logger);
        });
        services.AddSingleton<IMemoryStore, FileSystemMemoryStore>(sp =>
        {
            var workspace = sp.GetRequiredService<OpenClawConfig>().Workspace;
            var logger = sp.GetRequiredService<ILogger<FileSystemMemoryStore>>();
            return new FileSystemMemoryStore(workspace, logger);
        });

        services.AddSingleton<IContextBuilder, WorkspaceContextBuilder>(sp =>
        {
            var workspace = sp.GetRequiredService<OpenClawConfig>().Workspace;
            var memoryStore = sp.GetRequiredService<IMemoryStore>();
            var logger = sp.GetRequiredService<ILogger<WorkspaceContextBuilder>>();
            return new WorkspaceContextBuilder(workspace, memoryStore, logger);
        });
        services.AddSingleton<ISubagentManager, SubagentManager>();

        #region 2.5 新增：注册 Semantic Kernel（SkPluginRegistry 依赖）
        services.AddSingleton<Kernel>(sp =>
        {
            // 从 DI 容器获取 Ollama 配置和日志工厂
            var llmConfig = sp.GetRequiredService<LlmConfig>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger<Kernel>();

            // 校验配置（避免空值导致初始化失败）
            if (string.IsNullOrWhiteSpace(llmConfig.DefaultModel))
                throw new ArgumentNullException(nameof(llmConfig.DefaultModel), "Ollama 模型名称未配置（Llm:Model）");
            if (string.IsNullOrWhiteSpace(llmConfig.Endpoint))
                throw new ArgumentNullException(nameof(llmConfig.Endpoint), "Ollama 服务地址未配置（Llm:Endpoint）");

            // 构建 Semantic Kernel 实例
            var kernelBuilder = Kernel.CreateBuilder();
            // 添加 Ollama 聊天完成服务（适配你的 LLM 配置）
            kernelBuilder.AddOllamaChatCompletion(
                modelId: llmConfig.DefaultModel,
                endpoint: new Uri(llmConfig.Endpoint)
            );
            // 关联DI容器，确保Kernel能获取注册的服务
            kernelBuilder.Services.AddSingleton(sp); // 注入当前DI容器

            var kernel = kernelBuilder.Build();
     
            logger.LogInformation("✅ Semantic Kernel 初始化完成（模型：{Model}，地址：{Endpoint}）",
                llmConfig.DefaultModel, llmConfig.Endpoint);
            return kernel;
        });
        #endregion

        #region 4. LLM提供者（修复核心：通过IOptions<LlmConfig>获取配置）
        services.AddSingleton<ILlmProvider, OllamaLlmProvider>(sp =>
        {
            var llmConfig = sp.GetRequiredService<LlmConfig>(); // 从根配置解析的LLM配置
            var kernel = sp.GetRequiredService<Kernel>();
            var logger = sp.GetRequiredService<ILogger<OllamaLlmProvider>>();
            return new OllamaLlmProvider(llmConfig, kernel, logger);
        });
        #endregion

        services.AddSingleton<IAgentLoop, AgentLoop>();
        #endregion

        #region 6. 后台服务（保留你的实现，无错误）
        services.AddHostedService<AgentHostedService>();
        services.AddHostedService<MessageBusHostedService>(); 
        services.AddHostedService<ChannelHostedService>();
        #endregion
    })
    // 启动时验证所有服务（可选，开发环境推荐，快速发现DI错误）
    .UseDefaultServiceProvider((context, options) =>
    {
        if (context.HostingEnvironment.IsDevelopment())
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        }
    })
    .Build();

// 启动前打印工作目录，方便调试
var workspace = host.Services.GetRequiredService<OpenClawConfig>().Workspace
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw");

Console.WriteLine($"[OpenClaw] 工作目录：{workspace}");
Console.WriteLine("[OpenClaw] 服务启动中...");

// 启动主机
await host.RunAsync();

#region 后台服务：代理核心（保留你的实现，无错误）
public class AgentHostedService : BackgroundService
{
    private readonly IAgentLoop _agentLoop;
    private readonly ILogger<AgentHostedService> _logger;

    // 修复：构造函数注入IAgentLoop（面向接口，而非具体实现，符合DI原则）
    public AgentHostedService(IAgentLoop agentLoop, ILogger<AgentHostedService> logger)
    {
        _agentLoop = agentLoop ?? throw new ArgumentNullException(nameof(agentLoop));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("✅ 代理核心后台服务启动成功");
        await _agentLoop.RunAsync(stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("🛑 代理核心后台服务开始停止");
        _agentLoop.Stop();
        await base.StopAsync(cancellationToken);
        _logger.LogInformation("🛑 代理核心后台服务停止完成");
    }
}
#endregion

#region 后台服务：通道管理器（保留你的实现，优化日志）
public class ChannelHostedService : BackgroundService
{
    private readonly IChannelManager _channelManager;
    private readonly ILogger<ChannelHostedService> _logger;
    private readonly IChannel _consoleChannel;
    private readonly IMessageBus _messageBus;

    public ChannelHostedService(IChannelManager channelManager, ILogger<ChannelHostedService> logger, IChannel consoleChannel, IMessageBus messageBus)
    {
        _channelManager = channelManager ?? throw new ArgumentNullException(nameof(channelManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _consoleChannel = consoleChannel ?? throw new ArgumentNullException(nameof(consoleChannel));
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("✅ 通道管理器后台服务启动成功");
        // 等待消息总线分发器启动
        await Task.Delay(100, stoppingToken).ConfigureAwait(false);
        _channelManager.RegisterChannel(_consoleChannel);
        await _channelManager.StartAllChannelsAsync(stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("🛑 通道管理器后台服务开始停止");
        await _channelManager.StopAllChannelsAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
        _logger.LogInformation("🛑 通道管理器后台服务停止完成");
    }
}
#endregion

#region 新增：消息总线后台服务（确保分发器先启动）
public class MessageBusHostedService : BackgroundService
{
    private readonly IMessageBus _messageBus;
    private readonly ILogger<MessageBusHostedService> _logger;

    public MessageBusHostedService(IMessageBus messageBus, ILogger<MessageBusHostedService> logger)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("✅ 消息总线后台服务启动");
        await _messageBus.StartAsync(stoppingToken).ConfigureAwait(false);

        // 等待停止信号
        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("🛑 消息总线后台服务停止");
        await _messageBus.StopAsync(cancellationToken).ConfigureAwait(false);
        await base.StopAsync(cancellationToken);
    }
}
#endregion