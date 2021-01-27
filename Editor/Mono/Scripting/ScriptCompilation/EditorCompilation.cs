// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NiceIO;
using Bee.BeeDriver;
using ScriptCompilationBuildProgram.Data;
using Unity.Profiling;
using UnityEditor.Compilation;
using UnityEditor.Modules;
using UnityEditor.Scripting.Compilers;
using UnityEditor.Utils;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Profiling;
using CompilerMessage = UnityEditor.Scripting.Compilers.CompilerMessage;
using CompilerMessageType = UnityEditor.Scripting.Compilers.CompilerMessageType;
using Directory = System.IO.Directory;
using File = System.IO.File;

namespace UnityEditor.Scripting.ScriptCompilation
{
    class EditorCompilation
    {
        private const int kLogIdentifierFor_EditorMessages = 1234;
        private const int kLogIdentifierFor_PlayerMessages = 1235;

        public enum CompileStatus
        {
            Idle,
            Compiling,
            CompilationStarted,
            CompilationFailed,
            CompilationComplete
        }

        public enum DeleteFileOptions
        {
            NoLogError = 0,
            LogError = 1,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TargetAssemblyInfo
        {
            public string Name;
            public AssemblyFlags Flags;
        }

        public struct CustomScriptAssemblyAndReference
        {
            public CustomScriptAssembly Assembly;
            public CustomScriptAssembly Reference;
        }

        public PrecompiledAssemblyProviderBase PrecompiledAssemblyProvider { get; set; } = new PrecompiledAssemblyProvider();
        public CompilationSetupErrorsTrackerBase CompilationSetupErrorsTracker { get; set; } = new CompilationSetupErrorsTracker();
        public ResponseFileProvider ResponseFileProvider { get; set; } = new MicrosoftCSharpResponseFileProvider();

        internal string projectDirectory = string.Empty;
        Dictionary<string, string> allScripts = new Dictionary<string, string>();

        private bool m_ScriptsForEditorHaveBeenCompiledSinceLastDomainReload;
        private RequestScriptCompilationOptions? m_ScriptCompilationRequest = null;
        CustomScriptAssembly[] customScriptAssemblies = new CustomScriptAssembly[0];
        List<CustomScriptAssemblyReference> customScriptAssemblyReferences = new List<CustomScriptAssemblyReference>();
        Dictionary<string, TargetAssembly> customTargetAssemblies = new Dictionary<string, TargetAssembly>(); // TargetAssemblies for customScriptAssemblies.
        PrecompiledAssembly[] unityAssemblies;

        string outputDirectory;
        bool skipCustomScriptAssemblyGraphValidation = false;
        List<AssemblyBuilder> assemblyBuilders = new List<Compilation.AssemblyBuilder>();
        bool _logCompilationMessages = true;

        private AssetPathMetaData[] m_AssetPathsMetaData;
        private Dictionary<string, VersionMetaData> m_VersionMetaDatas;

        public event Action<object> compilationStarted;
        public event Action<object> compilationFinished;
        public event Action<ScriptAssembly, UnityEditor.Compilation.CompilerMessage[]> assemblyCompilationFinished;

        class BeeScriptCompilationState
        {
            public BeeDriver Driver;
            public ScriptAssembly[] assemblies;
            public ScriptAssemblySettings settings { get; set; }
        }

        private BeeScriptCompilationState activeBeeBuild;
        private CompilerMessage[] _currentEditorCompilationCompilerMessages = new CompilerMessage[0];
        public bool IsRunningRoslynAnalysisSynchronously { get; private set; }

        internal void SetProjectDirectory(string projectDirectory)
        {
            this.projectDirectory = projectDirectory;
        }

        internal void SetAssetPathsMetaData(AssetPathMetaData[] assetPathMetaDatas)
        {
            m_AssetPathsMetaData = assetPathMetaDatas;

            var versionMetaDataComparer = new VersionMetaDataComparer();

            m_VersionMetaDatas = assetPathMetaDatas ?
                .Where(x => x.VersionMetaData != null)
                .Select(x => x.VersionMetaData)
                .Distinct(versionMetaDataComparer)
                .ToDictionary(x => x.Name, x => x);
            UpdateCustomTargetAssembliesAssetPathsMetaData(customScriptAssemblies, assetPathMetaDatas, forceUpdate: true);
        }

        internal void SetAdditionalVersionMetaDatas(VersionMetaData[] versionMetaDatas)
        {
            Assert.IsTrue(m_VersionMetaDatas != null, "EditorCompilation.SetAssetPathsMetaData() must be called before EditorCompilation.SetAdditionalVersionMetaDatas()");
            foreach (var versionMetaData in versionMetaDatas)
                m_VersionMetaDatas[versionMetaData.Name] = versionMetaData;
        }

        internal AssetPathMetaData[] GetAssetPathsMetaData()
        {
            return m_AssetPathsMetaData;
        }

        internal Dictionary<string, VersionMetaData> GetVersionMetaDatas()
        {
            return m_VersionMetaDatas;
        }

        public void SetAllScripts(string[] allScripts, string[] assemblyFilenames)
        {
            this.allScripts = new Dictionary<string, string>();

            for (int i = 0; i < allScripts.Length; ++i)
            {
                this.allScripts[allScripts[i]] = assemblyFilenames[i];
            }
        }

        public bool HaveScriptsForEditorBeenCompiledSinceLastDomainReload()
        {
            return m_ScriptsForEditorHaveBeenCompiledSinceLastDomainReload;
        }

        public void RequestScriptCompilation(string reason = null, RequestScriptCompilationOptions options = RequestScriptCompilationOptions.None)
        {
            if (reason != null)
                Console.WriteLine($"[ScriptCompilation] Requested script compilation because: {reason}");
            PrecompiledAssemblyProvider.Dirty();
            m_ScriptCompilationRequest = options;
            CancelActiveBuild();
        }

        private void CleanCache()
        {
            new NPath("Library/Bee").DeleteIfExists(DeleteMode.Soft);
        }

        public void SetAllUnityAssemblies(PrecompiledAssembly[] unityAssemblies)
        {
            this.unityAssemblies = unityAssemblies;
        }

        // Burst package depends on this method, so we can't remove it.
        public void SetCompileScriptsOutputDirectory(string directory)
        {
            outputDirectory = directory;
        }

        public string GetCompileScriptsOutputDirectory()
        {
            if (string.IsNullOrEmpty(outputDirectory))
                throw new Exception("Must set an output directory through SetCompileScriptsOutputDirectory before compiling");
            return outputDirectory;
        }

        //Used by the TestRunner package.
        internal PrecompiledAssembly[] GetAllPrecompiledAssemblies()
        {
            return PrecompiledAssemblyProvider.GetPrecompiledAssemblies(true, EditorUserBuildSettings.activeBuildTargetGroup, EditorUserBuildSettings.activeBuildTarget);
        }

        private Dictionary<string, PrecompiledAssembly> GetPrecompiledAssembliesDictionaryWithSetupErrorsTracking(bool isEditor, BuildTargetGroup buildTargetGroup, BuildTarget buildTarget)
        {
            Dictionary<string, PrecompiledAssembly> precompiledAssemblies;
            CompilationSetupErrorsTracker.ClearCompilationSetupErrors(CompilationSetupErrors.PrecompiledAssemblyError); // this will also remove the console errors associated with the setup error flags set in the past
            try
            {
                precompiledAssemblies = PrecompiledAssemblyProvider.GetPrecompiledAssembliesDictionary(isEditor, buildTargetGroup, buildTarget);
            }
            catch (PrecompiledAssemblyException exception)
            {
                CompilationSetupErrorsTracker.ProcessPrecompiledAssemblyException(exception);
                throw;
            }
            return precompiledAssemblies;
        }

        public PrecompiledAssembly[] GetPrecompiledAssembliesWithSetupErrorsTracking(bool isEditor, BuildTargetGroup buildTargetGroup, BuildTarget buildTarget)
        {
            return GetPrecompiledAssembliesDictionaryWithSetupErrorsTracking(isEditor, buildTargetGroup, buildTarget)?.Values.ToArray();
        }

        public void GetAssemblyDefinitionReferencesWithMissingAssemblies(out List<CustomScriptAssemblyReference> referencesWithMissingAssemblies)
        {
            var nameLookup = customScriptAssemblies.ToDictionary(x => x.Name);
            referencesWithMissingAssemblies = new List<CustomScriptAssemblyReference>();
            foreach (var asmref in customScriptAssemblyReferences)
            {
                if (!nameLookup.ContainsKey(asmref.Reference))
                {
                    referencesWithMissingAssemblies.Add(asmref);
                }
            }
        }

        public TargetAssembly GetCustomTargetAssemblyFromName(string name)
        {
            TargetAssembly targetAssembly;

            if (name.EndsWith(".dll", StringComparison.Ordinal))
            {
                customTargetAssemblies.TryGetValue(name, out targetAssembly);
            }
            else
            {
                customTargetAssemblies.TryGetValue(name + ".dll", out targetAssembly);
            }

            if (targetAssembly == null)
            {
                throw new ArgumentException("Assembly not found", name);
            }

            return targetAssembly;
        }

