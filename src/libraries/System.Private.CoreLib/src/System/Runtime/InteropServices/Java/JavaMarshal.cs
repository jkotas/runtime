// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace System.Runtime.InteropServices.Java
{
    [CLSCompliant(false)]
    [SupportedOSPlatform("android")]
    public static partial class JavaMarshal
    {
        public static unsafe void Initialize(
            // Callback used to perform the marking of SCCs.
            delegate* unmanaged<
                nint,                               // Length of SCC collection
                StronglyConnectedComponent*,        // SCC collection
                nint,                               // Length of CCR collection
                ComponentCrossReference*,           // CCR collection
                delegate* unmanaged<nint, IntPtr, void>, // Callback to mark GCHandles
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
#endif
        }

        public static GCHandle CreateReferenceTrackingHandle(object obj, IntPtr contextMemory)
        {
#if NATIVEAOT
            throw new NotImplementedException();
#else
            ArgumentNullException.ThrowIfNull(obj);

            IntPtr handle = CreateReferenceTrackingHandleInternal(ObjectHandleOnStack.Create(ref obj), contextMemory);
            return GCHandle.FromIntPtr(handle);
#endif
        }

#if !NATIVEAOT
        [LibraryImport(RuntimeHelpers.QCall, EntryPoint = "JavaMarshal_Initialize")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool InitializeInternal(IntPtr callback);

        [LibraryImport(RuntimeHelpers.QCall, EntryPoint = "JavaMarshal_CreateReferenceTrackingHandle")]
        private static partial IntPtr CreateReferenceTrackingHandleInternal(ObjectHandleOnStack obj, IntPtr contextMemory);
#endif
    }
}
