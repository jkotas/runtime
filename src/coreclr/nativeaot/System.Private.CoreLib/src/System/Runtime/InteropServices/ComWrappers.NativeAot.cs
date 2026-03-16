// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;

using Internal.Runtime;

namespace System.Runtime.InteropServices
{
    /// <summary>
    /// Class for managing wrappers of COM IUnknown types.
    /// </summary>
    public abstract partial class ComWrappers
    {
        internal sealed unsafe partial class ManagedObjectWrapperHolder
        {
            private static void RegisterIsRootedCallback()
            {
                delegate* unmanaged<IntPtr, bool> callback = &IsRootedCallback;
                if (!RuntimeImports.RhRegisterRefCountedHandleCallback((nint)callback, MethodTable.Of<ManagedObjectWrapperHolder>()))
                {
                    throw new OutOfMemoryException();
                }
            }

            [UnmanagedCallersOnly]
            private static bool IsRootedCallback(IntPtr pObj)
            {
                // We are paused in the GC, so this is safe.
                ManagedObjectWrapperHolder* holder = (ManagedObjectWrapperHolder*)&pObj;
                return holder->_wrapper->IsRooted;
            }

            private static IntPtr AllocateRefCountedHandle(ManagedObjectWrapperHolder holder)
            {
                return RuntimeImports.RhHandleAllocRefCounted(holder);
            }
        }

        /// <summary>
        /// Get the runtime provided IUnknown implementation.
        /// </summary>
        /// <param name="fpQueryInterface">Function pointer to QueryInterface.</param>
        /// <param name="fpAddRef">Function pointer to AddRef.</param>
        /// <param name="fpRelease">Function pointer to Release.</param>
        public static unsafe void GetIUnknownImpl(out IntPtr fpQueryInterface, out IntPtr fpAddRef, out IntPtr fpRelease)
        {
            fpQueryInterface = (IntPtr)(delegate* unmanaged[MemberFunction]<IntPtr, Guid*, IntPtr*, int>)&ComWrappers.IUnknown_QueryInterface;
            fpAddRef = (IntPtr)(delegate*<IntPtr, uint>)&RuntimeImports.RhIUnknown_AddRef; // Implemented in C/C++ to avoid GC transitions
            fpRelease = (IntPtr)(delegate* unmanaged[MemberFunction]<IntPtr, uint>)&ComWrappers.IUnknown_Release;
        }

