// Structure adapted from Microsoft's rdp-dvc-plugin-samples (MIT).
using System;
using System.Runtime.InteropServices;

namespace COMRegistration
{
    [ComImport]
    [ComVisible(false)]
    [Guid("00000001-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IClassFactory
    {
        void CreateInstance([MarshalAs(UnmanagedType.Interface)] object outer, ref Guid riid, out IntPtr result);
        void LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
    }

    [ComVisible(true)]
    internal sealed class SimpleClassFactory<T> : IClassFactory where T : new()
    {
        private static readonly Guid IidIUnknown = new Guid("00000000-0000-0000-C000-000000000046");

        public void CreateInstance(object outer, ref Guid riid, out IntPtr result)
        {
            if (outer != null)
                throw new COMException("Aggregation is not supported", unchecked((int)0x80040110));

            object instance = new T();
            if (riid == IidIUnknown)
            {
                result = Marshal.GetIUnknownForObject(instance);
                return;
            }

            foreach (Type i in typeof(T).GetInterfaces())
            {
                if (i.GUID == riid)
                {
                    result = Marshal.GetComInterfaceForObject(instance, i);
                    return;
                }
            }
            throw new COMException("No such interface", unchecked((int)0x80004002));
        }

        public void LockServer(bool fLock) { }
    }
}
