using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

class ExternGen
{
    private static Dictionary<string, int> GENERIC_ARITY = new(StringComparer.Ordinal);
    private static Dictionary<string, List<int>> GENERIC_DEFS = new(StringComparer.Ordinal);

    public static string GenerateFromRuntime(string outRoot)
    {
        if (string.IsNullOrWhiteSpace(outRoot))
            throw new ArgumentException(nameof(outRoot));

        if (!Directory.Exists(outRoot))
            Directory.CreateDirectory(outRoot);

        _ = typeof(Sandbox.GameObject);

        EnsureSandboxAssembliesLoaded();
        var runtimeTypes = CollectSandboxTypes();

        // 1) build generic definitions index once
        GENERIC_DEFS = BuildGenericDefsIndex(runtimeTypes);

        // 2) runtime -> ApiType (schema-like)
        var apiTypes = runtimeTypes
            .Select(BuildApiType)
            .Where(t => !string.IsNullOrWhiteSpace(t.FullName))
            .ToList();

        GENERIC_ARITY = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in apiTypes)
        {
            var fn = Str(t.FullName);
            var sp = SplitGenericType(fn);
            if (sp.Args.Count > 0)
                GENERIC_ARITY[sp.Base] = sp.Args.Count;
        }

        int typeCount = 0;
        int memberCount = 0;

