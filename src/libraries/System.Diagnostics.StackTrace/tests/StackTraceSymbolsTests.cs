// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using Xunit;

namespace System.Diagnostics.SymbolStore.Tests
{
    public class StackTraceSymbolsTests
    {
        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.HasAssemblyFiles))]
        [SkipOnPlatform(TestPlatforms.iOS | TestPlatforms.tvOS | TestPlatforms.MacCatalyst | TestPlatforms.Android, "Symbols are in a different location on mobile platforms")]
        public void StackTraceSymbolsDoNotLockFile()
        {
            var asmPath = AssemblyPathHelper.GetAssemblyLocation(typeof(StackTraceSymbolsTests).Assembly);
            var pdbPath = Path.ChangeExtension(asmPath, ".pdb");

            Assert.True(File.Exists(pdbPath));
            new StackTrace(true).GetFrames();
            File.Move(pdbPath, pdbPath);
        }
    }
}
