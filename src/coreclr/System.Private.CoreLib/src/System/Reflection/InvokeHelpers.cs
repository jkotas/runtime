// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace System.Reflection
{
    internal static unsafe class InvokeHelpers
    {
        [Intrinsic]
        internal static object? InvokeInstance_Int32_Void(object o, IntPtr pfn, object arg)
        {
            ((delegate*<object?, int, void>)pfn)(o, (int)arg);
            return null;
        }

        [Intrinsic]
        internal static object? InvokeInstance_Guid(object o, IntPtr pfn)
        {
            return ((delegate *<object, Guid>)pfn)(o);
        }
    }
}
