using System.Security.Cryptography;

namespace HdcSharp.Security;

/// <summary>
/// 主机密钥库接口，抽象宿主 RSA 密钥对的来源；消费端可替换为内存实现或自定义目录实现（spec §7.1）。
/// </summary>
public interface IHostKeyStore
{
    /// <summary>获取宿主 RSA 私钥。实现必须线程安全；每次调用返回全新独立实例，调用方负责 Dispose。</summary>
    /// <returns>新建的 RSA 私钥实例（约定为 RSA-3072）。</returns>
    RSA GetPrivateKey();

    /// <summary>获取 SPKI PEM 格式公钥。</summary>
    /// <returns>以 -----BEGIN PUBLIC KEY----- 开头、结尾带换行的 PEM 字符串；RSA-3072 密钥时恒 625 字符（与官方 hdc 密钥文件字节一致）。</returns>
    string GetPublicKeyPem();
}
