﻿Imports System.IO.Packaging
Imports System.IO
Imports System.Threading
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports System.Reflection.PortableExecutable
Imports System.Reflection.Metadata
Imports System.Runtime.InteropServices

' Set the global XML namespace to be the NuSpec namespace. This will simplify 
' the building of xml literals in this file
Imports <xmlns="http://schemas.microsoft.com/packaging/2011/08/nuspec.xsd">

Public Class BuildDevDivInsertionFiles
    Private Const DevDivInsertionFilesDirName = "DevDivInsertionFiles"
    Private Const DevDivPackagesDirName = "DevDivPackages"
    Private Const DevDivVsixDirName = "DevDivVsix"
    Private Const ExternalApisDirName = "ExternalAPIs"
    Private Const PublicKeyToken = "31BF3856AD364E35"

    Private ReadOnly _binDirectory As String
    Private ReadOnly _outputDirectory As String
    Private ReadOnly _outputPackageDirectory As String
    Private ReadOnly _setupDirectory As String
    Private ReadOnly _nugetPackageRoot As String
    Private ReadOnly _nuspecDirectory As String
    Private ReadOnly _pathMap As Dictionary(Of String, String)
    Private ReadOnly _verbose As Boolean

    Private Sub New(args As String())
        _binDirectory = Path.GetFullPath(args(0))

        Dim repoDirectory = Path.GetFullPath(args(1))
        _setupDirectory = Path.Combine(repoDirectory, "src\Setup")
        _nuspecDirectory = Path.Combine(repoDirectory, "src\Nuget")
        _nugetPackageRoot = Path.GetFullPath(args(2))
        _verbose = args.Last() = "/verbose"
        _outputDirectory = Path.Combine(_binDirectory, DevDivInsertionFilesDirName)
        _outputPackageDirectory = Path.Combine(_binDirectory, DevDivPackagesDirName)
        _pathMap = CreatePathMap()
    End Sub

    Public Shared Function Main(args As String()) As Integer
        If args.Length < 3 Then
            Console.WriteLine("Expected arguments: <bin dir> <setup dir> <nuget root dir> [/verbose]")
            Console.WriteLine($"Actual argument count is {args.Length}")
            Return 1
        End If

        Try
            Call New BuildDevDivInsertionFiles(args).Execute()
            Return 0
        Catch ex As Exception
            Console.Error.WriteLine(ex.ToString())
            Return 1
        End Try
    End Function

    Private ReadOnly VsixContentsToSkip As String() = {
        "Microsoft.Data.ConnectionUI.dll",
        "Microsoft.TeamFoundation.TestManagement.Client.dll",
        "Microsoft.TeamFoundation.TestManagement.Common.dll",
        "Microsoft.VisualStudio.CallHierarchy.Package.Definitions.dll",
        "Microsoft.VisualStudio.CodeAnalysis.Sdk.UI.dll",
        "Microsoft.VisualStudio.Data.dll",
        "Microsoft.VisualStudio.QualityTools.OperationalStore.ClientHelper.dll",
        "Microsoft.VisualStudio.QualityTools.WarehouseCommon.dll",
        "Microsoft.VisualStudio.TeamSystem.Common.dll",
        "Microsoft.VisualStudio.TeamSystem.Common.Framework.dll",
        "Microsoft.VisualStudio.TeamSystem.Integration.dll",
        "SQLitePCLRaw.batteries_green.dll",
        "SQLitePCLRaw.batteries_v2.dll",
        "SQLitePCLRaw.core.dll",
        "SQLitePCLRaw.provider.e_sqlite3.dll",
        "e_sqlite3.dll",
        "Newtonsoft.Json.dll",
        "StreamJsonRpc.dll",
        "StreamJsonRpc.resources.dll",
        "roslynCodeAnalysis.servicehub.service.json",
        "roslynRemoteHost.servicehub.service.json",
        "roslynSnapshot.servicehub.service.json",
        "roslynRemoteSymbolSearchUpdateEngine.servicehub.service.json",
        "roslynCodeAnalysis64.servicehub.service.json",
        "roslynRemoteHost64.servicehub.service.json",
        "roslynSnapshot64.servicehub.service.json",
        "roslynRemoteSymbolSearchUpdateEngine64.servicehub.service.json",
        "Microsoft.Build.Conversion.Core.dll",
        "Microsoft.Build.dll",
        "Microsoft.Build.Engine.dll",
        "Microsoft.Build.Framework.dll",
        "Microsoft.Build.Tasks.Core.dll",
        "Microsoft.Build.Utilities.Core.dll",
        "Microsoft.VisualStudio.Threading.dll",
        "Microsoft.VisualStudio.Threading.resources.dll",
        "Microsoft.VisualStudio.Validation.dll",
        "System.Composition.AttributedModel.dll",
        "System.Composition.Runtime.dll",
        "System.Composition.Convention.resources.dll",
        "System.Composition.Hosting.resources.dll",
        "System.Composition.TypedParts.resources.dll",
        "Microsoft.CodeAnalysis.VisualBasic.Scripting.dll",
        "Microsoft.CodeAnalysis.VisualBasic.InteractiveEditorFeatures.dll",
        "Microsoft.VisualStudio.VisualBasic.Repl.dll",
        "Microsoft.VisualStudio.VisualBasic.Repl.pkgdef",
        "VisualBasicInteractive.png",
        "VisualBasicInteractive.rsp",
        "VisualBasicInteractivePackageRegistration.pkgdef"
    }

    Private ReadOnly VsixesToInstall As String() = {
        "Vsix\VisualStudioSetup\Roslyn.VisualStudio.Setup.vsix",
        "Vsix\ExpressionEvaluatorPackage\ExpressionEvaluatorPackage.vsix",
        "Vsix\VisualStudioInteractiveComponents\Roslyn.VisualStudio.InteractiveComponents.vsix"
    }

    Private Sub DeleteDirContents(dir As String)
        If Directory.Exists(dir) Then
            ' Delete everything within it. We'll keep the top-level one around.
            For Each file In New DirectoryInfo(dir).GetFiles()
                file.Delete()
            Next

            For Each directory In New DirectoryInfo(dir).GetDirectories()
                directory.Delete(recursive:=True)
            Next
        End If
    End Sub

    Public Sub Execute()
        Retry(Sub()
                  DeleteDirContents(_outputDirectory)
                  DeleteDirContents(_outputPackageDirectory)
              End Sub)

        ' Build a dependency map
        Dim dependencies = BuildDependencyMap(_binDirectory)
        GenerateContractsListMsbuild(dependencies)
        GenerateAssemblyVersionList(dependencies)
        GeneratePortableFacadesSwrFile(dependencies)
        CopyDependencies(dependencies)

        ' List of files to add to VS.ExternalAPI.Roslyn.nuspec.
        ' Paths are relative to input directory.
        ' Files in DevDivInsertionFiles\ExternalAPIs don't need to be added, they are included in the nuspec using a pattern.
        ' May contain duplicates.
        Dim filesToInsert = New List(Of NugetFileInfo)

        ' And now copy over all our core compiler binaries and related files
        ' Build tools setup authoring depends on these files being inserted.
        For Each fileName In GetCompilerInsertFiles()
            Dim dependency As DependencyInfo = Nothing
            If Not dependencies.TryGetValue(fileName, dependency) Then
                AddXmlDocumentationFile(filesToInsert, fileName)
                filesToInsert.Add(New NugetFileInfo(GetMappedPath(fileName)))
            End If
        Next

        GenerateVSToolsRoslynCoreXTNuspec()

        ' Copy over the files in the NetFX20 subdirectory (identical, except for references and Authenticode signing).
        ' These are for msvsmon, whose setup authoring is done by the debugger.
        For Each folder In Directory.EnumerateDirectories(Path.Combine(_binDirectory, "Dlls"), "*.NetFX20")
            For Each eePath In Directory.EnumerateFiles(folder, "*.ExpressionEvaluator.*.dll", SearchOption.TopDirectoryOnly)
                filesToInsert.Add(New NugetFileInfo(GetPathRelativeToBinaries(eePath), GetPathRelativeToBinaries(folder)))
            Next
        Next

        ProcessVsixFiles(filesToInsert, dependencies)

        ' Generate Roslyn.nuspec:
        GenerateRoslynNuSpec(filesToInsert)
    End Sub

    Private Function GetPathRelativeToBinaries(p As String) As String
        Debug.Assert(p.StartsWith(_binDirectory, StringComparison.OrdinalIgnoreCase))
        p = p.Substring(_binDirectory.Length)
        If Not String.IsNullOrEmpty(p) AndAlso p(0) = "\"c Then
            p = p.Substring(1)
        End If
        Return p
    End Function

    Private Shared Function GetExternalApiDirectory() As String
        Return Path.Combine(ExternalApisDirName, "Roslyn")
    End Function

    Private Shared Function GetExternalApiDirectory(dependency As DependencyInfo, contract As Boolean) As String
        Return If(dependency Is Nothing,
                GetExternalApiDirectory(),
                Path.Combine(ExternalApisDirName, dependency.PackageName, If(contract, dependency.ContractDir, dependency.ImplementationDir)))
    End Function

    Private Class NugetFileInfo
        Implements IEquatable(Of NugetFileInfo)

        Public Path As String
        Public Target As String

        Sub New(path As String, Optional target As String = "")
            If IO.Path.IsPathRooted(path) Then
                Throw New ArgumentException($"Parameter {NameOf(path)} cannot be absolute: {path}")
            End If

            If IO.Path.IsPathRooted(target) Then
                Throw New ArgumentException($"Parameter {NameOf(target)} cannot be absolute: {target}")
            End If

            Me.Path = path
            Me.Target = target
        End Sub

        Public Overrides Function Equals(obj As Object) As Boolean
            Return IEquatableEquals(TryCast(obj, NugetFileInfo))
        End Function

        Public Overrides Function GetHashCode() As Integer
            Return StringComparer.OrdinalIgnoreCase.GetHashCode(Path) Xor StringComparer.OrdinalIgnoreCase.GetHashCode(Target)
        End Function

        Public Function IEquatableEquals(other As NugetFileInfo) As Boolean Implements IEquatable(Of NugetFileInfo).Equals
            Return other IsNot Nothing AndAlso
                    StringComparer.OrdinalIgnoreCase.Equals(Path, other.Path) AndAlso
                    StringComparer.OrdinalIgnoreCase.Equals(Target, other.Target)
        End Function

        Public Overrides Function ToString() As String
            Return Path
        End Function
    End Class

    Private Class DependencyInfo
        ' For example, "ref/net46"
        Public ContractDir As String

        ' For example, "lib/net46"
        Public ImplementationDir As String

        ' For example, "System.AppContext"
        Public PackageName As String

        ' For example, "4.1.0"
        Public PackageVersion As String

        Public IsNative As Boolean
        Public IsFacade As Boolean

        Sub New(contractDir As String, implementationDir As String, packageName As String, packageVersion As String, isNative As Boolean, isFacade As Boolean)
            Me.ContractDir = contractDir
            Me.ImplementationDir = implementationDir
            Me.PackageName = packageName
            Me.PackageVersion = packageVersion
            Me.IsNative = isNative
            Me.IsFacade = isFacade
        End Sub
    End Class

    Private Function BuildDependencyMap(inputDirectory As String) As Dictionary(Of String, DependencyInfo)
        Dim result = New Dictionary(Of String, DependencyInfo)
        Dim objDir = Path.Combine(Path.GetDirectoryName(_binDirectory.TrimEnd(Path.DirectorySeparatorChar)), "Obj")
        Dim files = New List(Of String)
        files.Add(Path.Combine(objDir, "CompilerExtension\project.assets.json"))
        files.Add(Path.Combine(objDir, "VisualStudioSetup.Dependencies\project.assets.json"))

        For Each projectLockJson In files
            Dim items = JsonConvert.DeserializeObject(File.ReadAllText(projectLockJson))
            Const targetFx = ".NETFramework,Version=v4.6/win"

            Dim targetObj = DirectCast(DirectCast(DirectCast(items, JObject).Property("targets")?.Value, JObject).Property(targetFx)?.Value, JObject)
            If targetObj Is Nothing Then
                Throw New InvalidDataException($"Expected platform Not found in '{projectLockJson}': '{targetFx}'")
            End If

            For Each targetProperty In targetObj.Properties
                Dim packageNameAndVersion = targetProperty.Name.Split("/"c)
                Dim packageName = packageNameAndVersion(0)
                Dim packageVersion = packageNameAndVersion(1)
                Dim packageObj = DirectCast(targetProperty.Value, JObject)

                If packageObj.Property("type").Value.Value(Of String) = "project" Then
                    Continue For
                End If

                Dim contracts = DirectCast(packageObj.Property("compile")?.Value, JObject)
                Dim runtime = DirectCast(packageObj.Property("runtime")?.Value, JObject)
                Dim native = DirectCast(packageObj.Property("native")?.Value, JObject)
                Dim frameworkAssemblies = packageObj.Property("frameworkAssemblies")?.Value

                Dim implementations = If(runtime, native)
                If implementations Is Nothing Then
                    Continue For
                End If

                ' No need to insert Visual Studio packages back into the repository itself
                If packageName.StartsWith("Microsoft.VisualStudio.") OrElse
                   packageName = "EnvDTE" OrElse
                   packageName = "stdole" OrElse
                   packageName.StartsWith("Microsoft.Build") OrElse
                   packageName = "Microsoft.Composition" OrElse
                   packageName = "System.Net.Http" OrElse
                   packageName = "System.Diagnostics.DiagnosticSource" Then
                    Continue For
                End If

                For Each assemblyProperty In implementations.Properties()
                    Dim fileName = Path.GetFileName(assemblyProperty.Name)
                    If fileName <> "_._" Then

                        Dim existingDependency As DependencyInfo = Nothing
                        If result.TryGetValue(fileName, existingDependency) Then

                            If existingDependency.PackageVersion <> packageVersion Then
                                Throw New InvalidOperationException($"Found multiple versions of package '{existingDependency.PackageName}': {existingDependency.PackageVersion} and {packageVersion}")
                            End If

                            Continue For
                        End If

                        Dim runtimeTarget = Path.GetDirectoryName(assemblyProperty.Name)

                        Dim compileDll = contracts?.Properties().Select(Function(p) p.Name).Where(Function(n) Path.GetFileName(n) = fileName).SingleOrDefault()
                        Dim compileTarget = If(compileDll IsNot Nothing, Path.GetDirectoryName(compileDll), Nothing)

                        result.Add(fileName, New DependencyInfo(compileTarget,
                                                                runtimeTarget,
                                                                packageName,
                                                                packageVersion,
                                                                isNative:=native IsNot Nothing,
                                                                isFacade:=(frameworkAssemblies IsNot Nothing AndAlso
                                                                           packageName <> "Microsoft.Build" AndAlso
                                                                           packageName <> "Microsoft.DiaSymReader") OrElse
                                                                           packageName = "System.IO.Pipes.AccessControl"))
                    End If
                Next
            Next
        Next

        Return result
    End Function

    Private Sub GenerateContractsListMsbuild(dependencies As IReadOnlyDictionary(Of String, DependencyInfo))
        Using writer = New StreamWriter(GetAbsolutePathInOutputDirectory("ProductData\ContractAssemblies.props"))
            writer.WriteLine("<?xml version=""1.0"" encoding=""utf-8""?>")
            writer.WriteLine("<Project ToolsVersion=""14.0"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">")
            writer.WriteLine("  <!-- Generated file, do not directly edit. Contact mlinfraswat@microsoft.com if you need to add a library that's not listed -->")
            writer.WriteLine("  <PropertyGroup>")

            For Each entry In GetContracts(dependencies)
                writer.WriteLine($"    <{entry.Key}>{entry.Value}</{entry.Key}>")
            Next

            writer.WriteLine("  </PropertyGroup>")
            writer.WriteLine("</Project>")
        End Using
    End Sub

    Private Iterator Function GetContracts(dependencies As IReadOnlyDictionary(Of String, DependencyInfo)) As IEnumerable(Of KeyValuePair(Of String, String))
        For Each entry In dependencies.OrderBy(Function(e) e.Key)
            Dim fileName = entry.Key
            Dim dependency = entry.Value
            If dependency.ContractDir IsNot Nothing Then
                Dim variableName = "FXContract_" + Path.GetFileNameWithoutExtension(fileName).Replace(".", "_")
                Dim dir = Path.Combine(dependency.PackageName, dependency.ContractDir)
                Yield New KeyValuePair(Of String, String)(variableName, Path.Combine(dir, fileName))
            End If
        Next
    End Function

    Private Iterator Function GetImplementations(dependencies As IReadOnlyDictionary(Of String, DependencyInfo)) As IEnumerable(Of KeyValuePair(Of String, String))
        For Each entry In dependencies.OrderBy(Function(e) e.Key)
            Dim fileName = entry.Key
            Dim dependency = entry.Value
            Dim variableName = "CoreFXLib_" + Path.GetFileNameWithoutExtension(fileName).Replace(".", "_")
            Dim dir = Path.Combine(dependency.PackageName, dependency.ImplementationDir)
            Yield New KeyValuePair(Of String, String)(variableName, Path.Combine(dir, fileName))
        Next
    End Function

    Private Sub GenerateAssemblyVersionList(dependencies As IReadOnlyDictionary(Of String, DependencyInfo))
        Using writer = New StreamWriter(GetAbsolutePathInOutputDirectory("DependentAssemblyVersions.csv"))
            For Each entry In dependencies.OrderBy(Function(e) e.Key)
                Dim fileName = entry.Key
                Dim dependency = entry.Value
                If Not dependency.IsNative Then

                    Dim version As Version
                    Dim dllPath = Path.Combine(_nugetPackageRoot, dependency.PackageName, dependency.PackageVersion, dependency.ImplementationDir, fileName)

                    Using peReader = New PEReader(File.OpenRead(dllPath))
                        version = peReader.GetMetadataReader().GetAssemblyDefinition().Version
                    End Using

                    writer.WriteLine($"{Path.GetFileNameWithoutExtension(fileName)},{version}")
                End If
            Next
        End Using
    End Sub

    Private Sub CopyDependencies(dependencies As IReadOnlyDictionary(Of String, DependencyInfo))
        For Each dependency In dependencies.Values
            Dim nupkg = $"{dependency.PackageName}.{dependency.PackageVersion}.nupkg"
            Dim srcPath = Path.Combine(_nugetPackageRoot, dependency.PackageName, dependency.PackageVersion, nupkg)
            Dim dstDir = Path.Combine(_outputPackageDirectory, If(dependency.IsNative, "NativeDependencies", "ManagedDependencies"))
            Dim dstPath = Path.Combine(dstDir, nupkg)

            Directory.CreateDirectory(dstDir)
            File.Copy(srcPath, dstPath, overwrite:=True)
        Next
    End Sub

    Private Sub GeneratePortableFacadesSwrFile(dependencies As Dictionary(Of String, DependencyInfo))
        Dim facades = dependencies.Where(Function(e) e.Value.IsFacade).OrderBy(Function(e) e.Key).ToArray()

        Dim swrPath = Path.Combine(_setupDirectory, DevDivVsixDirName, "PortableFacades", "PortableFacades.swr")
        Dim swrVersion As Version = Nothing
        Dim swrFiles As IEnumerable(Of String) = Nothing
        ParseSwrFile(swrPath, swrVersion, swrFiles)

        Dim expectedFiles = New List(Of String)
        For Each entry In facades
            Dim dependency = entry.Value
            Dim fileName = entry.Key
            Dim implPath = IO.Path.Combine(dependency.PackageName, dependency.PackageVersion, dependency.ImplementationDir, fileName)
            expectedFiles.Add($"    file source=""$(NuGetPackageRoot)\{implPath}"" vs.file.ngen=yes")
        Next

        If Not swrFiles.SequenceEqual(expectedFiles) Then
            Using writer = New StreamWriter(File.Open(swrPath, FileMode.Truncate, FileAccess.Write))
                writer.WriteLine("use vs")
                writer.WriteLine()
                writer.WriteLine($"package name=PortableFacades")
                writer.WriteLine($"        version={New Version(swrVersion.Major, swrVersion.Minor + 1, 0, 0)}")
                writer.WriteLine()
                writer.WriteLine("folder InstallDir:\Common7\IDE\PrivateAssemblies")

                For Each entry In expectedFiles
                    writer.WriteLine(entry)
                Next
            End Using

            Throw New Exception($"The content of file {swrPath} is not up-to-date. The file has been updated to reflect the changes in dependencies made in the repo " &
                                $"(in files {Path.Combine(_setupDirectory, DevDivPackagesDirName)}\**\project.json). Include this file change in your PR and rebuild.")
        End If
    End Sub

    Private Sub ParseSwrFile(path As String, <Out> ByRef version As Version, <Out> ByRef files As IEnumerable(Of String))
        Dim lines = File.ReadAllLines(path)

        version = Version.Parse(lines.Single(Function(line) line.TrimStart().StartsWith("version=")).Split("="c)(1))
        files = (From line In lines Where line.TrimStart().StartsWith("file")).ToArray()
    End Sub

    ''' <summary>
    ''' Enumerate files specified in the list. The specifications may include file names, directory names, and patterns.
    ''' </summary>
    ''' <param name="fileSpecs">
    ''' If the item contains '*', then it will be treated as a search pattern for the top directory.
    ''' Otherwise, if the item represents a directory, then all files and subdirectories of the item will be copied over.
    ''' 
    ''' This funtion will fail and throw and exception if any of the specified files do not exist on disk.
    ''' </param>
    Private Iterator Function ExpandTestDependencies(fileSpecs As String()) As IEnumerable(Of String)
        Dim allGood = True
        For Each spec In fileSpecs
            If spec.Contains("*") Then
                For Each path In Directory.EnumerateFiles(_binDirectory, spec, SearchOption.TopDirectoryOnly)
                    Yield path.Substring(_binDirectory.Length)
                Next
            Else
                spec = GetPotentiallyMappedPath(spec)
                Dim inputItem = Path.Combine(_binDirectory, spec)

                If Directory.Exists(inputItem) Then
                    For Each path In Directory.EnumerateFiles(inputItem, "*.*", SearchOption.AllDirectories)
                        Yield path.Substring(_binDirectory.Length)
                    Next
                ElseIf File.Exists(inputItem) Then
                    Yield spec
                Else
                    Console.WriteLine($"File Or directory '{spec}' listed in test dependencies doesn't exist.", spec)
                    allGood = False
                End If
            End If
        Next

        If Not allGood Then
            Throw New Exception("Unable to expand test dependencies")
        End If
    End Function

    ''' <summary>
    ''' A simple method to retry an operation. Helpful if some file locking is going on and you want to just try the operation again.
    ''' </summary>
    Private Sub Retry(action As Action)
        For i = 1 To 10
            Try
                action()
                Return
            Catch ex As Exception
                Thread.Sleep(100)
            End Try
        Next
    End Sub

    ''' <summary>
    ''' Recently a number of our compontents have moved from the root of the output directory to sub-directories. The
    ''' map returned from this function maps file names to their relative path in the build output.
    '''
    ''' This is still pretty terrible though.  Instead of doing all this name matching we should have explicit paths 
    ''' and match on file contents.  That is a large change for this tool though.  As a temporary work around this 
    ''' map will be used instead.
    ''' </summary>
    Private Function CreatePathMap() As Dictionary(Of String, String)

        Dim map As New Dictionary(Of String, String)
        Dim add = Sub(filePath As String)
                      If Not File.Exists(Path.Combine(_binDirectory, filePath)) Then
                          Throw New Exception($"Mapped VSIX path does not exist: {filePath}")
                      End If
                      Dim name = Path.GetFileName(filePath)
                      If map.ContainsKey(name) Then
                          Throw New Exception($"{name} already exist!")
                      End If

                      map.Add(name, filePath)
                  End Sub

        Dim configPath = Path.Combine(_binDirectory, "..\..\build\config\SignToolData.json")
        Dim comparison = StringComparison.OrdinalIgnoreCase
        Dim obj = JObject.Parse(File.ReadAllText(configPath))
        Dim array = CType(obj.Property("sign").Value, JArray)
        For Each element As JObject In array
            Dim values = CType(element.Property("values").Value, JArray)
            For Each item As String In values
                Dim parent = Path.GetDirectoryName(item)
                Dim name = Path.GetFileName(item)

                If parent.EndsWith("NetFX20", comparison) Then
                    Continue For
                End If

                ' Don't add in the netcoreapp2.0 version of DLL
                If Path.GetFileName(parent) = "netcoreapp2.0" AndAlso name = "Microsoft.Build.Tasks.CodeAnalysis.dll" Then
                    Continue For
                End If

                ' There are items in SignToolData which are built after this tool is run and hence
                ' can't be a part of the map.
                If parent.EndsWith("DevDivPackages\Roslyn", comparison) OrElse
                    parent.StartsWith("Vsix\CodeAnalysisCompilers", comparison) Then
                    Continue For
                End If

                ' Ignore wild cards. The map contains full file paths and supporting wildcards would
                ' require expansion. That is doable but given none of the files identified by wild cards
                ' are used by other downstream tools this isn't necessary.
                If item.Contains("*") Then
                    Continue For
                End If

                add(item)
            Next
        Next

        add("Exes\csc\net46\csc.exe.config")
        add("Exes\csc\net46\csc.rsp")
        add("Exes\vbc\net46\vbc.exe.config")
        add("Exes\vbc\net46\vbc.rsp")
        add("Exes\VBCSCompiler\net46\VBCSCompiler.exe.config")
        add("Exes\InteractiveHost32\InteractiveHost32.exe.config")
        add("Exes\InteractiveHost64\InteractiveHost64.exe.config")
        add("Exes\csi\net46\csi.rsp")
        add("Exes\csi\net46\csi.exe.config")
        add("Vsix\VisualStudioInteractiveComponents\CSharpInteractive.rsp")
        add("Vsix\VisualStudioSetup\Microsoft.CodeAnalysis.Elfie.dll")
        add("Vsix\VisualStudioSetup\Microsoft.VisualStudio.CallHierarchy.Package.Definitions.dll")
        add("Vsix\VisualStudioSetup\System.Composition.Convention.dll")
        add("Vsix\VisualStudioSetup\System.Composition.Hosting.dll")
        add("Vsix\VisualStudioSetup\System.Composition.TypedParts.dll")
        add("Vsix\VisualStudioSetup\System.Threading.Tasks.Extensions.dll")
        add("Vsix\VisualStudioSetup\Mono.Cecil.dll")
        add("Vsix\VisualStudioSetup\Mono.Cecil.Mdb.dll")
        add("Vsix\VisualStudioSetup\Mono.Cecil.Pdb.dll")
        add("Vsix\VisualStudioSetup\Mono.Cecil.Rocks.dll")
        add("Vsix\VisualStudioSetup\ICSharpCode.Decompiler.dll")
        add("Dlls\BasicExpressionCompiler\Microsoft.CodeAnalysis.VisualBasic.ExpressionEvaluator.ExpressionCompiler.vsdconfig")
        add("Dlls\BasicResultProvider.Portable\Microsoft.CodeAnalysis.VisualBasic.ExpressionEvaluator.ResultProvider.vsdconfig")
        add("Dlls\CSharpExpressionCompiler\Microsoft.CodeAnalysis.CSharp.ExpressionEvaluator.ExpressionCompiler.vsdconfig")
        add("Dlls\CSharpResultProvider.Portable\Microsoft.CodeAnalysis.CSharp.ExpressionEvaluator.ResultProvider.vsdconfig")
        add("Dlls\FunctionResolver\Microsoft.CodeAnalysis.ExpressionEvaluator.FunctionResolver.vsdconfig")
        add("Dlls\ServicesVisualStudio\Microsoft.VisualStudio.LanguageServices.vsdconfig")
        add("Dlls\MSBuildTask\net46\Microsoft.Managed.Core.targets")
        add("Dlls\MSBuildTask\net46\Microsoft.CSharp.Core.targets")
        add("Dlls\MSBuildTask\net46\Microsoft.VisualBasic.Core.targets")
        add("Dlls\CSharpCompilerTestUtilities\Roslyn.Compilers.CSharp.Test.Utilities.dll")
        add("Dlls\BasicCompilerTestUtilities\Roslyn.Compilers.VisualBasic.Test.Utilities.dll")
        add("Dlls\CompilerTestResources\\Roslyn.Compilers.Test.Resources.dll")
        add("Dlls\ExpressionCompilerTestUtilities\Roslyn.ExpressionEvaluator.ExpressionCompiler.Test.Utilities.dll")
        add("Dlls\ResultProviderTestUtilities\Roslyn.ExpressionEvaluator.ResultProvider.Test.Utilities.dll")
        add("Dlls\PdbUtilities\Roslyn.Test.PdbUtilities.dll")
        add("UnitTests\EditorServicesTest\BasicUndo.dll")
        add("UnitTests\EditorServicesTest\Moq.dll")
        add("UnitTests\EditorServicesTest\Microsoft.CodeAnalysis.Test.Resources.Proprietary.dll")
        add("UnitTests\CSharpCompilerEmitTest\net46\Microsoft.DiaSymReader.PortablePdb.dll")
        add("UnitTests\CSharpCompilerEmitTest\net46\Microsoft.DiaSymReader.Converter.dll")
        add("UnitTests\CSharpCompilerEmitTest\net46\Microsoft.DiaSymReader.Converter.Xml.dll")
        add("UnitTests\CSharpCompilerEmitTest\net46\Microsoft.DiaSymReader.dll")
        add("UnitTests\CSharpCompilerEmitTest\net46\Microsoft.DiaSymReader.Native.amd64.dll")
        add("UnitTests\CSharpCompilerEmitTest\net46\Microsoft.DiaSymReader.Native.x86.dll")
        add("Vsix\ExpressionEvaluatorPackage\Microsoft.VisualStudio.Debugger.Engine.dll")
        add("Vsix\VisualStudioIntegrationTestSetup\Microsoft.Diagnostics.Runtime.dll")
        add("Exes\Toolset\System.AppContext.dll")
        add("Exes\Toolset\System.Console.dll")
        add("Exes\Toolset\System.Collections.Immutable.dll")
        add("Exes\Toolset\System.Diagnostics.DiagnosticSource.dll")
        add("Exes\Toolset\System.Diagnostics.FileVersionInfo.dll")
        add("Exes\Toolset\System.Diagnostics.StackTrace.dll")
        add("Exes\Toolset\System.IO.Compression.dll")
        add("Exes\Toolset\System.IO.FileSystem.dll")
        add("Exes\Toolset\System.IO.FileSystem.Primitives.dll")
        add("Exes\Toolset\System.Net.Http.dll")
        add("Exes\Toolset\System.Reflection.Metadata.dll")
        add("Exes\Toolset\System.Security.Cryptography.Algorithms.dll")
        add("Exes\Toolset\System.Security.Cryptography.Encoding.dll")
        add("Exes\Toolset\System.Security.Cryptography.Primitives.dll")
        add("Exes\Toolset\System.Security.Cryptography.X509Certificates.dll")
        add("Exes\Toolset\System.Text.Encoding.CodePages.dll")
        add("Exes\Toolset\System.ValueTuple.dll")
        add("Exes\Toolset\System.Xml.ReaderWriter.dll")
        add("Exes\Toolset\System.Xml.XmlDocument.dll")
        add("Exes\Toolset\System.Xml.XPath.dll")
        add("Exes\Toolset\System.Xml.XPath.XDocument.dll")
        add("Vsix\VisualStudioSetup\Humanizer.dll")
        Return map
    End Function

    Private Function GetMappedPath(fileName As String) As String
        Dim mappedPath As String = Nothing
        If Not _pathMap.TryGetValue(fileName, mappedPath) Then
            Throw New Exception($"File name {fileName} does not have a mapped path")
        End If

        Return mappedPath
    End Function

    Private Function GetPotentiallyMappedPath(fileName As String) As String
        Dim mappedPath As String = Nothing
        If _pathMap.TryGetValue(fileName, mappedPath) Then
            Return mappedPath
        Else
            Return fileName
        End If
    End Function

    Private Sub ProcessVsixFiles(filesToInsert As List(Of NugetFileInfo), dependencies As Dictionary(Of String, DependencyInfo))
        Dim processedFiles = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim allGood = True

        ' We build our language service authoring by cracking our .vsixes and pulling out the bits that matter
        For Each vsixFileName In VsixesToInstall
            Dim vsixName As String = Path.GetFileNameWithoutExtension(vsixFileName)
            WriteLineIfVerbose($"Processing {vsixName}")

            Using vsix = Package.Open(Path.Combine(_binDirectory, vsixFileName), FileMode.Open, FileAccess.Read, FileShare.Read)
                For Each vsixPart In vsix.GetParts()

                    ' This part might be metadata for the digital signature. In that case, skip it
                    If vsixPart.ContentType.StartsWith("application/vnd.openxmlformats-package.") Then
                        Continue For
                    End If

                    Dim partRelativePath = GetPartRelativePath(vsixPart)
                    Dim partFileName = Path.GetFileName(partRelativePath)

                    WriteLineIfVerbose($"     Processing {partFileName}")

                    ' If this is something that we don't need to ship, skip it
                    If VsixContentsToSkip.Contains(partFileName) Then
                        WriteLineIfVerbose($"        Skipping because {partFileName} is in {NameOf(VsixContentsToSkip)}")
                        Continue For
                    End If

                    If IsLanguageServiceRegistrationFile(partFileName) Then
                        WriteLineIfVerbose($"        Skipping because {partFileName} is a language service registration file that doesn't need to be processed")
                        Continue For
                    End If

                    ' Files generated by the VSIX v3 installer that don't need to be inserted.
                    If partFileName = "catalog.json" OrElse partFileName = "manifest.json" Then
                        Continue For
                    End If

                    If dependencies.ContainsKey(partFileName) Then
                        WriteLineIfVerbose($"        Skipping because {partFileName} is a dependency that is coming from NuGet package {dependencies(partFileName).PackageName}")
                        Continue For
                    End If

                    Dim relativeOutputFilePath = Path.Combine(GetExternalApiDirectory(), partFileName)

                    ' paths are relative to input directory:
                    If processedFiles.Add(relativeOutputFilePath) Then
                        ' In Razzle src\ArcProjects\debugger\ConcordSDK.targets references .vsdconfig files under LanguageServiceRegistration\ExpressionEvaluatorPackage
                        Dim target = If(Path.GetExtension(partFileName).Equals(".vsdconfig"), "LanguageServiceRegistration\ExpressionEvaluatorPackage", "")

                        Dim partPath = GetPotentiallyMappedPath(partFileName)

                        If Not File.Exists(Path.Combine(_binDirectory, partPath)) Then
                            Console.WriteLine($"File {partPath} does not exist at {_binDirectory}")
                            allGood = False
                        End If

                        filesToInsert.Add(New NugetFileInfo(partPath, target))
                        AddXmlDocumentationFile(filesToInsert, partPath)
                    End If
                Next
            End Using
        Next

        If Not allGood Then
            Throw New Exception("Error processing VSIX files")
        End If
    End Sub

    Private Function GetPartRelativePath(part As PackagePart) As String
        Dim name = part.Uri.OriginalString
        If name.Length > 0 AndAlso name(0) = "/"c Then
            name = name.Substring(1)
        End If

        Return name.Replace("/"c, "\"c)
    End Function

    ' XML doc file if exists:
    Private Sub AddXmlDocumentationFile(filesToInsert As List(Of NugetFileInfo), fileName As String)
        If IsExecutableCodeFileName(fileName) Then
            Dim xmlDocFile = Path.ChangeExtension(fileName, ".xml")
            If File.Exists(Path.Combine(_binDirectory, xmlDocFile)) Then
                ' paths are relative to input directory
                filesToInsert.Add(New NugetFileInfo(xmlDocFile))
            End If
        End If
    End Sub

    ''' <summary>
    ''' Takes a list of paths relative to <see cref="_outputDirectory"/> and generates a nuspec file that includes them.
    ''' </summary>
    Private Sub GenerateRoslynNuSpec(filesToInsert As List(Of NugetFileInfo))
        Const PackageName As String = "VS.ExternalAPIs.Roslyn"

        ' Do a quick sanity check for the files existing.  If they don't exist at this time then the tool output
        ' is going to be unusable
        Dim allGood = True
        For Each fileInfo In filesToInsert
            Dim filePath = Path.Combine(_binDirectory, fileInfo.Path)
            If Not File.Exists(filePath) Then
                allGood = False
                Console.WriteLine($"File {fileInfo.Path} does not exist at {_binDirectory}")
            End If
        Next

        Dim xml = <?xml version="1.0" encoding="utf-8"?>
                  <package>
                      <metadata>
                          <id><%= PackageName %></id>
                          <summary>Roslyn binaries for the VS build.</summary>
                          <description>CoreXT package for the VS build.</description>
                          <authors>Managed Languages</authors>
                          <version>0.0</version>
                      </metadata>
                      <files>
                          <%= filesToInsert.
                              OrderBy(Function(f) f.Path).
                              Distinct().
                              Select(Function(f) <file src=<%= f.Path %> target=<%= f.Target %>/>) %>
                      </files>
                  </package>

        xml.Save(GetAbsolutePathInOutputDirectory(PackageName & ".nuspec"), SaveOptions.OmitDuplicateNamespaces)
    End Sub

    ''' <summary>
    ''' Generates the nuspec + supporting file layout for the Roslyn toolset nupkg file. This is the toolset
    ''' which will be used during the VS build. This will exactly match the layout of the toolset used 
    ''' by the Microsoft.Net.Compilers package + some devdiv environment files.
    ''' </summary>
    Private Sub GenerateVSToolsRoslynCoreXTNuspec()
        Const packageName As String = "VS.Tools.Roslyn"
        Dim outputDir = GetAbsolutePathInOutputDirectory(packageName)
        Dim nuspecFiles As New List(Of String)
        Directory.CreateDirectory(outputDir)

        ' First copy over all the files from the compilers toolset. 
        For Each fileFullPath In GetCompilerToolsetNuspecFiles()
            Dim fileName = Path.GetFileName(fileFullPath)

            ' Skip satellite assemblies; we don't need these for the compiler insertion
            If fileName.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase) Then
                Continue For
            End If

            Dim destFilepath = Path.Combine(outputDir, fileName)
            File.Copy(fileFullPath, destFilepath)
            nuspecFiles.Add(fileName)

            ' A bug in VS forces all of our exes to use the prefer 32 bit mode. Mark the copies added 
            ' to this nuspec as such. They are isolated and hence allow our binaries shipped to customers
            ' to remain executable as 64 bit apps
            ' See https://github.com/dotnet/roslyn/issues/17864
            If Path.GetExtension(fileName) = ".exe" Then
                MarkFile32BitPref(destFilepath)
            End If
        Next

        ' Write an Init.cmd that sets DEVPATH to the toolset location. This overrides
        ' assembly loading during the VS build to always look in the Roslyn toolset
        ' first. This is necessary because there are various incompatible versions
        ' of Roslyn littered throughout the DEVPATH already and this one should always
        ' take precedence.
        Dim initFileName = "Init.cmd"
        Dim fileContents = "@echo off

