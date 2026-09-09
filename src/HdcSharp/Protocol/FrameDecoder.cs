using System.Buffers.Binary;
using HdcSharp.Protocol.Messages;

namespace HdcSharp.Protocol;

/// <summary>
/// 流式帧解码器：接收任意切分的 TCP 字节流，缓冲至帧完整后逐帧吐出（支持粘帧连续读取）。
/// 帧头魔数、协议版本、vCode 非法或单帧超长抛出 <see cref="HdcException"/>，此类错误不可恢复，调用方必须断连。
/// </summary>
public sealed class FrameDecoder
{
    /// <summary>单帧总长上限：1MiB - 1KiB，对齐原版接收缓冲 HDC_SOCKETPAIR_SIZE 的设计余量。</summary>
    private const long MaxFrameTotalLength = 1024 * 1024 - 1024;

    private const int InitialCapacity = 4096;

    private byte[] _buffer = new byte[InitialCapacity];
    private int _count;

    /// <summary>
    /// 追加本次到达的字节流片段，切分方式不限。
    /// </summary>
    /// <param name="data">字节流片段。</param>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        EnsureCapacity(_count + data.Length);
        data.CopyTo(_buffer.AsSpan(_count));
        _count += data.Length;
    }

    /// <summary>
    /// 尝试解出一个完整帧；帧不完整时返回 false 并保留缓冲，粘在一起的多帧可连续调用逐一取出。
    /// </summary>
    /// <param name="frame">解码得到的帧。</param>
    /// <returns>是否解出完整帧。</returns>
    /// <exception cref="HdcException">帧头魔数或协议版本非法、单帧总长超过上限，或 Protect 解析/vCode 校验失败时抛出。</exception>
    public bool TryRead(out Frame frame)
    {
        frame = default;
        if (_count < HdcConstants.PayloadHeadSize)
        {
            return false;
        }

        ValidateHead();
        ushort headSize = BinaryPrimitives.ReadUInt16BigEndian(_buffer.AsSpan(5, 2));
        uint dataSize = BinaryPrimitives.ReadUInt32BigEndian(_buffer.AsSpan(7, 4));
        long frameLength = (long)HdcConstants.PayloadHeadSize + headSize + dataSize;
        if (frameLength > MaxFrameTotalLength)
        {
            throw new HdcException($"单帧总长 {frameLength} 字节超过上限 {MaxFrameTotalLength} 字节，连接不可恢复");
        }

        if (_count < frameLength)
        {
            return false;
        }

        PayloadProtect protect = PayloadProtect.Parse(_buffer.AsSpan(HdcConstants.PayloadHeadSize, headSize));
        if (protect.VCode != HdcConstants.PayloadVCode)
        {
            throw new HdcException($"帧 vCode 为 0x{protect.VCode:X2}，应为 0x{HdcConstants.PayloadVCode:X2}，连接不可恢复");
        }

        byte[] payload = _buffer.AsSpan(HdcConstants.PayloadHeadSize + headSize, (int)dataSize).ToArray();
        int consumed = (int)frameLength;
        _count -= consumed;
        _buffer.AsSpan(consumed, _count).CopyTo(_buffer);
        frame = new Frame(protect.ChannelId, protect.Command, payload);
        return true;
    }

    private void ValidateHead()
    {
        if (_buffer[0] != FrameCodec.FlagBytes[0] || _buffer[1] != FrameCodec.FlagBytes[1])
        {
            throw new HdcException($"帧头魔数 0x{_buffer[0]:X2} 0x{_buffer[1]:X2} 非法，应为 \"{HdcConstants.PacketFlag}\"，连接不可恢复");
        }

        if (_buffer[4] != HdcConstants.ProtocolVer)
        {
            throw new HdcException($"帧协议版本 0x{_buffer[4]:X2} 不受支持，应为 0x{HdcConstants.ProtocolVer:X2}，连接不可恢复");
        }
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
        {
            return;
        }

        int newSize = _buffer.Length;
        while (newSize < required && newSize <= int.MaxValue / 2)
        {
            newSize *= 2;
        }

        Array.Resize(ref _buffer, Math.Max(newSize, required));
    }
}
