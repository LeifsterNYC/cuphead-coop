using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using Mono.Cecil;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: Inspect <command> <assembly-path> [args...]");
    Console.Error.WriteLine("commands:");
    Console.Error.WriteLine("  list <asm> [namePattern]              -- list type FullNames matching regex");
    Console.Error.WriteLine("  members <asm> <typeFullName>          -- methods + fields of a type");
    Console.Error.WriteLine("  decompile <asm> <typeFullName>        -- decompiled C# of a type");
    Console.Error.WriteLine("  refs <asm>                            -- referenced assemblies");
    return 1;
}

var cmd = args[0];
var asmPath = args[1];

if (!File.Exists(asmPath))
{
    Console.Error.WriteLine($"not found: {asmPath}");
    return 2;
}

var managedDir = Path.GetDirectoryName(asmPath)!;

switch (cmd)
{
    case "list":
    {
        var pattern = args.Length >= 3 ? args[2] : ".*";
        var rx = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managedDir);
        using var asm = AssemblyDefinition.ReadAssembly(asmPath, new ReaderParameters { AssemblyResolver = resolver });
        foreach (var t in AllTypes(asm.MainModule))
        {
            if (rx.IsMatch(t.FullName)) Console.WriteLine(t.FullName);
        }
        return 0;
    }
    case "members":
    {
        var typeName = args[2];
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managedDir);
        using var asm = AssemblyDefinition.ReadAssembly(asmPath, new ReaderParameters { AssemblyResolver = resolver });
        var t = AllTypes(asm.MainModule).FirstOrDefault(x => x.FullName == typeName);
        if (t == null) { Console.Error.WriteLine($"type not found: {typeName}"); return 3; }
        Console.WriteLine($"// {t.FullName}  (base: {t.BaseType?.FullName})");
        foreach (var f in t.Fields)
            Console.WriteLine($"  field {Vis(f)} {f.FieldType.FullName} {f.Name}");
        foreach (var p in t.Properties)
            Console.WriteLine($"  prop  {p.PropertyType.FullName} {p.Name}");
        foreach (var m in t.Methods)
        {
            var sig = string.Join(", ", m.Parameters.Select(pp => $"{pp.ParameterType.Name} {pp.Name}"));
            Console.WriteLine($"  meth  {Vis(m)} {m.ReturnType.Name} {m.Name}({sig})");
        }
        return 0;
    }
    case "decompile":
    {
        var typeName = args[2];
        var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
        var decompiler = new CSharpDecompiler(asmPath, new UniversalAssemblyResolver(asmPath, false, null), settings);
        var fullName = new FullTypeName(typeName);
        var src = decompiler.DecompileTypeAsString(fullName);
        Console.Write(src);
        return 0;
    }
    case "refs":
    {
        using var asm = AssemblyDefinition.ReadAssembly(asmPath);
        foreach (var r in asm.MainModule.AssemblyReferences)
            Console.WriteLine($"{r.Name}, {r.Version}");
        return 0;
    }
    default:
        Console.Error.WriteLine($"unknown command: {cmd}");
        return 1;
}

static string Vis(MemberReference m) => m switch
{
    MethodDefinition md => md.IsPublic ? "public" : md.IsPrivate ? "private" : md.IsFamily ? "protected" : "internal",
    FieldDefinition fd => fd.IsPublic ? "public" : fd.IsPrivate ? "private" : fd.IsFamily ? "protected" : "internal",
    _ => "?"
};

static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition mod)
{
    foreach (var t in mod.Types)
    {
        yield return t;
        foreach (var nt in Nested(t)) yield return nt;
    }
    static IEnumerable<TypeDefinition> Nested(TypeDefinition t)
    {
        foreach (var n in t.NestedTypes)
        {
            yield return n;
            foreach (var nn in Nested(n)) yield return nn;
        }
    }
}
