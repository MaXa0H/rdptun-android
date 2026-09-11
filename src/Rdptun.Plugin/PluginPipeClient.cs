using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using Rdptun.Common;

namespace Rdptun.Plugin
{
    internal sealed class PluginPipeClient : IDisposable
    {
        private readonly object _writeLock = new object();
        private volatile bool _running;
        private Thread _thread;
        private NamedPipeClientStream _pipe;

        public event Action<byte[]> PacketToDvc;

        public static string TracePath
        {
            get { return Path.Combine(Path.GetTempPath(), "rdptun-plugin.log"); }
        }

        private static void Trace(string text)
        {
            try
            {
                File.AppendAllText(
                    TracePath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + " pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + " " + text + Environment.NewLine);
            }
            catch { }
        }

        public void Start()
        {
            Trace("PluginPipeClient.Start");
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "rdptun-plugin-pipe" };
            _thread.Start();
        }

        private void Loop()
        {
            while (_running)
            {
                try
                {
                    using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.None))
                    {
                        Trace("connecting IPC pipe");
                        pipe.Connect(1000);
                        _pipe = pipe;
                        Trace("IPC connected");
                        SendStatus("DVC plugin IPC connected");
                        while (_running && pipe.IsConnected)
                        {
                            PipeFrame frame = PipeProtocol.ReadFrame(pipe);
                            if (frame == null) break;
                            if (frame.Type == PipeFrameType.PacketToDvc)
                            {
                                Action<byte[]> handler = PacketToDvc;
                                if (handler != null) handler(frame.Payload);
                            }
                        }
                    }
                }
                catch (TimeoutException ex) { Trace("IPC timeout: " + ex.Message); }
                catch (IOException ex) { Trace("IPC IO error: " + ex.Message); }
                catch (Exception ex) { Trace("IPC error: " + ex); }
                finally { _pipe = null; }
                if (_running) Thread.Sleep(250);
            }
        }

        public void SendStatus(string text)
        {
            Trace("STATUS " + text);
            Send(PipeFrameType.Status, PipeProtocol.Utf8(text));
        }

        public void SendPacketFromDvc(byte[] packet) { Send(PipeFrameType.PacketFromDvc, packet); }
        public void SendDvcOpened() { Trace("DVC OPENED"); Send(PipeFrameType.DvcOpened, new byte[0]); }
        public void SendDvcClosed() { Trace("DVC CLOSED"); Send(PipeFrameType.DvcClosed, new byte[0]); }

        private void Send(PipeFrameType type, byte[] payload)
        {
            NamedPipeClientStream pipe = _pipe;
            if (pipe == null || !pipe.IsConnected) return;
            try
            {
                lock (_writeLock) PipeProtocol.WriteFrame(pipe, type, payload);
            }
            catch (Exception ex) { Trace("pipe send failed: " + ex.Message); }
        }

        public void Dispose()
        {
            Trace("PluginPipeClient.Dispose");
            _running = false;
            try { if (_pipe != null) _pipe.Dispose(); } catch { }
            if (_thread != null && _thread.IsAlive) _thread.Join(1000);
        }
    }
}
