using System.Security.Cryptography;

namespace HdcSharp.Security;

/// <summary>
/// 基于文件的宿主密钥库：私钥存 <c>hdckey</c>（PKCS#8 PEM），公钥存 <c>hdckey.pub</c>（SPKI PEM）。
/// 默认目录 ~/.harmony 与官方 hdc 完全共享，已授权过的设备免二次弹窗（spec §7.1）。
/// 密钥缺失时自动生成 RSA-3072（e=65537）并落盘；私钥文件损坏时抛 CryptographicException；
/// 公钥文件缺失或与私钥不一致时按私钥重导出补写。实例不可变且线程安全。
/// </summary>
public sealed class FileHostKeyStore : IHostKeyStore, IDisposable
{
    private const int RsaKeySizeBits = 3072;
    private const string PrivateKeyFileName = "hdckey";
    private const string PublicKeyFileName = "hdckey.pub";

    private readonly string _privateKeyPath;
    private readonly string _publicKeyPath;
    private readonly string _privateKeyPem;
    private readonly string _publicKeyPem;

    /// <summary>
    /// 创建密钥库并完成加载或生成：hdckey 存在则导入（导入即校验），否则生成 RSA-3072
    /// 并写出 hdckey 与 hdckey.pub；目录不存在时创建。
    /// </summary>
    /// <param name="baseDir">密钥目录；null 表示 ~/.harmony（与官方 hdc 相同）。</param>
    /// <exception cref="CryptographicException">hdckey 存在但内容不是有效 PEM 私钥。</exception>
    public FileHostKeyStore(string? baseDir = null)
    {
        string dir = baseDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harmony");
        _privateKeyPath = Path.Combine(dir, PrivateKeyFileName);
        _publicKeyPath = Path.Combine(dir, PublicKeyFileName);
        EnsureDirectory(dir);

        bool createNew = !File.Exists(_privateKeyPath);
        if (createNew)
        {
            using var generated = RSA.Create(RsaKeySizeBits);
            _privateKeyPem = WithTrailingNewline(generated.ExportPkcs8PrivateKeyPem());
            _publicKeyPem = WithTrailingNewline(generated.ExportSubjectPublicKeyInfoPem());
            WriteKeyFile(_privateKeyPath, _privateKeyPem);
        }
        else
        {
            _privateKeyPem = WithTrailingNewline(File.ReadAllText(_privateKeyPath));
            using var imported = RSA.Create();
            try
            {
                imported.ImportFromPem(_privateKeyPem);
            }
            catch (Exception ex)
            {
                throw new CryptographicException($"{_privateKeyPath} 不是有效的 PEM 私钥", ex);
            }
            _publicKeyPem = WithTrailingNewline(imported.ExportSubjectPublicKeyInfoPem());
        }
        WriteKeyFileIfChanged(_publicKeyPath, _publicKeyPem);
    }

    /// <summary>获取新的 RSA 私钥实例；每次调用独立创建、互不影响（线程安全），调用方负责 Dispose。</summary>
    /// <returns>新建的 RSA-3072 实例。</returns>
    public RSA GetPrivateKey()
    {
        RSA rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(_privateKeyPem);
            return rsa;
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    /// <summary>获取 SPKI PEM 公钥（构造时已由私钥导出并缓存，与 hdckey.pub 文件内容一致，结尾带换行）。</summary>
    /// <returns>以 -----BEGIN PUBLIC KEY----- 开头的 PEM 字符串，RSA-3072 时恒 625 字符（与官方 hdc 密钥文件字节一致）。</returns>
    public string GetPublicKeyPem() => _publicKeyPem;

    /// <summary>释放资源。实例不持有非托管资源，此方法为空实现（仅为支持 using 语法）。</summary>
    public void Dispose()
    {
    }

    private static void EnsureDirectory(string dir)
    {
        if (Directory.Exists(dir))
        {
            return;
        }
        Directory.CreateDirectory(dir);
#if !WINDOWS
        if (!OperatingSystem.IsWindows())
        {
            // 与官方 hdc 密钥目录权限 0750 对齐，防止同机其他用户读取
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }
#endif
    }

    private static string WithTrailingNewline(string pem) => pem.EndsWith('\n') ? pem : pem + "\n";

    private static void WriteKeyFile(string path, string content)
    {
        File.WriteAllText(path, content);
#if !WINDOWS
        if (!OperatingSystem.IsWindows())
        {
            // 与官方 hdc 密钥文件权限 0600 对齐，防止同机其他用户读取
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
#endif
    }

    private static void WriteKeyFileIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return;
        }
        WriteKeyFile(path, content);
    }
}
