using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml;

namespace RentalWarehouse.Tools
{
    /// <summary>
    /// Mirrors RimWorld's DirectXmlToObject rules (Verse.XmlToObjectUtils.DoFieldSearch, GenTypes.GetTypeInAnyAssembly,
    /// list and Class handling) over metadata only, and reports what the game would log as XML errors at load time.
    /// </summary>
    public sealed class DefValidator : IDisposable
    {
        /// <summary>Namespaces the game searches when a type name has no namespace (GenTypes.IgnoredNamespaceNames).</summary>
        private static readonly string[] IgnoredNamespaces =
        {
            "RimWorld", "Verse", "LudeonTK", "Verse.AI", "Verse.AI.Group", "Verse.Sound", "Verse.Grammar", "RimWorld.Planet",
            "RimWorld.BaseGen", "RimWorld.QuestGen", "RimWorld.SketchGen", "System",
        };

        /// <summary>
        /// Fields the loader accepts but that the game rebuilds at runtime, so XML values are silently lost. Found in the
        /// 1.7 sources: ThingFilter.ResolveReferences repopulates disallowedSpecialFilters from specialFiltersToDisallow.
        /// </summary>
        private static readonly Dictionary<(string Type, string Field), string> RuntimeOnlyFields = new()
        {
            [("RimWorld.ThingFilter", "disallowedSpecialFilters")] = "is rebuilt at load; use <specialFiltersToDisallow>",
            [("Verse.ThingFilter", "disallowedSpecialFilters")] = "is rebuilt at load; use <specialFiltersToDisallow>",
        };

        /// <summary>Types with a custom XML loader whose child element names are def names.</summary>
        private static readonly Dictionary<string, string> CustomLoaderDefChildren = new()
        {
            ["ThingDefCountClass"] = "ThingDef",
            ["StatModifier"] = "StatDef",
            ["ThingDefCountRangeClass"] = "ThingDef",
            ["SkillRequirement"] = "SkillDef",
        };

        private readonly MetadataLoadContext context;
        private readonly List<Assembly> assemblies = new();
        private readonly Dictionary<string, Type?> typeCache = new();
        private readonly Type? defType;
        private readonly List<string> issues = new();
        private readonly List<(string File, string Where, Type Target, string DefName)> references = new();

        public DefValidator(IEnumerable<string> assemblyPaths)
        {
            var paths = assemblyPaths.Select(Path.GetFullPath).ToList();
            var resolverPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths)
            {
                foreach (var dll in Directory.GetFiles(Path.GetDirectoryName(p)!, "*.dll")) resolverPaths.Add(dll);
            }
            var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
            var runtimeNames = new HashSet<string>(resolverPaths.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
            foreach (var dll in Directory.GetFiles(runtimeDir, "*.dll"))
            {
                if (runtimeNames.Add(Path.GetFileName(dll))) resolverPaths.Add(dll);
            }
            context = new MetadataLoadContext(new PathAssemblyResolver(resolverPaths), "System.Private.CoreLib");
            foreach (var p in paths) assemblies.Add(context.LoadFromAssemblyPath(p));
            defType = FindType("Verse.Def");
        }

        public void Dispose() => context.Dispose();

        public List<string> Validate(IEnumerable<string> defFiles, IEnumerable<string>? referenceDefFiles = null)
        {
            issues.Clear();
            references.Clear();
            if (defType == null)
            {
                issues.Add("type Verse.Def not found in the given assemblies");
                return new List<string>(issues);
            }

            var declared = new List<(string DefName, Type Type)>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var parents = new List<(string File, string Where, string Parent)>();
            foreach (var file in defFiles) ValidateFile(file, declared, names, parents, report: true);
            if (referenceDefFiles != null)
            {
                foreach (var file in referenceDefFiles) ValidateFile(file, declared, names, parents, report: false);
                foreach (var (file, where, parent) in parents)
                {
                    if (!names.Contains(parent)) issues.Add($"{file}: {where}: ParentName \"{parent}\" does not exist");
                }
                foreach (var (file, where, target, defName) in references)
                {
                    bool found = declared.Any(d => d.DefName == defName && target.IsAssignableFrom(d.Type))
                        || ImpliedPrefixes.Any(p => defName.StartsWith(p, StringComparison.Ordinal));
                    if (!found) issues.Add($"{file}: {where}: no {target.Name} named \"{defName}\"");
                }
            }
            return new List<string>(issues);
        }

