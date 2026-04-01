#nullable enable
using Microsoft.SemanticKernel;
using OpenClawSharp.Bus;
using OpenClawSharp.Core;
using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawSharp.Tools.Impl;

/// <summary>
/// 读取文件插件（SK风格实现）
/// </summary>
public class ReadFilePlugin
{
    [KernelFunction, Description("读取指定路径文件的内容，跨平台兼容~目录")]
    public async Task<string> ReadFileAsync(
        [Description("文件路径（支持~表示用户目录）")] string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = path.ExpandUser();

            if (!File.Exists(filePath))
                return $"错误：文件不存在 → {path}";
            return await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            return $"错误：权限不足，无法读取 → {path}";
        }
        catch (Exception ex)
        {
            return $"读取文件失败 → {path}，原因：{ex.Message}";
        }
    }

    // 【关键】为AOT提供静态方法，显式创建KernelFunction（避免反射扫描）
    public static KernelFunction CreateReadFileFunction(ReadFilePlugin pluginInstance)
    {
        return KernelFunctionFactory.CreateFromMethod(
            target: pluginInstance,
            method: typeof(ReadFilePlugin).GetMethod(nameof(ReadFileAsync))!,
            functionName: nameof(ReadFileAsync),
            description: "读取指定路径文件的内容，跨平台兼容~目录",
            parameters: new[]
            {
                new KernelParameterMetadata("path")
                {
                    Description = "文件路径（支持~表示用户目录）",
                    IsRequired = true
                }
            });
    }

    // 【关键】为AOT提供静态方法，创建完整的Plugin
    public static KernelPlugin CreatePlugin(ReadFilePlugin pluginInstance, string pluginName = "ReadFilePlugin")
    {
        return KernelPluginFactory.CreateFromFunctions(
            pluginName: pluginName,
            functions: new[] { CreateReadFileFunction(pluginInstance) });
    }
}

/// <summary>
/// 写入文件插件（SK风格实现，自动创建父目录）
/// </summary>
public class WriteFilePlugin
{
    [KernelFunction, Description("写入内容到指定路径文件，自动创建不存在的父目录，跨平台兼容~目录")]
    public async Task<string> WriteFileAsync(
        [Description("文件路径（支持~表示用户目录）")] string path,
        [Description("要写入的文件内容")] string content,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = path.ExpandUser();

            // 自动创建父目录
            var parentDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(parentDir))
                parentDir.EnsureDirectoryExists();

            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            return $"成功写入 {content.Length} 字节到 → {path}";
        }
        catch (UnauthorizedAccessException)
        {
            return $"错误：权限不足，无法写入 → {path}";
        }
        catch (Exception ex)
        {
            return $"写入文件失败 → {path}，原因：{ex.Message}";
        }
    }

    /// <summary>
    /// AOT友好：创建WriteFileAsync的KernelFunction（绑定实例）
    /// </summary>
    public static KernelFunction CreateWriteFileFunction(WriteFilePlugin pluginInstance)
    {
        if (pluginInstance == null)
            throw new ArgumentNullException(nameof(pluginInstance), "WriteFilePlugin实例不能为空");

        var method = typeof(WriteFilePlugin).GetMethod(nameof(WriteFileAsync))
            ?? throw new InvalidOperationException($"未找到 {nameof(WriteFileAsync)} 方法");

        return KernelFunctionFactory.CreateFromMethod(
            method: method,
            target: pluginInstance,
            functionName: nameof(WriteFileAsync),
            description: "写入内容到指定路径文件，自动创建不存在的父目录，跨平台兼容~目录",
            parameters: new[]
            {
                new KernelParameterMetadata("path")
                {
                    Description = "文件路径（支持~表示用户目录）",
                    IsRequired = true
                },
                new KernelParameterMetadata("content")
                {
                    Description = "要写入的文件内容",
                    IsRequired = true
                }
            });
    }

    /// <summary>
    /// AOT友好：创建WriteFilePlugin的KernelPlugin
    /// </summary>
    public static KernelPlugin CreatePlugin(WriteFilePlugin pluginInstance, string pluginName = "WriteFilePlugin")
    {
        if (pluginInstance == null)
            throw new ArgumentNullException(nameof(pluginInstance));

        return KernelPluginFactory.CreateFromFunctions(
            pluginName: pluginName,
            functions: new[] { CreateWriteFileFunction(pluginInstance) });
    }
}

