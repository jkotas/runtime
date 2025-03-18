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

        MarkCrossReferenceArgs(size_t sccsLen, StronglyConnectedComponent* sccs, size_t ccrsLen, ComponentCrossReference* ccrs)
            : sccsLen(sccsLen)
            , sccs(sccs)
            , ccrsLen(ccrsLen)
            , ccrs(ccrs)
        { }

        ~MarkCrossReferenceArgs()
        {
            // Free the memory allocated for the arguments
            // Allocated by the GC.
            // See TriggerGCBridge.
            for (size_t i = 0; i < sccsLen; i++)
            {
                free(sccs[i].Context);
            }
            free(sccs);
            free(ccrs);
        }
    };

    VolatilePtr<MarkCrossReferenceArgs> g_mcrargs;
    CLREvent* g_BridgeTrigger;

    void BridgeThreadWorker()
    {
        while (TRUE)
        {
            switch (g_BridgeTrigger->Wait(INFINITE, FALSE))
            {
            case (WAIT_TIMEOUT):
            case (WAIT_ABANDONED):
                return;
            case (WAIT_OBJECT_0):
                break;
            }

            if (!g_mcrargs)
                continue;

            MarkCrossReferenceArgs* mcrargs = (MarkCrossReferenceArgs*)g_mcrargs;
            g_MarkCrossReferences(mcrargs->sccsLen, mcrargs->sccs, mcrargs->ccrsLen, mcrargs->ccrs);

            delete mcrargs;
            InterlockedExchangeT(g_mcrargs.GetPointer(), NULL);
        }
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

            g_BridgeTrigger = new CLREvent();
            g_BridgeTrigger->CreateAutoEvent(FALSE);
            success = TRUE;
        }
    }

    END_QCALL;

    return success;
}

extern "C" void QCALLTYPE JavaMarshal_BridgeMain()
{
    QCALL_CONTRACT;

    BEGIN_QCALL;

    BridgeThreadWorker();

    END_QCALL;
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

    // Not initialized
    if (g_MarkCrossReferences == NULL)
        return;

    // Bridge is currently running
    if (g_mcrargs)
        return;

    g_mcrargs = new MarkCrossReferenceArgs(sccsLen, sccs, ccrsLen, ccrs);
    g_BridgeTrigger->Set();
}

#endif // FEATURE_JAVAMARSHAL
