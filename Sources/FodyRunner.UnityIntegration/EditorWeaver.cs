namespace Malimbe.FodyRunner.UnityIntegration
{
    using System;
    using Reflection = System.Reflection;
    using System.Collections.Generic;
    using System.Linq;
    using JetBrains.Annotations;
    using UnityEditor;
    using UnityEditor.Compilation;
    using UnityEngine;

    internal static class EditorWeaver
    {
        [InitializeOnLoadMethod]
        private static void OnEditorInitialization()
        {
#if !UNITY_2021_1_OR_NEWER
            CompilationPipeline.assemblyCompilationFinished += OnCompilationFinished;
#endif
            WeaveAllAssemblies();
        }

        private static void WeaveAllAssemblies()
        {
            EditorApplication.LockReloadAssemblies();

#if !UNITY_2021_1_OR_NEWER
            bool didChangeAnyAssembly = false;
#endif

            try
            {
                IReadOnlyCollection<string> searchPaths = WeaverPathsHelper.GetSearchPaths().ToList();
                Runner runner = new Runner(new Logger());
                runner.Configure(searchPaths, searchPaths);

                foreach (Assembly assembly in GetAllAssemblies())
                {
                    if (!WeaveAssembly(assembly, runner))
                    {
                        continue;
                    }

                    string sourceFilePath = assembly.sourceFiles.FirstOrDefault();
                    if (sourceFilePath == null)
                    {
                        continue;
                    }

#if !UNITY_2021_1_OR_NEWER
                    AssetDatabase.ImportAsset(sourceFilePath, ImportAssetOptions.ForceUpdate);
                    didChangeAnyAssembly = true;
#endif
                }
            }
            finally
            {
                EditorApplication.UnlockReloadAssemblies();

#if !UNITY_2021_1_OR_NEWER
                if (didChangeAnyAssembly)
                {
                    AssetDatabase.Refresh();
                    Reflection.MethodInfo checkMethod = typeof(EditorUtility).GetMethod("RequestScriptReload", Reflection.BindingFlags.Public | Reflection.BindingFlags.Static);
                    if (checkMethod != null)
                    {
                        checkMethod.Invoke(null, null);
                    }
                }
#endif
            }
        }

#if !UNITY_2021_1_OR_NEWER
        private static void OnCompilationFinished(string path, CompilerMessage[] messages)
        {
            Assembly foundAssembly = GetAllAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.outputPath, path, StringComparison.Ordinal));
            if (foundAssembly == null)
            {
                return;
            }

            IReadOnlyCollection<string> searchPaths = WeaverPathsHelper.GetSearchPaths().ToList();
            Runner runner = new Runner(new Logger());
            runner.Configure(searchPaths, searchPaths);

            WeaveAssembly(foundAssembly, runner);
        }
#endif

        [NotNull]
        private static IEnumerable<Assembly> GetAllAssemblies() =>
            CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                .GroupBy(assembly => assembly.outputPath)
                .Select(grouping => grouping.First());

        private static bool WeaveAssembly(Assembly assembly, Runner runner)
        {
            try
            {
                string assemblyPath = WeaverPathsHelper.AddProjectPathRootIfNeeded(assembly.outputPath);
                IEnumerable<string> references =
                    assembly.allReferences.Select(WeaverPathsHelper.AddProjectPathRootIfNeeded);
                return runner.RunAsync(assemblyPath, references, assembly.defines.ToList(), true)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return false;
            }
        }
    }
}