/// <summary>
/// 编辑文件插件（SK风格实现，精确替换文本）
/// </summary>
public class EditFilePlugin
{
    [KernelFunction, Description("精确替换文件中的指定文本，仅替换一次，重复匹配会给出警告")]
    public async Task<string> EditFileAsync(
        [Description("文件路径（支持~表示用户目录）")] string path,
        [Description("要替换的原始文本（必须与文件中内容完全一致）")] string old_text,
        [Description("替换后的新文本")] string new_text,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = path.ExpandUser();

            if (!File.Exists(filePath))
                return $"错误：文件不存在 → {path}";

            var content = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            if (!content.Contains(old_text, StringComparison.Ordinal))
                return "错误：未找到要替换的原始文本，请确保内容完全一致";

            var occurrenceCount = CountStringOccurrences(content, old_text);
            if (occurrenceCount > 1)
                return $"警告：原始文本出现{occurrenceCount}次，请提供更具体的上下文保证唯一性";

            var newContent = content.Replace(old_text, new_text, StringComparison.Ordinal);
            await File.WriteAllTextAsync(filePath, newContent, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            return $"成功编辑文件 → {path}";
        }
        catch (UnauthorizedAccessException)
        {
            return $"错误：权限不足，无法编辑 → {path}";
        }
        catch (Exception ex)
        {
            return $"编辑文件失败 → {path}，原因：{ex.Message}";
        }
    }

    /// <summary>
    /// 统计字符串精确出现次数
    /// </summary>
    private int CountStringOccurrences(string source, string search)
    {
        if (string.IsNullOrEmpty(search) || source.Length < search.Length) return 0;
        int count = 0, index = 0;
        while ((index = source.IndexOf(search, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += search.Length;
        }
        return count;
    }

    /// <summary>
    /// AOT友好：创建EditFileAsync的KernelFunction（绑定实例）
    /// </summary>
    public static KernelFunction CreateEditFileFunction(EditFilePlugin pluginInstance)
    {
        if (pluginInstance == null)
            throw new ArgumentNullException(nameof(pluginInstance), "EditFilePlugin实例不能为空");

        var method = typeof(EditFilePlugin).GetMethod(nameof(EditFileAsync))
            ?? throw new InvalidOperationException($"未找到 {nameof(EditFileAsync)} 方法");

        return KernelFunctionFactory.CreateFromMethod(
            method: method,
            target: pluginInstance,
            functionName: nameof(EditFileAsync),
            description: "精确替换文件中的指定文本，仅替换一次，重复匹配会给出警告",
            parameters: new[]
            {
                new KernelParameterMetadata("path")
                {
                    Description = "文件路径（支持~表示用户目录）",
                    IsRequired = true
                },
                new KernelParameterMetadata("old_text")
                {
                    Description = "要替换的原始文本（必须与文件中内容完全一致）",
                    IsRequired = true
                },
                new KernelParameterMetadata("new_text")
                {
                    Description = "替换后的新文本",
                    IsRequired = true
                }
            });
    }

    /// <summary>
    /// AOT友好：创建EditFilePlugin的KernelPlugin
    /// </summary>
    public static KernelPlugin CreatePlugin(EditFilePlugin pluginInstance, string pluginName = "EditFilePlugin")
    {
        if (pluginInstance == null)
            throw new ArgumentNullException(nameof(pluginInstance));

        return KernelPluginFactory.CreateFromFunctions(
            pluginName: pluginName,
            functions: new[] { CreateEditFileFunction(pluginInstance) });
    }
}

/// <summary>
/// 列目录插件（SK风格实现，区分文件/目录）
/// </summary>
public class ListDirPlugin
{
    [KernelFunction, Description("列出指定目录的内容，区分文件/目录并添加图标，跨平台兼容~目录")]
    public Task<string> ListDirAsync(
        [Description("目录路径（支持~表示用户目录）")] string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var dirPath = path.ExpandUser();

            if (!Directory.Exists(dirPath))
                return Task.FromResult($"错误：目录不存在 → {path}");

            var dirInfo = new DirectoryInfo(dirPath);
            var items = dirInfo.GetFileSystemInfos()
                               .OrderBy(item => item.Name)
                               .ToList();

            if (!items.Any())
                return Task.FromResult($"目录 {path} 为空");

            var itemList = new List<string>();
            foreach (var item in items)
            {
                var prefix = item is DirectoryInfo ? "📁 " : "📄 ";
                itemList.Add($"{prefix}{item.Name}");
            }

            return Task.FromResult(string.Join(Environment.NewLine, itemList));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult($"错误：权限不足，无法列出 → {path}");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"列目录失败 → {path}，原因：{ex.Message}");
        }
    }