        // generation loop (same as your Haxe)
        foreach (var t in apiTypes)
        {
            var full = Str(t.FullName);

            if (full.Contains("$", StringComparison.Ordinal) ||
                full.Contains(".<", StringComparison.Ordinal) ||
                full.Contains("+<", StringComparison.Ordinal) ||
                full.Contains("\\u003C", StringComparison.Ordinal) ||
                full.Contains("\\u003E", StringComparison.Ordinal))
                continue;

            if (full == "") full = Str(t.Name);
            if (full == "") continue;

            var pkgInfo = MapPackageForOutRootSbox(full);
            var hxPackage = pkgInfo.Pkg;
            var relDir = pkgInfo.RelDir;

            var hxTypeName = SanitizeTypeName(SimpleName(full));
            var typeOutDir = (relDir == "") ? outRoot : Join(outRoot, relDir);
            EnsureDir(typeOutDir);

            var tgen = TypeGenericParams(full);
            var tgenPart = tgen.Count > 0 ? "<" + string.Join(",", tgen) + ">" : "";

            var sb = new StringBuilder();
            sb.Append("package ").Append(hxPackage).Append(";\n\n");
            sb.Append("@:native(\"").Append(Esc(full)).Append("\")\n");
            sb.Append("extern class ").Append(hxTypeName).Append(tgenPart).Append(" {\n");

            // ctors
            if (t.Constructors != null)
            {
                var uniq = new Dictionary<string, List<ApiParam>>(StringComparer.Ordinal);
                foreach (var c in t.Constructors)
                {
                    if (!c.IsPublic) continue;
                    var ps = c.Parameters ?? new List<ApiParam>();

                    var key = "new(" + string.Join(",", ps.Select(p => NormTypeForKey(MapType(p.Type)))) + ")";
                    if (!uniq.ContainsKey(key))
                        uniq[key] = ps;
                }

                var list = uniq.Select(kv => (Key: kv.Key, Ps: kv.Value))
                               .OrderBy(x => x.Key, StringComparer.Ordinal)
                               .ToList();

                bool allOver = list.Count > 1;
                foreach (var it in list)
                {
                    var ov = allOver ? "overload " : "";
                    sb.Append("  ").Append(ov)
                      .Append("function new(").Append(FmtPars(it.Ps))
                      .Append("):Void;\n");
                    memberCount++;
                }
            }

            // props
            if (t.Properties != null)
            {
                foreach (var p in t.Properties)
                {
                    if (!p.IsPublic) continue;
                    var name = Str(p.Name);
                    if (name == "") continue;

                    var pt = Str(p.PropertyType);
                    if (pt == "") pt = "System.Object";

                    bool hasGet = p.HasGet ?? true;
                    bool hasSet = p.HasSet ?? true;

                    var acc = (!hasGet && !hasSet) ? "default"
                            : (hasGet && hasSet) ? "get,set"
                            : (hasGet) ? "get,never"
                            : "never,set";

                    var summary = p.Documentation?.Summary ?? "";
                    if (!string.IsNullOrWhiteSpace(summary))
                        sb.Append("  /** ").Append(EscDoc(summary)).Append(" */\n");

                    sb.Append("  var ").Append(SanitizeMemberName(name))
                      .Append("(").Append(acc).Append("):")
                      .Append(MapType(pt)).Append(";\n");
                    memberCount++;
                }
            }

            // methods (same grouping/dedup logic)
            var methods = new List<ApiMethod>();
            if (t.Methods != null)
            {
                foreach (var m in t.Methods)
                {
                    var isPublic = m.IsPublic;
                    var isProtected = m.IsProtected;
                    if (!isPublic && !isProtected) continue;

                    var name = Str(m.Name);
                    if (name == "") continue;

                    var ret = Str(m.ReturnType);
                    if (ret == "") ret = "System.Void";

                    methods.Add(m);
                }
            }

            if (methods.Count > 0)
            {
                var byName = new Dictionary<string, List<ApiMethod>>(StringComparer.Ordinal);
                foreach (var m in methods)
                {
                    if (!byName.TryGetValue(m.Name, out var arr))
                    {
                        arr = new List<ApiMethod>();
                        byName[m.Name] = arr;
                    }
                    arr.Add(m);
                }

                var names = byName.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();

                foreach (var mn in names)
                {
                    var grp = byName[mn];

                    var unique = new Dictionary<string, ApiMethod>(StringComparer.Ordinal);
                    foreach (var mm in grp)
                    {
                        var gen = CollectGenericParams(mm);
                        var key = SignatureKey(mm, gen);
                        if (!unique.ContainsKey(key))
                            unique[key] = mm;
                    }

                    var uniqArr = unique.Values.ToList();
                    if (uniqArr.Count == 0) continue;

                    bool allOverloads = uniqArr.Count > 1;

                    uniqArr.Sort((a, b) =>
                    {
                        int pa = a.Parameters.Count;
                        int pb = b.Parameters.Count;
                        if (pa != pb) return pa.CompareTo(pb);

                        var ga = CollectGenericParams(a);
                        var gb = CollectGenericParams(b);
                        return string.Compare(SignatureKey(a, ga), SignatureKey(b, gb), StringComparison.Ordinal);
                    });

                    var summaryText = FirstSummary(uniqArr);
                    if (summaryText != "")
                        sb.Append("  /** ").Append(EscDoc(summaryText)).Append(" */\n");

                    foreach (var mm in uniqArr)
                    {
                        var gen = CollectGenericParams(mm);
                        WriteMethodLine(sb, mm, gen, allOverloads);
                        memberCount++;
                    }
                }
            }

            sb.Append("}\n");
            File.WriteAllText(Path.Combine(typeOutDir, hxTypeName + ".hx"), sb.ToString(), Encoding.UTF8);
            typeCount++;
        }