        public TargetAssemblyInfo[] GetAllCompiledAndResolvedTargetAssemblies(
            EditorScriptCompilationOptions options,
            BuildTarget buildTarget,
            out CustomScriptAssemblyAndReference[] assembliesWithMissingReference)
        {
            var allTargetAssemblies = GetTargetAssemblies();
            var targetAssemblyCompiledPaths = new Dictionary<TargetAssembly, string>();

            foreach (var assembly in allTargetAssemblies)
            {
                var path = assembly.FullPath(outputDirectory);

                // Collect all assemblies that have been compiled (exist on file system)
                if (File.Exists(path))
                    targetAssemblyCompiledPaths.Add(assembly, path);
            }

            bool removed;

            var removedCustomAssemblies = new List<CustomScriptAssemblyAndReference>();
            var assembliesWithScripts = GetTargetAssembliesWithScriptsHashSet(options);

            do
            {
                removed = false;

                if (targetAssemblyCompiledPaths.Count > 0)
                {
                    foreach (var assembly in allTargetAssemblies)
                    {
                        if (!targetAssemblyCompiledPaths.ContainsKey(assembly))
                            continue;

                        // Check for each compiled assembly that all it's references
                        // have also been compiled. If not, remove it from the list
                        // of compiled assemblies.
                        foreach (var reference in assembly.References)
                        {
                            // Don't check references that are not compatible with the current build target,
                            // as those assemblies have not been compiled.
                            if (!EditorBuildRules.IsCompatibleWithPlatformAndDefines(reference, buildTarget, options))
                                continue;

                            if (!assembliesWithScripts.Contains(reference))
                            {
                                continue;
                            }

                            if (assembly.Type == TargetAssemblyType.Custom && !targetAssemblyCompiledPaths.ContainsKey(reference))
                            {
                                targetAssemblyCompiledPaths.Remove(assembly);

                                var customScriptAssembly = FindCustomTargetAssemblyFromTargetAssembly(assembly);
                                var customScriptAssemblyReference = FindCustomTargetAssemblyFromTargetAssembly(reference);

                                removedCustomAssemblies.Add(new CustomScriptAssemblyAndReference { Assembly = customScriptAssembly, Reference = customScriptAssemblyReference });
                                removed = true;
                                break;
                            }
                        }
                    }
                }
            }
            while (removed);

            var count = targetAssemblyCompiledPaths.Count;
            var targetAssemblies = new TargetAssemblyInfo[count];
            int index = 0;

            foreach (var entry in targetAssemblyCompiledPaths)
            {
                var assembly = entry.Key;
                targetAssemblies[index++] = ToTargetAssemblyInfo(assembly);
            }

            assembliesWithMissingReference = removedCustomAssemblies.ToArray();
            return targetAssemblies;
        }

        static CustomScriptAssembly LoadCustomScriptAssemblyFromJsonPath(string path, string guid)
        {
            var json = Utility.ReadTextAsset(path);

            try
            {
                var customScriptAssemblyData = CustomScriptAssemblyData.FromJson(json);
                return CustomScriptAssembly.FromCustomScriptAssemblyData(path, guid, customScriptAssemblyData);
            }
            catch (Exception e)
            {
                throw new AssemblyDefinitionException(e.Message, path);
            }
        }

        static CustomScriptAssembly LoadCustomScriptAssemblyFromJson(string path, string json, string guid)
        {
            try
            {
                var customScriptAssemblyData = CustomScriptAssemblyData.FromJson(json);
                return CustomScriptAssembly.FromCustomScriptAssemblyData(path, guid, customScriptAssemblyData);
            }
            catch (Exception e)
            {
                throw new Compilation.AssemblyDefinitionException(e.Message, path);
            }
        }

        static CustomScriptAssemblyReference LoadCustomScriptAssemblyReferenceFromJsonPath(string path)
        {
            var json = Utility.ReadTextAsset(path);
            return LoadCustomScriptAssemblyReferenceFromJson(path, json);
        }

        static CustomScriptAssemblyReference LoadCustomScriptAssemblyReferenceFromJson(string path, string json)
        {
            try
            {
                var customScriptAssemblyRefData = CustomScriptAssemblyReferenceData.FromJson(json);
                return CustomScriptAssemblyReference.FromCustomScriptAssemblyReferenceData(path, customScriptAssemblyRefData);
            }
            catch (Exception e)
            {
                throw new Compilation.AssemblyDefinitionException(e.Message, path);
            }
        }

        string[] CustomTargetAssembliesToFilePaths(IEnumerable<TargetAssembly> targetAssemblies)
        {
            var customAssemblies = targetAssemblies.Select(a => FindCustomTargetAssemblyFromTargetAssembly(a));
            var filePaths = customAssemblies.Select(a => a.FilePath).ToArray();
            return filePaths;
        }

        string CustomTargetAssemblyToFilePath(TargetAssembly targetAssembly)
        {
            return FindCustomTargetAssemblyFromTargetAssembly(targetAssembly).FilePath;
        }

        public struct CheckCyclicAssemblyReferencesFunctions
        {
            public Func<TargetAssembly, string> ToFilePathFunc;
            public Func<IEnumerable<TargetAssembly>, string[]> ToFilePathsFunc;
        }

        static void CheckCyclicAssemblyReferencesDFS(TargetAssembly visitAssembly,
            HashSet<TargetAssembly> visited,
            HashSet<TargetAssembly> recursion,
            CheckCyclicAssemblyReferencesFunctions functions)
        {
            visited.Add(visitAssembly);
            recursion.Add(visitAssembly);

            foreach (var reference in visitAssembly.References)
            {
                if (reference.Filename == visitAssembly.Filename)
                {
                    throw new Compilation.AssemblyDefinitionException("Assembly contains a references to itself",
                        AssemblyDefinitionErrorType.CyclicReferences, functions.ToFilePathFunc(visitAssembly));
                }

                if (recursion.Contains(reference))
                {
                    throw new Compilation.AssemblyDefinitionException("Assembly with cyclic references detected",
                        AssemblyDefinitionErrorType.CyclicReferences, functions.ToFilePathsFunc(recursion));
                }

                if (!visited.Contains(reference))
                {
                    CheckCyclicAssemblyReferencesDFS(reference,
                        visited,
                        recursion,
                        functions);
                }
            }

            recursion.Remove(visitAssembly);
        }

        public static void CheckCyclicAssemblyReferences(IDictionary<string, TargetAssembly> customTargetAssemblies,
            CheckCyclicAssemblyReferencesFunctions functions)
        {
            if (customTargetAssemblies == null || customTargetAssemblies.Count < 1)
                return;

            var visited = new HashSet<TargetAssembly>();

            foreach (var entry in customTargetAssemblies)
            {
                var assembly = entry.Value;
                if (!visited.Contains(assembly))
                {
                    var recursion = new HashSet<TargetAssembly>();

                    CheckCyclicAssemblyReferencesDFS(assembly,
                        visited,
                        recursion,
                        functions);
                }
            }
        }

        void CheckCyclicAssemblyReferences()
        {
            try
            {
                CheckCyclicAssemblyReferencesFunctions functions;

                functions.ToFilePathFunc = CustomTargetAssemblyToFilePath;
                functions.ToFilePathsFunc = CustomTargetAssembliesToFilePaths;

                CheckCyclicAssemblyReferences(customTargetAssemblies, functions);
            }
            catch (AssemblyDefinitionException e)
            {
                if (e.errorType == AssemblyDefinitionErrorType.CyclicReferences)
                    CompilationSetupErrorsTracker.SetCompilationSetupErrors(CompilationSetupErrors.CyclicReferences);
                throw e;
            }
        }

        public static Exception[] UpdateCustomScriptAssemblies(CustomScriptAssembly[] customScriptAssemblies,
            List<CustomScriptAssemblyReference> customScriptAssemblyReferences,
            AssetPathMetaData[] assetPathsMetaData, ResponseFileProvider responseFileProvider)
        {
            var asmrefLookup = customScriptAssemblyReferences.ToLookup(x => x.Reference);

            // Add AdditionalPrefixes
            foreach (var assembly in customScriptAssemblies)
            {
                var foundAsmRefs = asmrefLookup[assembly.Name];

                // Assign the found references or null. We need to assign null so as not to hold onto references that may have been removed/changed.
                assembly.AdditionalPrefixes = foundAsmRefs.Any() ? foundAsmRefs.Select(ar => ar.PathPrefix).ToArray() : null;
            }

            UpdateCustomTargetAssembliesResponseFileData(customScriptAssemblies, responseFileProvider);
            var exceptions = UpdateCustomTargetAssembliesAssetPathsMetaData(customScriptAssemblies, assetPathsMetaData);
            return exceptions.ToArray();
        }

        static void UpdateCustomTargetAssembliesResponseFileData(CustomScriptAssembly[] customScriptAssemblies, ResponseFileProvider responseFileProvider)
        {
            foreach (var assembly in customScriptAssemblies)
            {
                string rspFile = responseFileProvider.Get(assembly.PathPrefix)
                    .SingleOrDefault();
                if (!string.IsNullOrEmpty(rspFile))
                {
                    var responseFileContent = MicrosoftResponseFileParser.GetResponseFileContent(Directory.GetParent(Application.dataPath).FullName, rspFile);
                    var compilerOptions = MicrosoftResponseFileParser.GetCompilerOptions(responseFileContent);
                    assembly.ResponseFileDefines = MicrosoftResponseFileParser.GetDefines(compilerOptions).ToArray();
                }
            }
        }

        static Exception[] UpdateCustomTargetAssembliesAssetPathsMetaData(CustomScriptAssembly[] customScriptAssemblies,
            AssetPathMetaData[] assetPathsMetaData, bool forceUpdate = false)
        {
            if (assetPathsMetaData == null)
            {
                return new Exception[0];
            }

            var exceptions = new List<Exception>();
            var assetMetaDataPaths = new string[assetPathsMetaData.Length];
            var lowerAssetMetaDataPaths = new string[assetPathsMetaData.Length];

            for (int i = 0; i < assetPathsMetaData.Length; ++i)
            {
                var assetPathMetaData = assetPathsMetaData[i];
                assetMetaDataPaths[i] = AssetPath.ReplaceSeparators(assetPathMetaData.DirectoryPath + AssetPath.Separator);
                lowerAssetMetaDataPaths[i] = Utility.FastToLower(assetMetaDataPaths[i]);
            }

            foreach (var assembly in customScriptAssemblies)
            {
                if (assembly.AssetPathMetaData != null && !forceUpdate)
                {
                    continue;
                }

                try
                {
                    for (int i = 0; i < assetMetaDataPaths.Length; ++i)
                    {
                        var path = assetMetaDataPaths[i];
                        var lowerPath = lowerAssetMetaDataPaths[i];

                        if (Utility.FastStartsWith(assembly.PathPrefix, path, lowerPath))
                        {
                            assembly.AssetPathMetaData = assetPathsMetaData[i];
                            break;
                        }
                    }
                }
                catch (Exception e)
                {
                    exceptions.Add(e);
                }
            }

            return exceptions.ToArray();
        }

