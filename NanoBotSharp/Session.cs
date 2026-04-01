#nullable enable
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenClawSharp.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawSharp.Session;

/// <summary>
/// 会话接口（存储SK ChatMessageContent）
/// </summary>
public interface ISession
{
    /// <summary>
    /// 会话唯一标识（格式：Channel:ChatId）
    /// </summary>
    string Key { get; }

    /// <summary>
    /// 会话创建时间（UTC）
    /// </summary>
    DateTime CreatedAt { get; }

    /// <summary>
    /// 会话最后更新时间（UTC）
    /// </summary>
    DateTime UpdatedAt { get; }

    /// <summary>
    /// 会话消息列表（SK原生类型）
    /// </summary>
    IReadOnlyList<ChatMessageContent> Messages { get; }

    /// <summary>
    /// 会话元数据
    /// </summary>
    IDictionary<string, object> Metadata { get; }

    /// <summary>
    /// 添加消息到会话
    /// </summary>
    void AddMessage(ChatMessageContent message);

    /// <summary>
    /// 获取会话历史（供LLM上下文使用）
    /// </summary>
    List<ChatMessageContent> GetHistory(int maxMessages = 50);

    /// <summary>
    /// 清空会话消息
    /// </summary>
    void Clear();
}

/// <summary>
/// 会话实现（存储SK ChatMessageContent）
/// </summary>
public class Session : ISession
{
    private readonly List<ChatMessageContent> _messages = new();
    private readonly object _lockObj = new();

    public Session(string key)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key), "会话标识不能为空");
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
        Metadata = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    }

    public string Key { get; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public IReadOnlyList<ChatMessageContent> Messages => _messages.AsReadOnly();
    public IDictionary<string, object> Metadata { get; }

    public void AddMessage(ChatMessageContent message)
    {
        if (message == null) throw new ArgumentNullException(nameof(message));
        lock (_lockObj)
        {
            _messages.Add(message);
            UpdatedAt = DateTime.UtcNow;
        }
    }

    public List<ChatMessageContent> GetHistory(int maxMessages = 50)
    {
        if (maxMessages < 1) maxMessages = 50;
        lock (_lockObj)
        {
            return _messages.Count > maxMessages
                ? _messages.Skip(_messages.Count - maxMessages).ToList()
                : _messages.ToList();
        }
    }

    public void Clear()
    {
        lock (_lockObj)
        {
            _messages.Clear();
            UpdatedAt = DateTime.UtcNow;
        }
    }
}

