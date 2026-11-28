using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Mcs.Compiler;

public static class Program
{
    const string Usage = """
        MC# compiler (MC# {LANG})

          mcs build [mcs.json]                         Build according to the project file
          mcs <file.mcs ...> [options]                 Build the given files directly
          mcs run [mcs.json | file.mcs ...] [options]  Build, then run the pack in the simulator (no Minecraft)

        options:
          --mc <version|min-max>   Target Minecraft version (default: 1.20.1-26.3)
          --runtime <mode>         MC# runtime mod (mcs.jar, Minecraft 26.1+): optional (default: faster paths when
                                   the mod is installed, a one-time warning when not), required (the pack only starts
                                   with the mod; enables Runtime.Sudo) or off
          --division <mode>        csharp (default: / rounds toward zero) or minecraft (one scoreboard
                                   operation, rounds toward −∞ — faster)
                                   e.g. --mc 1.21.4, --mc 1.20.2-1.21.11
          -o, --out <dir>          Output datapack folder (default: out/<namespace>)
          -n, --namespace <ns>     Datapack namespace (default: name of the first file)
          -r, --resources <dir>    Extra files copied into the datapack as-is (e.g. data/<ns>/worldgen/...)
          --emit-csharp            Print the preprocessed C# source (for debugging)
          --check                  Only report errors; write nothing (used by editors)
          --stdin <file>           Read the text of <file> from standard input (unsaved editor buffers)
          --language <x.y>         MC# language version the project needs (error if newer than this compiler)
          --version                Print the MC# language and compiler version
          --versions               List supported versions and #if symbols
          --api-docs               Print the API reference (docs/api.md) generated from the built-in declarations

        mcs run options (simulator):
          --ticks <n>              Ticks to simulate after load (default: until nothing is scheduled;
                                   20 when there are [Tick] methods)
          --do "<tick> <action>"   Scripted player action, repeatable: "0 join Steve", "5 die Steve",
                                   "3 trigger Steve menu set 2", "4 click Steve" (see docs/compiling.md)
          --format <n>             Pack format to simulate (default: the newest layer)

        mcs.json:
          { "namespace": "hello", "sources": ["src/*.mcs"], "output": "out/hello", "minecraft": "1.20.1-26.3", "resources": "res",
            "division": "csharp", "runtime": "optional", "language": "{LANG}" }
        """;

    public static int Main(string[] argv)
    {
        try { return Run(argv); }
        catch (CompileException e) { Console.Error.WriteLine($"error: {e.Message}"); return 1; }
    }