        Exception[] UpdateCustomTargetAssemblies()
        {
            var exceptions = UpdateCustomScriptAssemblies(customScriptAssemblies, customScriptAssemblyReferences, m_AssetPathsMetaData, ResponseFileProvider);

            if (exceptions.Length > 0)
            {
                CompilationSetupErrorsTracker.SetCompilationSetupErrors(CompilationSetupErrors.LoadError);
            }

            customTargetAssemblies = EditorBuildRules.CreateTargetAssemblies(customScriptAssemblies);

            CompilationSetupErrorsTracker.ClearCompilationSetupErrors(CompilationSetupErrors.CyclicReferences);

            return exceptions;
        }

        public void SkipCustomScriptAssemblyGraphValidation(bool skipChecks)
        {
            skipCustomScriptAssemblyGraphValidation = skipChecks;
        }

        public void ClearCustomScriptAssemblies()
        {
            customScriptAssemblies = null;
            customScriptAssemblyReferences.Clear();
        }

        public Exception[] SetAllCustomScriptAssemblyReferenceJsons(string[] paths)
        {
            return SetAllCustomScriptAssemblyReferenceJsonsContents(paths, null);
        }

        public Exception[] SetAllCustomScriptAssemblyReferenceJsonsContents(string[] paths, string[] contents)
        {
            var assemblyRefs = new List<CustomScriptAssemblyReference>();
            var exceptions = new List<Exception>();

            // We only construct this lookup if it is required, which is when we are using guids instead of assembly names.
            Dictionary<string, CustomScriptAssembly> guidsToAssemblies = null;

            // To check if a path prefix is already being used we use a Dictionary where the key is the prefix and the value is the file path.
            var prefixToFilePathLookup = skipCustomScriptAssemblyGraphValidation ?
                null :
                customScriptAssemblies.GroupBy(x => x.PathPrefix).ToDictionary(x => x.First().PathPrefix, x => new List<string>() { x.First().FilePath }, StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < paths.Length; ++i)
            {
                var path = paths[i];

                CustomScriptAssemblyReference loadedCustomScriptAssemblyReference = null;

                try
                {
                    var fullPath = AssetPath.IsPathRooted(path) ? AssetPath.GetFullPath(path) : AssetPath.Combine(projectDirectory, path);

                    if (contents != null)
                    {
                        var jsonContents = contents[i];
                        loadedCustomScriptAssemblyReference = LoadCustomScriptAssemblyReferenceFromJson(fullPath, jsonContents);
                    }
                    else
                    {
                        loadedCustomScriptAssemblyReference = LoadCustomScriptAssemblyReferenceFromJsonPath(fullPath);
                    }

                    if (!skipCustomScriptAssemblyGraphValidation)
                    {
                        // Check both asmdef and asmref files.
                        List<string> duplicateFilePaths;
                        if (prefixToFilePathLookup.TryGetValue(loadedCustomScriptAssemblyReference.PathPrefix, out duplicateFilePaths))
                        {
                            var filePaths = new List<string>();
                            filePaths.Add(loadedCustomScriptAssemblyReference.FilePath);
                            filePaths.AddRange(duplicateFilePaths);

                            throw new Compilation.AssemblyDefinitionException(string.Format("Folder '{0}' contains multiple assembly definition files", loadedCustomScriptAssemblyReference.PathPrefix), filePaths.ToArray());
                        }
                    }

                    // Convert GUID references to assembly names
                    if (GUIDReference.IsGUIDReference(loadedCustomScriptAssemblyReference.Reference))
                    {
                        // Generate the guid to assembly lookup?
                        if (guidsToAssemblies == null)
                            guidsToAssemblies = customScriptAssemblies.ToDictionary(x => x.GUID);

                        var guid = Utility.FastToLower(GUIDReference.GUIDReferenceToGUID(loadedCustomScriptAssemblyReference.Reference));
                        CustomScriptAssembly foundAssembly;
                        if (guidsToAssemblies.TryGetValue(guid, out foundAssembly))
                        {
                            loadedCustomScriptAssemblyReference.Reference = foundAssembly.Name;
                        }
                    }
                }
                catch (Exception e)
                {
                    CompilationSetupErrorsTracker.SetCompilationSetupErrors(CompilationSetupErrors.LoadError);
                    exceptions.Add(e);
                }

                if (loadedCustomScriptAssemblyReference != null)
                {
                    assemblyRefs.Add(loadedCustomScriptAssemblyReference);

                    if (!skipCustomScriptAssemblyGraphValidation)
                    {
                        List<string> duplicateFilePaths;
                        if (!prefixToFilePathLookup.TryGetValue(loadedCustomScriptAssemblyReference.PathPrefix, out duplicateFilePaths))
                        {
                            duplicateFilePaths = new List<string>();
                            prefixToFilePathLookup[loadedCustomScriptAssemblyReference.PathPrefix] = duplicateFilePaths;
                        }

                        duplicateFilePaths.Add(loadedCustomScriptAssemblyReference.FilePath);
                    }
                }
            }

            customScriptAssemblyReferences = assemblyRefs;
            var updateCustomTargetAssembliesExceptions = UpdateCustomTargetAssemblies();
            exceptions.AddRange(updateCustomTargetAssembliesExceptions);
            return exceptions.ToArray();
        }

        public Exception[] SetAllCustomScriptAssemblyJsons(string[] paths, string[] guids)
        {
            return SetAllCustomScriptAssemblyJsonContents(paths, null, guids);
        }

        public Exception[] SetAllCustomScriptAssemblyJsonContents(string[] paths, string[] contents, string[] guids)
        {
            var assemblies = new List<CustomScriptAssembly>();
            var assemblyLowercaseNamesLookup = new Dictionary<string, CustomScriptAssembly>();
            var exceptions = new List<Exception>();
            var guidsToAssemblies = new Dictionary<string, CustomScriptAssembly>();
            HashSet<string> predefinedAssemblyNames = null;

            // To check if a path prefix is already being used we use a Dictionary where the key is the prefix and the value is the file path.
            var prefixToFilePathLookup = customScriptAssemblyReferences.ToDictionary(x => x.PathPrefix, x => new List<string>() { x.FilePath }, StringComparer.OrdinalIgnoreCase);

            CompilationSetupErrorsTracker.ClearCompilationSetupErrors(CompilationSetupErrors.LoadError);

            // Load first to setup guidsToAssemblies dictionary and convert guids to assembly names
            // before checking for assembly reference errors, so errors emit assembly names instead of guids.
            for (var i = 0; i < paths.Length; ++i)
            {
                var path = paths[i];
                var guid = guids[i];

                CustomScriptAssembly loadedCustomScriptAssembly = null;
                string lowerCaseName = null;

                try
                {
                    var fullPath = AssetPath.IsPathRooted(path) ? AssetPath.GetFullPath(path) : AssetPath.Combine(projectDirectory, path);

                    if (contents != null)
                    {
                        var jsonContents = contents[i];
                        loadedCustomScriptAssembly = LoadCustomScriptAssemblyFromJson(fullPath, jsonContents, guid);
                    }
                    else
                    {
                        loadedCustomScriptAssembly = LoadCustomScriptAssemblyFromJsonPath(fullPath, guid);
                    }

                    if (loadedCustomScriptAssembly.References == null)
                        loadedCustomScriptAssembly.References = new string[0];

                    lowerCaseName = Utility.FastToLower(loadedCustomScriptAssembly.Name);
                    guidsToAssemblies[Utility.FastToLower(guid)] = loadedCustomScriptAssembly;

                    if (!skipCustomScriptAssemblyGraphValidation)
                    {
                        if (predefinedAssemblyNames == null)
                        {
                            predefinedAssemblyNames = new HashSet<string>(EditorBuildRules.PredefinedTargetAssemblyNames);
                            var net46 = MonoLibraryHelpers.GetSystemLibraryReferences(ApiCompatibilityLevel.NET_4_6).Select(Path.GetFileNameWithoutExtension);
                            var netstandard20 = MonoLibraryHelpers.GetSystemLibraryReferences(ApiCompatibilityLevel.NET_Standard_2_0).Select(Path.GetFileNameWithoutExtension);
                            predefinedAssemblyNames.UnionWith(net46);
                            predefinedAssemblyNames.UnionWith(netstandard20);
                        }

                        if (predefinedAssemblyNames.Contains(loadedCustomScriptAssembly.Name))
                        {
                            throw new Compilation.AssemblyDefinitionException(
                                $"Assembly cannot be have reserved name '{loadedCustomScriptAssembly.Name}'",
                                loadedCustomScriptAssembly.FilePath);
                        }

                        CustomScriptAssembly duplicate;
                        if (assemblyLowercaseNamesLookup.TryGetValue(lowerCaseName, out duplicate))
                        {
                            var filePaths = new string[]
                            {
                                loadedCustomScriptAssembly.FilePath,
                                duplicate.FilePath
                            };
                            var errorMsg = string.Format("Assembly with name '{0}' already exists", loadedCustomScriptAssembly.Name);
                            loadedCustomScriptAssembly = null; // Set to null to prevent it being added.
                            throw new Compilation.AssemblyDefinitionException(errorMsg, filePaths);
                        }

                        // Check both asmdef and asmref files.
                        List<string> duplicateFilePaths;
                        if (prefixToFilePathLookup.TryGetValue(loadedCustomScriptAssembly.PathPrefix, out duplicateFilePaths))
                        {
                            var filePaths = new List<string>();
                            filePaths.Add(loadedCustomScriptAssembly.FilePath);
                            filePaths.AddRange(duplicateFilePaths);

                            throw new Compilation.AssemblyDefinitionException(
                                string.Format("Folder '{0}' contains multiple assembly definition files",
                                    loadedCustomScriptAssembly.PathPrefix), filePaths.ToArray());
                        }
                    }
                }
                catch (Exception e)
                {
                    CompilationSetupErrorsTracker.SetCompilationSetupErrors(CompilationSetupErrors.LoadError);
                    exceptions.Add(e);
                }

                if (loadedCustomScriptAssembly != null)
                {
                    if (loadedCustomScriptAssembly.References == null)
                        loadedCustomScriptAssembly.References = new string[0];

                    if (!skipCustomScriptAssemblyGraphValidation || !assemblyLowercaseNamesLookup.ContainsKey(lowerCaseName))
                    {
                        assemblyLowercaseNamesLookup[lowerCaseName] = loadedCustomScriptAssembly;
                        assemblies.Add(loadedCustomScriptAssembly);

                        List<string> duplicateFilePaths;
                        if (!prefixToFilePathLookup.TryGetValue(loadedCustomScriptAssembly.PathPrefix, out duplicateFilePaths))
                        {
                            duplicateFilePaths = new List<string>();
                            prefixToFilePathLookup[loadedCustomScriptAssembly.PathPrefix] = duplicateFilePaths;
                        }
                        duplicateFilePaths.Add(loadedCustomScriptAssembly.FilePath);
                    }
                }
            }

            // Convert GUID references to assembly names
            foreach (var assembly in assemblies)
            {
                for (int i = 0; i < assembly.References.Length; ++i)
                {
                    var reference = assembly.References[i];

                    if (!GUIDReference.IsGUIDReference(reference))
                    {
                        continue;
                    }

                    var guid = Utility.FastToLower(GUIDReference.GUIDReferenceToGUID(reference));

                    CustomScriptAssembly referenceAssembly;

                    if (guidsToAssemblies.TryGetValue(guid, out referenceAssembly))
                    {
                        reference = referenceAssembly.Name;
                    }
                    else
                    {
                        reference = string.Empty;
                    }

                    assembly.References[i] = reference;
                }
            }

            // Check loaded assemblies for assembly reference errors after all GUID references have been
            // converted to names.
            if (!skipCustomScriptAssemblyGraphValidation)
            {
                foreach (var loadedCustomScriptAssembly in assemblies)
                {
                    try
                    {
                        var references = loadedCustomScriptAssembly.References.Where(r => !string.IsNullOrEmpty(r));

                        if (references.Count() != references.Distinct().Count())
                        {
                            var duplicateRefs = references.GroupBy(r => r).Where(g => g.Count() > 1).Select(g => g.Key)
                                .ToArray();
                            var duplicateRefsString = string.Join(",", duplicateRefs);

                            throw new Compilation.AssemblyDefinitionException(string.Format(
                                "Assembly has duplicate references: {0}",
                                duplicateRefsString),
                                loadedCustomScriptAssembly.FilePath);
                        }
                    }
                    catch (Exception e)
                    {
                        CompilationSetupErrorsTracker.SetCompilationSetupErrors(CompilationSetupErrors.LoadError);
                        exceptions.Add(e);
                    }
                }
            }

            customScriptAssemblies = assemblies.ToArray();

            var updateCustomTargetAssembliesExceptions = UpdateCustomTargetAssemblies();
            exceptions.AddRange(updateCustomTargetAssembliesExceptions);

            return exceptions.ToArray();
        }

