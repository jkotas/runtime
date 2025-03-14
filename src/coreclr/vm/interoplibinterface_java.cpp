// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#ifdef FEATURE_JAVAMARSHAL

// Runtime headers
#include "common.h"

// Interop library header
#include <interoplibimports.h>

#include "interoplibinterface.h"

using CrossreferenceHandleCallback = void(STDMETHODCALLTYPE *)(size_t, StronglyConnectedComponent*, size_t, ComponentCrossReference*);

namespace
{
    BOOL g_Initialized;
    CrossreferenceHandleCallback g_MarkCrossReferences;

    struct MarkCrossReferenceArgs
    {
        size_t sccsLen;
        StronglyConnectedComponent* sccs;
        size_t ccrsLen;
        ComponentCrossReference* ccrs;
    };

    MarkCrossReferenceArgs* g_mcrargs;

    Volatile<BOOL> g_BridgeReady;
    CLREvent* g_BridgeTrigger;
    Thread* g_BridgeThread;

    void FreeArgs(MarkCrossReferenceArgs* mcrargs)
    {
        _ASSERTE(mcrargs != NULL);

        // Free the memory allocated for the arguments
        // Allocated by the GC.
        for (size_t i = 0; i < mcrargs->sccsLen; i++)
        {
            free(mcrargs->sccs[i].Context);
        }
        free(mcrargs->sccs);
        free(mcrargs->ccrs);
        // Allocated during the trigger call
        delete mcrargs;
    }

    DWORD WINAPI BridgeThreadStart(void *args)
    {
        while (TRUE)
        {
            g_BridgeReady = TRUE;
            switch (g_BridgeTrigger->Wait(INFINITE, FALSE))
            {
            case (WAIT_TIMEOUT):
            case (WAIT_ABANDONED):
                g_BridgeReady = FALSE;
                return 0;
            case (WAIT_OBJECT_0):
                g_BridgeReady = FALSE;
                break;
            }

            if (g_mcrargs == NULL)
                continue;

            MarkCrossReferenceArgs* mcrargs = g_mcrargs;
            InterlockedExchange((LONG*)&g_mcrargs, NULL);

            g_MarkCrossReferences(mcrargs->sccsLen, mcrargs->sccs, mcrargs->ccrsLen, mcrargs->ccrs);

            FreeArgs(mcrargs);
        }

        return 0;
    }
}

extern "C" BOOL QCALLTYPE JavaMarshal_Initialize(
    _In_ void* markCrossReferences)
{
    QCALL_CONTRACT;
    _ASSERTE(markCrossReferences != NULL);

    BOOL success = FALSE;

    BEGIN_QCALL;

    // Switch to Cooperative mode since we are setting callbacks that
    // will be used during a GC and we want to ensure a GC isn't occurring
    // while they are being set.
    {
        GCX_COOP();
        if (InterlockedCompareExchange((LONG*)&g_Initialized, TRUE, FALSE) == FALSE)
        {
            g_MarkCrossReferences = (CrossreferenceHandleCallback)markCrossReferences;

            g_BridgeReady = FALSE;
            g_BridgeTrigger = new CLREvent();
            g_BridgeTrigger->CreateAutoEvent(FALSE);

            g_BridgeThread = SetupUnstartedThread();
            BOOL createdThread = g_BridgeThread->CreateNewThread(0, &BridgeThreadStart, NULL, W("JavaBridge"));
            _ASSERTE(createdThread != FALSE);
            g_BridgeThread->StartThread();
            success = TRUE;
        }
    }

    END_QCALL;

    return success;
}

extern "C" void* QCALLTYPE JavaMarshal_CreateReferenceTrackingHandle(
    _In_ QCall::ObjectHandleOnStack obj,
    _In_ void* context)
{
    QCALL_CONTRACT;

    OBJECTHANDLE instHandle = NULL;

    BEGIN_QCALL;

    GCX_COOP();
    instHandle = GetAppDomain()->CreateCrossReferenceHandle(obj.Get(), context);

    END_QCALL;

    return (void*)instHandle;
}

extern "C" BOOL QCALLTYPE JavaMarshal_GetContext(
    _In_ OBJECTHANDLE handle,
    _Out_ void** context)
{
    QCALL_CONTRACT;
    _ASSERTE(handle != NULL);
    _ASSERTE(context != NULL);

    BOOL success = FALSE;

    BEGIN_QCALL;

    IGCHandleManager* mgr = GCHandleUtilities::GetGCHandleManager();
    HandleType type = mgr->HandleFetchType(handle);
    if (type == HNDTYPE_CROSSREFERENCE)
    {
        *context = mgr->GetExtraInfoFromHandle(handle);
        success = TRUE;
    }

    END_QCALL;

    return success;
}

void JavaNative::TriggerGCBridge(
    _In_ size_t sccsLen,
    _In_ StronglyConnectedComponent* sccs,
    _In_ size_t ccrsLen,
    _In_ ComponentCrossReference* ccrs)
{
    CONTRACTL
    {
        NOTHROW;
        GC_NOTRIGGER;
    }
    CONTRACTL_END;

    if (g_MarkCrossReferences == NULL)
        return;

    MarkCrossReferenceArgs* mcrargs = new MarkCrossReferenceArgs();
    mcrargs->sccsLen = sccsLen;
    mcrargs->sccs = sccs;
    mcrargs->ccrsLen = ccrsLen;
    mcrargs->ccrs = ccrs;

    if (g_BridgeReady == FALSE)
    {
        FreeArgs(mcrargs);
        return;
    }

    g_mcrargs = mcrargs;
    g_BridgeTrigger->Set();
}

#endif // FEATURE_JAVAMARSHAL
