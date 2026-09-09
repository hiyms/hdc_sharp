using System.Numerics;
using System.Security.Cryptography;
using HdcSharp.Protocol;
using HdcSharp.Security;
using Xunit;

namespace HdcSharp.Tests.Security;

public class RsaRawTests
{
    [Fact]
    public void Pkcs1PrivateEncrypt_PublicOpRestoresBlock()
    {
        using var rsa = RSA.Create(2048);
        RSAParameters parameters = rsa.ExportParameters(true);
        byte[] token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"u8.ToArray();
        int modulusLength = (rsa.KeySize + 7) / 8;

        byte[] signature = RsaRaw.Pkcs1PrivateEncrypt(rsa, token);

        Assert.Equal(modulusLength, signature.Length);
        BigInteger n = ToBigInteger(parameters.Modulus!, true);
        BigInteger e = ToBigInteger(parameters.Exponent!, false);
        BigInteger restored = BigInteger.ModPow(ToBigInteger(signature, true), e, n);
        byte[] block = restored.ToByteArray(isBigEndian: true, isUnsigned: true);
        byte[] padded = new byte[modulusLength];
        block.CopyTo(padded, modulusLength - block.Length);
        Assert.Equal(BuildBlock(token, modulusLength), padded);
    }

    [Fact]
    public void Pkcs1PrivateEncrypt_Rsa3072_SixtyFourByteTokenRestoresBlock()
    {
        using var rsa = RSA.Create(3072);
        RSAParameters parameters = rsa.ExportParameters(true);
        byte[] token = new byte[64];
        Random.Shared.NextBytes(token);
        int modulusLength = (rsa.KeySize + 7) / 8;

        byte[] signature = RsaRaw.Pkcs1PrivateEncrypt(rsa, token);

        Assert.Equal(modulusLength, signature.Length);
        BigInteger n = ToBigInteger(parameters.Modulus!, true);
        BigInteger e = ToBigInteger(parameters.Exponent!, false);
        BigInteger restored = BigInteger.ModPow(ToBigInteger(signature, true), e, n);
        byte[] block = restored.ToByteArray(isBigEndian: true, isUnsigned: true);
        byte[] padded = new byte[modulusLength];
        block.CopyTo(padded, modulusLength - block.Length);
        Assert.Equal(BuildBlock(token, modulusLength), padded);
    }

    [Fact]
    public void Pkcs1PrivateEncrypt_DataExceedsCapacity_ThrowsHdcException()
    {
        using var rsa = RSA.Create(2048);
        byte[] oversized = new byte[246];                       // 模长 256 字节，容量上限 256-11=245
        Assert.Throws<HdcException>(() => RsaRaw.Pkcs1PrivateEncrypt(rsa, oversized));
    }

    [Fact]
    public void PssSign_VerifiesWithDotNet()
    {
        using var rsa = RSA.Create(2048);
        byte[] signature = RsaRaw.PssSign(rsa, "token"u8);
        Assert.True(rsa.VerifyData("token"u8.ToArray(), signature, HashAlgorithmName.SHA512, RSASignaturePadding.Pss));
    }

    private static BigInteger ToBigInteger(byte[] bigEndian, bool unsigned)
    {
        byte[] littleEndian = (byte[])bigEndian.Clone();
        Array.Reverse(littleEndian);
        if (unsigned)
        {
            return new BigInteger(littleEndian.Concat(new byte[] { 0 }).ToArray());
        }

        return new BigInteger(littleEndian);
    }

    private static byte[] BuildBlock(byte[] data, int modulusLength)
    {
        byte[] block = new byte[modulusLength];
        block[0] = 0x00;
        block[1] = 0x01;
        int paddingLength = modulusLength - 3 - data.Length;
        Array.Fill(block, (byte)0xFF, 2, paddingLength);
        block[2 + paddingLength] = 0x00;
        data.CopyTo(block, 3 + paddingLength);
        return block;
    }
}