    /// <summary>
    /// AOT友好：创建ListDirAsync的KernelFunction（绑定实例）
    /// </summary>
    public static KernelFunction CreateListDirFunction(ListDirPlugin pluginInstance)
    {
        if (pluginInstance == null)
            throw new ArgumentNullException(nameof(pluginInstance), "ListDirPlugin实例不能为空");

        var method = typeof(ListDirPlugin).GetMethod(nameof(ListDirAsync))
            ?? throw new InvalidOperationException($"未找到 {nameof(ListDirAsync)} 方法");

        return KernelFunctionFactory.CreateFromMethod(
            method: method,
            target: pluginInstance,
            functionName: nameof(ListDirAsync),
            description: "列出指定目录的内容，区分文件/目录并添加图标，跨平台兼容~目录",
            parameters: new[]
            {
                new KernelParameterMetadata("path")
                {
                    Description = "目录路径（支持~表示用户目录）",
                    IsRequired = true
                }
            });
    }

    /// <summary>
    /// AOT友好：创建ListDirPlugin的KernelPlugin
    /// </summary>
    public static KernelPlugin CreatePlugin(ListDirPlugin pluginInstance, string pluginName = "ListDirPlugin")
    {
        if (pluginInstance == null)
            throw new ArgumentNullException(nameof(pluginInstance));

        return KernelPluginFactory.CreateFromFunctions(
            pluginName: pluginName,
            functions: new[] { CreateListDirFunction(pluginInstance) });
    }
}

/// <summary>
/// 执行Shell命令插件（SK风格实现，工作区限制）
/// </summary>
public class ExecPlugin
{
    private readonly string _workingDir;
    private readonly int _timeout;
    private readonly bool _restrictToWorkspace;

    /// <summary>
    /// 构造函数（注入配置）
    /// </summary>
    public ExecPlugin(string workingDir, int timeout = 30000, bool restrictToWorkspace = true)
    {
        _workingDir = workingDir.ExpandUser();
        _workingDir.EnsureDirectoryExists();
        _timeout = timeout;
        _restrictToWorkspace = restrictToWorkspace;
    }

    [KernelFunction, Description("执行Shell/CMD命令，支持工作区限制，防止越权访问")]
    public async Task<string> ExecAsync(
        [Description("要执行的Shell/CMD命令")] string command,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 工作区限制校验（如果开启）
            if (_restrictToWorkspace && command.Contains("..", StringComparison.Ordinal))
                return "错误：命令包含..，禁止越权访问工作区";

            // 跨平台执行命令
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.OSVersion.Platform == PlatformID.Win32NT ? "cmd.exe" : "/bin/bash",
                Arguments = Environment.OSVersion.Platform == PlatformID.Win32NT ? $"/c {command}" : $"-c \"{command}\"",
                WorkingDirectory = _workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new System.Diagnostics.Process { StartInfo = psi };
            process.Start();

            // 等待执行完成并设置超时
            var processTask = process.WaitForExitAsync(cancellationToken);
            var completedTask = await Task.WhenAny(processTask, Task.Delay(TimeSpan.FromMilliseconds(_timeout), cancellationToken));

            if (completedTask != processTask)
            {
                // 超时：杀死进程并返回错误
                try { if (!process.HasExited) process.Kill(); }
                catch { /* 忽略杀死进程的异常 */ }
                return $"Error: Command timed out after {_timeout / 1000} seconds";
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
                return $"命令执行失败（退出码：{process.ExitCode}）→ {command}\n错误信息：{error}";

            return string.IsNullOrEmpty(output) ? "命令执行成功，无输出" : output;
        }
        catch (Exception ex)
        {
            return $"执行命令失败 → {command}，原因：{ex.Message}";
        }
    }

    /// <summary>
    /// AOT友好：创建ExecAsync的KernelFunction（绑定实例）
    /// </summary>
    public static KernelFunction CreateExecFunction(ExecPlugin pluginInstance)
    {
        if (pluginInstance == null)
            throw new ArgumentNullException(nameof(pluginInstance), "ExecPlugin实例不能为空");

        var method = typeof(ExecPlugin).GetMethod(nameof(ExecAsync))
            ?? throw new InvalidOperationException($"未找到 {nameof(ExecAsync)} 方法");

        return KernelFunctionFactory.CreateFromMethod(
            method: method,
            target: pluginInstance,
            functionName: nameof(ExecAsync),
            description: "执行Shell/CMD命令，支持工作区限制，防止越权访问",
            parameters: new[]
            {
                new KernelParameterMetadata("command")
                {
                    Description = "要执行的Shell/CMD命令",
                    IsRequired = true
                }
            });
    }