    static int Run(string[] argv)
    {
        var files = new List<string>();
        string? outDir = null, ns = null, mc = null, res = null, desc = null, division = null, runtime = null, language = null;
        bool emitCs = false, check = false;
        string? stdinFile = null;
        bool simulate = false;
        var simArgs = new List<string>();

        if (argv.Length > 0 && argv[0] == "run")
        {
            simulate = true;
            argv = argv[1..];
            if (!argv.Any(a => a.EndsWith(".mcs", StringComparison.OrdinalIgnoreCase))) argv = ["build", .. argv];   // a project
        }

        if (argv.Length > 0 && argv[0] == "build")
        {
            bool explicitFile = argv.Length > 1 && !argv[1].StartsWith('-');
            var proj = Project.Load(explicitFile ? argv[1] : "mcs.json");
            files.AddRange(proj.Files);
            ns = proj.Namespace; outDir = proj.Output; mc = proj.Minecraft; res = proj.Resources; desc = proj.Description; division = proj.Division; runtime = proj.Runtime; language = proj.Language;
            argv = argv.Skip(explicitFile ? 2 : 1).ToArray(); // CLI options override the project file
        }

        for (int i = 0; i < argv.Length; i++)
        {
            string Next() => i + 1 < argv.Length ? argv[++i] : throw new CompileException($"{argv[i]} needs a value.");
            switch (argv[i])
            {
                case "-o" or "--out": outDir = Next(); break;
                case "-n" or "--namespace": ns = Next(); break;
                case "--mc": mc = Next(); break;
                case "--division": division = Next(); break;
                case "--runtime": runtime = Next(); break;
                case "--language": language = Next(); break;
                case "--version": Console.WriteLine($"MC# {Language.Version} (mcs {typeof(Program).Assembly.GetName().Version?.ToString(3)})"); return 0;
                case "-r" or "--resources": res = Next(); break;
                case "--emit-csharp": emitCs = true; break;
                case "--check": check = true; break;
                case "--stdin": stdinFile = Next(); break;
                case "--versions": PrintVersions(); return 0;
                case "--api-docs": Console.Write(ApiDocs.Generate()); return 0;
                case "--ticks" or "--do" or "--format" when simulate: simArgs.Add(argv[i]); simArgs.Add(Next()); break;
                case "-h" or "--help": Console.WriteLine(Usage.Replace("{LANG}", Language.Version)); return 0;
                default:
                    if (argv[i].StartsWith('-')) throw new CompileException($"Unknown option: {argv[i]}");
                    files.Add(argv[i]); break;
            }
        }
        if (files.Count == 0) { Console.Error.WriteLine(Usage.Replace("{LANG}", Language.Version)); return 1; }
        if (language != null) Language.Require(language);

        ns ??= Regex.Replace(Path.GetFileNameWithoutExtension(files[0]).ToLowerInvariant(), "[^a-z0-9_.-]", "_");
        if (!Regex.IsMatch(ns, "^[a-z0-9_.-]+$")) throw new CompileException($"Invalid namespace '{ns}' (only a-z0-9_.- allowed)");
        outDir ??= Path.Combine("out", ns);
        var (min, max) = Versions.ParseRange(mc ?? Versions.DefaultRange);

        string? stdinText = stdinFile != null ? Console.In.ReadToEnd() : null;
        bool IsStdin(string f) => stdinFile != null && Path.GetFullPath(f) == Path.GetFullPath(stdinFile);
        if (stdinFile != null && !files.Any(IsStdin)) files.Add(stdinFile);   // a new file not in the project yet
        var opts = new CompileOptions(MinecraftDivision: (division ?? "csharp").ToLowerInvariant() switch
        {
            "csharp" or "c#" => false,
            "minecraft" or "floor" => true,
            _ => throw new CompileException($"Unknown division mode '{division}' (csharp or minecraft)."),
        }, Runtime: (runtime ?? "optional").ToLowerInvariant() switch
        {
            "optional" => RuntimeMode.Optional,
            "required" => RuntimeMode.Required,
            "off" or "none" => RuntimeMode.Off,
            _ => throw new CompileException($"Unknown runtime mode '{runtime}' (optional, required or off)."),
        });
        if (opts.Runtime == RuntimeMode.Required && min.Format < TargetProfile.RuntimeFormat)
            throw new CompileException($"\"runtime\": \"required\" needs Minecraft 26.1 or newer (the MC# runtime mod exists for 26.1+), but the target range starts at {min}. Use --mc 26.1-{max}.");
        var result = Compiler.Compile(files.Select(f => (f, IsStdin(f) ? stdinText! : File.ReadAllText(f))).ToList(), ns, min, max, opts);
        if (emitCs) foreach (var (f, cs) in result.Preprocessed) Console.WriteLine($"// ==== {f}\n{cs}");
        foreach (var d in result.Diagnostics) Console.Error.WriteLine(d);
        if (result.Layers == null) { Console.Error.WriteLine($"Build failed: {result.Diagnostics.Count(d => !d.Warning)} error(s)"); return 1; }
        if (check) { Console.WriteLine("Check succeeded"); return 0; }

        DatapackWriter.Write(outDir, ns, desc ?? $"MC# datapack: {ns} ({min}-{max})", result.Layers);
        if (res != null)
        {
            if (!Directory.Exists(res)) throw new CompileException($"Resources folder not found: {res}");
            foreach (var f in Directory.GetFiles(res, "*", SearchOption.AllDirectories))
            {
                string dst = Path.Combine(outDir, Path.GetRelativePath(res, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(f, dst, true);
            }
        }
        Console.WriteLine($"Build succeeded: {outDir} (Minecraft {min} – {max})");
        foreach (var l in result.Layers)
            Console.WriteLine($"  {(l == result.Layers[0] ? "base" : DatapackWriter.OverlayDir(l)),-14} {l.Name,-18} {l.Pack.Functions.Count} functions");
        return simulate ? Simulate(outDir, simArgs) : 0;
    }

    /// <summary>Runs the built pack in tools/mcsim.py (shipped next to the compiler).</summary>
    static int Simulate(string pack, List<string> simArgs)
    {
        string sim = new[] { Path.Combine(AppContext.BaseDirectory, "tools", "mcsim.py"),
                             Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "mcsim.py") }
            .Select(Path.GetFullPath).FirstOrDefault(File.Exists)
            ?? throw new CompileException("The simulator (tools/mcsim.py) was not found next to the compiler. Reinstall with tools/install.sh.");
        Console.WriteLine($"Simulating {pack} (scores, storage, functions, schedule; blocks and positions are not simulated)");
        var psi = new System.Diagnostics.ProcessStartInfo("python3") { UseShellExecute = false };
        psi.ArgumentList.Add(sim);
        psi.ArgumentList.Add(pack);
        foreach (var a in simArgs) psi.ArgumentList.Add(a);
        Console.Out.Flush();
        try
        {
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new CompileException("mcs run needs Python 3 (python3) for the simulator.");
        }
    }

    static void PrintVersions()
    {
        Console.WriteLine($"MC# {Language.Version}: #if {string.Join(", ", Language.Symbols)}\n");
        Console.WriteLine("version     data format   #if symbol (defined for this version and above)");
        foreach (var g in Versions.Groups)
            foreach (var r in Versions.Releases.Where(r => r.Format == g.Format))
                Console.WriteLine($"{r.Id,-11} {r.Format + "." + r.FormatMinor,-13} {(r == g.First ? g.First.Symbol : "")}");
    }
}

/// <summary>mcs.json — the simplest possible project file.</summary>
public sealed class Project
{
    public string? Namespace { get; set; }
    public List<string> Sources { get; set; } = new();
    public string? Output { get; set; }
    public string? Minecraft { get; set; }
    public string? Resources { get; set; }
    public string? Description { get; set; }
    public string? Division { get; set; }
    public string? Runtime { get; set; }
    public string? Language { get; set; }
    public List<string> Files { get; private set; } = new();

