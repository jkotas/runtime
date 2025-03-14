// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;

namespace System.Runtime.InteropServices.Java
{
    [CLSCompliant(false)]
    [SupportedOSPlatform("android")]
    public static partial class JavaMarshal
    {
#if !NATIVEAOT
        private static Thread? s_bridgeThread;
#endif

        public static unsafe void Initialize(
            // Callback used to perform the marking of SCCs.
            delegate* unmanaged<
                nint,                               // Length of SCC collection
                StronglyConnectedComponent*,        // SCC collection
                nint,                               // Length of CCR collection
                ComponentCrossReference*,           // CCR collection
                void> markCrossReferences)
        {
#if NATIVEAOT
            throw new NotImplementedException();
#else
            ArgumentNullException.ThrowIfNull(markCrossReferences);

            if (!InitializeInternal((IntPtr)markCrossReferences))
            {
                throw new InvalidOperationException(SR.InvalidOperation_ReinitializeJavaMarshal);
            }

            s_bridgeThread = new Thread(BridgeMain)
            {
                IsBackground = true,
                Name = ".NET GC Bridge"
            };
            s_bridgeThread.Start();
#endif
        }

        public static GCHandle CreateReferenceTrackingHandle(object obj, IntPtr context)
        {
#if NATIVEAOT
            throw new NotImplementedException();
#else
            ArgumentNullException.ThrowIfNull(obj);

            IntPtr handle = CreateReferenceTrackingHandleInternal(ObjectHandleOnStack.Create(ref obj), context);
            return GCHandle.FromIntPtr(handle);
#endif
        }

        public static IntPtr GetContext(GCHandle obj)
        {
#if NATIVEAOT
            throw new NotImplementedException();
#else
            IntPtr handle = GCHandle.ToIntPtr(obj);
            if (handle == IntPtr.Zero
                || !GetContextInternal(handle, out IntPtr context))
            {
                throw new InvalidOperationException(SR.InvalidOperation_IncorrectGCHandleType);
            }

            return context;
#endif
        }

#if !NATIVEAOT
        [LibraryImport(RuntimeHelpers.QCall, EntryPoint = "JavaMarshal_Initialize")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool InitializeInternal(IntPtr callback);

        [LibraryImport(RuntimeHelpers.QCall, EntryPoint = "JavaMarshal_BridgeMain")]
        private static partial void BridgeMain();

        [LibraryImport(RuntimeHelpers.QCall, EntryPoint = "JavaMarshal_CreateReferenceTrackingHandle")]
        private static partial IntPtr CreateReferenceTrackingHandleInternal(ObjectHandleOnStack obj, IntPtr context);

        [LibraryImport(RuntimeHelpers.QCall, EntryPoint = "JavaMarshal_GetContext")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetContextInternal(IntPtr handle, out IntPtr context);
#endif
    }
}