        return $"Done. Types: {typeCount}, Members: {memberCount}\n{outRoot}";
    }

    // ==========================================================
    // Runtime -> ApiType (the ONLY place we "change logic")
    // ==========================================================
    private static ApiType BuildApiType(Type t)
    {
        // schema-like FullName string (keeps `N for generic defs; keeps <...> for constructed)
        var fullName = TypeToSchemaName(t).Replace("+", ".", StringComparison.Ordinal);

        var at = new ApiType
        {
            FullName = fullName,
            Name = t.Name ?? "",
            Constructors = new List<ApiConstructor>(),
            Properties = new List<ApiProperty>(),
            Methods = new List<ApiMethod>()
        };

        // ctors (public only)
        foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            at.Constructors.Add(new ApiConstructor
            {
                IsPublic = true,
                Parameters = c.GetParameters().Select(p => new ApiParam
                {
                    Name = string.IsNullOrWhiteSpace(p.Name) ? "arg" : p.Name!,
                    Type = TypeToSchemaName(p.ParameterType).Replace("+", ".", StringComparison.Ordinal)
                }).ToList()
            });
        }

        // props (public accessors)
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        foreach (var p in props)
        {
            var get = p.GetGetMethod(true);
            var set = p.GetSetMethod(true);
            bool anyPublic = (get != null && get.IsPublic) || (set != null && set.IsPublic);
            if (!anyPublic) continue;

            at.Properties.Add(new ApiProperty
            {
                IsPublic = true,
                Name = p.Name ?? "",
                PropertyType = TypeToSchemaName(p.PropertyType).Replace("+", ".", StringComparison.Ordinal),
                HasGet = get != null,
                HasSet = set != null,
                Documentation = null
            });
        }

        // methods
        var ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        foreach (var m in ms)
        {
            if (m.IsSpecialName) continue;
            if (m.DeclaringType != t) continue;

            bool isPublic = m.IsPublic;
            bool isProtected = m.IsFamily || m.IsFamilyOrAssembly;
            if (!isPublic && !isProtected) continue;

            // method generic parameter names (T, TKey, ...)
            var mg = m.IsGenericMethodDefinition
                ? m.GetGenericArguments().Select(a => a.Name).ToList()
                : new List<string>();

            // return type + params in "schema-like strings"
            var ret = TypeToSchemaName(m.ReturnType).Replace("+", ".", StringComparison.Ordinal);
            ret = UpgradeNonGenericToKnownGeneric(ret, mg);

            var pars = m.GetParameters().Select(p =>
            {
                var pt = TypeToSchemaName(p.ParameterType).Replace("+", ".", StringComparison.Ordinal);
                pt = UpgradeNonGenericToKnownGeneric(pt, mg);

                return new ApiParam
                {
                    Name = string.IsNullOrWhiteSpace(p.Name) ? "arg" : p.Name!,
                    Type = pt
                };
            }).ToList();

            at.Methods.Add(new ApiMethod
            {
                Name = m.Name ?? "",
                ReturnType = ret,
                IsStatic = m.IsStatic,
                IsPublic = isPublic,
                IsProtected = isProtected,
                Parameters = pars,
                Documentation = null
            });
        }

        return at;
    }

    // Generic "upgrade" rule:
    // If a type string is non-generic BUT there exists a generic definition with same base name,
    // rewrite it to Base`N<methodGenericArgs...> (or Dynamic) so collectGenericParams/mapType behave like schema.
    private static string UpgradeNonGenericToKnownGeneric(string typeStr, List<string> methodGen)
    {
        typeStr = (typeStr ?? "").Trim();
        if (typeStr == "") return typeStr;

        // array: upgrade element
        if (typeStr.EndsWith("[]", StringComparison.Ordinal))
        {
            var inner = typeStr.Substring(0, typeStr.Length - 2);
            inner = UpgradeNonGenericToKnownGeneric(inner, methodGen);
            return inner + "[]";
        }

        // already has <...> or `N -> leave
        if (typeStr.IndexOf('<') >= 0 || typeStr.IndexOf('`') >= 0)
            return typeStr;

        // base name key: full name without generic args/backticks (here none), already normalized '.' not '+'
        if (!GENERIC_DEFS.TryGetValue(typeStr, out var arities) || arities.Count == 0)
            return typeStr;

        int methodN = methodGen.Count;

        // choose arity: prefer exact match to method generic count, else smallest
        int n = arities.Contains(methodN) && methodN > 0 ? methodN : arities.Min();

        var args = new List<string>(n);
        for (int i = 0; i < n; i++)
        {
            if (i < methodGen.Count) args.Add(methodGen[i]);
            else args.Add("Dynamic");
        }

        return typeStr + "`" + n + "<" + string.Join(",", args) + ">";
    }

    private static Dictionary<string, List<int>> BuildGenericDefsIndex(List<Type> types)
    {
        var map = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        foreach (var t in types)
        {
            if (!t.IsGenericTypeDefinition) continue;

            var fn = (t.FullName ?? "").Replace("+", ".", StringComparison.Ordinal);
            if (fn == "") continue;

            int tick = fn.IndexOf('`');
            if (tick < 0) continue;

            var baseName = fn.Substring(0, tick);
            int n = t.GetGenericArguments().Length;
            if (n <= 0) continue;

            if (!map.TryGetValue(baseName, out var arr))
            {
                arr = new List<int>();
                map[baseName] = arr;
            }
            if (!arr.Contains(n)) arr.Add(n);
        }

        return map;
    }

    // ==========================================================
    // Type string builder (schema-like)
    // ==========================================================
    private static string TypeToSchemaName(Type t)
    {
        if (t == null) return "System.Object";

        if (t.IsArray)
            return TypeToSchemaName(t.GetElementType()!) + "[]";

        if (t.IsGenericParameter)
            return t.Name;

        if (t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Nullable`1")
        {
            var inner = t.GetGenericArguments()[0];
            return "System.Nullable`1<" + TypeToSchemaName(inner) + ">";
        }

        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            var defName = def.FullName ?? def.Name; // includes `N
            var args = t.GetGenericArguments().Select(TypeToSchemaName);
            return defName + "<" + string.Join(",", args) + ">";
        }

        return t.FullName ?? t.Name ?? "System.Object";
    }

    // ==========================================================
    // Haxe-generator logic (ported 1:1)
    // ==========================================================
    private static string NormTypeForKey(string t)
    {
        t = (t ?? "").Trim();
        if (t.StartsWith("Null<", StringComparison.Ordinal) && t.EndsWith(">", StringComparison.Ordinal))
            return t.Substring(5, t.Length - 6).Trim();
        return t;
    }

    private static void WriteMethodLine(StringBuilder sb, ApiMethod m, List<string> gen, bool asOverload)
    {
        if (m.IsProtected)
            sb.Append("  @:protected\n");

        var st = m.IsStatic ? "static " : "";
        var ov = asOverload ? "overload " : "";
        var genPart = gen.Count > 0 ? "<" + string.Join(",", gen) + ">" : "";

        sb.Append("  ").Append(st).Append(ov)
          .Append("function ").Append(SanitizeMemberName(m.Name)).Append(genPart)
          .Append("(").Append(FmtPars(m.Parameters))
          .Append("):").Append(MapType(m.ReturnType)).Append(";\n");
    }

    private static string SignatureKey(ApiMethod m, List<string> gen)
    {
        var ret = MapType(m.ReturnType);
        var pars = string.Join(",", m.Parameters.Select(p => NormTypeForKey(MapType(p.Type))));
        var gp = string.Join(",", gen);
        return (m.IsStatic ? "S" : "I") + (m.IsProtected ? "P" : "U") + "|" + m.Name + "|<" + gp + ">|" + ret + "|(" + pars + ")";
    }

    private static string FirstSummary(List<ApiMethod> arr)
    {
        foreach (var m in arr)
        {
            var s = m.Documentation?.Summary ?? "";
            if (!string.IsNullOrWhiteSpace(s))
                return s.Trim();
        }
        return "";
    }

    private static List<string> CollectGenericParams(ApiMethod m)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        AddGenericParamsFromType(set, m.ReturnType);
        foreach (var p in m.Parameters)
            AddGenericParamsFromType(set, p.Type);
        var outList = set.ToList();
        outList.Sort(StringComparer.Ordinal);
        return outList;
    }

    private static void AddGenericParamsFromType(HashSet<string> set, string typeStr)
    {
        foreach (var tok in ExtractTypeTokens(typeStr))
            if (IsGenericParamName(tok))
                set.Add(tok);
    }

    private static List<string> TypeGenericParams(string nativeType)
    {
        int lt = nativeType.IndexOf("<", StringComparison.Ordinal);
        int gt = nativeType.LastIndexOf(">", StringComparison.Ordinal);
        if (lt < 0 || gt <= lt) return new List<string>();
        var inner = nativeType.Substring(lt + 1, gt - lt - 1).Trim();
        if (inner == "") return new List<string>();
        return inner.Split(',').Select(x => x.Trim()).Where(x => x != "").ToList();
    }

    private static List<string> ExtractTypeTokens(string s)
    {
        s = (s ?? "").Trim();
        if (s == "") return new List<string>();

        if (s.EndsWith("[]", StringComparison.Ordinal))
            return new List<string> { StripNamespaceAndTick(s.Substring(0, s.Length - 2)) };

        int lt = s.IndexOf("<", StringComparison.Ordinal);
        if (lt < 0)
            return new List<string> { StripNamespaceAndTick(s) };

        var outList = new List<string>();
        outList.Add(StripNamespaceAndTick(s.Substring(0, lt)));

        foreach (var a in GenericArgs(s))
            outList.AddRange(ExtractTypeTokens(a));

        return outList;
    }

    private static string StripNamespaceAndTick(string s)
    {
        s = (s ?? "").Trim();
        int dot = s.LastIndexOf(".", StringComparison.Ordinal);
        if (dot >= 0) s = s.Substring(dot + 1);

        int tick = s.IndexOf("`", StringComparison.Ordinal);
        if (tick >= 0) s = s.Substring(0, tick);

        return s;
    }

    private static bool IsGenericParamName(string s)
    {
        if (s == null) return false;
        s = s.Trim();

        if (s.Length == 1)
        {
            char c = s[0];
            return c >= 'A' && c <= 'Z';
        }

        if (s == "T") return true;

        if (s.Length >= 2 && s[0] == 'T')
        {
            for (int i = 1; i < s.Length; i++)
            {
                char c = s[i];
                bool isAZ = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
                bool is09 = (c >= '0' && c <= '9');
                if (!isAZ && !is09) return false;
            }
            return true;
        }

        return false;
    }

    private readonly struct GenericSplit
    {
        public readonly string Base;
        public readonly List<string> Args;
        public GenericSplit(string @base, List<string> args) { Base = @base; Args = args; }
    }

    private static GenericSplit SplitGenericType(string s)
    {
        s = (s ?? "").Trim();
        int lt = s.IndexOf("<", StringComparison.Ordinal);
        int gt = s.LastIndexOf(">", StringComparison.Ordinal);
        if (lt < 0 || gt <= lt)
            return new GenericSplit(s, new List<string>());

        var @base = s.Substring(0, lt).Trim();
        var args = GenericArgs(s);
        return new GenericSplit(@base, args);
    }

    private static List<string> GenericArgs(string s)
    {
        int lt = s.IndexOf("<", StringComparison.Ordinal);
        int gt = s.LastIndexOf(">", StringComparison.Ordinal);
        if (lt < 0 || gt <= lt) return new List<string>();

        var inner = s.Substring(lt + 1, gt - lt - 1);

        var outList = new List<string>();
        var buf = new StringBuilder();
        int depth = 0;

        for (int i = 0; i < inner.Length; i++)
        {
            char ch = inner[i];
            if (ch == '<') { depth++; buf.Append(ch); continue; }
            if (ch == '>') { depth--; buf.Append(ch); continue; }
            if (ch == ',' && depth == 0)
            {
                outList.Add(buf.ToString().Trim());
                buf.Clear();
                continue;
            }
            buf.Append(ch);
        }

        var last = buf.ToString().Trim();
        if (last != "")
            outList.Add(last);

        return outList;
    }

    private static string MapType(string cs)
    {
        cs = (cs ?? "").Trim();

        switch (cs)
        {
            case "":
                return "Dynamic";
            case "Dynamic":
            case "Void":
            case "Int":
            case "UInt":
            case "Float":
            case "Bool":
            case "String":
                return cs;
        }

        var sp = SplitGenericType(cs);
        var @base = sp.Base;
        var args = sp.Args;

        if (args.Count == 0 && GENERIC_ARITY.TryGetValue(@base, out var n))
        {
            args = new List<string>();
            for (int i = 0; i < n; i++)
                args.Add("Dynamic");
        }

        if (IsGenericParamName(@base))
            return @base;

        var sr = SimpleName(@base);
        if (IsGenericParamName(sr))
            return sr;

        if (@base.EndsWith("[]", StringComparison.Ordinal))
        {
            var inner = @base.Substring(0, @base.Length - 2);
            return "Array<" + MapType(inner) + ">";
        }

        if (cs.StartsWith("System.Nullable`1<", StringComparison.Ordinal) && cs.EndsWith(">", StringComparison.Ordinal))
        {
            var inner = (args.Count > 0) ? args[0] : "System.Object";
            return "Null<" + MapType(inner) + ">";
        }

        switch (@base)
        {
            case "System.Void": return "Void";
            case "System.Boolean": return "Bool";
            case "System.String": return "String";
            case "System.Single":
            case "System.Double":
            case "System.Decimal":
                return "Float";
            case "System.Int16":
            case "System.UInt16":
            case "System.Int32":
            case "System.Byte":
            case "System.SByte":
                return "Int";
            case "System.UInt32":
                return "UInt";
            case "System.Int64":
                return "haxe.Int64";
            case "System.Object":
                return "Dynamic";
        }

        var mappedBase = MapNonSystem(@base);

        if (args.Count > 0)
        {
            mappedBase = MapNonSystem(@base);
            if (mappedBase == "Dynamic") return "Dynamic";

            var mappedArgs = string.Join(",", args.Select(MapType));
            return mappedBase + "<" + mappedArgs + ">";
        }

        return mappedBase;
    }

    private static string MapNonSystem(string cs)
    {
        var simple = SanitizeTypeName(SimpleName(cs));

        if (!cs.Contains(".", StringComparison.Ordinal))
            return "sbox." + simple;

        if (cs.StartsWith("Sandbox.", StringComparison.Ordinal) || cs == "Sandbox")
        {
            int lastDot = cs.LastIndexOf(".", StringComparison.Ordinal);
            var ns = (lastDot >= 0) ? cs.Substring(0, lastDot) : "";

            if (ns == "Sandbox")
                return "sbox." + simple;

            var rest = ns.Substring("Sandbox.".Length).ToLowerInvariant();
            return "sbox." + rest + "." + simple;
        }

        return "Dynamic";
    }

    private static string FmtPars(List<ApiParam> pars)
    {
        if (pars.Count == 0) return "";

        var used = new HashSet<string>(StringComparer.Ordinal);
        var outList = new List<string>();

        for (int i = 0; i < pars.Count; i++)
        {
            var raw = pars[i].Name;
            var n = SanitizeMemberName(string.IsNullOrWhiteSpace(raw) ? ("arg" + i) : raw.Trim());

            var baseName = n;
            int k = 2;
            while (used.Contains(n))
            {
                n = baseName + k;
                k++;
            }

            used.Add(n);
            outList.Add(n + ":" + MapType(pars[i].Type));
        }

        return string.Join(", ", outList);
    }

    private static string SanitizeTypeName(string name)
    {
        int lt = name.IndexOf("<", StringComparison.Ordinal);
        if (lt >= 0) name = name.Substring(0, lt);

        int tick = name.IndexOf("`", StringComparison.Ordinal);
        if (tick >= 0) name = name.Substring(0, tick);

        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool isAZ = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
            bool is09 = (c >= '0' && c <= '9');
            if (isAZ || is09 || c == '_')
                sb.Append(c);
        }

        var s = sb.ToString();
        if (s == "") s = "Type";

        if (s.Length > 0 && char.IsDigit(s[0]))
            s = "_" + s;

        return s;
    }

    private static string SanitizeMemberName(string name)
    {
        return name switch
        {
            "new" => "_new",
            "switch" => "_switch",
            "function" => "_function",
            "default" => "_default",
            "var" => "_var",
            _ => name
        };
    }

    private static string SimpleName(string full)
    {
        int dot = full.LastIndexOf(".", StringComparison.Ordinal);
        return dot >= 0 ? full.Substring(dot + 1) : full;
    }

    private static string Esc(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string EscDoc(string s)
    {
        s = (s ?? "");
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Replace("*/", "* /");
    }

    private static string Join(string a, string b)
    {
        a = Norm(a);
        b = Norm(b);

        if (a.EndsWith("/", StringComparison.Ordinal))
            a = a.Substring(0, a.Length - 1);

        if (b.StartsWith("/", StringComparison.Ordinal))
            b = b.Substring(1);

        return a + "/" + b;
    }

    private static string Norm(string p) => (p ?? "").Replace("\\", "/");

    private static void EnsureDir(string p) => Directory.CreateDirectory(p);

    private static string Str(string v) => v ?? "";

    private static (string Pkg, string RelDir) MapPackageForOutRootSbox(string nativeFull)
    {
        string ns = "";
        int dot = nativeFull.LastIndexOf(".", StringComparison.Ordinal);
        if (dot >= 0) ns = nativeFull.Substring(0, dot);

        if (ns == "Sandbox")
            return ("sbox", "");

        if (ns.StartsWith("Sandbox.", StringComparison.Ordinal))
        {
            var rest = ns.Substring("Sandbox.".Length).ToLowerInvariant();
            return ("sbox." + rest, rest.Replace('.', '/'));
        }

        return ("sbox", "");
    }

    // ==========================================================
    // Sandbox runtime collection
    // ==========================================================
    private static void EnsureSandboxAssembliesLoaded()
    {
        var candidates = new[]
        {
            "Sandbox.System",
            "Sandbox.Engine",
            "Sandbox.Reflection"
        };

        foreach (var n in candidates)
        {
            try
            {
                if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => (a.GetName().Name ?? "") == n))
                    Assembly.Load(new AssemblyName(n));
            }
            catch { }
        }

        foreach (var a in AppDomain.CurrentDomain.GetAssemblies().ToArray())
        {
            AssemblyName[] refs;
            try { refs = a.GetReferencedAssemblies(); }
            catch { continue; }

            foreach (var r in refs)
            {
                var rn = r.Name ?? "";
                if (!rn.StartsWith("Sandbox", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    if (!AppDomain.CurrentDomain.GetAssemblies().Any(x => (x.GetName().Name ?? "") == rn))
                        Assembly.Load(r);
                }
                catch { }
            }
        }
    }

    private static List<Type> CollectSandboxTypes()
    {
        var list = new List<Type>();

        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            var an = a.GetName().Name ?? "";
            if (!an.StartsWith("Sandbox.", StringComparison.OrdinalIgnoreCase))
                continue;

            Type[] ts;
            try { ts = a.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { ts = ex.Types.Where(x => x != null).ToArray()!; }
            catch { continue; }

            foreach (var t in ts)
            {
                if (t == null) continue;
                if (t.FullName == null) continue;
                if (t.IsPointer) continue;

                if (IsCompilerGeneratedType(t)) continue;

                var ns = t.Namespace ?? "";
                bool ok =
                    ns == "Sandbox" ||
                    ns.StartsWith("Sandbox.", StringComparison.Ordinal) ||
                    ns == "";

                if (!ok) continue;

                if (!(t.IsPublic || t.IsNestedPublic))
                    continue;

                list.Add(t);
            }
        }

        return list
            .GroupBy(t => t.FullName!, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    private static bool IsCompilerGeneratedType(Type t)
    {
        if (t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            return true;

        var name = t.Name ?? "";
        if (name.StartsWith("<", StringComparison.Ordinal))
            return true;

        if (name.Contains("DisplayClass", StringComparison.Ordinal) ||
            name.Contains("AnonStorey", StringComparison.Ordinal))
            return true;

        return false;
    }

    // ==========================================================
    // Api-like models (minimal)
    // ==========================================================
    private sealed class ApiType
    {
        public string FullName = "";
        public string Name = "";
        public List<ApiConstructor>? Constructors;
        public List<ApiProperty>? Properties;
        public List<ApiMethod>? Methods;
    }

    private sealed class ApiConstructor
    {
        public bool IsPublic;
        public List<ApiParam>? Parameters;
    }

    private sealed class ApiProperty
    {
        public bool IsPublic;
        public string Name = "";
        public string PropertyType = "";
        public bool? HasGet;
        public bool? HasSet;
        public ApiDoc? Documentation;
    }

    private sealed class ApiMethod
    {
        public string Name = "";
        public string ReturnType = "";
        public bool IsStatic;
        public bool IsProtected;
        public bool IsPublic;
        public List<ApiParam> Parameters = new();
        public ApiDoc? Documentation;
    }

    private sealed class ApiParam
    {
        public string Name = "";
        public string Type = "";
    }

    private sealed class ApiDoc
    {
        public string Summary = "";
    }
}
