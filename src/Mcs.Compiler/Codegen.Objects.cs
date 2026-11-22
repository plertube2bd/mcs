using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcs.Compiler;

/// An item object (ItemLib): a compile-time item stack with an identity ("mcs_obj" custom data).
sealed class ItemVar(string id, string objId) : Var
{
    public string Id = id;            // minecraft:carrot_on_a_stick
    public string ObjId = objId;      // <ns>:<field> — shared by clones, used by [OnRightClickObject]
    public string? Name, Lore;
    public bool Unbreakable, Glint;
    public ItemVar Clone() => new(Id, ObjId) { Name = Name, Lore = Lore, Unbreakable = Unbreakable, Glint = Glint };
}

/// Objects (entity / block / item objects) and their single-use rule:
/// m.SpawnEntityObject, m.SetBlockObject, m.FillBlockObject, m.GiveObject and x.Dispose() consume an object.
///   - compile time: a second consumption in the same method is an error (if/else branches are checked separately);
///   - run time: where a consumption can run more than once (inside a loop around it, or a static object),
///     a flag score makes later attempts do nothing and print a warning.
/// x.Clone() makes a fresh object with the same contents (and, for items, the same identity).
public sealed partial class Codegen
{
    readonly Dictionary<IFieldSymbol, ItemVar> itemStatics = new(SymbolEqualityComparer.Default);
    readonly Dictionary<ItemVar, string> staticItemFlags = new(ReferenceEqualityComparer.Instance);
    static readonly string[] ConsumingIntrinsics = ["spawn", "block.setobj", "block.fillobj", "item.give", "obj.dispose"];

    // ---------------------------------------------------------------- items
    ItemVar CreateItem(SemanticModel model, BaseObjectCreationExpressionSyntax oc, string objId, bool inMethod)
    {
        var arg = oc.ArgumentList?.Arguments.FirstOrDefault()?.Expression ?? throw new UnsupportedException(oc, "new Item(\"item_id\")");
        string raw = model.GetConstantValue(arg).Value as string ?? throw new UnsupportedException(arg, "The item ID must be a string constant.");
        var item = new ItemVar(ResolveId("items", raw, arg), objId);
        if (oc.Initializer != null)
            foreach (var init in oc.Initializer.Expressions.OfType<AssignmentExpressionSyntax>())
            {
                var p = (IPropertySymbol)model.GetSymbolInfo(init.Left).Symbol!;
                if (inMethod) SetObjProp(item, p, init.Right);
                else SetItemProp(item, p.Name, model.GetConstantValue(init.Right).Value, init.Right);
            }
        return item;
    }

    void SetItemProp(ItemVar item, string prop, object? value, SyntaxNode at)
    {
        switch (prop)
        {
            case "Name": item.Name = value as string ?? throw new UnsupportedException(at, "Name must be a string constant."); return;
            case "Lore": item.Lore = value as string ?? throw new UnsupportedException(at, "Lore must be a string constant."); return;
            case "Unbreakable": item.Unbreakable = value is true; return;
            case "Glint": item.Glint = value is true; return;
        }
        throw new UnsupportedException(at, $"Cannot set {prop}.");
    }

    void DeclareItemStatic(SemanticModel model, IFieldSymbol f, VariableDeclaratorSyntax v)
    {
        if (v.Initializer?.Value is not BaseObjectCreationExpressionSyntax oc) { Error(v, $"A static Item needs an initializer: static Item {f.Name} = new Item(\"minecraft:…\") {{ Name = \"…\" }};"); return; }
        try
        {
            var item = CreateItem(model, oc, $"{ns}:{Snake(f.Name)}", false);
            itemStatics[f] = item;
            staticItemFlags[item] = H($"$static.{f.Name}#used");
        }
        catch (UnsupportedException e) { Error(e.Node, e.Message); }
    }