/// <summary>
/// 会话管理器接口（适配SK ChatMessageContent）
/// </summary>
public interface ISessionManager
{
    /// <summary>
    /// 获取或创建会话（核心方法）
    /// </summary>
    Task<ISession> GetOrCreateAsync(string sessionKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存会话到持久化存储（JSONL）
    /// </summary>
    Task SaveAsync(ISession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除会话（内存+文件）
    /// </summary>
    Task<bool> DeleteAsync(string sessionKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出所有会话（仅返回元数据，优化性能）
    /// </summary>
    Task<List<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 会话摘要（列出会话时的轻量模型）
/// </summary>
public class SessionSummary
{
    public string Key { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string FilePath { get; set; }
    public int MessageCount { get; set; } = 0;
}

/// <summary>
/// 会话管理器实现（适配SK ChatMessageContent）
/// </summary>
public class FileSystemSessionManager : ISessionManager
{
    private readonly string _sessionsDir;
    private readonly Dictionary<string, ISession> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<FileSystemSessionManager> _logger;
    private readonly object _cacheLock = new();

    public FileSystemSessionManager(string workspace, ILogger<FileSystemSessionManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // 会话目录（workspace/.nanobot/sessions）
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _sessionsDir = Path.Combine(userHome, ".nanobot", OpenClawConstants.SessionsDirName);
        _sessionsDir.EnsureDirectoryExists();
        _logger.LogInformation("会话管理器初始化完成 | 持久化目录：{SessionsDir}", _sessionsDir);
    }

    /// <summary>
    /// 生成安全的会话文件名（替换:为_，过滤非法字符）
    /// </summary>
    private string GetSessionFilePath(string sessionKey)
    {
        var safeKey = new string(sessionKey.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return Path.Combine(_sessionsDir, $"{safeKey}.jsonl");
    }

    /// <summary>
    /// 从JSONL文件加载会话（适配SK ChatMessageContent）
    /// </summary>
    private async Task<ISession?> LoadFromFileAsync(string sessionKey, CancellationToken cancellationToken = default)
    {
        var filePath = GetSessionFilePath(sessionKey);
        if (!File.Exists(filePath)) return null;

        try
        {
            var session = new Session(sessionKey);
            using var reader = new StreamReader(filePath, Encoding.UTF8);
            string? line;
            bool isFirstLine = true;

            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
            {
                line = line.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                // 第一行是元数据
                if (isFirstLine)
                {
                    var meta = JsonHelper.Deserialize<Dictionary<string, object>>(line);
                    if (meta == null) continue;

                    if (meta.TryGetValue("created_at", out var caObj) && DateTime.TryParse(caObj.ToString(), out var ca))
                        session.CreatedAt = ca;
                    if (meta.TryGetValue("updated_at", out var uaObj) && DateTime.TryParse(uaObj.ToString(), out var ua))
                        session.UpdatedAt = ua;
                    if (meta.TryGetValue("metadata", out var metaObj) && metaObj is Dictionary<string, object> metaDict)
                    {
                        foreach (var kv in metaDict)
                            session.Metadata[kv.Key] = kv.Value;
                    }
                    isFirstLine = false;
                }
                else
                {
                    // 后续行是SK ChatMessageContent
                    var msg = JsonHelper.Deserialize<ChatMessageContent>(line);
                    if (msg != null) session.AddMessage(msg);
                }
            }

            _logger.LogDebug("从文件加载会话成功 | {Key} | {FilePath} | 消息数：{Count}",
                sessionKey, filePath, session.Messages.Count);
            return session;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "从文件加载会话失败 | {Key} | {FilePath}", sessionKey, filePath);
            return null;
        }
    }

    /// <summary>
    /// 将会话保存到JSONL文件（适配SK ChatMessageContent）
    /// </summary>
    private async Task SaveToFileAsync(ISession session, CancellationToken cancellationToken = default)
    {
        var filePath = GetSessionFilePath(session.Key);
        try
        {
            using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
            // 第一行：元数据
            var meta = new Dictionary<string, object>
            {
                ["_type"] = "metadata",
                ["created_at"] = session.CreatedAt.ToString("o"),
                ["updated_at"] = session.UpdatedAt.ToString("o"),
                ["metadata"] = session.Metadata
            };
            await writer.WriteLineAsync(JsonHelper.Serialize(meta)).ConfigureAwait(false);

            // 后续行：SK ChatMessageContent
            foreach (var msg in session.Messages)
            {
                await writer.WriteLineAsync(JsonHelper.Serialize(msg)).ConfigureAwait(false);
            }

            _logger.LogDebug("会话保存成功 | {Key} | {FilePath} | 消息数：{Count}",
                session.Key, filePath, session.Messages.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "会话保存失败 | {Key} | {FilePath}", session.Key, filePath);
            throw new OpenClawException($"保存会话{session.Key}失败", ex);
        }
    }

    public async Task<ISession> GetOrCreateAsync(string sessionKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionKey))
            throw new ArgumentException("会话标识不能为空", nameof(sessionKey));

        // 先从内存缓存获取
        lock (_cacheLock)
        {
            if (_memoryCache.TryGetValue(sessionKey, out var session))
                return session;
        }

        // 缓存无则从文件加载，加载失败则创建新会话
        var loaded = await LoadFromFileAsync(sessionKey, cancellationToken).ConfigureAwait(false);
        var newSession = loaded ?? new Session(sessionKey);

        // 写入缓存
        lock (_cacheLock)
        {
            _memoryCache[sessionKey] = newSession;
        }

        _logger.LogDebug("获取/创建会话 | {Key} | 缓存会话数：{CacheCount}",
            sessionKey, _memoryCache.Count);
        return newSession;
    }

    public async Task SaveAsync(ISession session, CancellationToken cancellationToken = default)
    {
        if (session == null) throw new ArgumentNullException(nameof(session));
        // 先保存到文件，再更新缓存
        await SaveToFileAsync(session, cancellationToken).ConfigureAwait(false);
        lock (_cacheLock)
        {
            _memoryCache[session.Key] = session;
        }
    }

    public async Task<bool> DeleteAsync(string sessionKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionKey))
            throw new ArgumentException("会话标识不能为空", nameof(sessionKey));

        // 先从缓存移除
        lock (_cacheLock)
        {
            _memoryCache.Remove(sessionKey);
        }

        // 再删除文件
        var filePath = GetSessionFilePath(sessionKey);
        if (!File.Exists(filePath))
        {
            _logger.LogDebug("会话文件不存在，无需删除 | {Key}", sessionKey);
            return false;
        }

        try
        {
            File.Delete(filePath);
            _logger.LogInformation("会话删除成功 | {Key} | {FilePath}", sessionKey, filePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "会话文件删除失败 | {Key} | {FilePath}", sessionKey, filePath);
            throw new OpenClawException($"删除会话{sessionKey}失败", ex);
        }
    }

    public async Task<List<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        var summaries = new List<SessionSummary>();
        if (!Directory.Exists(_sessionsDir)) return summaries;

        foreach (var file in Directory.EnumerateFiles(_sessionsDir, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // 仅读取第一行元数据，优化性能
                using var reader = new StreamReader(file, Encoding.UTF8);
                var firstLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(firstLine)) continue;

                var meta = JsonHelper.Deserialize<Dictionary<string, object>>(firstLine);
                if (meta == null || !meta.TryGetValue("_type", out var typeObj) || typeObj.ToString() != "metadata")
                    continue;

                // 还原会话标识（_替换为:）
                var fileName = Path.GetFileNameWithoutExtension(file);
                var sessionKey = fileName.Replace("_", ":", StringComparison.Ordinal);

                // 解析元数据
                DateTime.TryParse(meta.TryGetValue("created_at", out var caObj) ? caObj.ToString() : null, out var createdAt);
                DateTime.TryParse(meta.TryGetValue("updated_at", out var uaObj) ? uaObj.ToString() : null, out var updatedAt);

                summaries.Add(new SessionSummary
                {
                    Key = sessionKey,
                    CreatedAt = createdAt == default ? DateTime.UtcNow : createdAt,
                    UpdatedAt = updatedAt == default ? DateTime.UtcNow : updatedAt,
                    FilePath = file
                });
            }
            catch (Exception)
            {
                // 跳过损坏的文件
                continue;
            }
        }