        public bool IsPathInPackageDirectory(string path)
        {
            if (m_AssetPathsMetaData == null)
                return false;
            return m_AssetPathsMetaData.Any(p => path.StartsWith(p.DirectoryPath, StringComparison.OrdinalIgnoreCase));
        }

        public void DeleteScriptAssemblies()
        {
            NPath fullEditorAssemblyPath = AssetPath.Combine(projectDirectory, GetCompileScriptsOutputDirectory());
            fullEditorAssemblyPath.DeleteIfExists(DeleteMode.Soft);
        }

        public CustomScriptAssembly FindCustomScriptAssemblyFromAssemblyName(string assemblyName)
        {
            assemblyName = AssetPath.GetAssemblyNameWithoutExtension(assemblyName);

            if (customScriptAssemblies != null)
            {
                var result = customScriptAssemblies.FirstOrDefault(a => a.Name == assemblyName);
                if (result != null)
                    return result;
            }

            var exceptionMessage = "Cannot find CustomScriptAssembly with name '" + assemblyName + "'.";

            if (customScriptAssemblies == null)
            {
                exceptionMessage += " customScriptAssemblies is null.";
            }
            else
            {
                var assemblyNames = customScriptAssemblies.Select(a => a.Name).ToArray();
                var assemblyNamesString = string.Join(", ", assemblyNames);
                exceptionMessage += " Assembly names: " + assemblyNamesString;
            }

            throw new InvalidOperationException(exceptionMessage);
        }

        internal CustomScriptAssembly FindCustomScriptAssemblyFromScriptPath(string scriptPath)
        {
            var customTargetAssembly = EditorBuildRules.GetCustomTargetAssembly(scriptPath, projectDirectory, customTargetAssemblies);
            var customScriptAssembly = customTargetAssembly != null ? FindCustomScriptAssemblyFromAssemblyName(customTargetAssembly.Filename) : null;

            return customScriptAssembly;
        }

        internal CustomScriptAssembly FindCustomTargetAssemblyFromTargetAssembly(TargetAssembly assembly)
        {
            var assemblyName = AssetPath.GetAssemblyNameWithoutExtension(assembly.Filename);
            return FindCustomScriptAssemblyFromAssemblyName(assemblyName);
        }

        public CustomScriptAssembly FindCustomScriptAssemblyFromAssemblyReference(string reference)
        {
            if (!GUIDReference.IsGUIDReference(reference))
            {
                return FindCustomScriptAssemblyFromAssemblyName(reference);
            }

            if (customScriptAssemblies != null)
            {
                var guid = GUIDReference.GUIDReferenceToGUID(reference);
                var result = customScriptAssemblies.FirstOrDefault(a => string.Equals(a.GUID, guid, StringComparison.OrdinalIgnoreCase));

                if (result != null)
                    return result;
            }

            throw new InvalidOperationException($"Cannot find CustomScriptAssembly with reference '{reference}'");
        }

        public CompileStatus CompileScripts(
            EditorScriptCompilationOptions editorScriptCompilationOptions,
            BuildTargetGroup platformGroup,
            BuildTarget platform,
            string[] extraScriptingDefines
        )
        {
            IsRunningRoslynAnalysisSynchronously =
                (editorScriptCompilationOptions & EditorScriptCompilationOptions.BuildingWithRoslynAnalysis) != 0 && PlayerSettings.EnableRoslynAnalyzers;

            var scriptAssemblySettings = CreateScriptAssemblySettings(platformGroup, platform, editorScriptCompilationOptions, extraScriptingDefines);

            CompileStatus compilationResult;
            using (new ProfilerMarker("Initiating Script Compilation").Auto())
            {
                try
                {
                    compilationResult = CompileScriptsWithSettings(scriptAssemblySettings);
                }
                catch (PrecompiledAssemblyException)
                {
                    // The setup errors for this exception has already been logged earlier, so there's no need to log the exception.
                    compilationResult = CompileStatus.CompilationFailed;
                }
            }

            if (compilationResult != CompileStatus.Idle)
            {
                WarnIfThereAreScriptsThatDoNotBelongToAnyAssembly();
            }

            return compilationResult;
        }

        static string PDBPath(string dllPath)
        {
            return dllPath.Replace(".dll", ".pdb");
        }

        static string MDBPath(string dllPath)
        {
            return dllPath + ".mdb";
        }

        // Delete all .dll's that aren't used anymore
        public void DeleteUnusedAssemblies(ScriptAssemblySettings settings)
        {
            string fullEditorAssemblyPath = AssetPath.Combine(projectDirectory, GetCompileScriptsOutputDirectory());

            if (!Directory.Exists(fullEditorAssemblyPath))
                return;

            var deleteFiles = Directory.GetFiles(fullEditorAssemblyPath).Select(f => AssetPath.ReplaceSeparators(f)).ToList();

            var targetAssemblies = GetTargetAssembliesWithScripts(settings);

            foreach (var assembly in targetAssemblies)
            {
                string path = AssetPath.Combine(fullEditorAssemblyPath, assembly.Name);
                deleteFiles.Remove(path);
                deleteFiles.Remove(MDBPath(path));
                deleteFiles.Remove(PDBPath(path));
            }

            foreach (var path in deleteFiles)
                path.ToNPath().Delete(DeleteMode.Soft);
        }

        private void CancelActiveBuild()
        {
            activeBeeBuild?.Driver.CancelBuild();
        }

        private void WarnIfThereAreScriptsThatDoNotBelongToAnyAssembly()
        {
            foreach (var script in GetScriptsThatDoNotBelongToAnyAssembly().OrderBy(s => s))
                Debug.LogWarning($"Script '{script}' will not be compiled because it exists outside the Assets folder and does not to belong to any assembly definition file.");
        }

        private void WarnIfThereAreAssembliesWithoutAnyScripts(ScriptAssemblySettings scriptAssemblySettings, ScriptAssembly[] scriptAssemblies)
        {
            foreach (var targetAssembly in customTargetAssemblies.Values)
            {
                if (!EditorBuildRules.IsCompatibleWithPlatformAndDefines(targetAssembly, scriptAssemblySettings))
                    continue;

                if (scriptAssemblies.Any(s => s.Filename == targetAssembly.Filename))
                    continue;

                var customTargetAssembly = FindCustomTargetAssemblyFromTargetAssembly(targetAssembly);
                Debug.LogWarningFormat(
                    "Assembly for Assembly Definition File '{0}' will not be compiled, because it has no scripts associated with it.",
                    customTargetAssembly.FilePath);
            }
        }

        internal IEnumerable<string> GetScriptsThatDoNotBelongToAnyAssembly()
        {
            return allScripts
                .Where(e => EditorBuildRules.GetTargetAssembly(e.Key, e.Value, projectDirectory, customTargetAssemblies) == null)
                .Select(e => e.Key);
        }

        private static TargetAssembly[] GetPredefinedAssemblyReferences(IDictionary<string, TargetAssembly> targetAssemblies)
        {
            var targetAssembliesResult = (targetAssemblies.Values ?? Enumerable.Empty<TargetAssembly>())
                .Where(x => (x.Flags & AssemblyFlags.ExplicitlyReferenced) == AssemblyFlags.None)
                .ToArray();
            return targetAssembliesResult;
        }

