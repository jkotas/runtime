// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include <assert.h>
#include "pal_evp_pkey.h"

EVP_PKEY* CryptoNative_LoadPrivateKeyUsingEngine(char* engine, char* file)
{
   EVP_PKEY* pkey;
   ENGINE* e;

   e = ENGINE_by_id(engine);
   if (e == NULL)
   {
      return NULL;
   }

   if (!ENGINE_init(e))
   {
       return NULL;
   }

   pkey = ENGINE_load_private_key(e, file, NULL, NULL);
   ENGINE_finish(e);
   return pkey;
}

EVP_PKEY* CryptoNative_EvpPkeyCreate()
{
    return EVP_PKEY_new();
}

void CryptoNative_EvpPkeyDestroy(EVP_PKEY* pkey)
{
    if (pkey != NULL)
    {
        EVP_PKEY_free(pkey);
    }
}

int32_t CryptoNative_EvpPKeySize(EVP_PKEY* pkey)
{
    assert(pkey != NULL);
    return EVP_PKEY_size(pkey);
}

int32_t CryptoNative_UpRefEvpPkey(EVP_PKEY* pkey)
{
    if (!pkey)
    {
        return 0;
    }

    return EVP_PKEY_up_ref(pkey);
}
