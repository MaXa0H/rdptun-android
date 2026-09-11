// Structure adapted from Microsoft's rdp-dvc-plugin-samples (MIT).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace COMRegistration
{
    internal sealed class LocalServer : IDisposable
    {
        private static readonly ManualResetEventSlim ShutdownEvent = new ManualResetEventSlim(false);
        private readonly List<int> _cookies = new List<int>();
        private bool _comInitialized;

        public LocalServer()
        {
            int hr = Ole32.CoInitializeEx(IntPtr.Zero, Ole32.COINIT_MULTITHREADED);
            if (hr >= 0) _comInitialized = true;
            else if (hr != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(hr);
        }

        public static void RequestShutdown()
        {
            ShutdownEvent.Set();
        }

        public void RegisterClass<T>(Guid clsid) where T : new()
        {
            int cookie;
            int hr = Ole32.CoRegisterClassObject(ref clsid, new SimpleClassFactory<T>(), Ole32.CLSCTX_LOCAL_SERVER,
                Ole32.REGCLS_MULTIPLEUSE | Ole32.REGCLS_SUSPENDED, out cookie);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            _cookies.Add(cookie);
            hr = Ole32.CoResumeClassObjects();
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }

        public void Run()
        {
            ShutdownEvent.Wait();
        }

        public void Dispose()
        {
            foreach (int cookie in _cookies)
                Ole32.CoRevokeClassObject(cookie);
            _cookies.Clear();
            if (_comInitialized) Ole32.CoUninitialize();
        }

        private static class Ole32
        {
            internal const int COINIT_MULTITHREADED = 0x0;
            internal const int CLSCTX_LOCAL_SERVER = 0x4;
            internal const int REGCLS_MULTIPLEUSE = 0x1;
            internal const int REGCLS_SUSPENDED = 0x4;

            [DllImport("ole32.dll")]
            internal static extern int CoInitializeEx(IntPtr reserved, int coInit);
            [DllImport("ole32.dll")]
            internal static extern void CoUninitialize();
            [DllImport("ole32.dll")]
            internal static extern int CoRegisterClassObject(ref Guid clsid, [MarshalAs(UnmanagedType.IUnknown)] object classFactory, int context, int flags, out int cookie);
            [DllImport("ole32.dll")]
            internal static extern int CoResumeClassObjects();
            [DllImport("ole32.dll")]
            internal static extern int CoRevokeClassObject(int cookie);
        }
    }
}