        internal CompileStatus CompileScriptsWithSettings(ScriptAssemblySettings scriptAssemblySettings)
        {
            m_ScriptCompilationRequest = null;

            DeleteUnusedAssemblies(scriptAssemblySettings);

            IsRunningRoslynAnalysisSynchronously =
                PlayerSettings.EnableRoslynAnalyzers &&
                (scriptAssemblySettings.CompilationOptions & EditorScriptCompilationOptions.BuildingWithRoslynAnalysis) != 0;

            // If we have successfully compiled and reloaded all assemblies, then we can
            // skip checks on the asmdef compilation graph to ensure there no
            // setup errors like cyclic references, duplicate assembly names, etc.
            // If there is compilation errors n Safe Mode domain or a partial domain (if SafeMode is forcefully exited),
            // Then we need to keep the validation checks to rediscover potential setup errors in subsequent compilations.
            if (!skipCustomScriptAssemblyGraphValidation)
                CheckCyclicAssemblyReferences();

            ScriptAssembly[] scriptAssemblies;
            try
            {
                scriptAssemblies = GetAllScriptAssembliesOfType(scriptAssemblySettings, TargetAssemblyType.Undefined);
            }
            catch (PrecompiledAssemblyException)
            {
                // The setup errors for this exception has already been logged earlier, so there's no need to log the exception.
                return CompileStatus.Idle;
            }

            WarnIfThereAreAssembliesWithoutAnyScripts(scriptAssemblySettings, scriptAssemblies);

            if (scriptAssemblySettings.BuildingForEditor)
                m_ScriptsForEditorHaveBeenCompiledSinceLastDomainReload = true;

            var debug = scriptAssemblySettings.CodeOptimization == CodeOptimization.Debug;

            //we're going to hash the output directory path into the dag name. We do this because when users build players into different directories,
            //we'd like to treat those as different dags. If we wouldn't do this, you could run into situations where building into directory2 will make
            //bee delete the previously built game from directory1.
            Hash128 hash = Hash128.Parse(scriptAssemblySettings.OutputDirectory);

            var config =
                hash.ToString().Substring(0, 5) +
                $"{(scriptAssemblySettings.BuildingForEditor ? "E" : "P")}" +
                $"{(scriptAssemblySettings.BuildingDevelopmentBuild ? "Dev" : "")}" +
                $"{(debug ? "Dbg" : "")}" +
                "";

            BuildTarget buildTarget = scriptAssemblySettings.BuildTarget;
            activeBeeBuild = new BeeScriptCompilationState()
            {
                Driver = UnityBeeDriver.Make(ScriptCompilationBuildProgram, this, $"{(int)buildTarget}{config}", useScriptUpdater: !scriptAssemblySettings.BuildingWithoutScriptUpdater),
                settings = scriptAssemblySettings,
                assemblies = scriptAssemblies,
            };

            BeeScriptCompilation.AddScriptCompilationData(activeBeeBuild.Driver, this, activeBeeBuild.assemblies, debug, scriptAssemblySettings.OutputDirectory, buildTarget, scriptAssemblySettings.BuildingForEditor);

            InvokeCompilationStarted(activeBeeBuild);

            activeBeeBuild.Driver.BuildAsync(Constants.ScriptAssembliesTarget);
            return CompileStatus.CompilationStarted;
        }

        internal static SystemProcessRunnableProgram ScriptCompilationBuildProgram { get; } = MakeScriptCompilationBuildProgram();

        private static SystemProcessRunnableProgram MakeScriptCompilationBuildProgram()
        {
            var buildProgramAssembly = new NPath($"{EditorApplication.applicationContentsPath}/ScriptCompilationBuildProgram/ScriptCompilationBuildProgram.exe");
            return new SystemProcessRunnableProgram($"{EditorApplication.applicationContentsPath}/Tools/netcorerun/netcorerun{BeeScriptCompilation.ExecutableExtension}", buildProgramAssembly.ToString(SlashMode.Native));
        }

        public void InvokeCompilationStarted(object context)
        {
            compilationStarted?.Invoke(context);
        }

        public void InvokeCompilationFinished(object context)
        {
            compilationFinished?.Invoke(context);
        }

        public bool DoesProjectFolderHaveAnyScripts()
        {
            return allScripts != null && allScripts.Count > 0;
        }

        internal ScriptAssemblySettings CreateScriptAssemblySettings(BuildTargetGroup buildTargetGroup, BuildTarget buildTarget, EditorScriptCompilationOptions options)
        {
            return CreateScriptAssemblySettings(buildTargetGroup, buildTarget, options, new string[] {});
        }

        internal ScriptAssemblySettings CreateScriptAssemblySettings(BuildTargetGroup buildTargetGroup, BuildTarget buildTarget, EditorScriptCompilationOptions options, string[] extraScriptingDefines)
        {
            var predefinedAssembliesCompilerOptions = new ScriptCompilerOptions();

            if ((options & EditorScriptCompilationOptions.BuildingPredefinedAssembliesAllowUnsafeCode) == EditorScriptCompilationOptions.BuildingPredefinedAssembliesAllowUnsafeCode)
                predefinedAssembliesCompilerOptions.AllowUnsafeCode = true;

            if ((options & EditorScriptCompilationOptions.BuildingUseDeterministicCompilation) == EditorScriptCompilationOptions.BuildingUseDeterministicCompilation)
                predefinedAssembliesCompilerOptions.UseDeterministicCompilation = true;

            predefinedAssembliesCompilerOptions.ApiCompatibilityLevel = PlayerSettings.GetApiCompatibilityLevel(buildTargetGroup);

            ICompilationExtension compilationExtension = null;
            if ((options & EditorScriptCompilationOptions.BuildingForEditor) == 0)
            {
                compilationExtension = ModuleManager.FindPlatformSupportModule(ModuleManager.GetTargetStringFromBuildTarget(buildTarget))?.CreateCompilationExtension();
            }


            List<string> additionalCompilationArguments = new List<string>(PlayerSettings.GetAdditionalCompilerArgumentsForGroup(buildTargetGroup));

            if (PlayerSettings.suppressCommonWarnings)
            {
                additionalCompilationArguments.Add("/nowarn:0169");
                additionalCompilationArguments.Add("/nowarn:0649");
            }

            var additionalCompilationArgumentsArray = additionalCompilationArguments.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToArray();

            var settings = new ScriptAssemblySettings
            {
                BuildTarget = buildTarget,
                BuildTargetGroup = buildTargetGroup,
                OutputDirectory = GetCompileScriptsOutputDirectory(),
                CompilationOptions = options,
                PredefinedAssembliesCompilerOptions = predefinedAssembliesCompilerOptions,
                CompilationExtension = compilationExtension,
                EditorCodeOptimization = CompilationPipeline.codeOptimization,
                ExtraGeneralDefines = extraScriptingDefines,
                ProjectRootNamespace = EditorSettings.projectGenerationRootNamespace,
                ProjectDirectory = projectDirectory,
                AdditionalCompilerArguments = additionalCompilationArgumentsArray
            };

            return settings;
        }

        ScriptAssemblySettings CreateEditorScriptAssemblySettings(EditorScriptCompilationOptions options)
        {
            return CreateScriptAssemblySettings(EditorUserBuildSettings.activeBuildTargetGroup, EditorUserBuildSettings.activeBuildTarget, options);
        }

        private static CompilerMessage AsCompilerMessage(BeeDriverResult.Message message)
        {
            return new CompilerMessage
            {
                message = message.Text,
                type = message.Kind == BeeDriverResult.MessageKind.Error ? CompilerMessageType.Error : CompilerMessageType.Warning,
            };
        }

        //only used in for tests to peek in.
        internal CompilerMessage[] GetCompileMessages() => _currentEditorCompilationCompilerMessages;

        public bool IsScriptCompilationRequested()
        {
            return m_ScriptCompilationRequest != null;
        }

        public bool IsAnyAssemblyBuilderCompiling()
        {
            if (assemblyBuilders.Count > 0)
            {
                bool isCompiling = false;

                var removeAssemblyBuilders = new List<Compilation.AssemblyBuilder>();

                // Check status of compile tasks
                foreach (var assemblyBuilder in assemblyBuilders)
                {
                    var status = assemblyBuilder.status;

                    if (status == Compilation.AssemblyBuilderStatus.IsCompiling)
                        isCompiling = true;
                    else if (status == Compilation.AssemblyBuilderStatus.Finished)
                        removeAssemblyBuilders.Add(assemblyBuilder);
                }

                // Remove all compile tasks that finished compiling.
                if (removeAssemblyBuilders.Count > 0)
                    assemblyBuilders.RemoveAll(t => removeAssemblyBuilders.Contains(t));

                return isCompiling;
            }

            return false;
        }

        public bool IsCompiling()
        {
            // Native code expects IsCompiling to be true after requesting a script reload,
            // therefore return true if the compilation is pending
            return IsCompilationTaskCompiling() || IsScriptCompilationRequested() || IsAnyAssemblyBuilderCompiling();
        }

        public bool IsCompilationTaskCompiling()
        {
            return activeBeeBuild != null;
        }