    public static Project Load(string file)
    {
        if (!File.Exists(file)) throw new CompileException($"Project file not found: {file}");
        Project p;
        try
        {
            p = JsonSerializer.Deserialize<Project>(File.ReadAllText(file),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
        }
        catch (JsonException e) { throw new CompileException($"{file}: {e.Message}"); }

        string baseDir = Path.GetDirectoryName(Path.GetFullPath(file))!;
        string Rel(string path) => Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.Combine(baseDir, path));
        if (p.Output != null) p.Output = Rel(p.Output);
        if (p.Resources != null) p.Resources = Rel(p.Resources);
        foreach (var src in p.Sources.DefaultIfEmpty("*.mcs"))
        {
            string full = Path.Combine(baseDir, src);
            if (Directory.Exists(full)) p.Files.AddRange(Directory.GetFiles(full, "*.mcs", SearchOption.AllDirectories).Order());
            else if (src.Contains('*'))
            {
                string dir = Path.GetDirectoryName(full)!;
                if (Directory.Exists(dir)) p.Files.AddRange(Directory.GetFiles(dir, Path.GetFileName(full)).Order());
            }
            else if (File.Exists(full)) p.Files.Add(full);
            else throw new CompileException($"{file}: source not found: {src}");
        }
        p.Files = p.Files.Select(f => Path.GetRelativePath(Directory.GetCurrentDirectory(), f)).Distinct().ToList();
        if (p.Files.Count == 0) throw new CompileException($"{file}: no .mcs sources found.");
        return p;
    }
}

public sealed record CompileResult(List<(string file, string csharp)> Preprocessed, List<Diag> Diagnostics, List<Layer>? Layers);

public static class Compiler
{
    public static CompileResult Compile(List<(string path, string source)> sources, string ns, McVersion min, McVersion max, CompileOptions? opts = null)
    {
        var warnings = new List<Diag>();
        var pre = sources.Select(s => (s.path, Preprocessor.Process(s.source))).ToList();
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is "System.Runtime.dll" or "System.Private.CoreLib.dll" or "netstandard.dll" or "System.Collections.dll" or "System.Linq.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();

        // Built-in declarations are the same for every version group, so parse them once
        var baseOpts = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var preludeTrees = new[]
        {
            CSharpSyntaxTree.ParseText(Prelude.Source, baseOpts, "<prelude>"),
            CSharpSyntaxTree.ParseText(PreludeGen.Source, baseOpts, "<prelude-generated>"),
            CSharpSyntaxTree.ParseText(Prelude.GlobalUsings, baseOpts, "<usings>"),
        };

        // Build once per version group (branching at build time)
        var groups = Versions.GroupsIn(min, max);
        var built = new List<(TargetProfile, CompiledPack)>();
        var diags = new List<(Diag d, TargetProfile g)>();
        foreach (var g in groups)
        {
            var parseOpts = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest).WithPreprocessorSymbols(g.Symbols);
            var userTrees = MountRewriter.Apply(pre.Select(p => CSharpSyntaxTree.ParseText(p.Item2, parseOpts, p.path)).ToList());
            if (g == groups[0]) warnings.AddRange(Deprecations(userTrees));
            var trees = new List<SyntaxTree>(userTrees);
            trees.AddRange(preludeTrees);
            var comp = CSharpCompilation.Create("mcs_user", trees, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));