        // 按最后更新时间倒序
        return summaries.OrderByDescending(s => s.UpdatedAt).ToList();
    }
}
/// <summary>
/// AuthorRole枚举的自定义JSON转换器（兼容字符串/对象格式）
/// </summary>
public class AuthorRoleJsonConverter : JsonConverter<AuthorRole>
{
    // 反序列化：兼容 "user" 或 {"Label":"user"} 格式
    public override AuthorRole Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // 情况1：JSON是字符串（正确格式）
        if (reader.TokenType == JsonTokenType.String)
        {
            var roleStr = reader.GetString()?.Trim().ToLowerInvariant();
            return roleStr switch
            {
                "user" => AuthorRole.User,
                "assistant" => AuthorRole.Assistant,
                "system" => AuthorRole.System,
                "tool" => AuthorRole.Tool,
                _ => AuthorRole.User // 默认值
            };
        }

        // 情况2：JSON是对象（旧格式 {"Label":"user"}）
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            string? label = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType == JsonTokenType.PropertyName && reader.GetString() == "Label")
                {
                    reader.Read();
                    label = reader.GetString()?.Trim().ToLowerInvariant();
                }
            }
            return label switch
            {
                "user" => AuthorRole.User,
                "assistant" => AuthorRole.Assistant,
                "system" => AuthorRole.System,
                "function" => AuthorRole.Tool,
                _ => AuthorRole.User // 默认值
            };
        }

        // 其他情况返回默认值
        return AuthorRole.User;
    }

    // 序列化：将枚举转为小写字符串（如 AuthorRole.User → "user"）
    public override void Write(Utf8JsonWriter writer, AuthorRole value, JsonSerializerOptions options)
    {
        string roleStr = string.Empty;
        if (value == AuthorRole.User)
        {
            roleStr = "user";
        }
        else if (value == AuthorRole.Assistant)
        {
            roleStr = "assistant";
        }
        else if (value == AuthorRole.System)
        {
            roleStr = "system";
        }
        else if (value == AuthorRole.Tool)
        {
            roleStr = "tool";
        }
        writer.WriteStringValue(roleStr);
    }
}