        public CompileStatus TickCompilationPipeline(EditorScriptCompilationOptions options, BuildTargetGroup platformGroup, BuildTarget platform, string[] extraScriptingDefines)
        {
            // Return CompileStatus.Compiling if any compile task is still compiling.
            // This ensures that the compile tasks finish compiling before any
            // scripts in the Assets folder are compiled and a domain reload
            // is triggered.
            if (IsAnyAssemblyBuilderCompiling())
                return CompileStatus.Compiling;

            // If we are not currently compiling and there are new dirty assemblies, start compilation.
            if (!IsCompilationTaskCompiling() && IsScriptCompilationRequested())
            {
                Profiler.BeginSample("CompilationPipeline.CompileScripts");
                if (m_ScriptCompilationRequest == RequestScriptCompilationOptions.CleanBuildCache)
                    CleanCache();
                CompileStatus compileStatus;
                try
                {
                    compileStatus = CompileScripts(options, platformGroup, platform, extraScriptingDefines);
                }
                finally
                {
                    Profiler.EndSample();
                }
                return compileStatus;
            }

            if (activeBeeBuild == null)
                return CompileStatus.Idle;

            var result = activeBeeBuild.Driver.Tick();
            if (result == null)
                return CompileStatus.Compiling;

            var messagesForNodeResults = ProcessCompilationResult(activeBeeBuild.assemblies, result, activeBeeBuild);

            int logIdentifier = activeBeeBuild.settings.BuildingForEditor
                //these numbers are "randomly picked". they are used to so that when you log a message with a certain identifier, later all messages with that identifier can be cleared.
                //one means "compilation error for compiling-assemblies-for-editor"  the other means "compilation error for building a player".
                ? kLogIdentifierFor_EditorMessages
                : kLogIdentifierFor_PlayerMessages;
            var compilerMessages = result.BeeDriverMessages.Select(AsCompilerMessage).Concat(messagesForNodeResults.SelectMany(m => m)).ToArray();

            if (activeBeeBuild.settings.BuildingForEditor)
                _currentEditorCompilationCompilerMessages = compilerMessages;

            if (_logCompilationMessages)
            {
                Debug.RemoveLogEntriesByIdentifier(logIdentifier);
                foreach (var message in compilerMessages)
                    Debug.LogCompilerMessage(message.message, message.file, message.line, message.column, activeBeeBuild.settings.BuildingForEditor, message.type == CompilerMessageType.Error, logIdentifier);
            }

            activeBeeBuild = null;
            return result.Success
                ? CompileStatus.CompilationComplete
                : CompileStatus.CompilationFailed;
        }

        internal void DisableLoggingEditorCompilerMessages()
        {
            _logCompilationMessages = false;
        }

        internal CompilerMessage[][] ProcessCompilationResult(ScriptAssembly[] assemblies, BeeDriverResult result, object context)
        {
            var compilerMessagesForNodeResults = BeeScriptCompilation.ParseAllNodeResultsIntoCompilerMessages(result.NodeResults, this);
            InvokeAssemblyCompilationFinished(assemblies, result, compilerMessagesForNodeResults);
            InvokeCompilationFinished(context);
            return compilerMessagesForNodeResults;
        }

        private void InvokeAssemblyCompilationFinished(ScriptAssembly[] assemblies, BeeDriverResult beeDriverResult, CompilerMessage[][] compilerMessagesForNodeResults)
        {
            bool Belongs(ScriptAssembly scriptAssembly, NodeResult nodeResult) => new NPath(nodeResult.outputfile).FileName == scriptAssembly.Filename;

            if (assemblyCompilationFinished == null)
                return;

            //we want to send callbacks for assemblies that were copied, for assemblies that failed to compile, but not for assemblies that were compiled but not copied (because they ended up identically)
            bool RequiresCallbackInvocation(ScriptAssembly scriptAssembly)
            {
                var relatedNodes = beeDriverResult.NodeResults.Where(nodeResult => Belongs(scriptAssembly, nodeResult));
                return relatedNodes.Any(n => n.exitcode != 0 || n.annotation.StartsWith("CopyTool"));
            }

            foreach (var scriptAssembly in assemblies)
            {
                if (!RequiresCallbackInvocation(scriptAssembly))
                    continue;

                var nodeResultIndicesRelatedToAssembly = beeDriverResult.NodeResults
                    .Select((result, index) => (result, index))
                    .Where(c => Belongs(scriptAssembly, c.result))
                    .Select(c => c.index);

                var messagesForAssembly = nodeResultIndicesRelatedToAssembly.SelectMany(index => compilerMessagesForNodeResults[index]).ToArray();
                scriptAssembly.HasCompileErrors = !beeDriverResult.Success;
                assemblyCompilationFinished?.Invoke(scriptAssembly, ConvertCompilerMessages(messagesForAssembly));
            }
        }

        public TargetAssemblyInfo[] GetTargetAssemblyInfos()
        {
            TargetAssembly[] predefindTargetAssemblies = EditorBuildRules.GetPredefinedTargetAssemblies();

            TargetAssemblyInfo[] targetAssemblyInfo = new TargetAssemblyInfo[predefindTargetAssemblies.Length + (customTargetAssemblies != null ? customTargetAssemblies.Count : 0)];

            for (int i = 0; i < predefindTargetAssemblies.Length; ++i)
                targetAssemblyInfo[i] = ToTargetAssemblyInfo(predefindTargetAssemblies[i]);

            if (customTargetAssemblies != null)
            {
                int i = predefindTargetAssemblies.Length;
                foreach (var entry in customTargetAssemblies)
                {
                    var customTargetAssembly = entry.Value;
                    targetAssemblyInfo[i] = ToTargetAssemblyInfo(customTargetAssembly);
                    i++;
                }
            }

            return targetAssemblyInfo;
        }

        TargetAssembly[] GetTargetAssemblies()
        {
            TargetAssembly[] predefindTargetAssemblies = EditorBuildRules.GetPredefinedTargetAssemblies();

            TargetAssembly[] targetAssemblies = new TargetAssembly[predefindTargetAssemblies.Length + (customTargetAssemblies != null ? customTargetAssemblies.Count : 0)];

            for (int i = 0; i < predefindTargetAssemblies.Length; ++i)
                targetAssemblies[i] = predefindTargetAssemblies[i];

            if (customTargetAssemblies != null)
            {
                int i = predefindTargetAssemblies.Length;
                foreach (var entry in customTargetAssemblies)
                {
                    var customTargetAssembly = entry.Value;
                    targetAssemblies[i] = customTargetAssembly;
                    i++;
                }
            }

            return targetAssemblies;
        }

        public TargetAssemblyInfo[] GetTargetAssembliesWithScripts(EditorScriptCompilationOptions options)
        {
            ScriptAssemblySettings settings = CreateEditorScriptAssemblySettings(EditorScriptCompilationOptions.BuildingForEditor | options);
            return GetTargetAssembliesWithScripts(settings);
        }

        public TargetAssemblyInfo[] GetTargetAssembliesWithScripts(ScriptAssemblySettings settings)
        {
            UpdateAllTargetAssemblyDefines(customTargetAssemblies, EditorBuildRules.GetPredefinedTargetAssemblies(), m_VersionMetaDatas, settings);

            var targetAssemblies = EditorBuildRules.GetTargetAssembliesWithScripts(allScripts, projectDirectory, customTargetAssemblies, settings);

            var targetAssemblyInfos = new TargetAssemblyInfo[targetAssemblies.Length];

            for (int i = 0; i < targetAssemblies.Length; ++i)
                targetAssemblyInfos[i] = ToTargetAssemblyInfo(targetAssemblies[i]);

            return targetAssemblyInfos;
        }

        public HashSet<TargetAssembly> GetTargetAssembliesWithScriptsHashSet(EditorScriptCompilationOptions options)
        {
            ScriptAssemblySettings settings = CreateEditorScriptAssemblySettings(EditorScriptCompilationOptions.BuildingForEditor | options);
            var targetAssemblies = EditorBuildRules.GetTargetAssembliesWithScriptsHashSet(allScripts, projectDirectory, customTargetAssemblies, settings);

            return targetAssemblies;
        }

        public TargetAssembly[] GetCustomTargetAssemblies()
        {
            return customTargetAssemblies.Values.ToArray();
        }

        public CustomScriptAssembly[] GetCustomScriptAssemblies()
        {
            return customScriptAssemblies;
        }

        public PrecompiledAssembly[] GetUnityAssemblies()
        {
            return unityAssemblies;
        }

        public TargetAssemblyInfo GetTargetAssembly(string scriptPath)
        {
            TargetAssembly targetAssembly = EditorBuildRules.GetTargetAssemblyLinearSearch(scriptPath, projectDirectory, customTargetAssemblies);

            TargetAssemblyInfo targetAssemblyInfo = ToTargetAssemblyInfo(targetAssembly);
            return targetAssemblyInfo;
        }

        public TargetAssembly GetTargetAssemblyDetails(string scriptPath)
        {
            return EditorBuildRules.GetTargetAssemblyLinearSearch(scriptPath, projectDirectory, customTargetAssemblies);
        }

        public ScriptAssembly[] GetAllEditorScriptAssemblies(EditorScriptCompilationOptions additionalOptions)
        {
            return GetAllScriptAssemblies(EditorScriptCompilationOptions.BuildingForEditor | EditorScriptCompilationOptions.BuildingIncludingTestAssemblies | additionalOptions, null);
        }

        public ScriptAssembly[] GetAllEditorScriptAssemblies(EditorScriptCompilationOptions additionalOptions, string[] defines)
        {
            return GetAllScriptAssemblies(EditorScriptCompilationOptions.BuildingForEditor | EditorScriptCompilationOptions.BuildingIncludingTestAssemblies | additionalOptions, defines);
        }

        public ScriptAssembly[] GetScriptAssembliesForRoslynAnalysis(string[] candidateAssemblies)
        {
            Dictionary<string, PrecompiledAssembly> precompiledAssemblies =
                GetPrecompiledAssembliesDictionaryWithSetupErrorsTracking(
                    isEditor: true,
                    EditorUserBuildSettings.activeBuildTargetGroup,
                    EditorUserBuildSettings.activeBuildTarget);

            ScriptAssemblySettings settings =
                CreateEditorScriptAssemblySettings(EditorScriptCompilationOptions.BuildingForEditor | EditorScriptCompilationOptions.BuildingWithRoslynAnalysis);

            return GetAllScriptAssemblies(
                settings,
                unityAssemblies,
                precompiledAssemblies,
                defines: null,
                targetAssemblyCondition: assembly => (assembly.Flags & AssemblyFlags.CandidateForCompilingWithRoslynAnalyzers) != 0
                && candidateAssemblies.Contains(assembly.Filename));
        }

