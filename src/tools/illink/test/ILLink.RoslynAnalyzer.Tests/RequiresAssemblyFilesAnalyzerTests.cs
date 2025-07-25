﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ILLink.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Text;
using Xunit;
using VerifyCS = ILLink.RoslynAnalyzer.Tests.CSharpCodeFixVerifier<
    ILLink.RoslynAnalyzer.DynamicallyAccessedMembersAnalyzer,
    ILLink.CodeFix.RequiresAssemblyFilesCodeFixProvider>;

namespace ILLink.RoslynAnalyzer.Tests
{
    public class RequiresAssemblyFilesAnalyzerTests
    {
        static Task VerifyRequiresAssemblyFilesAnalyzer(string source, params DiagnosticResult[] expected)
        {
            return VerifyRequiresAssemblyFilesAnalyzer(source, null, expected);
        }

        static async Task VerifyRequiresAssemblyFilesAnalyzer(
            string source,
            IEnumerable<MetadataReference>? additionalReferences,
            params DiagnosticResult[] expected)
        {

            await VerifyCS.VerifyAnalyzerAsync(
                source,
                consoleApplication: false,
                TestCaseUtils.UseMSBuildProperties(MSBuildPropertyOptionNames.EnableSingleFileAnalyzer),
                additionalReferences ?? Array.Empty<MetadataReference>(),
                expected);
        }

