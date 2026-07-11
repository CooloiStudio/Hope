using System.IO;
using System.Security.Cryptography;

namespace Hope.Desktop.Services;

/// <summary>文件内容 SHA-256（大写 hex），供更新校验与单测复用。</summary>
public static class FileSha256
{
    public static string ComputeHex(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    /// <summary>期望哈希非空且与文件一致（忽略大小写）时返回 true。</summary>
    public static bool Matches(string path, string? expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex)) return false;
        return ComputeHex(path).Equals(expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
