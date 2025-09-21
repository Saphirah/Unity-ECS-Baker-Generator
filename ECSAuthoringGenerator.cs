using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

public static class ECSAuthoringGenerator
{
    const string OutputDir = "Assets/Generated/Authoring";

    [DidReloadScripts]
    static void OnReload() => EditorApplication.delayCall += GenerateAll;

    [MenuItem("Tools/ECS/Generate Authoring Now")]
    public static void GenerateAll()
    {
        Directory.CreateDirectory(OutputDir);

        var targets = GetMarkedIComponentDataTypes();
        var sig = ComputeSignature(targets);

        var generatedNow = new HashSet<string>(StringComparer.Ordinal);

        foreach (var t in targets)
        {
            var code = BuildAuthoringSource(t);
            var path = Path.Combine(OutputDir, t.Name + "Authoring.cs").Replace('\\', '/');

            if (!File.Exists(path) || File.ReadAllText(path) != code)
                File.WriteAllText(path, code, new UTF8Encoding(false));

            generatedNow.Add(path);
            Debug.Log($"[ECSAuthoringGenerator] Generated authoring for {t.Name}");
        }

        // Find all .cs files in OutputDir
        var allFiles = Directory.GetFiles(OutputDir, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(f => f.Replace('\\', '/')).ToArray();

        // Delete stale files
        int cleaned = 0;
        foreach (var old in allFiles)
        {
            if (generatedNow.Contains(old)) continue;
            File.Delete(old);
            cleaned++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[ECSAuthoringGenerator] Generated {generatedNow.Count} file(s). Cleaned {cleaned} stale file(s).");
    }

    // ----- helpers -----

    static Type[] GetMarkedIComponentDataTypes()
    {
        var iComp = Type.GetType("Unity.Entities.IComponentData, Unity.Entities", false);
        if (iComp == null) return Array.Empty<Type>();
        var marker = typeof(GenerateAuthoringAttribute);

        bool IsProjectAsm(Assembly a)
        {
            var n = a.GetName().Name;
            if (n.StartsWith("UnityEngine")) return false;
            if (n.StartsWith("UnityEditor")) return false;
            if (n.StartsWith("System")) return false;
            if (n.StartsWith("mscorlib")) return false;
            if (n.StartsWith("netstandard")) return false;
            return true;
        }

        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(IsProjectAsm)
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
            })
            .Where(t => t != null && t.IsValueType && !t.IsPrimitive && iComp.IsAssignableFrom(t) && t.GetCustomAttributes(marker, false).Length > 0)
            .OrderBy(t => t.FullName)
            .ToArray();
    }

    static string ComputeSignature(Type[] types)
    {
        // Signature = set of component names + their serializable fields (name + type full name).
        var sb = new StringBuilder(4096);
        foreach (var t in types)
        {
            sb.AppendLine(t.FullName);
            foreach (var f in GetSerializableFields(t))
            {
                sb.Append(t.FullName).Append('|').Append(f.Name).Append('|').Append(f.FieldType.AssemblyQualifiedName).AppendLine();
            }
        }
        using var md5 = MD5.Create();
        var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        var hex = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        return hex;
    }

    static FieldInfo[] GetSerializableFields(Type t)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return t.GetFields(flags)
            .Where(f => !f.IsStatic && (f.IsPublic || f.GetCustomAttributes(typeof(SerializeField), false).Length > 0))
            .ToArray();
    }

    static string CsTypeName(Type t)
    {
        if (t.IsByRef) t = t.GetElementType();
        if (t.IsArray) return $"{CsTypeName(t.GetElementType())}[]";

        string Qualify(string full) => $"global::{full.Replace('+', '.')}";

        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            var name = def.FullName.Substring(0, def.FullName.IndexOf('`')).Replace('+', '.');
            var args = string.Join(", ", t.GetGenericArguments().Select(CsTypeName));
            return $"global::{name}<{args}>";
        }

        return Qualify(t.FullName);
    }

    static string BuildAuthoringSource(Type componentType)
    {
        var ns = componentType.Namespace;
        var compName = componentType.Name;
        var authoringName = compName + "Authoring";
        var bakerName = compName + "Baker";
        var fields = GetSerializableFields(componentType);

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated> Generated by ECSAuthoringGenerator. Do not edit. </auto-generated>");
        sb.AppendLine("using Unity.Entities;");
        sb.AppendLine("using UnityEngine;");
        sb.AppendLine("using Unity.Mathematics;");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(ns)) sb.AppendLine($"namespace {ns} {{");

        sb.AppendLine($"public sealed class {authoringName} : MonoBehaviour");
        sb.AppendLine("{");
        foreach (var f in fields)
        {
            var typeName = CsTypeName(f.FieldType);
            var name = f.Name;
            if (f.IsPublic) sb.AppendLine($"    public {typeName} {name};");
            else sb.AppendLine($"    [SerializeField] public {typeName} {name};");
        }
        sb.AppendLine();
        sb.AppendLine($"    private sealed class {bakerName} : Baker<{authoringName}>");
        sb.AppendLine("    {");
        sb.AppendLine($"        public override void Bake({authoringName} authoring)");
        sb.AppendLine("        {");
        sb.AppendLine("            var entity = GetEntity(TransformUsageFlags.Dynamic);");
        sb.AppendLine($"            AddComponent(entity, new {CsTypeName(componentType)}");
        sb.AppendLine("            {");
        for (int i = 0; i < fields.Length; i++)
        {
            var f = fields[i];
            var comma = i < fields.Length - 1 ? "," : "";
            sb.AppendLine($"                {f.Name} = authoring.{f.Name}{comma}");
        }
        sb.AppendLine("            });");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        if (!string.IsNullOrEmpty(ns)) sb.AppendLine("}");
        return sb.ToString();
    }
}
