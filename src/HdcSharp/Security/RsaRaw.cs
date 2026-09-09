using System.Numerics;
using System.Security.Cryptography;
using HdcSharp.Protocol;

namespace HdcSharp.Security;

/// <summary>
/// HDC 认证所需的 RSA 底层原语：PKCS#1 v1.5 块类型 1 私钥运算（旧式认证，daemon 用公钥幂运算
/// 还原后逐字节比对原文 token）与 PSS+SHA512 签名（新式认证）。私钥幂运算以 BigInteger CRT
/// 手工实现（.NET RSA 类未暴露该原语且 SignData 会先哈希），AOT 安全且不引入第三方依赖（spec §7.2/§7.3）。
/// </summary>
public static class RsaRaw
{
    /// <summary>
    /// 以 PKCS#1 v1.5 块类型 1 对原文执行私钥幂运算（等价 OpenSSL <c>RSA_private_encrypt</c>）：
    /// 构造块 <c>0x00 0x01 PS(0xFF×k) 0x00 || data</c>（k = 模长 - 3 - |data|），经 CRT 加速模幂后
    /// 返回与模长等长的大端签名字节数组。daemon 侧以公钥幂运算还原块并逐字节比对原文。
    /// </summary>
    /// <param name="rsa">持有私钥的 RSA 实例。</param>
    /// <param name="data">待签名原文（daemon 下发的 token，最长 64 字节）。</param>
    /// <returns>与密钥模长等长的大端签名字节数组。</returns>
    /// <exception cref="HdcException">data 超过模长-11 字节（PKCS#1 块至少需 8 字节 PS 填充），或密钥缺少私钥参数。</exception>
    public static byte[] Pkcs1PrivateEncrypt(RSA rsa, ReadOnlySpan<byte> data)
    {
        RSAParameters parameters = rsa.ExportParameters(true);
        if (parameters.Modulus is null || parameters.P is null || parameters.Q is null
            || parameters.DP is null || parameters.DQ is null || parameters.InverseQ is null)
        {
            throw new HdcException("RSA 密钥缺少私钥参数，无法执行 PKCS1 块类型 1 私钥运算");
        }

        int modulusLength = parameters.Modulus.Length;
        if (data.Length > modulusLength - 11)
        {
            throw new HdcException($"待签名数据长度 {data.Length} 超过 PKCS1 块类型 1 容量 {modulusLength - 11}");
        }

        BigInteger c = FromBigEndian(BuildType1Block(data, modulusLength));
        BigInteger p = FromBigEndian(parameters.P);
        BigInteger q = FromBigEndian(parameters.Q);
        BigInteger m1 = BigInteger.ModPow(c, FromBigEndian(parameters.DP), p);
        BigInteger m2 = BigInteger.ModPow(c, FromBigEndian(parameters.DQ), q);
        BigInteger h = FromBigEndian(parameters.InverseQ) * (m1 - m2) % p;
        if (h < 0)
        {
            // BigInteger 对负被除数取模结果仍为负，须加回 P 归一化到 [0, P)
            h += p;
        }

        BigInteger signature = m2 + h * q;
        return ToFixedLengthBigEndian(signature, modulusLength);
    }

    /// <summary>
    /// 标准 PSS+SHA512 签名（新式认证）：daemon 侧按 salt 长度自适应（saltlen=auto）校验。
    /// </summary>
    /// <param name="rsa">持有私钥的 RSA 实例。</param>
    /// <param name="data">待签名原文。</param>
    /// <returns>与密钥模长等长的大端签名字节数组。</returns>
    public static byte[] PssSign(RSA rsa, ReadOnlySpan<byte> data)
        => rsa.SignData(data.ToArray(), HashAlgorithmName.SHA512, RSASignaturePadding.Pss);

    private static byte[] BuildType1Block(ReadOnlySpan<byte> data, int modulusLength)
    {
        byte[] block = new byte[modulusLength];
        block[0] = 0x00;
        block[1] = 0x01;
        int paddingLength = modulusLength - 3 - data.Length;
        Array.Fill(block, (byte)0xFF, 2, paddingLength);
        block[2 + paddingLength] = 0x00;
        data.CopyTo(block.AsSpan(3 + paddingLength));
        return block;
    }

    private static BigInteger FromBigEndian(byte[] bigEndian)
    {
        // 末位再补一个 0x00（对应 little-endian 最高位），保证解析为正数
        byte[] littleEndian = new byte[bigEndian.Length + 1];
        for (int i = 0; i < bigEndian.Length; i++)
        {
            littleEndian[i] = bigEndian[bigEndian.Length - 1 - i];
        }

        return new BigInteger(littleEndian);
    }

    private static byte[] ToFixedLengthBigEndian(BigInteger value, int length)
    {
        byte[] littleEndian = value.ToByteArray();
        int magnitudeLength = littleEndian.Length;

        // 正数的 ToByteArray 末尾可能带符号填充 0x00（幅值最高位为 1 时），不属于幅值，剥离后再反转为大端
        while (magnitudeLength > 1 && littleEndian[magnitudeLength - 1] == 0x00)
        {
            magnitudeLength--;
        }

        byte[] result = new byte[length];
        for (int i = 0; i < magnitudeLength; i++)
        {
            result[length - magnitudeLength + i] = littleEndian[magnitudeLength - 1 - i];
        }

        return result;
    }
}
