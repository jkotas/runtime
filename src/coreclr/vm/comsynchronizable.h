// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.


/*============================================================
**
** Header: COMSynchronizable.h
**
** Purpose: Native methods on System.SynchronizableObject
**          and its subclasses.
**
**
===========================================================*/

#ifndef _COMSYNCHRONIZABLE_H
#define _COMSYNCHRONIZABLE_H

class ThreadNative
{
public:
    enum
    {
        PRIORITY_LOWEST = 0,
        PRIORITY_BELOW_NORMAL = 1,
        PRIORITY_NORMAL = 2,
        PRIORITY_ABOVE_NORMAL = 3,
        PRIORITY_HIGHEST = 4,
    };

    enum
    {
        ThreadStopRequested = 1,
        ThreadSuspendRequested = 2,
        ThreadBackground = 4,
        ThreadUnstarted = 8,
        ThreadStopped = 16,
        ThreadWaitSleepJoin = 32,
        ThreadSuspended = 64,
        ThreadAbortRequested = 128,
    };

    static FCDECL0(INT32,   GetOptimalMaxSpinWaitsPerSpinIteration);
};

extern "C" void QCALLTYPE ThreadNative_Start(Thread* pThread, int threadStackSize, int priority, BOOL isThreadPool, PCWSTR pThreadName);
extern "C" void QCALLTYPE ThreadNative_SetPriority(Thread* pThread, INT32 iPriority);
extern "C" void QCALLTYPE ThreadNative_GetCurrentThread(QCall::ObjectHandleOnStack thread);
extern "C" BOOL QCALLTYPE ThreadNative_GetIsBackground(Thread* pThread);
extern "C" void QCALLTYPE ThreadNative_SetIsBackground(Thread* pThread, BOOL value);
extern "C" void QCALLTYPE ThreadNative_InformThreadNameChange(Thread* pThread, LPCWSTR name, INT32 len);
extern "C" BOOL QCALLTYPE ThreadNative_YieldThread();
extern "C" void QCALLTYPE ThreadNative_PollGC();
extern "C" UINT64 QCALLTYPE ThreadNative_GetCurrentOSThreadId();
extern "C" void QCALLTYPE ThreadNative_Initialize(QCall::ObjectHandleOnStack t);
extern "C" INT32 QCALLTYPE ThreadNative_GetThreadState(Thread* pThread);
extern "C" void QCALLTYPE ThreadNative_Destroy(Thread* pThread);

#ifdef FEATURE_COMINTEROP_APARTMENT_SUPPORT
extern "C" INT32 QCALLTYPE ThreadNative_GetApartmentState(Thread* pThread);
extern "C" INT32 QCALLTYPE ThreadNative_SetApartmentState(Thread* pThread, INT32 iState);
#endif // FEATURE_COMINTEROP_APARTMENT_SUPPORT

extern "C" BOOL QCALLTYPE ThreadNative_Join((Thread* pThread, INT32 Timeout);
extern "C" void QCALLTYPE ThreadNative_Abort((Thread* pThread);
extern "C" void QCALLTYPE ThreadNative_ResetAbort();
extern "C" void QCALLTYPE ThreadNative_SpinWait(INT32 iterations);
extern "C" void QCALLTYPE ThreadNative_Interrupt((Thread* pThread);
extern "C" void QCALLTYPE ThreadNative_Sleep(INT32 iTime);
#ifdef FEATURE_COMINTEROP
extern "C" void QCALLTYPE ThreadNative_DisableComObjectEagerCleanup((Thread* pThread);
#endif // FEATURE_COMINTEROP

#endif // _COMSYNCHRONIZABLE_H
