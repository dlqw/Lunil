using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Lunil.Hosting;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Lunil.Unity.Editor
{
    /// <summary>Runs the Lunil binding generator outside Unity's compiler and imports C# 9 output.</summary>
    public static class LuaUnityBindingPrecompiler
    {
        private const string DefaultOutput = "Assets/LunilGenerated/LuaClrGeneratedBindings.g.cs";

        [MenuItem("Tools/Lunil/Generate AOT CLR Bindings")]
        public static void GenerateFromMenu()
        {
            Generate(DefaultOutput);
        }

        public static void GenerateFromCommandLine()
        {
            var output = GetArgument("-lunilBindingOutput") ?? DefaultOutput;
            Generate(output);
        }

        public static void Generate(string outputAssetPath)
        {
            if (string.IsNullOrWhiteSpace(outputAssetPath) ||
                !outputAssetPath.Replace('\\', '/').StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException("Generated bindings must be written below Assets/.", nameof(outputAssetPath));

            var requests = CollectRequests();
            if (requests.Count == 0)
                throw new InvalidOperationException(
                    "No LuaClrGenerateBinding assembly attributes were found in loaded project assemblies.");

            var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var work = Path.Combine(root, "Library", "Lunil", "BindingGeneration");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            var generated = Path.Combine(work, "generated");
            Directory.CreateDirectory(generated);

            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                typeof(LuaUnityBindingPrecompiler).Assembly);
            if (package == null) throw new InvalidOperationException("Could not locate the Lunil Unity package.");
            var generatorBytes = Path.Combine(package.resolvedPath, "Editor", "Tools",
                "Lunil.Hosting.Generators.dll.bytes");
            var generator = Path.Combine(work, "Lunil.Hosting.Generators.dll");
            File.Copy(generatorBytes, generator, true);

            File.WriteAllText(Path.Combine(work, "BindingRequests.cs"), CreateRequestsSource(requests),
                new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(work, "BindingGeneration.csproj"),
                CreateProjectSource(root, generator), new UTF8Encoding(false));

            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "build \"" + Path.Combine(work, "BindingGeneration.csproj") +
                    "\" -c Release --nologo -p:EmitCompilerGeneratedFiles=true" +
                    " -p:CompilerGeneratedFilesOutputPath=\"" + generated + "\"",
                WorkingDirectory = work,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = System.Diagnostics.Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("Could not start dotnet binding generation.");
                var standardOutput = new StringBuilder();
                var standardError = new StringBuilder();
                process.OutputDataReceived += (sender, args) =>
                {
                    if (args.Data != null) standardOutput.AppendLine(args.Data);
                };
                process.ErrorDataReceived += (sender, args) =>
                {
                    if (args.Data != null) standardError.AppendLine(args.Data);
                };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("Binding generation failed.\n" +
                        standardOutput + "\n" + standardError);
            }

            var generatedFile = Directory.GetFiles(generated, "*LuaClrGeneratedBindings.g.cs",
                SearchOption.AllDirectories).SingleOrDefault();
            if (generatedFile == null)
                throw new InvalidOperationException("The binding generator did not produce its expected source file.");
            var output = Path.GetFullPath(Path.Combine(root, outputAssetPath));
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.Copy(generatedFile, output, true);
            AssetDatabase.ImportAsset(outputAssetPath.Replace('\\', '/'), ImportAssetOptions.ForceUpdate);
            UnityEngine.Debug.Log("Generated " + requests.Count + " Lunil AOT binding request(s) at " + outputAssetPath + ".");
        }

        private static List<LuaClrGenerateBindingAttribute> CollectRequests()
        {
            var requests = new List<LuaClrGenerateBindingAttribute>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    requests.AddRange(assembly.GetCustomAttributes<LuaClrGenerateBindingAttribute>());
                }
                catch (ReflectionTypeLoadException)
                {
                }
            }
            return requests
                .OrderBy(item => item.Type.AssemblyQualifiedName, StringComparer.Ordinal)
                .ThenBy(item => string.Join("\0", item.MemberNames), StringComparer.Ordinal)
                .ToList();
        }

        private static string CreateRequestsSource(IEnumerable<LuaClrGenerateBindingAttribute> requests)
        {
            var source = new StringBuilder();
            source.AppendLine("// <auto-generated />");
            foreach (var request in requests)
            {
                source.Append("[assembly: global::Lunil.Hosting.LuaClrGenerateBinding(typeof(")
                    .Append(GetTypeExpression(request.Type)).Append(')');
                foreach (var member in request.MemberNames)
                    source.Append(", \"").Append(Escape(member)).Append('"');
                source.AppendLine(")] ");
            }
            return source.ToString();
        }

        private static string CreateProjectSource(string root, string generator)
        {
            var references = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var scriptAssemblies = Path.Combine(root, "Library", "ScriptAssemblies");
            if (Directory.Exists(scriptAssemblies))
                foreach (var path in Directory.GetFiles(scriptAssemblies, "*.dll")) references.Add(path);
            var lunilDirectory = Path.GetDirectoryName(typeof(LuaClrBindingRegistry).Assembly.Location);
            foreach (var path in Directory.GetFiles(lunilDirectory, "*.dll")) references.Add(path);
            var managed = Path.Combine(EditorApplication.applicationContentsPath, "Managed");
            foreach (var path in Directory.GetFiles(managed, "UnityEngine.dll")) references.Add(path);
            foreach (var path in Directory.GetFiles(managed, "UnityEditor.dll")) references.Add(path);
            var modules = Path.Combine(managed, "UnityEngine");
            if (Directory.Exists(modules))
                foreach (var path in Directory.GetFiles(modules, "UnityEngine.*.dll")) references.Add(path);

            var project = new StringBuilder();
            project.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">")
                .AppendLine("  <PropertyGroup>")
                .AppendLine("    <TargetFramework>netstandard2.1</TargetFramework>")
                .AppendLine("    <LangVersion>9.0</LangVersion>")
                .AppendLine("    <Nullable>enable</Nullable>")
                .AppendLine("    <ImplicitUsings>disable</ImplicitUsings>")
                .AppendLine("    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>")
                .AppendLine("    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>")
                .AppendLine("    <EnableNETAnalyzers>false</EnableNETAnalyzers>")
                .AppendLine("    <AnalysisLevel>none</AnalysisLevel>")
                .AppendLine("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>")
                .AppendLine("  </PropertyGroup>")
                .AppendLine("  <ItemGroup>")
                .AppendLine("    <Compile Include=\"BindingRequests.cs\" />")
                .Append("    <Analyzer Include=\"").Append(Xml(generator)).AppendLine("\" />");
            var index = 0;
            foreach (var reference in references)
            {
                project.Append("    <Reference Include=\"UnityReference").Append(index++)
                    .Append("\"><HintPath>").Append(Xml(reference))
                    .AppendLine("</HintPath><Private>false</Private></Reference>");
            }
            project.AppendLine("  </ItemGroup>").AppendLine("</Project>");
            return project.ToString();
        }

        private static string GetTypeExpression(Type type)
        {
            if (type.IsArray) return GetTypeExpression(type.GetElementType()) + "[]";
            if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                var name = (definition.FullName ?? definition.Name).Replace('+', '.');
                name = name.Substring(0, name.IndexOf('`'));
                return "global::" + name + "<" +
                    string.Join(", ", type.GetGenericArguments().Select(GetTypeExpression)) + ">";
            }
            return "global::" + (type.FullName ?? type.Name).Replace('+', '.');
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string Xml(string value)
        {
            return value.Replace("&", "&amp;").Replace("\"", "&quot;")
                .Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private static string GetArgument(string name)
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
                if (string.Equals(arguments[index], name, StringComparison.Ordinal))
                    return arguments[index + 1];
            return null;
        }
    }
}
