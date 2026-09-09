using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using HdcSharp.Protocol.Messages;

namespace HdcSharp.Protocol;

/// <summary>
/// 解码得到的完整帧。
/// </summary>
/// <param name="ChannelId">帧所属通道号。</param>
/// <param name="Command">帧命令字。</param>
/// <param name="Payload">命令负载数据。</param>
public readonly record struct Frame(uint ChannelId, HdcCommand Command, byte[] Payload);

/// <summary>
/// HDC 帧编码器：组装 PayloadHead + PayloadProtect + 负载 的完整帧。
/// headSize 取实际序列化后的 PayloadProtect 长度（channelId 极大时 varint 变长，故不可硬编码）。
/// </summary>
public static class FrameCodec
{
    /// <summary>帧头魔数 "HW" 的 ASCII 字节，与官方 hdc 宿主/守护实现字节级一致（供帧解码器共享校验）。</summary>
    internal static readonly byte[] FlagBytes = Encoding.ASCII.GetBytes(HdcConstants.PacketFlag);

    /// <summary>
    /// 将一帧编码为完整字节数组。
    /// </summary>
    /// <param name="channelId">通道号。</param>
    /// <param name="cmd">命令字。</param>
    /// <param name="payload">命令负载。</param>
    /// <returns>完整帧字节（11 字节头 + Protect + 负载）。</returns>
    public static byte[] Encode(uint channelId, HdcCommand cmd, ReadOnlySpan<byte> payload)
    {
        int total = GetFrameLength(channelId, cmd, payload.Length, out byte[] protect);
        var frame = new byte[total];
        WriteFrame(frame, protect, payload);
        return frame;
    }

    /// <summary>
    /// 将一帧编码写入 <see cref="IBufferWriter{T}"/>，供写路径复用缓冲，避免逐帧分配中间数组。
    /// </summary>
    /// <param name="channelId">通道号。</param>
    /// <param name="cmd">命令字。</param>
    /// <param name="payload">命令负载。</param>
    /// <param name="sink">帧字节写入目标。</param>
    public static void Encode(uint channelId, HdcCommand cmd, ReadOnlySpan<byte> payload, IBufferWriter<byte> sink)
    {
        int total = GetFrameLength(channelId, cmd, payload.Length, out byte[] protect);
        Span<byte> span = sink.GetSpan(total);
        WriteFrame(span, protect, payload);
        sink.Advance(total);
    }

    private static int GetFrameLength(uint channelId, HdcCommand cmd, int payloadLength, out byte[] protect)
    {
        protect = new PayloadProtect { ChannelId = channelId, Command = cmd }.Serialize();
        return HdcConstants.PayloadHeadSize + protect.Length + payloadLength;
    }

    private static void WriteFrame(Span<byte> destination, byte[] protect, ReadOnlySpan<byte> payload)
    {
        WriteHead(destination, protect.Length, payload.Length);
        protect.CopyTo(destination.Slice(HdcConstants.PayloadHeadSize));
        payload.CopyTo(destination.Slice(HdcConstants.PayloadHeadSize + protect.Length));
    }

    private static void WriteHead(Span<byte> head, int headSize, int dataSize)
    {
        head[0] = FlagBytes[0];
        head[1] = FlagBytes[1];
        head[2] = 0;
        head[3] = 0;
        head[4] = HdcConstants.ProtocolVer;
        // 大端序：官方实现 headSize/dataSize 恒按大端写出
        BinaryPrimitives.WriteUInt16BigEndian(head.Slice(5, 2), (ushort)headSize);
        BinaryPrimitives.WriteUInt32BigEndian(head.Slice(7, 4), (uint)dataSize);
    }
}
