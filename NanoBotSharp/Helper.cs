#nullable enable
using OpenClawSharp.Session;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace OpenClawSharp.Core;

/// <summary>
/// 通用路径辅助类（替代Python的Path工具，跨平台兼容）
/// </summary>
public static class PathHelper
{
    /// <summary>
    /// 展开路径中的~为当前用户目录（对标Python Path.expanduser()）
    /// </summary>
    public static string ExpandUser(this string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (!path.StartsWith("~", StringComparison.Ordinal)) return Path.GetFullPath(path);

        var userHome = Environment.OSVersion.Platform == PlatformID.Win32NT
            ? Environment.GetEnvironmentVariable("USERPROFILE")
            : Environment.GetEnvironmentVariable("HOME");
        return string.IsNullOrEmpty(userHome)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path.Replace("~", userHome, StringComparison.Ordinal));
    }

    /// <summary>
    /// 确保目录存在（不存在则递归创建）
    /// </summary>
    public static void EnsureDirectoryExists(this string dirPath)
    {
        if (!Directory.Exists(dirPath)) Directory.CreateDirectory(dirPath);
    }
}

/// <summary>
/// XML辅助类（转义/解析）
/// </summary>
public static class XmlHelper
{
    /// <summary>
    /// XML特殊字符转义（& < > " '）
    /// </summary>
    public static string Escape(this string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;
        return XmlConvert.EncodeName(content)
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">");
    }
}

/// <summary>
/// JSON序列化辅助类（统一配置，避免重复创建JsonSerializerOptions）
/// </summary>
public static class JsonHelper
{
    public static readonly JsonSerializerOptions DefaultOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };

    static JsonHelper()
    {
        // 添加AuthorRole自定义转换器
        DefaultOptions.Converters.Add(new AuthorRoleJsonConverter());
        // 可选：添加其他常用转换器
        DefaultOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, DefaultOptions);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, DefaultOptions);
}

/// <summary>
/// 业务基础异常（所有自定义异常的基类）
/// </summary>
public class OpenClawException : Exception
{
    public OpenClawException(string message) : base(message) { }
    public OpenClawException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// 工具执行异常
/// </summary>
/// <summary>
/// 工具执行异常
/// </summary>
public class ToolExecutionException : OpenClawException
{
    public string ToolName { get; }
    public ToolExecutionException(string toolName, string message) : base(message) => ToolName = toolName;
    public ToolExecutionException(string toolName, string message, Exception innerException) : base(message, innerException) => ToolName = toolName;
}

/// <summary>
/// 技能加载异常
/// </summary>
public class SkillLoadException : OpenClawException
{
    public string SkillName { get; }
    public SkillLoadException(string skillName, string message) : base(message) => SkillName = skillName;
    public SkillLoadException(string skillName, string message, Exception innerException) : base(message, innerException) => SkillName = skillName;
}

/// <summary>
/// LLM调用异常
/// </summary>
public class LlmCallException : OpenClawException
{
    public string Model { get; }
    public LlmCallException(string model, string message) : base(message) => Model = model;
    public LlmCallException(string model, string message, Exception innerException) : base(message, innerException) => Model = model;
}

/// <summary>
/// 通用常量定义（移除原代码中的魔法值）
/// </summary>
public static class OpenClawConstants
{
    // 队列/超时
    public const int ConsumeTimeoutSeconds = 1;
    public const int DefaultMaxIterations = 20;
    public const int SubagentMaxIterations = 15;
    // 技能/工具
    public const string SkillFileName = "SKILL.md";
    public const string ConsoleChannelName = "console";
    public const string SystemChannelName = "system";
    // 会话/内存
    public const string DefaultSessionKey = "cli:direct";
    public const string MemoryDirName = "memory";
    public const string SkillsDirName = "skills";
    public const string LongTermMemoryFileName = "MEMORY.md";
    // 退出指令
    public static readonly HashSet<string> ExitCommands = new(StringComparer.OrdinalIgnoreCase) { "exit", "quit", "退出" };
    // LLM默认配置
    public const string DefaultOllamaModel = "qwen3:4b-instruct-2507-q4_K_M";
    public const string DefaultOllamaApiBase = "http://localhost:11434";
    public const int DefaultLlmMaxTokens = 4096;
    public const float DefaultLlmTemperature = 0.7f;
    public const string SessionsDirName = "sessions"; // 新增
}