        private void ValidateFile(string file, List<(string, Type)> declared, HashSet<string> names, List<(string, string, string)> parents, bool report)
        {
            var doc = new XmlDocument();
            try
            {
                doc.Load(file);
            }
            catch (XmlException e)
            {
                issues.Add($"{file}: not well-formed XML: {e.Message}");
                return;
            }
            string shortName = Path.GetFileName(file);
            foreach (XmlNode node in doc.DocumentElement!.ChildNodes)
            {
                if (node is not XmlElement element) continue;
                var type = FindType(element.Name);
                string defName = element["defName"]?.InnerText ?? element.GetAttribute("Name");
                string where = element.Name + "(" + defName + ")";
                if (type == null || !defType!.IsAssignableFrom(type))
                {
                    if (report) issues.Add($"{shortName}: {where}: \"{element.Name}\" is not a Def type");
                    continue;
                }
                var classAttr = element.GetAttribute("Class");
                if (classAttr.Length > 0)
                {
                    var cls = FindType(classAttr, type.Namespace);
                    if (cls == null || !type.IsAssignableFrom(cls))
                    {
                        if (report) issues.Add($"{shortName}: {where}: Class \"{classAttr}\" is not a {type.Name}");
                    }
                    else
                    {
                        type = cls;
                    }
                }
                if (element.HasAttribute("Name")) names.Add(element.GetAttribute("Name"));
                if (element.HasAttribute("ParentName") && report) parents.Add((shortName, where, element.GetAttribute("ParentName")));
                bool isAbstract = element.GetAttribute("Abstract").Equals("true", StringComparison.OrdinalIgnoreCase);
                if (!isAbstract && element["defName"] != null) declared.Add((element["defName"]!.InnerText.Trim(), type));
                if (!report) continue;
                int before = references.Count;
                ValidateComplex(element, type, shortName, where);
                // Abstract defs are never instantiated, so the game never resolves their references (often DLC-only).
                if (isAbstract) references.RemoveRange(before, references.Count - before);
            }
        }

        private void ValidateNode(XmlElement node, Type type, string file, string where)
        {
            type = UnwrapNullable(type);
            if (type.Name.StartsWith("SlateRef", StringComparison.Ordinal)) return; // quest slate expressions, parsed from text by the game
            if (IsDef(type))
            {
                AddReference(node, type, file, where);
                return;
            }
            if (HasCustomLoader(type))
            {
                if (CustomLoaderDefChildren.TryGetValue(type.Name, out var defTypeName))
                {
                    var target = FindType(defTypeName);
                    // <Steel>80</Steel> form: the element name is the def. The <li><thingDef>…</thingDef></li> form is left to the game.
                    if (target != null && node.Name != "li" && !Optional(node)) references.Add((file, where, target, node.Name));
                }
                return;
            }
            var elements = node.ChildNodes.OfType<XmlElement>().ToList();
            if (type.IsEnum)
            {
                if (elements.Count > 0 && IsFlags(type))
                {
                    foreach (var li in elements) CheckEnum(li.InnerText.Trim(), type, file, where);
                }
                else
                {
                    CheckEnum(node.InnerText.Trim(), type, file, where);
                }
                return;
            }
            if (type.FullName == "System.Type")
            {
                string name = node.InnerText.Trim();
                if (name.Length > 0 && FindType(name) == null) issues.Add($"{file}: {where}: type \"{name}\" not found");
                return;
            }
            if (IsList(type, out var itemType))
            {
                foreach (var li in elements)
                {
                    if (li.Name != "li" && !HasCustomLoader(itemType))
                    {
                        issues.Add($"{file}: {where}: list item <{li.Name}> must be <li>");
                        continue;
                    }
                    ValidateNode(li, ResolveClass(li, itemType, file, where), file, where + "/li");
                }
                return;
            }
            if (IsDictionary(type) || type.IsPrimitive || type.FullName == "System.String" || type.FullName == "System.Decimal") return;
            if (elements.Count == 0) return; // parsed from text by ParseHelper (IntVec2, Color, FloatRange…)
            ValidateComplex(node, ResolveClass(node, type, file, where), file, where);
        }

        private void ValidateComplex(XmlElement node, Type type, string file, string where)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var child in node.ChildNodes.OfType<XmlElement>())
            {
                string path = where + "/" + child.Name;
                if (!seen.Add(child.Name)) issues.Add($"{file}: {path}: field defined twice");
                var field = FindField(type, child.Name, out bool caseMismatch);
                if (field == null)
                {
                    if (!child.GetAttribute("IgnoreIfNoMatchingField").Equals("true", StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add($"{file}: {path}: <{child.Name}> doesn't correspond to any field in {type.Name}");
                    }
                    continue;
                }
                if (caseMismatch) issues.Add($"{file}: {path}: <{child.Name}> matches field {field.Name} only by ignoring case; tags are case-sensitive");
                if (field.DeclaringType != null && RuntimeOnlyFields.TryGetValue((field.DeclaringType.FullName!, field.Name), out var why))
                {
                    issues.Add($"{file}: {path}: {field.DeclaringType.Name}.{field.Name} {why}");
                    continue;
                }
                ValidateNode(child, field.FieldType, file, path);
            }
        }

        private Type ResolveClass(XmlElement node, Type declared, string file, string where)
        {
            var cls = node.GetAttribute("Class");
            if (cls.Length == 0) return declared;
            var type = FindType(cls, declared.Namespace);
            if (type == null)
            {
                issues.Add($"{file}: {where}: Class \"{cls}\" not found");
                return declared;
            }
            if (!declared.IsAssignableFrom(type))
            {
                issues.Add($"{file}: {where}: Class \"{cls}\" is not a {declared.Name}");
                return declared;
            }
            return type;
        }