    static string Text1(string s) => $"{{\"text\":{Json.Str(s)},\"italic\":false}}";
    static string SnbtSingle(string json) => "'" + json.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    /// The item stack argument of /give for this version.
    string ItemStack(ItemVar it)
    {
        var lore = it.Lore?.Split('\n').Select(Text1).ToList();
        string custom = $"{{mcs_obj:{Json.Str(it.ObjId)}}}";
        if (!prof.ItemPredicates)   // ≤1.20.4: NBT
        {
            var tag = new List<string> { $"mcs_obj:{Json.Str(it.ObjId)}" };
            var display = new List<string>();
            if (it.Name != null) display.Add($"Name:{SnbtSingle(Text1(it.Name))}");
            if (lore != null) display.Add($"Lore:[{string.Join(",", lore.Select(SnbtSingle))}]");
            if (display.Count > 0) tag.Add($"display:{{{string.Join(",", display)}}}");
            if (it.Unbreakable) tag.Add("Unbreakable:1b");
            if (it.Glint) tag.Add("Enchantments:[{}]");
            return $"{it.Id}{{{string.Join(",", tag)}}}";
        }
        // 1.20.5+: components; 1.21.5+ writes text components as SNBT instead of JSON strings
        string T(string json) => prof.NewTextComponents ? json : SnbtSingle(json);
        var comps = new List<string> { $"minecraft:custom_data={custom}" };
        if (it.Name != null) comps.Add($"minecraft:custom_name={T(Text1(it.Name))}");
        if (lore != null) comps.Add($"minecraft:lore=[{string.Join(",", lore.Select(T))}]");
        if (it.Unbreakable) comps.Add("minecraft:unbreakable={}");
        if (it.Glint) comps.Add("minecraft:enchantment_glint_override=true");
        if (clickObjects.Contains(it.ObjId))   // [OnRightClickObject] on an item that has no use action of its own
            comps.Add(prof.ConsumableComponent
                ? "minecraft:consumable={consume_seconds:1000000f,animation:\"none\",sound:\"minecraft:intentionally_empty\",has_consume_particles:false}"
                : "minecraft:food={nutrition:0,saturation:0f,can_always_eat:true,eat_seconds:1000000f}");
        return $"{it.Id}[{string.Join(",", comps)}]";
    }

    /// Condition parts: "@s holds the object in its main hand" / "…uses it from the off hand".
    (string main, string off) HoldsObject(ItemVar it)
    {
        if (prof.ItemPredicates)
        {
            string pred = $"*[minecraft:custom_data~{{mcs_obj:{Json.Str(it.ObjId)}}}]";
            return ($"if items entity @s weapon.mainhand {pred}",
                    $"unless items entity @s weapon.mainhand {it.Id} if items entity @s weapon.offhand {pred}");
        }
        string tag = $"tag:{{mcs_obj:{Json.Str(it.ObjId)}}}";
        return ($"if entity @s[nbt={{SelectedItem:{{{tag}}}}}]",
                $"unless entity @s[nbt={{SelectedItem:{{id:{Json.Str(it.Id)}}}}}] if entity @s[nbt={{Inventory:[{{Slot:-106b,{tag}}}]}}]");
    }

    /// Finds the static Item named in [OnRightClickObject(nameof(field))].
    ItemVar ItemByName(string name, SyntaxNode at)
    {
        var found = itemStatics.Where(kv => kv.Key.Name == name).ToList();
        if (found.Count == 0) throw new UnsupportedException(at, $"No static Item field named '{name}' (use nameof(field)).");
        if (found.Count > 1) throw new UnsupportedException(at, $"Several static Item fields are named '{name}'; give them unique names.");
        return found[0].Value;
    }