        internal static unsafe void GetUntrackedIUnknownImpl(out delegate* unmanaged[MemberFunction]<IntPtr, uint> fpAddRef, out delegate* unmanaged[MemberFunction]<IntPtr, uint> fpRelease)
        {
            // Implemented in C/C++ to avoid GC transitions during shutdown
            fpAddRef = (delegate* unmanaged[MemberFunction]<IntPtr, uint>)(void*)(delegate*<IntPtr, uint>)&RuntimeImports.RhUntracked_AddRefRelease;
            fpRelease = (delegate* unmanaged[MemberFunction]<IntPtr, uint>)(void*)(delegate*<IntPtr, uint>)&RuntimeImports.RhUntracked_AddRefRelease;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
        internal static unsafe int IUnknown_QueryInterface(IntPtr pThis, Guid* guid, IntPtr* ppObject)
        {
            ManagedObjectWrapper* wrapper = ComInterfaceDispatch.ToManagedObjectWrapper((ComInterfaceDispatch*)pThis);
            return wrapper->QueryInterface(in *guid, out *ppObject);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
        internal static unsafe uint IUnknown_Release(IntPtr pThis)
        {
            ManagedObjectWrapper* wrapper = ComInterfaceDispatch.ToManagedObjectWrapper((ComInterfaceDispatch*)pThis);
            uint refcount = wrapper->Release();
            return refcount;
        }

        private static IntPtr GetTaggedImplCurrentVersion()
        {
            unsafe
            {
                return (IntPtr)(delegate* unmanaged[MemberFunction]<IntPtr, IntPtr, int>)&VtableImplementations.ITaggedImpl_IsCurrentVersion;
            }
        }

        internal static unsafe IntPtr DefaultIUnknownVftblPtr => (IntPtr)Unsafe.AsPointer(in VtableImplementations.IUnknown);
        internal static unsafe IntPtr TaggedImplVftblPtr => (IntPtr)Unsafe.AsPointer(in VtableImplementations.ITaggedImpl);
        internal static unsafe IntPtr DefaultIReferenceTrackerTargetVftblPtr => (IntPtr)Unsafe.AsPointer(in VtableImplementations.IReferenceTrackerTarget);

        /// <summary>
        /// Define the vtable layout for the COM interfaces we provide.
        /// </summary>
        /// <remarks>
        /// This is defined as a nested class to ensure that the vtable types are the only things initialized in the class's static constructor.
        /// As long as that's the case, we can easily guarantee that they are pre-initialized and that we don't end up having startup code
        /// needed to set up the vtable layouts.
        /// </remarks>
        private static class VtableImplementations
        {
            public unsafe struct IUnknownVftbl
            {
                public delegate* unmanaged[MemberFunction]<IntPtr, Guid*, IntPtr*, int> QueryInterface;
                public delegate* unmanaged[MemberFunction]<IntPtr, int> AddRef;
                public delegate* unmanaged[MemberFunction]<IntPtr, uint> Release;
            }

            public unsafe struct IReferenceTrackerTargetVftbl
            {
                public delegate* unmanaged[MemberFunction]<IntPtr, Guid*, IntPtr*, int> QueryInterface;
                public delegate* unmanaged[MemberFunction]<IntPtr, int> AddRef;
                public delegate* unmanaged[MemberFunction]<IntPtr, uint> Release;
                public delegate* unmanaged[MemberFunction]<IntPtr, uint> AddRefFromReferenceTracker;
                public delegate* unmanaged[MemberFunction]<IntPtr, uint> ReleaseFromReferenceTracker;
                public delegate* unmanaged[MemberFunction]<IntPtr, uint> Peg;
                public delegate* unmanaged[MemberFunction]<IntPtr, uint> Unpeg;
            }

            public unsafe struct ITaggedImplVftbl
            {
                public delegate* unmanaged[MemberFunction]<IntPtr, Guid*, IntPtr*, int> QueryInterface;
                public delegate* unmanaged[MemberFunction]<IntPtr, int> AddRef;
                public delegate* unmanaged[MemberFunction]<IntPtr, uint> Release;
                public delegate* unmanaged[MemberFunction]<IntPtr, IntPtr, int> IsCurrentVersion;
            }

            [FixedAddressValueType]
            public static readonly IUnknownVftbl IUnknown;

            [FixedAddressValueType]
            public static readonly IReferenceTrackerTargetVftbl IReferenceTrackerTarget;

            [FixedAddressValueType]
            public static readonly ITaggedImplVftbl ITaggedImpl;

            [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
            internal static unsafe int IReferenceTrackerTarget_QueryInterface(IntPtr pThis, Guid* guid, IntPtr* ppObject)
            {
                ManagedObjectWrapper* wrapper = ComInterfaceDispatch.ToManagedObjectWrapper((ComInterfaceDispatch*)pThis);
                return wrapper->QueryInterfaceForTracker(in *guid, out *ppObject);
            }

            [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
            internal static unsafe uint IReferenceTrackerTarget_AddRefFromReferenceTracker(IntPtr pThis)
            {
                ManagedObjectWrapper* wrapper = ComInterfaceDispatch.ToManagedObjectWrapper((ComInterfaceDispatch*)pThis);
                return wrapper->AddRefFromReferenceTracker();
            }

            [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
            internal static unsafe uint IReferenceTrackerTarget_ReleaseFromReferenceTracker(IntPtr pThis)
            {
                ManagedObjectWrapper* wrapper = ComInterfaceDispatch.ToManagedObjectWrapper((ComInterfaceDispatch*)pThis);
                return wrapper->ReleaseFromReferenceTracker();
            }

            [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
            internal static unsafe uint IReferenceTrackerTarget_Peg(IntPtr pThis)
            {
                ManagedObjectWrapper* wrapper = ComInterfaceDispatch.ToManagedObjectWrapper((ComInterfaceDispatch*)pThis);
                return wrapper->Peg();
            }

            [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
            internal static unsafe uint IReferenceTrackerTarget_Unpeg(IntPtr pThis)
            {
                ManagedObjectWrapper* wrapper = ComInterfaceDispatch.ToManagedObjectWrapper((ComInterfaceDispatch*)pThis);
                return wrapper->Unpeg();
            }

            [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
            internal static unsafe int ITaggedImpl_IsCurrentVersion(IntPtr pThis, IntPtr version)
            {
                return version == (IntPtr)(delegate* unmanaged[MemberFunction]<IntPtr, IntPtr, int>)&ITaggedImpl_IsCurrentVersion
                    ? HResults.S_OK
                    : HResults.E_FAIL;
            }

            static unsafe VtableImplementations()
            {
                // Use the "pre-inited vtable" pattern to ensure that ILC can pre-compile these vtables.
                GetIUnknownImpl(
                    fpQueryInterface: out *(nint*)&((IUnknownVftbl*)Unsafe.AsPointer(ref IUnknown))->QueryInterface,
                    fpAddRef: out *(nint*)&((IUnknownVftbl*)Unsafe.AsPointer(ref IUnknown))->AddRef,
                    fpRelease: out *(nint*)&((IUnknownVftbl*)Unsafe.AsPointer(ref IUnknown))->Release);

                IReferenceTrackerTarget.QueryInterface = (delegate* unmanaged[MemberFunction]<IntPtr, Guid*, IntPtr*, int>)&IReferenceTrackerTarget_QueryInterface;
                GetIUnknownImpl(
                    fpQueryInterface: out _,
                    fpAddRef: out *(nint*)&((IReferenceTrackerTargetVftbl*)Unsafe.AsPointer(ref IReferenceTrackerTarget))->AddRef,
                    fpRelease: out *(nint*)&((IReferenceTrackerTargetVftbl*)Unsafe.AsPointer(ref IReferenceTrackerTarget))->Release);
                IReferenceTrackerTarget.AddRefFromReferenceTracker = (delegate* unmanaged[MemberFunction]<IntPtr, uint>)&IReferenceTrackerTarget_AddRefFromReferenceTracker;
                IReferenceTrackerTarget.ReleaseFromReferenceTracker = (delegate* unmanaged[MemberFunction]<IntPtr, uint>)&IReferenceTrackerTarget_ReleaseFromReferenceTracker;
                IReferenceTrackerTarget.Peg = (delegate* unmanaged[MemberFunction]<IntPtr, uint>)&IReferenceTrackerTarget_Peg;
                IReferenceTrackerTarget.Unpeg = (delegate* unmanaged[MemberFunction]<IntPtr, uint>)&IReferenceTrackerTarget_Unpeg;

                GetIUnknownImpl(
                    fpQueryInterface: out *(nint*)&((ITaggedImplVftbl*)Unsafe.AsPointer(ref ITaggedImpl))->QueryInterface,
                    fpAddRef: out *(nint*)&((ITaggedImplVftbl*)Unsafe.AsPointer(ref ITaggedImpl))->AddRef,
                    fpRelease: out *(nint*)&((ITaggedImplVftbl*)Unsafe.AsPointer(ref ITaggedImpl))->Release);
                ITaggedImpl.IsCurrentVersion = (delegate* unmanaged[MemberFunction]<IntPtr, IntPtr, int>)&ITaggedImpl_IsCurrentVersion;
            }
        }
    }

    // This is a GCHandle HashSet implementation based on LowLevelDictionary.
    // It uses no locking for readers. While for writers (add / remove),
    // it handles the locking itself.
    // This implementation specifically makes sure that any readers of this
    // collection during GC aren't impacted by other threads being
    // frozen while in the middle of an write. It makes no guarantees on
    // whether you will observe the element being added / removed, but does
    // make sure the collection is in a good state and doesn't run into issues
    // while iterating.
    internal sealed class GCHandleSet : IEnumerable<GCHandle>
    {
        private const int DefaultSize = 7;

        private Entry?[] _buckets = new Entry[DefaultSize];
        private int _numEntries;
        private readonly Lock _lock = new Lock(useTrivialWaits: true);

        public Lock ModificationLock => _lock;

        public void Add(GCHandle handle)
        {
            using (_lock.EnterScope())
            {
                int bucket = GetBucket(handle, _buckets.Length);
                Entry? prev = null;
                Entry? entry = _buckets[bucket];
                while (entry != null)
                {
                    // Handle already exists, nothing to add.
                    if (handle.Equals(entry.m_value))
                    {
                        return;
                    }

                    prev = entry;
                    entry = entry.m_next;
                }

                Entry newEntry = new Entry()
                {
                    m_value = handle
                };

                if (prev == null)
                {
                    _buckets[bucket] = newEntry;
                }
                else
                {
                    prev.m_next = newEntry;
                }

                // _numEntries is only maintained for the purposes of deciding whether to
                // expand the bucket and is not used during iteration to handle the
                // scenario where element is in bucket but _numEntries hasn't been incremented
                // yet.
                _numEntries++;
                if (_numEntries > (_buckets.Length * 2))
                {
                    ExpandBuckets();
                }
            }
        }

        private void ExpandBuckets()
        {
            int newNumBuckets = _buckets.Length * 2 + 1;
            Entry?[] newBuckets = new Entry[newNumBuckets];
            for (int i = 0; i < _buckets.Length; i++)
            {
                Entry? entry = _buckets[i];
                while (entry != null)
                {
                    Entry? nextEntry = entry.m_next;

                    int bucket = GetBucket(entry.m_value, newNumBuckets);

                    // We are allocating new entries for the bucket to ensure that
                    // if there is an enumeration already in progress, we don't
                    // modify what it observes by changing next in existing instances.
                    Entry newEntry = new Entry()
                    {
                        m_value = entry.m_value,
                        m_next = newBuckets[bucket],
                    };
                    newBuckets[bucket] = newEntry;

                    entry = nextEntry;
                }
            }
            _buckets = newBuckets;
        }

        public void Remove(GCHandle handle)
        {
            using (_lock.EnterScope())
            {
                int bucket = GetBucket(handle, _buckets.Length);
                Entry? prev = null;
                Entry? entry = _buckets[bucket];
                while (entry != null)
                {
                    if (handle.Equals(entry.m_value))
                    {
                        if (prev == null)
                        {
                            _buckets[bucket] = entry.m_next;
                        }
                        else
                        {
                            prev.m_next = entry.m_next;
                        }
                        _numEntries--;
                        return;
                    }

                    prev = entry;
                    entry = entry.m_next;
                }
            }
        }

        private static int GetBucket(GCHandle handle, int numBuckets)
        {
            int h = handle.GetHashCode();
            return (int)((uint)h % (uint)numBuckets);
        }

        public Enumerator GetEnumerator() => new Enumerator(this);

        IEnumerator<GCHandle> IEnumerable<GCHandle>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<GCHandle>)this).GetEnumerator();

        private sealed class Entry
        {
            public GCHandle m_value;
            public Entry? m_next;
        }

        public struct Enumerator : IEnumerator<GCHandle>
        {
            private readonly Entry?[] _buckets;
            private int _currentIdx;
            private Entry? _currentEntry;

            public Enumerator(GCHandleSet set)
            {
                // We hold onto the buckets of the set rather than the set itself
                // so that if it is ever expanded, we are not impacted by that during
                // enumeration.
                _buckets = set._buckets;
                Reset();
            }

            public GCHandle Current
            {
                get
                {
                    if (_currentEntry == null)
                    {
                        throw new InvalidOperationException("InvalidOperation_EnumOpCantHappen");
                    }

                    return _currentEntry.m_value;
                }
            }

            object IEnumerator.Current => Current;

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                if (_currentEntry != null)
                {
                    _currentEntry = _currentEntry.m_next;
                }

                if (_currentEntry == null)
                {
                    // Certain buckets might be empty, so loop until we find
                    // one with an entry.
                    while (++_currentIdx != _buckets.Length)
                    {
                        _currentEntry = _buckets[_currentIdx];
                        if (_currentEntry != null)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                return true;
            }

            public void Reset()
            {
                _currentIdx = -1;
                _currentEntry = null;
            }
        }
    }
}
