using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcs.Compiler;

/// The MC# runtime mod (mcs.jar, Minecraft 26.1+).
///
/// Every command of the mod is written on a macro line of a small function under <ns>:__rt/. Macro lines are
/// parsed only when the function is called, so the pack loads without errors on servers without the mod (an
/// unknown command on a plain line would make the function — and any tag that lists it — fail to load).
///
/// On load, <ns>:__rt/probe asks the mod for its protocol version ($__rt; 0 = not installed).
///  - optional: shared helpers (__math/ddiv, __str/indexof …) start with a fast path into the mod; without the
///    mod they run as before and a one-time warning is printed.
///  - required: without the mod the pack does not start (load stops, [Tick] methods return at once).
public sealed partial class Codegen
{
    /// Protocol version of the mod commands this compiler emits (the mod answers `mcs probe` with its own).
    const int RuntimeProtocol = 1;
    bool usesRuntime;

    bool RtActive => opts.Runtime != RuntimeMode.Off && prof.RuntimeMod;
    string RtHolder => H("$__rt");

    /// <ns>:__rt/<name>: one macro line running a mod command.
    string RtFn(string name, string macroLine)
    {
        string path = $"{ns}:__rt/{name}";
        pack.Functions[path] = new() { "$" + macroLine };
        return path;
    }

    /// The first line of a shared helper: run the mod's version of it instead when the mod is installed.
    List<string> WithFastPath(string op, List<string> body, params (string key, string value)[] args)
    {
        if (!RtActive) return body;
        usesRuntime = true;
        string fn = RtFn($"fast/{op}", $"return run mcs fast {op} {string.Join(" ", args.Select(a => $"$({a.key})"))}");
        string call = $"return run function {fn} {{{string.Join(",", args.Select(a => $"{a.key}:{Json.Str(a.value)}"))}}}";
        // required: the mod is always there when the pack runs (the body stays as the reference implementation)
        string first = opts.Runtime == RuntimeMode.Required ? call : $"execute if score {RtHolder} matches 1.. run {call}";
        return new List<string> { first }.Concat(body).ToList();
    }

    Operand? EmitRuntimeIntrinsic(string name, InvocationExpressionSyntax inv)
    {
        var args = inv.ArgumentList.Arguments;
        switch (name)
        {
            case "rt.sudo":
            {
                if (!prof.RuntimeMod)
                    throw new UnsupportedException(inv, "Runtime.Sudo needs the MC# runtime mod (Minecraft 26.1+). Use '#if MC_26_1_OR_GREATER' or raise the minimum target version.");
                if (opts.Runtime != RuntimeMode.Required)
                    throw new UnsupportedException(inv, "Runtime.Sudo only works when the pack requires the MC# runtime mod: set \"runtime\": \"required\" in mcs.json (or --runtime required).");
                usesRuntime = true;
                var (cmd, margs, force) = BuildTemplate(args[0].Expression);
                if (margs.Length == 0 && !force)
                {
                    string fn = RtFn("sudo", "mcs sudo $(c)");
                    Emit($"function {fn} {{c:{Json.Str(cmd)}}}");
                }
                else cur.Sink.AddRange(MacroCmd($"mcs sudo {cmd}", args[0].Expression, margs, force));
                return null;
            }
        }
        throw new UnsupportedException(inv, $"Cannot use '{name}' here.");
    }

    Operand RuntimeAvailable() => RtActive ? StoreT("success", $"if score {RtHolder} matches 1..") : new ConstOp(0);

    /// Probe, warning / start guard. Runs after everything else is generated (usesRuntime is known then).
    void FinishRuntime(List<string> load)
    {
        bool required = opts.Runtime == RuntimeMode.Required && prof.RuntimeMod;
        if (!RtActive || !(usesRuntime || required)) return;
        string probe = RtFn("probe", "return run mcs probe $(v)");
        var lines = new List<string>
        {
            $"scoreboard players set {RtHolder} 0",
            $"execute store result score {RtHolder} run function {probe} {{v:{RuntimeProtocol}}}",
        };
        if (required)
        {
            lines.Add($"execute unless score {RtHolder} matches 1.. run return run tellraw @a {{\"text\":\"[mcsRuntimeServices] Error: {ns} requires the MC# Runtime Server Mod (mcs.jar). The datapack was not started.\",\"color\":\"red\"}}");
            foreach (var f in pack.TickTag)
                pack.Functions[f].Insert(0, $"execute unless score {RtHolder} matches 1.. run return 0");
        }
        else
            lines.Add($"execute unless score {RtHolder} matches 1.. run tellraw @a {{\"text\":\"[mcsRuntimeServices] Warn: There is not MC# Runtime Server Mod. Performance may be reduced, but there are no issues with the datapack.\",\"color\":\"yellow\"}}");
        load.InsertRange(1, lines);   // right after the objective is created
    }
}