    // ---------------------------------------------------------------- clone / dispose
    bool IsCloneCall(ExpressionSyntax e, out ExpressionSyntax receiver)
    {
        receiver = null!;
        if (e is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma } inv || Intrinsic(cur.Model.GetSymbolInfo(inv).Symbol) != "obj.clone") return false;
        receiver = ma.Expression;
        return true;
    }

    EntVar CloneEntity(EntVar src, string tag) => new(tag)
    {
        TypeId = src.TypeId,
        Deferred = src.Deferred.Select(c => c.Replace($"tag={src.Tag},", $"tag={tag},").Replace($"tag={src.Tag}]", $"tag={tag}]")).ToList(),
    };

    // ---------------------------------------------------------------- the single-use rule
    static string Describe(ExpressionSyntax e) => e.ToString();

    /// Registers a newly created object variable (a local declaration or `x = new …`).
    void RegisterObject(Var v, ISymbol sym, SyntaxNode creation)
    {
        cur.Consumed.Remove(v);
        cur.Disposed.Remove(v);
        string? flag = null;
        // Will a consumption run more than once per creation? (inside a loop that does not contain the creation)
        foreach (var inv in cur.Info.Decl.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!ConsumingIntrinsics.Contains(Intrinsic(cur.Model.GetSymbolInfo(inv).Symbol))) continue;
            var target = Intrinsic(cur.Model.GetSymbolInfo(inv).Symbol) == "obj.dispose"
                ? (inv.Expression as MemberAccessExpressionSyntax)?.Expression
                : inv.ArgumentList.Arguments.FirstOrDefault()?.Expression;
            if (target == null || !SymbolEqualityComparer.Default.Equals(cur.Model.GetSymbolInfo(target).Symbol, sym)) continue;
            if (inv.Ancestors().Any(a => a is ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax or ForEachStatementSyntax && !creation.Ancestors().Contains(a)))
                flag = UniqueHolder(sym.Name + "#used");
        }
        if (flag != null) Emit($"scoreboard players set {flag} 0");
        cur.Objects[v] = (cur.Loops.Count, flag);
    }

    void CheckNotDisposed(Var v, SyntaxNode at)
    {
        if (cur.Disposed.TryGetValue(v, out var where))
            throw new UnsupportedException(at, $"'{at}' was disposed (line {where.GetLocation().GetMappedLineSpan().StartLinePosition.Line + 1}) and cannot be used any more.");
    }

    /// Emits a consuming use of the object in `objExpr`. `emit` writes the commands that use it.
    void Consume(ExpressionSyntax objExpr, Var v, SyntaxNode site, Action emit)
    {
        bool isVariable = cur.Objects.ContainsKey(v) || (v is ItemVar si && staticItemFlags.ContainsKey(si));
        if (!isVariable) { emit(); return; }   // a temporary (new … / x.Clone() inline): used once by construction
        CheckNotDisposed(v, objExpr);
        if (cur.Consumed.TryGetValue(v, out var first))
            throw new UnsupportedException(site, $"'{Describe(objExpr)}' was already used (line {first.GetLocation().GetMappedLineSpan().StartLinePosition.Line + 1}). " +
                "An object can be spawned / given / placed only once — use .Clone() to make another.");
        cur.Consumed[v] = site;

        string? flag = v is ItemVar iv && staticItemFlags.TryGetValue(iv, out var sf) ? sf : null;
        if (flag == null && cur.Objects.TryGetValue(v, out var info) && cur.Loops.Count > info.depth)
        {
            flag = info.flag;
            if (flag == null)   // not predicted (e.g. used through an alias): reset at method entry
            {
                flag = UniqueHolder(Describe(objExpr) + "#used");
                cur.EntryResets.Add($"scoreboard players set {flag} 0");
                cur.Objects[v] = (info.depth, flag);
            }
        }
        if (flag == null) { emit(); return; }
        var cmds = Capture(emit);
        Emit($"execute if score {flag} matches 1.. run tellraw @a [\"\",{{\"text\":\"[MC#] \",\"color\":\"gray\"}},{{\"text\":{Json.Str($"'{Describe(objExpr)}' was already used — an object can be used only once (use .Clone())")},\"color\":\"red\"}}]");
        EmitGuarded($"unless score {flag} matches 1..", cmds, "once");
        Emit($"scoreboard players set {flag} 1");
    }

    void EmitDispose(InvocationExpressionSyntax inv)
    {
        var recv = ((MemberAccessExpressionSyntax)inv.Expression).Expression;
        var v = GetVar(recv);
        Consume(recv, v, inv, () => { });
        cur.Disposed[v] = inv;
    }

    Operand EmitGive(InvocationExpressionSyntax inv)
    {
        var args = inv.ArgumentList.Arguments;
        var item = (ItemVar)EvalLibObject(args[0].Expression, VK.Item);
        string sel = GetRef(args[1].Expression).Selector;
        var count = args.Count > 2 ? EvalNum(args[2].Expression, VK.Int) : new ConstOp(1);
        Consume(args[0].Expression, item, inv, () => cur.Sink.AddRange(MacroCmd($"give {sel} {ItemStack(item)} $(n)", inv, ("n", count, VK.Int))));
        return new ConstOp(1);
    }

    void FinishObjects(List<string> load)
    {
        // static objects are created on every load
        foreach (var flag in staticItemFlags.Values) load.Add($"scoreboard players set {flag} 0");
    }
}
