#nullable enable
using OpenClawSharp.Core;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawSharp.Memory;

/// <summary>
/// 内存存储接口（代理的长期内存+每日笔记）
/// </summary>
public interface IMemoryStore
{
    /// <summary>
    /// 获取今日笔记文件路径
    /// </summary>
    string GetTodayNotePath();

    /// <summary>
    /// 读取今日笔记内容
    /// </summary>
    Task<string> ReadTodayNoteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 追加内容到今日笔记
    /// </summary>
    Task AppendToTodayNoteAsync(string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取长期内存内容
    /// </summary>
    Task<string> ReadLongTermMemoryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 覆盖写入长期内存
    /// </summary>
    Task WriteLongTermMemoryAsync(string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取最近N天的内存笔记内容
    /// </summary>
    Task<string> GetRecentMemoriesAsync(int days = 7, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取内存上下文（长期内存+今日笔记，供LLM上下文使用）
    /// </summary>
    Task<string> GetMemoryContextAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 内存存储实现（基于文件系统，跨平台兼容）
/// </summary>
public class FileSystemMemoryStore : IMemoryStore
{
    private readonly string _memoryDir;
    private readonly string _longTermMemoryFile;
    private readonly ILogger<FileSystemMemoryStore> _logger;

    public FileSystemMemoryStore(string workspace, ILogger<FileSystemMemoryStore> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // 初始化内存目录（workspace/memory）
        _memoryDir = Path.Combine(workspace.ExpandUser(), OpenClawConstants.MemoryDirName);
        _memoryDir.EnsureDirectoryExists();
        // 长期内存文件（memory/MEMORY.md）
        _longTermMemoryFile = Path.Combine(_memoryDir, OpenClawConstants.LongTermMemoryFileName);
        _logger.LogInformation("内存存储初始化完成 | 目录：{MemoryDir} | 长期内存文件：{LongTermFile}", _memoryDir, _longTermMemoryFile);
    }

    /// <summary>
    /// 获取格式化的今日日期字符串（YYYY-MM-DD）
    /// </summary>
    private string GetTodayDateString() => DateTime.Now.ToString("yyyy-MM-dd");

    public string GetTodayNotePath() => Path.Combine(_memoryDir, $"{GetTodayDateString()}.md");

    public async Task<string> ReadTodayNoteAsync(CancellationToken cancellationToken = default)
    {
        var todayFile = GetTodayNotePath();
        if (!File.Exists(todayFile))
        {
            _logger.LogDebug("今日笔记文件不存在 | {File}", todayFile);
            return string.Empty;
        }
        return await File.ReadAllTextAsync(todayFile, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendToTodayNoteAsync(string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.LogWarning("尝试追加空内容到今日笔记，忽略");
            return;
        }

        var todayFile = GetTodayNotePath();
        var finalContent = new StringBuilder();

        if (File.Exists(todayFile))
        {
            // 文件存在，追加内容
            var existing = await File.ReadAllTextAsync(todayFile, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            finalContent.AppendLine(existing);
        }
        else
        {
            // 文件不存在，添加日期头部
            finalContent.AppendLine($"# {GetTodayDateString()}");
            finalContent.AppendLine();
        }

        finalContent.AppendLine(content.Trim());
        await File.WriteAllTextAsync(todayFile, finalContent.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("成功追加内容到今日笔记 | {File}", todayFile);
    }

    public async Task<string> ReadLongTermMemoryAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_longTermMemoryFile))
        {
            _logger.LogDebug("长期内存文件不存在 | {File}", _longTermMemoryFile);
            return string.Empty;
        }
        return await File.ReadAllTextAsync(_longTermMemoryFile, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteLongTermMemoryAsync(string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.LogWarning("尝试写入空内容到长期内存，忽略");
            return;
        }

        await File.WriteAllTextAsync(_longTermMemoryFile, content.Trim(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("成功写入长期内存 | {File} | 内容长度：{Length}", _longTermMemoryFile, content.Length);
    }

    public async Task<string> GetRecentMemoriesAsync(int days = 7, CancellationToken cancellationToken = default)
    {
        if (days < 1) days = 7;
        var memories = new StringBuilder();
        var today = DateTime.Now;

        for (int i = 0; i < days; i++)
        {
            var targetDate = today.AddDays(-i);
            var dateStr = targetDate.ToString("yyyy-MM-dd");
            var noteFile = Path.Combine(_memoryDir, $"{dateStr}.md");

            if (File.Exists(noteFile))
            {
                var content = await File.ReadAllTextAsync(noteFile, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                memories.AppendLine(content);
                memories.AppendLine("---");
                memories.AppendLine();
            }
        }

        var result = memories.ToString().TrimEnd('-', '\n', '\r');
        _logger.LogDebug("获取最近{Days}天内存完成 | 内容长度：{Length}", days, result.Length);
        return result;
    }

    public async Task<string> GetMemoryContextAsync(CancellationToken cancellationToken = default)
    {
        var context = new StringBuilder();
        // 追加长期内存
        var longTerm = await ReadLongTermMemoryAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(longTerm))
        {
            context.AppendLine("## Long-term Memory");
            context.AppendLine(longTerm);
            context.AppendLine();
        }
        // 追加今日笔记
        var today = await ReadTodayNoteAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(today))
        {
            context.AppendLine("## Today's Notes");
            context.AppendLine(today);
        }
        return context.ToString().Trim();
    }
}