        public ScriptAssembly[] GetAllScriptAssemblies(EditorScriptCompilationOptions options, string[] defines)
        {
            var isForEditor = (options & EditorScriptCompilationOptions.BuildingForEditor) == EditorScriptCompilationOptions.BuildingForEditor;
            var precompiledAssemblies = GetPrecompiledAssembliesDictionaryWithSetupErrorsTracking(
                isForEditor, EditorUserBuildSettings.activeBuildTargetGroup, EditorUserBuildSettings.activeBuildTarget);
            return GetAllScriptAssemblies(options, unityAssemblies, precompiledAssemblies, defines);
        }

        public ScriptAssembly[] GetAllScriptAssemblies(
            EditorScriptCompilationOptions options,
            PrecompiledAssembly[] unityAssembliesArg,
            Dictionary<string, PrecompiledAssembly> precompiledAssembliesArg,
            string[] defines)
        {
            var settings = CreateEditorScriptAssemblySettings(options);

            return GetAllScriptAssemblies(
                settings,
                unityAssembliesArg,
                precompiledAssembliesArg,
                defines);
        }

        public ScriptAssembly[] GetAllScriptAssemblies(
            ScriptAssemblySettings settings,
            PrecompiledAssembly[] unityAssembliesArg,
            Dictionary<string, PrecompiledAssembly> precompiledAssembliesArg,
            string[] defines,
            Func<TargetAssembly, bool> targetAssemblyCondition = null)
        {
            if (defines != null)
            {
                settings.ExtraGeneralDefines = defines;
            }

            UpdateAllTargetAssemblyDefines(customTargetAssemblies, EditorBuildRules.GetPredefinedTargetAssemblies(), m_VersionMetaDatas, settings);

            var assemblies = new EditorBuildRules.CompilationAssemblies
            {
                UnityAssemblies = unityAssembliesArg,
                PrecompiledAssemblies = precompiledAssembliesArg,
                CustomTargetAssemblies = customTargetAssemblies,
                RoslynAnalyzerDllPaths = PrecompiledAssemblyProvider.GetRoslynAnalyzerPaths(),
                PredefinedAssembliesCustomTargetReferences = GetPredefinedAssemblyReferences(customTargetAssemblies),
                EditorAssemblyReferences = ModuleUtils.GetAdditionalReferencesForUserScripts(),
            };

            return EditorBuildRules.GetAllScriptAssemblies(
                allScripts,
                projectDirectory,
                settings,
                assemblies,
                targetAssemblyCondition: targetAssemblyCondition);
        }

        public string[] GetTargetAssemblyDefines(TargetAssembly targetAssembly, ScriptAssemblySettings settings)
        {
            var semVersionRangesFactory = new VersionRangesFactory<SemVersion>();
            var unityVersionRangesFactory = new VersionRangesFactory<UnityVersion>();
            var versionMetaDatas = GetVersionMetaDatas();
            var editorOnlyCompatibleDefines = InternalEditorUtility.GetCompilationDefines(settings.CompilationOptions, settings.BuildTargetGroup, settings.BuildTarget, ApiCompatibilityLevel.NET_4_6);
            var playerAssembliesDefines = InternalEditorUtility.GetCompilationDefines(settings.CompilationOptions, settings.BuildTargetGroup, settings.BuildTarget, settings.PredefinedAssembliesCompilerOptions.ApiCompatibilityLevel);

            return GetTargetAssemblyDefines(targetAssembly, semVersionRangesFactory, unityVersionRangesFactory, versionMetaDatas, editorOnlyCompatibleDefines, playerAssembliesDefines, settings);
        }

        // TODO: Get rid of calls to this method and ensure that the defines are always setup correctly at all times.
        private static void UpdateAllTargetAssemblyDefines(IDictionary<string, TargetAssembly> customScriptAssemblies, TargetAssembly[] predefinedTargetAssemblies,
            Dictionary<string, VersionMetaData> versionMetaDatas, ScriptAssemblySettings settings)
        {
            var allTargetAssemblies = customScriptAssemblies.Values.ToArray()
                .Concat(predefinedTargetAssemblies ?? new TargetAssembly[0]);

            var semVersionRangesFactory = new VersionRangesFactory<SemVersion>();
            var unityVersionRangesFactory = new VersionRangesFactory<UnityVersion>();

            string[] editorOnlyCompatibleDefines = null;

            editorOnlyCompatibleDefines = InternalEditorUtility.GetCompilationDefines(settings.CompilationOptions, settings.BuildTargetGroup, settings.BuildTarget, ApiCompatibilityLevel.NET_4_6);

            var playerAssembliesDefines = InternalEditorUtility.GetCompilationDefines(settings.CompilationOptions, settings.BuildTargetGroup, settings.BuildTarget, settings.PredefinedAssembliesCompilerOptions.ApiCompatibilityLevel);

            foreach (var targetAssembly in allTargetAssemblies)
            {
                SetTargetAssemblyDefines(targetAssembly, semVersionRangesFactory, unityVersionRangesFactory, versionMetaDatas, editorOnlyCompatibleDefines, playerAssembliesDefines, settings);
            }
        }

        private static void SetTargetAssemblyDefines(TargetAssembly targetAssembly, VersionRangesFactory<SemVersion> semVersionRangesFactory, VersionRangesFactory<UnityVersion> unityVersionRangesFactory,
            Dictionary<string, VersionMetaData> versionMetaDatas, string[] editorOnlyCompatibleDefines, string[] playerAssembliesDefines, ScriptAssemblySettings settings)
        {
            targetAssembly.Defines = GetTargetAssemblyDefines(targetAssembly, semVersionRangesFactory, unityVersionRangesFactory, versionMetaDatas, editorOnlyCompatibleDefines, playerAssembliesDefines, settings);
        }

        private static string[] GetTargetAssemblyDefines(TargetAssembly targetAssembly, VersionRangesFactory<SemVersion> semVersionRangesFactory, VersionRangesFactory<UnityVersion> unityVersionRangesFactory,
            Dictionary<string, VersionMetaData> versionMetaDatas, string[] editorOnlyCompatibleDefines, string[] playerAssembliesDefines, ScriptAssemblySettings settings)
        {
            string[] settingsExtraGeneralDefines = settings.ExtraGeneralDefines;
            int populatedVersionDefinesCount = 0;

            string[] compilationDefines;
            if ((targetAssembly.Flags & AssemblyFlags.EditorOnly) == AssemblyFlags.EditorOnly)
            {
                compilationDefines = editorOnlyCompatibleDefines;
            }
            else
            {
                compilationDefines = playerAssembliesDefines;
            }

            string[] defines = new string[compilationDefines.Length + targetAssembly.VersionDefines.Count + settingsExtraGeneralDefines.Length];

            Array.Copy(settingsExtraGeneralDefines, defines, settingsExtraGeneralDefines.Length);
            populatedVersionDefinesCount += settingsExtraGeneralDefines.Length;
            Array.Copy(compilationDefines, 0, defines, populatedVersionDefinesCount, compilationDefines.Length);
            populatedVersionDefinesCount += compilationDefines.Length;

            if (versionMetaDatas == null)
            {
                return defines;
            }

            var targetAssemblyVersionDefines = targetAssembly.VersionDefines;

            for (int i = 0; i < targetAssemblyVersionDefines.Count; i++)
            {
                var targetAssemblyVersionDefine = targetAssemblyVersionDefines[i];
                if (!versionMetaDatas.ContainsKey(targetAssemblyVersionDefine.name))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(targetAssemblyVersionDefine.expression))
                {
                    var define = targetAssemblyVersionDefine.define;
                    if (!string.IsNullOrEmpty(define))
                    {
                        defines[populatedVersionDefinesCount] = define;
                        ++populatedVersionDefinesCount;
                    }
                    continue;
                }

                try
                {
                    var versionMetaData = versionMetaDatas[targetAssemblyVersionDefine.name];
                    var versionString = versionMetaData.Version;
                    bool isValid = false;
                    switch (versionMetaData.Type)
                    {
                        case VersionType.VersionTypeUnity:
                        {
                            var versionDefineExpression = unityVersionRangesFactory.GetExpression(targetAssemblyVersionDefine.expression);
                            var unityVersion = UnityVersionParser.Parse(versionString);
                            isValid = versionDefineExpression.IsValid(unityVersion);
                            break;
                        }

                        case VersionType.VersionTypePackage:
                        {
                            var versionDefineExpression = semVersionRangesFactory.GetExpression(targetAssemblyVersionDefine.expression);
                            var semVersion = SemVersionParser.Parse(versionString);
                            isValid = versionDefineExpression.IsValid(semVersion);
                            break;
                        }

                        default:
                            throw new NotImplementedException($"EditorCompilation does not recognize versionMetaData.Type {versionMetaData.Type}. UNIMPLEMENTED");
                    }

                    if (isValid)
                    {
                        defines[populatedVersionDefinesCount] = targetAssemblyVersionDefine.define;
                        ++populatedVersionDefinesCount;
                    }
                }
                catch (Exception e)
                {
                    var asset = AssetDatabase.LoadAssetAtPath<AssemblyDefinitionAsset>(EditorCompilationInterface.Instance.FindCustomTargetAssemblyFromTargetAssembly(targetAssembly).FilePath);
                    Debug.LogException(e, asset);
                }
            }

            Array.Resize(ref defines, populatedVersionDefinesCount);
            return defines;
        }

        internal ScriptAssembly[] GetAllScriptAssembliesOfType(ScriptAssemblySettings settings, TargetAssemblyType type)
        {
            using (new ProfilerMarker(nameof(GetAllScriptAssembliesOfType)).Auto())
            {
                var precompiledAssemblies =
                    GetPrecompiledAssembliesDictionaryWithSetupErrorsTracking(settings.BuildingForEditor,
                        settings.BuildTargetGroup, settings.BuildTarget);

                UpdateAllTargetAssemblyDefines(customTargetAssemblies, EditorBuildRules.GetPredefinedTargetAssemblies(), m_VersionMetaDatas, settings);

                var assemblies = new EditorBuildRules.CompilationAssemblies
                {
                    UnityAssemblies = unityAssemblies,
                    PrecompiledAssemblies = precompiledAssemblies,
                    CustomTargetAssemblies = customTargetAssemblies,
                    RoslynAnalyzerDllPaths = PrecompiledAssemblyProvider.GetRoslynAnalyzerPaths(),
                    PredefinedAssembliesCustomTargetReferences = GetPredefinedAssemblyReferences(customTargetAssemblies),
                    EditorAssemblyReferences = ModuleUtils.GetAdditionalReferencesForUserScripts(),
                };

                return EditorBuildRules.GetAllScriptAssemblies(allScripts, projectDirectory, settings, assemblies, type);
            }
        }

