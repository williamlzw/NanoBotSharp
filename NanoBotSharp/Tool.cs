#nullable enable
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using OpenClawSharp.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawSharp.Tools;

/// <summary>
/// SK插件注册器（DI友好，管理所有SK插件）
/// </summary>
public interface ISkPluginRegistry
{
    /// <summary>
    /// 注册SK插件实例
    /// </summary>
    /// <param name="pluginName">插件名称</param>
    /// <param name="pluginInstance">插件实例</param>
    void RegisterPlugin(string pluginName, object pluginInstance);
   // void RegisterPlugin(KernelPlugin plugin); // 改为直接注册KernelPlugin
    //void RegisterPlugin<T>(string pluginName = null) where T : class, new(); // 兼容泛型注册
    /// <summary>
    /// 获取插件的所有函数
    /// </summary>
    /// <param name="pluginName">插件名称</param>
    /// <returns>函数元数据列表</returns>
    List<KernelFunction> GetPluginFunctions(string pluginName);

    /// <summary>
    /// 执行插件函数
    /// </summary>
    /// <param name="pluginName">插件名称</param>
    /// <param name="functionName">函数名称</param>
    /// <param name="arguments">函数参数</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>执行结果</returns>
    Task<string> ExecuteFunctionAsync(string pluginName, string functionName, Dictionary<string, object> arguments, CancellationToken cancellationToken = default);
}

/// <summary>
/// SK插件注册器实现（线程安全）
/// </summary>
public class SkPluginRegistry : ISkPluginRegistry
{
    private readonly Kernel _kernel;
    private readonly ConcurrentDictionary<string, object> _plugins = new();
    private readonly ILogger<SkPluginRegistry> _logger;

    public SkPluginRegistry(Kernel kernel, ILogger<SkPluginRegistry> logger)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    //public void RegisterPlugin(KernelPlugin plugin)
    //{
    //    if (plugin == null) throw new ArgumentNullException(nameof(plugin));
    //    if (_plugins.ContainsKey(plugin.Name))
    //        throw new InvalidOperationException($"插件 {plugin.Name} 已注册");

    //    _plugins.TryAdd(plugin.Name, plugin);
    //}

    //// 泛型注册（适配AOT，调用插件的静态CreatePlugin方法）
    //public void RegisterPlugin<T>(string pluginName = null) where T : class, new()
    //{
    //    var type = typeof(T);
    //    var createPluginMethod = type.GetMethod("CreatePlugin", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
    //    if (createPluginMethod == null)
    //        throw new InvalidOperationException($"插件 {type.Name} 未实现静态的 CreatePlugin 方法，无法在AOT下注册");

    //    // 调用静态方法创建插件（无反射扫描）
    //    var plugin = (KernelPlugin)(pluginName == null
    //        ? createPluginMethod.Invoke(null, null)!
    //        : createPluginMethod.Invoke(null, new object[] { pluginName })!);

    //    RegisterPlugin(plugin);
    //}

    public void RegisterPlugin(string pluginName, object pluginInstance)
    {
        if (string.IsNullOrWhiteSpace(pluginName))
            throw new ArgumentException("插件名称不能为空", nameof(pluginName));
        if (pluginInstance == null)
            throw new ArgumentNullException(nameof(pluginInstance));
        var plugin = KernelPluginFactory.CreateFromObject(
                pluginInstance,
                pluginName: pluginName
            );
        if (_plugins.TryAdd(pluginName, plugin))
        {
            _kernel.Plugins.AddFromObject(pluginInstance, pluginName);
            _logger.LogInformation("SK插件注册成功 | 名称：{PluginName}", pluginName);
        }
        else
        {
            throw new OpenClawException($"插件{pluginName}已注册，不允许重复注册");
        }
    }

    public List<KernelFunction> GetPluginFunctions(string pluginName)
    {
        if (!_plugins.ContainsKey(pluginName))
            throw new OpenClawException($"插件{pluginName}未注册");

        return _kernel.Plugins[pluginName].Select(f=>f).ToList();
    }

    public async Task<string> ExecuteFunctionAsync(string pluginName, string functionName, Dictionary<string, object> arguments, CancellationToken cancellationToken = default)
    {
        if (!_plugins.ContainsKey(pluginName))
            throw new ToolExecutionException(functionName, $"插件{pluginName}未注册");

        try
        {
            var function = _kernel.Plugins[pluginName][functionName];
            var skArguments = new KernelArguments();
            foreach (var (key, value) in arguments)
            {
                skArguments[key] = value?.ToString() ?? string.Empty;
            }

            var result = await _kernel.InvokeAsync(function, skArguments, cancellationToken).ConfigureAwait(false);
            return result.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "执行SK插件函数失败 | 插件：{PluginName} | 函数：{FunctionName}", pluginName, functionName);
            throw new ToolExecutionException(functionName, $"执行函数{functionName}失败：{ex.Message}", ex);
        }
    }
}