set RoslynToolsRoot=%~dp0
set DEVPATH=%RoslynToolsRoot%;%DEVPATH%"

        File.WriteAllText(Path.Combine(outputDir, initFileName), fileContents)
        nuspecFiles.Add(initFileName)

        Dim xml = <?xml version="1.0" encoding="utf-8"?>
                  <package>
                      <metadata>
                          <id><%= packageName %></id>
                          <summary>Roslyn compiler binaries used to build VS</summary>
                          <description>CoreXT package for Roslyn compiler toolset.</description>
                          <authors>Managed Language Compilers</authors>
                          <version>0.0</version>
                      </metadata>
                      <files>
                          <file src="Init.cmd"/>
                          <%= nuspecFiles.
                              OrderBy(Function(f) f).
                              Select(Function(f) <file src=<%= f %>/>) %>
                      </files>
                  </package>

        xml.Save(Path.Combine(outputDir, packageName & ".nuspec"), SaveOptions.OmitDuplicateNamespaces)
    End Sub

    Private Sub MarkFile32BitPref(filePath As String)
        Const OffsetFromStartOfCorHeaderToFlags = 4 + ' byte count 
                                                  2 + ' Major version
                                                  2 + ' Minor version
                                                  8   ' Metadata directory

        Using stream As FileStream = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)
            Using reader As PEReader = New PEReader(stream)
                Dim newFlags As Int32 = reader.PEHeaders.CorHeader.Flags Or
                                        CorFlags.Prefers32Bit Or
                                        CorFlags.Requires32Bit ' CLR requires both req and pref flags to be set

                Using writer = New BinaryWriter(stream)
                    Dim mdReader = reader.GetMetadataReader()
                    stream.Position = reader.PEHeaders.CorHeaderStartOffset + OffsetFromStartOfCorHeaderToFlags

                    writer.Write(newFlags)
                    writer.Flush()
                End Using
            End Using
        End Using
    End Sub

    Private Function IsLanguageServiceRegistrationFile(fileName As String) As Boolean
        Select Case Path.GetExtension(fileName)
            Case ".vsixmanifest", ".pkgdef", ".png", ".ico"
                Return True
            Case Else
                Return False
        End Select
    End Function

    Private Shared Function IsExecutableCodeFileName(fileName As String) As Boolean
        Dim extension = Path.GetExtension(fileName)
        Return extension = ".exe" OrElse extension = ".dll"
    End Function

    Private Function GetAbsolutePathInOutputDirectory(relativePath As String) As String
        Dim absolutePath = Path.Combine(_outputDirectory, relativePath)

        ' Ensure that the parent directories are all created
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath))

        Return absolutePath
    End Function

    ''' <summary>
    ''' Get the list of files that appear in the compiler toolset nuspec file. This is the authorative
    ''' list of files that make up the compiler toolset layout. 
    ''' </summary>
    Private Function GetCompilerToolsetNuspecFiles() As List(Of String)
        Dim files As New List(Of String)
        Dim nuspecFilePath = Path.Combine(_nuspecDirectory, "Microsoft.Net.Compilers.nuspec")
        Dim document = XDocument.Load(nuspecFilePath)
        For Each fileElement In document.<package>.<files>.<file>
            If fileElement.Attribute("target").Value = "tools" Then
                Dim fileRelativePath = fileElement.Attribute("src").Value
                Dim fileFullPath = Path.Combine(_binDirectory, fileRelativePath)
                If fileRelativePath.Contains("**") Then
                    Continue For
                ElseIf fileRelativePath.Contains("*") Then
                    Dim dir = Path.GetDirectoryName(fileRelativePath)
                    dir = Path.Combine(_binDirectory, dir)
                    For Each f In Directory.EnumerateFiles(dir, Path.GetFileName(fileRelativePath))
                        files.Add(f)
                    Next
                Else
                    files.Add(fileFullPath)
                End If
            End If
        Next

        Return files
    End Function

    ''' <summary>
    ''' Get the set of compiler files that need to be copied over during insertion. 
    ''' </summary>
    Private Function GetCompilerInsertFiles() As IEnumerable(Of String)
        Return GetCompilerToolsetNuspecFiles().
            Select(AddressOf Path.GetFileName).
            Where(Function(f)
                      ' Skip satellite assemblies; we don't need these for the compiler insertion
                      Return Not f.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase)
                  End Function).
            Where(Function(f)
                      Select Case f
                          ' These files are inserted by MSBuild setup 
                          Case "Microsoft.DiaSymReader.Native.amd64.dll", "Microsoft.DiaSymReader.Native.x86.dll"
                              Return False
                          ' Do not truly understand why these are excluded here. Just maintaining compat
                          Case "System.Collections.Immutable.dll", "System.Reflection.Metadata.dll"
                              Return False
                          Case Else
                              Return True
                      End Select
                  End Function)
    End Function

    Private Sub WriteLineIfVerbose(s As String)
        If _verbose Then
            Console.WriteLine(s)
        End If
    End Sub
End Class
