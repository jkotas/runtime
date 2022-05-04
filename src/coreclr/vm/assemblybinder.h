// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#ifndef _ASSEMBLYBINDER_H
#define _ASSEMBLYBINDER_H

#include <sarray.h>

class PEImage;
class NativeImage;
class Assembly;
class Module;
class AssemblyLoaderAllocator;
class AssemblySpec;

class AssemblyBinder
{
    bool m_fDefault;

public:
    AssemblyBinder(bool fDefault = false)
        : m_fDefault(fDefault)
    {
    }

    // HRESULT BindAssemblyByName(AssemblyNameData* pAssemblyNameData, BINDER_SPACE::Assembly** ppAssembly);
    // HRESULT BindUsingPEImage(PEImage* pPEImage, BINDER_SPACE::Assembly** ppAssembly);
    // HRESULT BindUsingAssemblyName(BINDER_SPACE::AssemblyName* pAssemblyName, BINDER_SPACE::Assembly** ppAssembly);

    /// <summary>
    /// Get LoaderAllocator for binders that contain it. For other binders, return NULL.
    /// </summary>
    AssemblyLoaderAllocator* GetLoaderAllocator();

    /// <summary>
    /// Tells if the binder is a default binder (not a custom one)
    /// </summary>
    bool IsDefault();

    void SetupBindingPaths(LPCWSTR pwzTrustedPlatformAssemblies,
                           LPCWSTR pwzPlatformResourceRoots,
                           LPCWSTR pwzAppPaths);
    INT_PTR GetManagedAssemblyLoadContext()
    {
        return m_ptrManagedAssemblyLoadContext;
    }

    void SetManagedAssemblyLoadContext(INT_PTR ptrManagedDefaultBinderInstance)
    {
        m_ptrManagedAssemblyLoadContext = ptrManagedDefaultBinderInstance;
    }

    NativeImage* LoadNativeImage(Module* componentModule, LPCUTF8 nativeImageName);
    void AddLoadedAssembly(Assembly* loadedAssembly);

    void ReleaseLoadContext();

    void GetNameForDiagnostics(/*out*/ SString& alcName);

    static void GetNameForDiagnosticsFromManagedALC(INT_PTR managedALC, /* out */ SString& alcName);
    static void GetNameForDiagnosticsFromSpec(AssemblySpec* spec, /*out*/ SString& alcName);

private:
    // A GC handle to the managed AssemblyLoadContext.
    // It is a long weak handle for collectible AssemblyLoadContexts and strong handle for non-collectible ones.
    INT_PTR m_ptrManagedAssemblyLoadContext;

    SArray<NativeImage*> m_nativeImages;
    SArray<Assembly*> m_loadedAssemblies;
};

#endif
