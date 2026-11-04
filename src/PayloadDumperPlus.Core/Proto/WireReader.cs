using System.Text;

namespace PayloadDumperPlus.Core.Proto;

/// <summary>
/// 极简 protobuf wire-format 读取器。
/// 只实现读取，不依赖 Google.Protobuf / protoc：本工具只需要读 manifest，
/// 字段固定，手写解析可以省掉整个代码生成链路。
/// </summary>
internal ref struct WireReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    public WireReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    public bool End => _position >= _buffer.Length;

    public int Position => _position;

    public ulong ReadVarint()
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            if (_position >= _buffer.Length)
                throw new InvalidDataException("protobuf: 变长整数在缓冲末尾处截断");

            byte b = _buffer[_position++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;

            shift += 7;
            if (shift >= 64)
                throw new InvalidDataException("protobuf: 变长整数超过 64 位");
        }
    }

    public (int Field, int WireType) ReadTag()
    {
        ulong tag = ReadVarint();
        return ((int)(tag >> 3), (int)(tag & 0x07));
    }

    public ReadOnlySpan<byte> ReadLengthDelimited()
    {
        ulong length = ReadVarint();
        int remaining = _buffer.Length - _position;
        if (length > (ulong)remaining)
            throw new InvalidDataException(
                $"protobuf: 长度前缀 {length} 超过剩余 {remaining} 字节");

        ReadOnlySpan<byte> slice = _buffer.Slice(_position, (int)length);
        _position += (int)length;
        return slice;
    }

    public string ReadString()
    {
        ReadOnlySpan<byte> bytes = ReadLengthDelimited();
        return Encoding.UTF8.GetString(bytes);
    }

    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case 0:
                ReadVarint();
                break;
            case 1:
                Require(8);
                _position += 8;
                break;
            case 2:
                ReadLengthDelimited();
                break;
            case 5:
                Require(4);
                _position += 4;
                break;
            default:
                throw new InvalidDataException($"protobuf: 不支持的 wire type {wireType}");
        }
    }

    private void Require(int count)
    {
        if (_buffer.Length - _position < count)
            throw new InvalidDataException("protobuf: 字段越界");
    }
}