    /// <summary>
    /// AOT友好：创建ExecPlugin的KernelPlugin
    /// </summary>
    public static KernelPlugin CreatePlugin(ExecPlugin pluginInstance, string pluginName = "ExecPlugin")
    {
        if (pluginInstance == null)
            throw new ArgumentNullException(nameof(pluginInstance));

        return KernelPluginFactory.CreateFromFunctions(
            pluginName: pluginName,
            functions: new[] { CreateExecFunction(pluginInstance) });
    }
}

/// <summary>
/// 消息发送插件（SK风格实现，注入消息总线）
/// </summary>
public class MessagePlugin
{
    private readonly Func<OutboundMessage, Task> _publishFunc;
    private string _channel = string.Empty;
    private string _chatId = string.Empty;

    /// <summary>
    /// 构造函数（注入消息发布委托）
    /// </summary>
    public MessagePlugin(Func<OutboundMessage, Task> publishFunc)
    {
        _publishFunc = publishFunc ?? throw new ArgumentNullException(nameof(publishFunc));
    }

    /// <summary>
    /// 设置消息上下文（通道/聊天ID）
    /// </summary>
    public void SetContext(string channel, string chatId)
    {
        _channel = channel;
        _chatId = chatId;
    }

    [KernelFunction, Description("发送消息到指定聊天通道，需先设置上下文")]
    public async Task<string> SendMessageAsync(
        [Description("要发送的消息内容")] string content,
        [Description("目标通道（可选，默认使用上下文通道）")] string? channel = null,
        [Description("目标聊天ID（可选，默认使用上下文聊天ID）")] string? chat_id = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var targetChannel = channel ?? _channel;
            var targetChatId = chat_id ?? _chatId;

            if (string.IsNullOrWhiteSpace(targetChannel) || string.IsNullOrWhiteSpace(targetChatId))
                return "错误：未设置消息上下文，无法发送消息";

            var msg = new OutboundMessage(targetChannel, targetChatId, content);
            await _publishFunc(msg).ConfigureAwait(false);
            return $"成功发送消息到 {targetChannel}:{targetChatId} → {content}";
        }
        catch (Exception ex)
        {
            return $"发送消息失败，原因：{ex.Message}";
        }
    }

    /// <summary>
    /// AOT友好：创建SendMessageAsync的KernelFunction（绑定实例）
    /// </summary>
    public static KernelFunction CreateSendMessageFunction(MessagePlugin pluginInstance)
    {
        if (pluginInstance == null)
            throw new ArgumentNullException(nameof(pluginInstance), "MessagePlugin实例不能为空");

        var method = typeof(MessagePlugin).GetMethod(nameof(SendMessageAsync))
            ?? throw new InvalidOperationException($"未找到 {nameof(SendMessageAsync)} 方法");

        return KernelFunctionFactory.CreateFromMethod(
            method: method,
            target: pluginInstance,
            functionName: nameof(SendMessageAsync),
            description: "发送消息到指定聊天通道，需先设置上下文",
            parameters: new[]
            {
                new KernelParameterMetadata("content")
                {
                    Description = "要发送的消息内容",
                    IsRequired = true
                },
                new KernelParameterMetadata("channel")
                {
                    Description = "目标通道（可选，默认使用上下文通道）",
                    IsRequired = false
                },
                new KernelParameterMetadata("chat_id")
                {
                    Description = "目标聊天ID（可选，默认使用上下文聊天ID）",
                    IsRequired = false
                }
            });
    }

    /// <summary>
    /// AOT友好：创建MessagePlugin的KernelPlugin
    /// </summary>
    public static KernelPlugin CreatePlugin(MessagePlugin pluginInstance, string pluginName = "MessagePlugin")
    {
        if (pluginInstance == null)
            throw new ArgumentNullException(nameof(pluginInstance));

        return KernelPluginFactory.CreateFromFunctions(
            pluginName: pluginName,
            functions: new[] { CreateSendMessageFunction(pluginInstance) });
    }
}