        static Task VerifyRequiresAssemblyFilesCodeFix(
            string source,
            string fixedSource,
            DiagnosticResult[] baselineExpected,
            DiagnosticResult[] fixedExpected,
            int? numberOfIterations = null)
        {
            var test = new VerifyCS.Test
            {
                TestCode = source,
                FixedCode = fixedSource
            };
            test.ExpectedDiagnostics.AddRange(baselineExpected);
            test.TestState.AnalyzerConfigFiles.Add(
                        ("/.editorconfig", SourceText.From(@$"
is_global = true
build_property.{MSBuildPropertyOptionNames.EnableSingleFileAnalyzer} = true")));
            if (numberOfIterations != null)
            {
                test.NumberOfIncrementalIterations = numberOfIterations;
                test.NumberOfFixAllIterations = numberOfIterations;
            }
            test.FixedState.ExpectedDiagnostics.AddRange(fixedExpected);
            return test.RunAsync();
        }

        [Fact]
        public Task NoDynamicallyAccessedMembersWarningsIfOnlySingleFileAnalyzerIsEnabled()
        {
            var TargetParameterWithAnnotations = $$"""
            using System;
            using System.Diagnostics.CodeAnalysis;

            class C
            {
                public static void Main()
                {
                    MethodCallPattern(typeof(int));
                    AssignmentPattern(typeof(int));
                    ReflectionAccessPattern();
                    FieldAccessPattern();
                    GenericRequirement<int>();
                }

                private static void NeedsPublicMethodsOnParameter(
                    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type parameter)
                {
                }

                private static void MethodCallPattern(Type type)
                {
                    NeedsPublicMethodsOnParameter(type);
                }

                [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)]
                private static Type NeedsPublicMethosOnField;

                private static void AssignmentPattern(Type type)
                {
                    NeedsPublicMethosOnField = type;
                }

                private static void ReflectionAccessPattern()
                {
                    Action<Type> action = NeedsPublicMethodsOnParameter;
                }

                private static void FieldAccessPattern()
                {
                    var i = BeforeFieldInit.StaticField;
                }

                [RequiresUnreferencedCode("BeforeFieldInit")]
                class BeforeFieldInit {
                    public static int StaticField = 0;
                }

                private static void GenericRequirement<T>()
                {
                    new NeedsPublicMethodsOnTypeParameter<T>();
                }

                class NeedsPublicMethodsOnTypeParameter<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>
                {
                }
            }
            """;
            return VerifyRequiresAssemblyFilesAnalyzer(TargetParameterWithAnnotations);
        }

        [Fact]
        public Task SimpleDiagnosticOnEvent()
        {
            var TestRequiresAssemblyFieldsOnEvent = $$"""
            #nullable enable
            using System.Diagnostics.CodeAnalysis;

            class C
            {
                [RequiresAssemblyFiles]
                event System.EventHandler? E;

                void M()
                {
                    E += (sender, e) => { };
                    var evt = E;
                }
            }
            """;
            return VerifyRequiresAssemblyFilesAnalyzer(TestRequiresAssemblyFieldsOnEvent,
                // (11,9): warning IL3002: Using member 'C.E.add' which has 'RequiresAssemblyFilesAttribute' can break functionality when embedded in a single-file app.
                VerifyCS.Diagnostic(DiagnosticId.RequiresAssemblyFiles).WithSpan(11, 9, 11, 32).WithArguments("C.E.add", "", ""));
        }

        [Fact]
        public Task SimpleDiagnosticOnProperty()
        {
            var TestRequiresAssemblyFilesOnProperty = $$"""
            using System.Collections.Generic;
            using System.Diagnostics.CodeAnalysis;

            class C
            {
                [RequiresAssemblyFiles]
                bool P { get; set; }

                void M()
                {
                    P = false;
                    List<bool> b = new List<bool> { P };
                }
            }
            """;
            return VerifyRequiresAssemblyFilesAnalyzer(TestRequiresAssemblyFilesOnProperty,
                // (11,9): warning IL3002: Using member 'C.P.set' which has 'RequiresAssemblyFilesAttribute' can break functionality when embedded in a single-file app.
                VerifyCS.Diagnostic(DiagnosticId.RequiresAssemblyFiles).WithSpan(11, 9, 11, 18).WithArguments("C.P.set", "", ""),
                // (12,41): warning IL3002: Using member 'C.P.get' which has 'RequiresAssemblyFilesAttribute' can break functionality when embedded in a single-file app.
                VerifyCS.Diagnostic(DiagnosticId.RequiresAssemblyFiles).WithSpan(12, 41, 12, 42).WithArguments("C.P.get", "", ""));
        }

        [Fact]
        public Task CallDangerousMethodInsideProperty()
        {
            var TestRequiresAssemblyFilesOnMethodInsideProperty = $$"""
            using System.Diagnostics.CodeAnalysis;

            class C
            {
                bool @field;

                [RequiresAssemblyFiles]
                bool P {
                    get {
                        return @field;
                    }
                    set {
                        CallDangerousMethod();
                        @field = value;
                    }
                }

                [RequiresAssemblyFiles]
                void CallDangerousMethod() {}

                void M()
                {
                    P = false;
                }
            }
            """;
            return VerifyRequiresAssemblyFilesAnalyzer(TestRequiresAssemblyFilesOnMethodInsideProperty,
                // (23,9): warning IL3002: Using member 'C.P.set' which has 'RequiresAssemblyFilesAttribute' can break functionality when embedded in a single-file app.
                VerifyCS.Diagnostic(DiagnosticId.RequiresAssemblyFiles).WithSpan(23, 9, 23, 18).WithArguments("C.P.set", "", ""));
        }

        [Fact]
        public Task RequiresAssemblyFilesWithUrlOnly()
        {
            var TestRequiresAssemblyFilesWithMessageAndUrl = $$"""
            using System.Diagnostics.CodeAnalysis;

            class C
            {
                [RequiresAssemblyFiles(Url = "https://helpurl")]
                void M1()
                {
                }

                void M2()
                {
                    M1();
                }
            }
            """;
            return VerifyRequiresAssemblyFilesAnalyzer(TestRequiresAssemblyFilesWithMessageAndUrl,
                // (12,9): warning IL3002: Using member 'C.M1()' which has 'RequiresAssemblyFilesAttribute' can break functionality when embedded in a single-file app. https://helpurl
                VerifyCS.Diagnostic(DiagnosticId.RequiresAssemblyFiles).WithSpan(12, 9, 12, 11).WithArguments("C.M1()", "", " https://helpurl"));
        }

        [Fact]
        public Task NoDiagnosticIfMethodNotCalled()
        {
            var TestNoDiagnosticIfMethodNotCalled = $$"""
            using System.Diagnostics.CodeAnalysis;

            class C
            {
                [RequiresAssemblyFiles]
                void M() { }
            }
            """;
            return VerifyRequiresAssemblyFilesAnalyzer(TestNoDiagnosticIfMethodNotCalled);
        }

        [Fact]
        public Task NoDiagnosticIsProducedIfCallerIsAnnotated()
        {
            var TestNoDiagnosticIsProducedIfCallerIsAnnotated = $$"""
            using System.Diagnostics.CodeAnalysis;

            class C
            {
                void M1()
                {
                    M2();
                }

                [RequiresAssemblyFiles("Warn from M2")]
                void M2()
                {
                    M3();
                }

                [RequiresAssemblyFiles("Warn from M3")]
                void M3()
                {
                }
            }
            """;
            return VerifyRequiresAssemblyFilesAnalyzer(TestNoDiagnosticIsProducedIfCallerIsAnnotated,
                // (7,9): warning IL3002: Using member 'C.M2()' which has 'RequiresAssemblyFilesAttribute' can break functionality when embedded in a single-file app. Warn from M2.
                VerifyCS.Diagnostic(DiagnosticId.RequiresAssemblyFiles).WithSpan(7, 9, 7, 11).WithArguments("C.M2()", " Warn from M2.", ""));
        }

        [Fact]
        public Task GetExecutingAssemblyLocation()
        {
            const string src = $$"""
            using System.Reflection;
            class C
            {
                public string M() => Assembly.GetExecutingAssembly().Location;
            }
            """;

            return VerifyRequiresAssemblyFilesAnalyzer(src,
                // (5,26): warning IL3000: 'System.Reflection.Assembly.Location' always returns an empty string for assemblies embedded in a single-file app. If the path to the app directory is needed, consider calling 'System.AppContext.BaseDirectory'.
                VerifyCS.Diagnostic(DiagnosticId.AvoidAssemblyLocationInSingleFile).WithSpan(4, 26, 4, 66).WithArguments("System.Reflection.Assembly.Location.get"));
        }

        [Fact]
        public Task GetAssemblyLocationViaAssemblyProperties()
        {
            var src = $$"""
            using System.Reflection;
            class C
            {
                public void M()
                {
                    var a = Assembly.GetExecutingAssembly();
                    _ = a.Location;
                    // below methods are marked as obsolete in 5.0
                    // _ = a.CodeBase;
                    // _ = a.EscapedCodeBase;
                }
            }
            """;
            return VerifyRequiresAssemblyFilesAnalyzer(src,
                // (7,13): warning IL3000: 'System.Reflection.Assembly.Location' always returns an empty string for assemblies embedded in a single-file app. If the path to the app directory is needed, consider calling 'System.AppContext.BaseDirectory'.
                VerifyCS.Diagnostic(DiagnosticId.AvoidAssemblyLocationInSingleFile).WithSpan(7, 13, 7, 23).WithArguments("System.Reflection.Assembly.Location.get")
            );
        }

        [Fact]
        public Task CallKnownDangerousAssemblyMethods()
        {
            var src = $$"""
            using System.Reflection;
            class C
            {
                public void M()
                {
                    var a = Assembly.GetExecutingAssembly();
                    _ = a.GetFile("/some/file/path");
                    _ = a.GetFiles();
                }
            }
            """;
            return VerifyRequiresAssemblyFilesAnalyzer(src,
                // (7,13): warning IL3001: Assemblies embedded in a single-file app cannot have additional files in the manifest.
                VerifyCS.Diagnostic(DiagnosticId.AvoidAssemblyGetFilesInSingleFile).WithSpan(7, 13, 7, 22).WithArguments("System.Reflection.Assembly.GetFile(String)"),
                // (8,13): warning IL3001: Assemblies embedded in a single-file app cannot have additional files in the manifest.
                VerifyCS.Diagnostic(DiagnosticId.AvoidAssemblyGetFilesInSingleFile).WithSpan(8, 13, 8, 23).WithArguments("System.Reflection.Assembly.GetFiles()")
                );
        }

        [Fact]
        public Task CallKnownDangerousAssemblyNameAttributes()
        {
            var src = $$"""
            using System.Reflection;
            class C
            {
                public void M()
                {
                    var a = Assembly.GetExecutingAssembly().GetName();
                    _ = a.CodeBase;
                    _ = a.EscapedCodeBase;
                }
            }
            """;
            return VerifyRequiresAssemblyFilesAnalyzer(src,
                // (7,13): warning SYSLIB0044: 'AssemblyName.CodeBase' is obsolete: 'AssemblyName.CodeBase and AssemblyName.EscapedCodeBase are obsolete. Using them for loading an assembly is not supported.'
                DiagnosticResult.CompilerWarning("SYSLIB0044").WithSpan(7, 13, 7, 23).WithArguments("System.Reflection.AssemblyName.CodeBase", "AssemblyName.CodeBase and AssemblyName.EscapedCodeBase are obsolete. Using them for loading an assembly is not supported."),
                // (8,13): warning SYSLIB0044: 'AssemblyName.EscapedCodeBase' is obsolete: 'AssemblyName.CodeBase and AssemblyName.EscapedCodeBase are obsolete. Using them for loading an assembly is not supported.'
                DiagnosticResult.CompilerWarning("SYSLIB0044").WithSpan(8, 13, 8, 30).WithArguments("System.Reflection.AssemblyName.EscapedCodeBase", "AssemblyName.CodeBase and AssemblyName.EscapedCodeBase are obsolete. Using them for loading an assembly is not supported."),
                // (7,13): warning IL3000: 'System.Reflection.AssemblyName.CodeBase' always returns an empty string for assemblies embedded in a single-file app. If the path to the app directory is needed, consider calling 'System.AppContext.BaseDirectory'.
                VerifyCS.Diagnostic(DiagnosticId.AvoidAssemblyLocationInSingleFile).WithSpan(7, 13, 7, 23).WithArguments("System.Reflection.AssemblyName.CodeBase.get"),
                // (8,13): warning IL3000: 'System.Reflection.AssemblyName.EscapedCodeBase' always returns an empty string for assemblies embedded in a single-file app. If the path to the app directory is needed, consider calling 'System.AppContext.BaseDirectory'.
                VerifyCS.Diagnostic(DiagnosticId.AvoidAssemblyLocationInSingleFile).WithSpan(8, 13, 8, 30).WithArguments("System.Reflection.AssemblyName.EscapedCodeBase.get")
                );
        }

        [Fact]
        public Task GetAssemblyLocationFalsePositive()
        {
            // This is an OK use of Location and GetFile since these assemblies were loaded from
            // a file, but the analyzer is conservative
            var src = $$"""
            using System.Reflection;
            class C
            {
                public void M()
                {
                    var a = Assembly.LoadFrom("/some/path/not/in/bundle");
                    _ = a.Location;
                    _ = a.GetFiles();
                }
            }
            """;
            return VerifyRequiresAssemblyFilesAnalyzer(src,
                // (7,13): warning IL3000: 'System.Reflection.Assembly.Location' always returns an empty string for assemblies embedded in a single-file app. If the path to the app directory is needed, consider calling 'System.AppContext.BaseDirectory'.
                VerifyCS.Diagnostic(DiagnosticId.AvoidAssemblyLocationInSingleFile).WithSpan(7, 13, 7, 23).WithArguments("System.Reflection.Assembly.Location.get"),
                // (8,13): warning IL3001: Assemblies embedded in a single-file app cannot have additional files in the manifest.
                VerifyCS.Diagnostic(DiagnosticId.AvoidAssemblyGetFilesInSingleFile).WithSpan(8, 13, 8, 23).WithArguments("System.Reflection.Assembly.GetFiles()")
                );
        }

        [Fact]
        public Task PublishSingleFileIsNotSet()
        {
            var src = $$"""
            using System.Reflection;
            class C
            {
                public void M()
                {
                    var a = Assembly.GetExecutingAssembly().Location;
                }
            }
            """;
            // If 'PublishSingleFile' is not set to true, no diagnostics should be produced by the analyzer. This will
            // effectively verify that the number of produced diagnostics matches the number of expected ones (zero).
            return VerifyCS.VerifyAnalyzerAsync(src, consoleApplication: false);
        }

        [Fact]
        public Task SupressWarningsWithRequiresAssemblyFiles()
        {
            const string src = $$"""
            using System.Reflection;
            using System.Diagnostics.CodeAnalysis;
            class C
            {
                [RequiresAssemblyFiles]
                public void M()
                {
                    var a = Assembly.GetExecutingAssembly();
                    _ = a.Location;
                    var b = Assembly.GetExecutingAssembly();
                    _ = b.GetFile("/some/file/path");
                    _ = b.GetFiles();
                }
            }
            """;

            return VerifyRequiresAssemblyFilesAnalyzer(src);
        }

        [Fact]
        public Task RequiresAssemblyFilesDiagnosticFix()
        {
            var test = $$"""
            using System.Diagnostics.CodeAnalysis;
            public class C
            {
                [RequiresAssemblyFiles("message")]
                public int M1() => 0;
                int M2() => M1();
            }
            class D
            {
                public int M3(C c) => c.M1();
                public class E
                {
                    public int M4(C c) => c.M1();
                }
            }
            public class E
            {
                public class F
                {
                    public int M5(C c) => c.M1();
                }
            }
            """;
            var fixtest = $$"""
            using System.Diagnostics.CodeAnalysis;
            public class C
            {
                [RequiresAssemblyFiles("message")]
                public int M1() => 0;
                [RequiresAssemblyFiles("Calls C.M1()")]
                int M2() => M1();
            }
            class D
            {
                [RequiresAssemblyFiles("Calls C.M1()")]
                public int M3(C c) => c.M1();
                public class E
                {
                    [RequiresAssemblyFiles("Calls C.M1()")]
                    public int M4(C c) => c.M1();
                }
            }
            public class E
            {
                public class F
                {
                    [RequiresAssemblyFiles()]
                    public int M5(C c) => c.M1();
                }
            }
            """;
            return VerifyRequiresAssemblyFilesCodeFix(
                source: test,
                fixedSource: fixtest,
                baselineExpected: new[] {
                    // /0/Test0.cs(6,17): warning IL3002: Using member 'C.M1()' which has 'RequiresAssemblyFilesAttribute' can break functionality when embedded in a single-file app. message.
                    VerifyCS.Diagnostic(DiagnosticId.RequiresAssemblyFiles).WithSpan(6, 17, 6, 19).WithArguments("C.M1()", " message.", ""),
                    // /0/Test0.cs(10,27): warning IL3002: Using member 'C.M1()' which has 'RequiresAssemblyFilesAttribute' can break functionality when embedded in a single-file app. message.
                    VerifyCS.Diagnostic(DiagnosticId.RequiresAssemblyFiles).WithSpan(10, 27, 10, 31).WithArguments("C.M1()", " message.", ""),
                    // /0/Test0.cs(13,31): warning IL3002: Using member 'C.M1()' which has 'RequiresAssemblyFilesAttribute' can break functionality when embedded in a single-file app. message.
                    VerifyCS.Diagnostic(DiagnosticId.RequiresAssemblyFiles).WithSpan(13, 31, 13, 35).WithArguments("C.M1()", " message.", ""),
                    // /0/Test0.cs(20,31): warning IL3002: Using member 'C.M1()' which has 'RequiresAssemblyFilesAttribute' can break functionality when embedded in a single-file app. message.
                    VerifyCS.Diagnostic(DiagnosticId.RequiresAssemblyFiles).WithSpan(20, 31, 20, 35).WithArguments("C.M1()", " message.", "")
                },
                fixedExpected: Array.Empty<DiagnosticResult>());
        }

        [Fact]
        public Task FixInSingleFileSpecialCases()
        {
            var test = $$"""
            using System.Reflection;
            using System.Diagnostics.CodeAnalysis;
            public class C
            {
                public static Assembly assembly = Assembly.LoadFrom("/some/path/not/in/bundle");
                public string M1() => assembly.Location;
                public void M2() {
                    _ = assembly.GetFiles();
                }
            }
            """;
            var fixtest = $$"""
            using System.Reflection;
            using System.Diagnostics.CodeAnalysis;
            public class C
            {
                public static Assembly assembly = Assembly.LoadFrom("/some/path/not/in/bundle");

                [RequiresAssemblyFiles()]
                public string M1() => assembly.Location;

                [RequiresAssemblyFiles()]
                public void M2() {
                    _ = assembly.GetFiles();
                }
            }
            """;
            return VerifyRequiresAssemblyFilesCodeFix(
                source: test,
                fixedSource: fixtest,
                baselineExpected: new[] {
                    // /0/Test0.cs(6,27): warning IL3000: 'System.Reflection.Assembly.Location' always returns an empty string for assemblies embedded in a single-file app. If the path to the app directory is needed, consider calling 'System.AppContext.BaseDirectory'.
                    VerifyCS.Diagnostic(DiagnosticId.AvoidAssemblyLocationInSingleFile).WithSpan(6, 27, 6, 44).WithArguments("System.Reflection.Assembly.Location.get"),
                    // /0/Test0.cs(8,13): warning IL3001: 'System.Reflection.Assembly.GetFiles()' will throw for assemblies embedded in a single-file app
                    VerifyCS.Diagnostic(DiagnosticId.AvoidAssemblyGetFilesInSingleFile).WithSpan(8, 13, 8, 30).WithArguments("System.Reflection.Assembly.GetFiles()"),
                },
                fixedExpected: Array.Empty<DiagnosticResult>());
        }

        [Fact]
        public Task FixInPropertyDecl()
        {
            var src = $$"""
            using System;
            using System.Diagnostics.CodeAnalysis;

            public class C
            {
                [RequiresAssemblyFiles("message")]
                public int M1() => 0;

                int M2 => M1();
            }
            """;
            var fix = $$"""
            using System;
            using System.Diagnostics.CodeAnalysis;

            public class C
            {
                [RequiresAssemblyFiles("message")]
                public int M1() => 0;

                [RequiresAssemblyFiles("Calls C.M1()")]
                int M2 => M1();
            }
            """;
            return VerifyRequiresAssemblyFilesCodeFix(
                source: src,
                fixedSource: fix,
                baselineExpected: new[] {
                    // /0/Test0.cs(9,15): warning IL3002: Using member 'C.M1()' which has 'RequiresAssemblyFilesAttribute' can break functionality when embedded in a single-file app. message.
                    VerifyCS.Diagnostic(DiagnosticId.RequiresAssemblyFiles).WithSpan(9, 15, 9, 17).WithArguments("C.M1()", " message.", "")
                },
                fixedExpected: Array.Empty<DiagnosticResult>());
        }

        [Fact]
        public Task FixInPropertyAccessor()
        {
            var src = $$"""
            using System;
            using System.Diagnostics.CodeAnalysis;

            public class C
            {
                [RequiresAssemblyFilesAttribute("message")]
                public int M1() => 0;

                public int field;

                private int M2 {
                    get { return M1(); }
                    set { field = M1(); }
                }
            }
            """;
            var fix = $$"""
            using System;
            using System.Diagnostics.CodeAnalysis;

            public class C
            {
                [RequiresAssemblyFilesAttribute("message")]
                public int M1() => 0;

                public int field;

                private int M2 {
                    [RequiresAssemblyFiles("Calls C.M1()")]
                    get { return M1(); }

                    [RequiresAssemblyFiles("Calls C.M1()")]
                    set { field = M1(); }
                }
            }
            """;
            var diag = new[] {
                // /0/Test0.cs(12,22): warning IL3002: Using member 'C.M1()' which has 'RequiresAssemblyFilesAttribute' can break functionality when embedded in a single-file app. message.
                VerifyCS.Diagnostic(DiagnosticId.RequiresAssemblyFiles).WithSpan(12, 22, 12, 24).WithArguments("C.M1()", " message.", ""),
                // /0/Test0.cs(13,23): warning IL3002: Using member 'C.M1()' which has 'RequiresAssemblyFilesAttribute' can break functionality when embedded in a single-file app. message.
                VerifyCS.Diagnostic(DiagnosticId.RequiresAssemblyFiles).WithSpan(13, 23, 13, 25).WithArguments("C.M1()", " message.", "")
            };
            return VerifyRequiresAssemblyFilesCodeFix(src, fix, diag, Array.Empty<DiagnosticResult>());
        }

        [Fact]
        public Task FixInField()
        {
            var src = $$"""
            using System;
            using System.Diagnostics.CodeAnalysis;
            class C
            {
                public static Lazy<C> _default = new Lazy<C>(InitC);
                public static C Default => _default.Value;

                [RequiresAssemblyFiles]
                public static C InitC() {
                    C cObject = new C();
                    return cObject;
                }
            }
            """;

            var diag = new[] {
                // /0/Test0.cs(5,50): warning IL3002: Using member 'C.InitC()' which has 'RequiresAssemblyFilesAttribute' can break functionality when embedded in a single-file app.
                VerifyCS.Diagnostic(DiagnosticId.RequiresAssemblyFiles).WithSpan(5, 50, 5, 55).WithArguments("C.InitC()", "", ""),
            };
            return VerifyRequiresAssemblyFilesCodeFix(src, src, diag, diag);
        }

        [Fact]
        public Task FixInLocalFunc()
        {
            var src = $$"""
            using System;
            using System.Diagnostics.CodeAnalysis;

            public class C
            {
                [RequiresAssemblyFiles("message")]
                public int M1() => 0;

                Action M2()
                {
                    void Wrapper() => M1();
                    return Wrapper;
                }
            }
            """;
            var fix = $$"""
            using System;
            using System.Diagnostics.CodeAnalysis;

            public class C
            {
                [RequiresAssemblyFiles("message")]
                public int M1() => 0;

                [RequiresAssemblyFiles("Calls Wrapper()")]
                Action M2()
                {
                    [RequiresAssemblyFiles("Calls C.M1()")] void Wrapper() => M1();
                    return Wrapper;
                }
            }
            """;
            // Roslyn currently doesn't simplify the attribute name properly, see https://github.com/dotnet/roslyn/issues/52039
            return VerifyRequiresAssemblyFilesCodeFix(
                source: src,
                fixedSource: fix,
                baselineExpected: new[] {
                    // /0/Test0.cs(11,27): warning IL3002: Using member 'C.M1()' which has 'RequiresAssemblyFilesAttribute' can break functionality when embedded in a single-file app. message.
                    VerifyCS.Diagnostic(DiagnosticId.RequiresAssemblyFiles).WithSpan(11, 27, 11, 29).WithArguments("C.M1()", " message.", "")
                },
                fixedExpected: Array.Empty<DiagnosticResult>(),
                numberOfIterations: 2);
        }

        [Fact]
        public Task FixInCtor()
        {
            var src = $$"""
            using System;
            using System.Diagnostics.CodeAnalysis;

            public class C
            {
                [RequiresAssemblyFiles("message")]
                public int M1() => 0;

                public C() => M1();
            }
            """;
            var fix = $$"""
            using System;
            using System.Diagnostics.CodeAnalysis;

            public class C
            {
                [RequiresAssemblyFiles("message")]
                public int M1() => 0;

                [RequiresAssemblyFiles()]
                public C() => M1();
            }
            """;
            return VerifyRequiresAssemblyFilesCodeFix(
                source: src,
                fixedSource: fix,
                baselineExpected: new[] {
                    // /0/Test0.cs(9,19): warning IL3002: Using member 'C.M1()' which has 'RequiresAssemblyFilesAttribute' can break functionality when embedded in a single-file app. message.
                    VerifyCS.Diagnostic(DiagnosticId.RequiresAssemblyFiles).WithSpan(9, 19, 9, 21).WithArguments("C.M1()", " message.", "")
                },
                fixedExpected: Array.Empty<DiagnosticResult>());
        }

        [Fact]
        public Task FixInEvent()
        {
            var src = $$"""
            using System;
            using System.Diagnostics.CodeAnalysis;

            public class C
            {
                [RequiresAssemblyFiles("message")]
                public int M1() => 0;

                public event EventHandler E1
                {
                    add
                    {
                        var a = M1();
                    }
                    remove { }
                }
            }
            """;
            var fix = $$"""
            using System;
            using System.Diagnostics.CodeAnalysis;

            public class C
            {
                [RequiresAssemblyFiles("message")]
                public int M1() => 0;

                public event EventHandler E1
                {
                    [RequiresAssemblyFiles()]
                    add
                    {
                        var a = M1();
                    }
                    remove { }
                }
            }
            """;
            return VerifyRequiresAssemblyFilesCodeFix(
                source: src,
                fixedSource: fix,
                baselineExpected: new[] {
                    // /0/Test0.cs(13,21): warning IL3002: Using method 'C.M1()' which has 'RequiresAssemblyFilesAttribute' can break functionality when trimming application code. message.
                    VerifyCS.Diagnostic(DiagnosticId.RequiresAssemblyFiles).WithSpan(13, 21, 13, 23).WithArguments("C.M1()", " message.", "")
                },
                fixedExpected: Array.Empty<DiagnosticResult>());
        }
    }
}
