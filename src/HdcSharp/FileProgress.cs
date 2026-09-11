namespace HdcSharp;

/// <summary>
/// 文件传输进度快照：每完成一个数据块回调一次，<see cref="BytesTransferred"/> 为累计值。
/// </summary>
/// <param name="BytesTransferred">已传输字节数（累计，单调不减）。</param>
/// <param name="TotalBytes">本次文件总字节数；接收端在收到 FILE_CHECK 后才有值。</param>
/// <param name="FileName">正在传输的文件名（发送为本地文件名，接收为设备端文件名）。</param>
public readonly record struct FileProgress(long BytesTransferred, long? TotalBytes, string FileName);
