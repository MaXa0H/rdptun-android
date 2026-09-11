using System;
using System.IO;
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

        private static string TracePath
        {
            get { return Path.Combine(Path.GetTempPath(), "rdptun-plugin.log"); }
        }

        private static void Trace(string text)
        {
            try
            {
                File.AppendAllText(TracePath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + " pid=" + System.Diagnostics.Process.GetCurrentProcess().Id +
                    " CLASSFACTORY " + text + Environment.NewLine);
            }
            catch { }
        }

        public void CreateInstance(object outer, ref Guid riid, out IntPtr result)
        {
            result = IntPtr.Zero;
            Trace("CreateInstance riid=" + riid.ToString("B") + " outer=" + (outer == null ? "null" : "non-null"));

            try
            {
                Type interfaceType = ValidateRequestedInterface(typeof(T), ref riid, outer);
                object instance = new T();

                if (outer != null)
                    instance = CreateAggregatedObject(outer, instance);

                if (interfaceType == typeof(object))
                    result = Marshal.GetIUnknownForObject(instance);
                else
                    result = Marshal.GetComInterfaceForObject(instance, interfaceType, CustomQueryInterfaceMode.Ignore);

                if (result == IntPtr.Zero)
                    throw new COMException("COM interface pointer is null", unchecked((int)0x80004002));

                Trace("CreateInstance success interface=" + interfaceType.FullName + " ptr=0x" + result.ToInt64().ToString("X"));
            }
            catch (Exception ex)
            {
                Trace("CreateInstance FAILED riid=" + riid.ToString("B") + " " + ex.GetType().FullName + ": " + ex.Message +
                      " hr=0x" + Marshal.GetHRForException(ex).ToString("X8"));
                throw;
            }
        }

        public void LockServer(bool fLock)
        {
            Trace("LockServer " + fLock);
        }

        private static Type ValidateRequestedInterface(Type classType, ref Guid riid, object outer)
        {
            if (riid == IidIUnknown)
                return typeof(object);

            if (outer != null)
                throw new COMException("Aggregation requires IID_IUnknown", unchecked((int)0x80040110));

            foreach (Type i in classType.GetInterfaces())
            {
                if (i.GUID == riid)
                    return i;
            }

            throw new COMException("Requested interface is not implemented: " + riid.ToString("B"), unchecked((int)0x80004002));
        }

        private static object CreateAggregatedObject(object outer, object instance)
        {
            IntPtr outerPtr = Marshal.GetIUnknownForObject(outer);
            try
            {
                IntPtr innerPtr = Marshal.CreateAggregatedObject(outerPtr, instance);
                try
                {
                    return Marshal.GetObjectForIUnknown(innerPtr);
                }
                finally
                {
                    Marshal.Release(innerPtr);
                }
            }
            finally
            {
                Marshal.Release(outerPtr);
            }
        }
    }
}