        public bool IsRuntimeScriptAssembly(string assemblyNameOrPath)
        {
            var assemblyFilename = AssetPath.GetFileName(assemblyNameOrPath);

            if (!assemblyFilename.EndsWith(".dll"))
                assemblyFilename += ".dll";

            var predefinedAssemblyTargets = EditorBuildRules.GetPredefinedTargetAssemblies();

            if (predefinedAssemblyTargets.Any(a => ((a.Flags & AssemblyFlags.EditorOnly) != AssemblyFlags.EditorOnly) && a.Filename == assemblyFilename))
                return true;

            if (customTargetAssemblies != null && customTargetAssemblies.Any(a => ((a.Value.Flags & AssemblyFlags.EditorOnly) != AssemblyFlags.EditorOnly) && a.Value.Filename == assemblyFilename))
                return true;

            return false;
        }

        TargetAssemblyInfo ToTargetAssemblyInfo(TargetAssembly targetAssembly)
        {
            TargetAssemblyInfo targetAssemblyInfo = new TargetAssemblyInfo();

            if (targetAssembly != null)
            {
                targetAssemblyInfo.Name = targetAssembly.Filename;
                targetAssemblyInfo.Flags = targetAssembly.Flags;
            }
            else
            {
                targetAssemblyInfo.Name = "";
                targetAssemblyInfo.Flags = AssemblyFlags.None;
            }

            return targetAssemblyInfo;
        }

        static EditorScriptCompilationOptions ToEditorScriptCompilationOptions(Compilation.AssemblyBuilderFlags flags)
        {
            EditorScriptCompilationOptions options = EditorScriptCompilationOptions.BuildingEmpty;

            if ((flags & Compilation.AssemblyBuilderFlags.DevelopmentBuild) == Compilation.AssemblyBuilderFlags.DevelopmentBuild)
                options |= EditorScriptCompilationOptions.BuildingDevelopmentBuild;

            if ((flags & Compilation.AssemblyBuilderFlags.EditorAssembly) == Compilation.AssemblyBuilderFlags.EditorAssembly)
                options |= EditorScriptCompilationOptions.BuildingForEditor;

            return options;
        }

        static AssemblyFlags ToAssemblyFlags(Compilation.AssemblyBuilderFlags assemblyBuilderFlags)
        {
            AssemblyFlags assemblyFlags = AssemblyFlags.None;

            if ((assemblyBuilderFlags & Compilation.AssemblyBuilderFlags.EditorAssembly) == Compilation.AssemblyBuilderFlags.EditorAssembly)
                assemblyFlags |= AssemblyFlags.EditorOnly;

            return assemblyFlags;
        }

        static EditorBuildRules.UnityReferencesOptions ToUnityReferencesOptions(ReferencesOptions options)
        {
            var result = EditorBuildRules.UnityReferencesOptions.ExcludeModules;

            if ((options & ReferencesOptions.UseEngineModules) == ReferencesOptions.UseEngineModules)
            {
                result = EditorBuildRules.UnityReferencesOptions.None;
            }

            return result;
        }

        ScriptAssembly InitializeScriptAssemblyWithoutReferencesAndDefines(Compilation.AssemblyBuilder assemblyBuilder)
        {
            var scriptFiles = assemblyBuilder.scriptPaths.Select(p => AssetPath.Combine(projectDirectory, p)).ToArray();
            var assemblyPath = AssetPath.Combine(projectDirectory, assemblyBuilder.assemblyPath);

            var scriptAssembly = new ScriptAssembly();
            scriptAssembly.Flags = ToAssemblyFlags(assemblyBuilder.flags);
            scriptAssembly.BuildTarget = assemblyBuilder.buildTarget;
            scriptAssembly.Files = scriptFiles;
            scriptAssembly.Filename = AssetPath.GetFileName(assemblyPath);
            scriptAssembly.OutputDirectory = AssetPath.GetDirectoryName(assemblyPath);
            scriptAssembly.CompilerOptions = assemblyBuilder.compilerOptions;
            scriptAssembly.CompilerOptions.ApiCompatibilityLevel = PlayerSettings.GetApiCompatibilityLevel(assemblyBuilder.buildTargetGroup);
            scriptAssembly.ScriptAssemblyReferences = new ScriptAssembly[0];
            scriptAssembly.RootNamespace = string.Empty;

            return scriptAssembly;
        }

        public ScriptAssembly CreateScriptAssembly(Compilation.AssemblyBuilder assemblyBuilder)
        {
            var scriptAssembly = InitializeScriptAssemblyWithoutReferencesAndDefines(assemblyBuilder);

            var options = ToEditorScriptCompilationOptions(assemblyBuilder.flags);
            var referencesOptions = ToUnityReferencesOptions(assemblyBuilder.referencesOptions);

            var references = GetAssemblyBuilderDefaultReferences(scriptAssembly, options, referencesOptions);

            if (assemblyBuilder.additionalReferences != null && assemblyBuilder.additionalReferences.Length > 0)
                references = references.Concat(assemblyBuilder.additionalReferences).ToArray();

            if (assemblyBuilder.excludeReferences != null && assemblyBuilder.excludeReferences.Length > 0)
                references = references.Where(r => !assemblyBuilder.excludeReferences.Contains(r)).ToArray();

            var defines = GetAssemblyBuilderDefaultDefines(assemblyBuilder);

            if (assemblyBuilder.additionalDefines != null)
                defines = defines.Concat(assemblyBuilder.additionalDefines).ToArray();

            scriptAssembly.References = references.ToArray();
            scriptAssembly.Defines = defines.ToArray();

            return scriptAssembly;
        }

        string[] GetAssemblyBuilderDefaultReferences(ScriptAssembly scriptAssembly, EditorScriptCompilationOptions options, EditorBuildRules.UnityReferencesOptions unityReferencesOptions)
        {
            bool buildingForEditor = (scriptAssembly.Flags & AssemblyFlags.EditorOnly) == AssemblyFlags.EditorOnly;

            var monolithicEngineAssemblyPath = InternalEditorUtility.GetMonolithicEngineAssemblyPath();

            var unityReferences = EditorBuildRules.GetUnityReferences(scriptAssembly, unityAssemblies, options, unityReferencesOptions);

            var customReferences = EditorBuildRules.GetCompiledCustomAssembliesReferences(scriptAssembly, customTargetAssemblies, GetCompileScriptsOutputDirectory());

            var precompiledAssemblies = GetPrecompiledAssembliesWithSetupErrorsTracking(
                buildingForEditor, EditorUserBuildSettings.activeBuildTargetGroup, EditorUserBuildSettings.activeBuildTarget);
            var precompiledReferences = EditorBuildRules.GetPrecompiledReferences(scriptAssembly, TargetAssemblyType.Custom, options, EditorCompatibility.CompatibleWithEditor, precompiledAssemblies);
            var additionalReferences = MonoLibraryHelpers.GetSystemLibraryReferences(scriptAssembly.CompilerOptions.ApiCompatibilityLevel);
            string[] editorReferences = buildingForEditor ? ModuleUtils.GetAdditionalReferencesForUserScripts() : new string[0];

            var references = new List<string>();

            if (unityReferencesOptions == EditorBuildRules.UnityReferencesOptions.ExcludeModules)
                references.Add(monolithicEngineAssemblyPath);

            references.AddRange(unityReferences.Values); // unity references paths
            references.AddRange(customReferences);
            references.AddRange(precompiledReferences);
            references.AddRange(editorReferences);
            references.AddRange(additionalReferences);

            return references.ToArray();
        }

        public string[] GetAssemblyBuilderDefaultReferences(AssemblyBuilder assemblyBuilder)
        {
            var scriptAssembly = InitializeScriptAssemblyWithoutReferencesAndDefines(assemblyBuilder);
            var options = ToEditorScriptCompilationOptions(assemblyBuilder.flags);
            var referencesOptions = ToUnityReferencesOptions(assemblyBuilder.referencesOptions);
            var references = GetAssemblyBuilderDefaultReferences(scriptAssembly, options, referencesOptions);

            return references;
        }

        public string[] GetAssemblyBuilderDefaultDefines(AssemblyBuilder assemblyBuilder)
        {
            var options = ToEditorScriptCompilationOptions(assemblyBuilder.flags);
            var defines = InternalEditorUtility.GetCompilationDefines(options, assemblyBuilder.buildTargetGroup, assemblyBuilder.buildTarget);
            return defines;
        }

        public void AddAssemblyBuilder(AssemblyBuilder assemblyBuilder)
        {
            assemblyBuilders.Add(assemblyBuilder);
        }

        public static UnityEditor.Compilation.CompilerMessage[] ConvertCompilerMessages(CompilerMessage[] messages)
        {
            static Compilation.CompilerMessageType TypeFor(CompilerMessageType compilerMessageType)
            {
                switch (compilerMessageType)
                {
                    case CompilerMessageType.Error:
                        return UnityEditor.Compilation.CompilerMessageType.Error;
                    case CompilerMessageType.Warning:
                        return UnityEditor.Compilation.CompilerMessageType.Warning;
                    case CompilerMessageType.Information:
                        return UnityEditor.Compilation.CompilerMessageType.Info;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            return messages.Select(message => new UnityEditor.Compilation.CompilerMessage
            {
                message = message.message,
                file = message.file,
                line = message.line,
                column = message.column,
                type = TypeFor(message.type)
            }).ToArray();
        }
    }
}