            // Only check user code (binding every generated block class would be slow)
            var errs = userTrees.SelectMany(t => comp.GetSemanticModel(t).GetDiagnostics()).Where(d => d.Severity == DiagnosticSeverity.Error).Select(d =>
            {
                var span = d.Location.GetMappedLineSpan();
                return new Diag(span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, $"{d.Id}: {d.GetMessage()}");
            }).ToList();
            if (errs.Count == 0)
            {
                var gen = new Codegen(comp, userTrees, ns, g, opts);
                var pack = gen.Run();
                errs = gen.Diags;
                foreach (var w in gen.Warnings) if (!warnings.Contains(w)) warnings.Add(w);
                if (errs.Count == 0) built.Add((g, pack));
            }
            diags.AddRange(errs.Select(e => (e, g)));
        }

        if (diags.Count > 0)
        {
            // Merge identical errors across versions; say which versions an error applies to if not all
            var merged = diags.GroupBy(x => x.d).Select(gr =>
            {
                var gs = gr.Select(x => x.g).ToList();
                if (gs.Count == groups.Count) return gr.Key;
                string where = string.Join(", ", gs.Select(x => x.Name));
                return gr.Key with { Message = gr.Key.Message.StartsWith('[') ? gr.Key.Message : $"[{where}] {gr.Key.Message}" };
            }).OrderBy(d => d.File).ThenBy(d => d.Line).ToList();
            return new(pre, warnings.Concat(merged).ToList(), null);
        }
        return new(pre, warnings, DatapackWriter.Merge(built));
    }

    /// float / double still work, but the MC# names are Milli (×1000 fixed point) and Q16 (Q15.16 fixed point).
    static IEnumerable<Diag> Deprecations(List<SyntaxTree> trees)
    {
        foreach (var t in trees)
            foreach (var p in t.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.PredefinedTypeSyntax>())
            {
                string? repl = p.Keyword.Kind() switch { SyntaxKind.FloatKeyword => "Milli", SyntaxKind.DoubleKeyword => "Q16", _ => null };
                if (repl == null) continue;
                var span = p.GetLocation().GetMappedLineSpan();
                yield return new Diag(span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
                    $"'{p.Keyword.Text}' is deprecated: use {repl}. '{p.Keyword.Text}' still works for now, but may be removed in any future version.", true);
            }
    }
}
