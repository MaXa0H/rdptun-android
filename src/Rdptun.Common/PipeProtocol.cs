using System;
using System.IO;
using System.Text;

namespace Rdptun.Common
{
    public enum PipeFrameType : byte
    {
        Status = 1,
        PacketFromDvc = 2,
        PacketToDvc = 3,
        DvcOpened = 4,
        DvcClosed = 5
    }

    public sealed class PipeFrame
    {
        public PipeFrameType Type { get; private set; }
        public byte[] Payload { get; private set; }

        public PipeFrame(PipeFrameType type, byte[] payload)
        {
            Type = type;
            Payload = payload ?? new byte[0];
        }
    }

    public static class PipeProtocol
    {
        private const string DefaultPipeName = "rdptun-control";
        private const int MaxFrame = 1024 * 1024;

        public static string PipeName
        {
            get
            {
                string value = Environment.GetEnvironmentVariable("RDPTUN_PIPE");
                return string.IsNullOrWhiteSpace(value) ? DefaultPipeName : value;
            }
        }

        public static string TracePath
        {
            get
            {
                string value = Environment.GetEnvironmentVariable("RDPTUN_TRACE");
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
                return Path.Combine(Path.GetTempPath(), "rdptun-plugin.log");
            }
        }

        public static PipeFrame ReadFrame(Stream stream)
        {
            byte[] header = new byte[5];
            if (!ReadExact(stream, header, 0, header.Length))
                return null;

            int length = header[1] | (header[2] << 8) | (header[3] << 16) | (header[4] << 24);
            if (length < 0 || length > MaxFrame)
                throw new InvalidDataException("Invalid pipe frame length: " + length);

            byte[] payload = new byte[length];
            if (length != 0 && !ReadExact(stream, payload, 0, length))
                return null;

            return new PipeFrame((PipeFrameType)header[0], payload);
        }

        public static void WriteFrame(Stream stream, PipeFrameType type, byte[] payload)
        {
            if (payload == null)
                payload = new byte[0];
            if (payload.Length > MaxFrame)
                throw new InvalidDataException("Pipe frame too large: " + payload.Length);

            int length = payload.Length;
            byte[] header = new byte[5];
            header[0] = (byte)type;
            header[1] = (byte)length;
            header[2] = (byte)(length >> 8);
            header[3] = (byte)(length >> 16);
            header[4] = (byte)(length >> 24);
            stream.Write(header, 0, header.Length);
            if (length != 0)
                stream.Write(payload, 0, length);
            stream.Flush();
        }

        public static byte[] Utf8(string text)
        {
            return Encoding.UTF8.GetBytes(text ?? string.Empty);
        }

        public static string Utf8(byte[] data)
        {
            return Encoding.UTF8.GetString(data ?? new byte[0]);
        }

        private static bool ReadExact(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read <= 0)
                    return false;
                offset += read;
                count -= read;
            }
            return true;
        }
    }
}
