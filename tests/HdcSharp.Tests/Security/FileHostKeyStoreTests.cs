using System.Security.Cryptography;
using HdcSharp.Security;
using Xunit;

namespace HdcSharp.Tests.Security;

public class FileHostKeyStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hdcsharp-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GeneratesAndReloadsKeyPair()
    {
        string pem1;
        using (var ks = new FileHostKeyStore(_dir))
        {
            pem1 = ks.GetPublicKeyPem();
            Assert.StartsWith("-----BEGIN PUBLIC KEY-----", pem1);
            Assert.Equal(625, pem1.Length);                       // RSA-3072 SPKI PEM 恒 625 字符
        }
        using var ks2 = new FileHostKeyStore(_dir);               // 重载，不重新生成
        Assert.Equal(pem1, ks2.GetPublicKeyPem());
        Assert.True(File.Exists(Path.Combine(_dir, "hdckey")));
        Assert.True(File.Exists(Path.Combine(_dir, "hdckey.pub")));
    }

    [Fact]
    public void GetPrivateKey_RepeatedCalls_ReturnsNewRsa3072ConsistentWithPem()
    {
        using var ks = new FileHostKeyStore(_dir);
        using var rsa1 = ks.GetPrivateKey();
        using var rsa2 = ks.GetPrivateKey();
        Assert.NotSame(rsa1, rsa2);                               // 每次返回全新独立实例
        Assert.Equal(3072, rsa1.KeySize);
        Assert.Equal(625, ks.GetPublicKeyPem().Length);
        Assert.Equal(ks.GetPublicKeyPem(), rsa1.ExportSubjectPublicKeyInfoPem() + "\n");
    }

    [Fact]
    public void Constructor_CorruptedPrivateKeyFile_Throws()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "hdckey"), "not a valid pem");
        Assert.Throws<CryptographicException>(() => new FileHostKeyStore(_dir));
    }
}
