// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

/*============================================================================
**
** Source:  test14.c
**
** Purpose: Tests swprintf_s with exponential format doubles (lowercase)
**
**
**==========================================================================*/



#include <palsuite.h>
#include "../_snwprintf_s.h"

/* memcmp is used to verify the results, so this test is dependent on it. */
/* ditto with wcslen */

PALTEST(c_runtime__snwprintf_s_test14_paltest_snwprintf_test14, "c_runtime/_snwprintf_s/test14/paltest_snwprintf_test14")
{
    double val = 256.0;
    double neg = -256.0;
    
    if (PAL_Initialize(argc, argv) != 0)
    {
        return FAIL;
    }


    DoDoubleTest(convert("foo %e"), val, convert("foo 2.560000e+002"),
                 convert("foo 2.560000e+02"));
    DoDoubleTest(convert("foo %le"), val, convert("foo 2.560000e+002"),
                 convert("foo 2.560000e+02"));
    DoDoubleTest(convert("foo %he"), val, convert("foo 2.560000e+002"),
                 convert("foo 2.560000e+02"));
    DoDoubleTest(convert("foo %Le"), val, convert("foo 2.560000e+002"),
                 convert("foo 2.560000e+02"));
    DoDoubleTest(convert("foo %I64e"), val, convert("foo 2.560000e+002"),
                 convert("foo 2.560000e+02"));
    DoDoubleTest(convert("foo %14e"), val, convert("foo  2.560000e+002"),
                 convert("foo   2.560000e+02"));
    DoDoubleTest(convert("foo %-14e"), val, convert("foo 2.560000e+002 "),
                 convert("foo 2.560000e+02  "));
    DoDoubleTest(convert("foo %.1e"), val, convert("foo 2.6e+002"),
                 convert("foo 2.6e+02"));
    DoDoubleTest(convert("foo %.8e"), val, convert("foo 2.56000000e+002"),
                 convert("foo 2.56000000e+02"));
    DoDoubleTest(convert("foo %014e"), val, convert("foo 02.560000e+002"),
                 convert("foo 002.560000e+02"));
    DoDoubleTest(convert("foo %#e"), val, convert("foo 2.560000e+002"),
                 convert("foo 2.560000e+02"));
    DoDoubleTest(convert("foo %+e"), val, convert("foo +2.560000e+002"),
                 convert("foo +2.560000e+02"));
    DoDoubleTest(convert("foo % e"), val, convert("foo  2.560000e+002"),
                 convert("foo  2.560000e+02"));
    DoDoubleTest(convert("foo %+e"), neg, convert("foo -2.560000e+002"),
                 convert("foo -2.560000e+02"));
    DoDoubleTest(convert("foo % e"), neg, convert("foo -2.560000e+002"),
                 convert("foo -2.560000e+02"));

    PAL_Terminate();
    return PASS;
}