        private void AddReference(XmlElement node, Type target, string file, string where)
        {
            string name = node.InnerText.Trim();
            if (name.Length == 0 || Optional(node)) return;
            references.Add((file, where, target, name));
        }

        /// <summary>A MayRequire on the node or any ancestor makes the reference conditional on another mod or DLC.</summary>
        private static bool Optional(XmlNode node)
        {
            for (var n = node; n is XmlElement e; n = n.ParentNode)
            {
                if (e.HasAttribute("MayRequire") || e.HasAttribute("MayRequireAnyOf")) return true;
            }
            return false;
        }

        /// <summary>Defs the game generates at load time (ThingDefGenerator_*), which no XML file declares.</summary>
        private static readonly string[] ImpliedPrefixes = { "Meat_", "Corpse_", "Blueprint_", "Frame_", "Install_", "Leather_", "Wool_" };

        private void CheckEnum(string value, Type type, string file, string where)
        {
            if (value.Length == 0) return;
            var names = type.GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!names.Contains(part) && !int.TryParse(part, out _)) issues.Add($"{file}: {where}: \"{part}\" is not a value of {type.Name}");
            }
        }

        private static FieldInfo? FindField(Type type, string name, out bool caseMismatch)
        {
            caseMismatch = false;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var t = type; t != null && t.FullName != "System.Object"; t = t.BaseType)
            {
                var f = t.GetField(name, flags);
                if (f != null) return f;
            }
            for (var t = type; t != null && t.FullName != "System.Object"; t = t.BaseType)
            {
                var f = t.GetField(name, flags | BindingFlags.IgnoreCase);
                if (f != null)
                {
                    caseMismatch = true;
                    return f;
                }
            }
            for (var t = type; t != null && t.FullName != "System.Object"; t = t.BaseType)
            {
                foreach (var f in t.GetFields(flags))
                {
                    foreach (var attr in f.CustomAttributes)
                    {
                        if (attr.AttributeType.Name != "LoadAliasAttribute" || attr.ConstructorArguments.Count == 0) continue;
                        if (string.Equals(attr.ConstructorArguments[0].Value as string, name, StringComparison.OrdinalIgnoreCase)) return f;
                    }
                }
            }
            return null;
        }

        private Type? FindType(string name, string? namespaceHint = null)
        {
            string key = name + "|" + namespaceHint;
            if (typeCache.TryGetValue(key, out var cached)) return cached;
            var candidates = new List<string> { name };
            if (namespaceHint != null) candidates.Add(namespaceHint + "." + name);
            candidates.AddRange(IgnoredNamespaces.Select(ns => ns + "." + name));
            Type? found = null;
            foreach (var full in candidates)
            {
                foreach (var a in assemblies)
                {
                    found = SafeGetType(a, full);
                    if (found != null) break;
                }
                // GenTypes looks types up with ignoreCase: true, which MetadataLoadContext cannot do itself.
                if (found == null) TypesIgnoringCase().TryGetValue(full, out found);
                if (found != null) break;
            }
            typeCache[key] = found;
            return found;
        }

        private Dictionary<string, Type>? typesIgnoringCase;

        private Dictionary<string, Type> TypesIgnoringCase()
        {
            if (typesIgnoringCase != null) return typesIgnoringCase;
            typesIgnoringCase = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in assemblies)
            {
                Type?[] types;
                try
                {
                    types = a.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types;
                }
                foreach (var t in types)
                {
                    if (t?.FullName != null) typesIgnoringCase.TryAdd(t.FullName, t);
                }
            }
            return typesIgnoringCase;
        }

        private static Type? SafeGetType(Assembly a, string fullName)
        {
            try
            {
                return a.GetType(fullName, throwOnError: false);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        /// <summary>Nullable.GetUnderlyingType compares against the runtime's Nullable, which a metadata type never equals.</summary>
        private static Type UnwrapNullable(Type type) =>
            type.IsGenericType && type.GetGenericTypeDefinition().FullName == "System.Nullable`1" ? type.GetGenericArguments()[0] : type;

        private bool IsDef(Type type) => defType != null && defType.IsAssignableFrom(type);

        private static bool HasCustomLoader(Type type) =>
            type.GetMethod("LoadDataFromXmlCustom", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null;

        private static bool IsFlags(Type type) => type.CustomAttributes.Any(a => a.AttributeType.FullName == "System.FlagsAttribute");

        private static bool IsList(Type type, out Type item)
        {
            item = type;
            if (!type.IsGenericType || type.GetGenericTypeDefinition().FullName != "System.Collections.Generic.List`1") return false;
            item = type.GetGenericArguments()[0];
            return true;
        }

        private static bool IsDictionary(Type type) =>
            type.IsGenericType && type.GetGenericTypeDefinition().FullName == "System.Collections.Generic.Dictionary`2";
    }
}
