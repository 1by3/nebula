// ApiGen: generates the Nebula API reference (MDX pages for the Fumadocs site) straight from the C# sources.
//
// It is syntax-only (Roslyn parses the files, nothing is compiled), so it needs neither Unity nor the
// SpacetimeDB SDK on the machine that runs it. Every public or protected type and member under
// Packages/com.1by3.nebula/{Runtime,World,Editor} and the SpacetimeDB control-plane module gets a signature and
// its XML documentation comment rendered to Markdown. Unity [Tooltip] text stands in for missing docs.
//
//   dotnet run --project website/tools/ApiGen -- <repo root> <output dir> [--source-url <base>]
//
// The npm script `gen:api` in website/package.json runs it with the right paths.

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Nebula.ApiGen;

public static class Program
{
    private static readonly (string Folder, string Title, string Slug, string Blurb)[] Groups =
    {
        ("Core", "Core", "core", "Gameplay API: identities, behaviours, variables, RPCs, prediction, the sync components and project configuration."),
        ("Containers", "Containers", "containers", "The designer-authored authority volumes and their registry."),
        ("World", "World partition", "world", "Cell grid, additive streaming and the floating origin (usable without Nebula), plus the baked container manifest and per-role cell streaming."),
        ("Worker", "Worker", "worker", "The simulation process: spawning, ghosting, handover, the per-tick loop."),
        ("Client", "Client", "client", "The game client: gateway connection, prediction driver, title screen."),
        ("Gateway", "Gateway", "gateway", "The one address clients connect to; routes inputs and re-emits replication."),
        ("Orchestrator", "Orchestrator", "orchestrator", "Worker lifecycle, container assignment, the dashboard and worker hosts."),
        ("ControlPlane", "Control plane", "control-plane", "The IControlPlane abstraction and its SpacetimeDB and in-process implementations."),
        ("Bootstrap", "Bootstrap", "bootstrap", "Process entry point and role selection."),
        ("Serialization", "Serialization", "serialization", "NetworkWriter, NetworkReader and type-driven serialization."),
        ("Transport", "Transport", "transport", "The ITransport seam and the LiteNetLib implementation."),
        ("Protocol", "Protocol", "protocol", "Wire messages exchanged between workers, gateway and clients."),
        ("Debug", "Debug", "debug", "The in-game status overlay."),
        ("Editor", "Editor", "editor", "Unity Editor tooling: builds, local mesh, control-plane helpers, the World window."),
        ("SpacetimeModule", "SpacetimeDB module", "spacetime-module", "Tables and reducers of the control-plane module."),
    };

    private static readonly HashSet<string> StrippedMemberAttributes = new() { "Tooltip", "Header", "SerializeField", "Space", "TextArea" };

    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ApiGen <repo root> <output dir> [--source-url <base url>]");
            return 2;
        }
        string root = Path.GetFullPath(args[0]);
        string outDir = Path.GetFullPath(args[1]);
        string? sourceUrl = null;
        for (int i = 2; i < args.Length - 1; i++)
            if (args[i] == "--source-url") sourceUrl = args[i + 1].TrimEnd('/');

        var files = new List<(string Path, string Group)>();
        string runtime = Path.Combine(root, "Packages", "com.1by3.nebula", "Runtime");
        foreach (var file in Directory.EnumerateFiles(runtime, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(runtime, file);
            string group = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (group.EndsWith(".cs")) continue; // AssemblyInfo.cs at the root
            files.Add((file, group));
        }
        string world = Path.Combine(root, "Packages", "com.1by3.nebula", "World");
        if (Directory.Exists(world))
            foreach (var file in Directory.EnumerateFiles(world, "*.cs", SearchOption.AllDirectories))
                files.Add((file, "World"));
        string editor = Path.Combine(root, "Packages", "com.1by3.nebula", "Editor");
        if (Directory.Exists(editor))
            foreach (var file in Directory.EnumerateFiles(editor, "*.cs", SearchOption.AllDirectories))
                files.Add((file, "Editor"));
        string module = Path.Combine(root, "Packages", "com.1by3.nebula", "SpacetimeDB", "Module~", "Lib.cs");
        if (File.Exists(module)) files.Add((module, "SpacetimeModule"));

        var types = new List<TypeInfo>();
        foreach (var (path, group) in files)
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);
            var unit = tree.GetCompilationUnitRoot();
            foreach (var decl in unit.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).OfType<BaseTypeDeclarationSyntax>())
            {
                if (!IsPublic(decl.Modifiers)) continue;
                types.Add(TypeInfo.From(decl, group, Path.GetRelativePath(root, path).Replace('\\', '/')));
            }
        }
        if (types.Count == 0)
        {
            Console.Error.WriteLine("no public types found under " + root);
            return 1;
        }

        var index = new Dictionary<string, TypeInfo>();
        foreach (var t in types)
        {
            index[t.Name] = t;
            foreach (var n in t.NestedTypes) index[t.Name + "." + n.Name] = t;
        }

        // Clear stale pages but keep the directory itself (a shell or the dev server may be sitting in it).
        Directory.CreateDirectory(outDir);
        foreach (var dir in Directory.EnumerateDirectories(outDir)) Directory.Delete(dir, recursive: true);
        foreach (var file in Directory.EnumerateFiles(outDir)) File.Delete(file);

        var renderer = new Renderer(index, sourceUrl);
        var byGroup = types.GroupBy(t => t.Group).ToDictionary(g => g.Key, g => g.OrderBy(t => t.Name, StringComparer.Ordinal).ToList());
        var groupOrder = Groups.Select(g => g.Folder).Where(byGroup.ContainsKey).ToList();
        foreach (var extra in byGroup.Keys.Where(k => !groupOrder.Contains(k)).OrderBy(k => k)) groupOrder.Add(extra);

        var indexPage = new StringBuilder();
        indexPage.AppendLine("---");
        indexPage.AppendLine("title: API reference");
        indexPage.AppendLine("description: Every public type in the Nebula runtime, generated from the source.");
        indexPage.AppendLine("---");
        indexPage.AppendLine();
        indexPage.AppendLine("This section is generated from the XML documentation comments in `Packages/com.1by3.nebula` by `website/tools/ApiGen` (run `npm run gen:api` in `website/`). It covers the runtime assembly, the Editor tooling and the SpacetimeDB control-plane module. Hand-written explanations of how the pieces fit together live in the [guides](/docs/guides/network-behaviour).");
        indexPage.AppendLine();

        var rootMeta = new List<string> { "index" };
        foreach (var folder in groupOrder)
        {
            var g = Groups.FirstOrDefault(x => x.Folder == folder);
            string title = g.Title ?? folder;
            string slug = g.Slug ?? Kebab(folder);
            string blurb = g.Blurb ?? "";
            string dir = Path.Combine(outDir, slug);
            Directory.CreateDirectory(dir);
            var pages = new List<string>();
            indexPage.AppendLine($"## {title}");
            indexPage.AppendLine();
            if (blurb.Length > 0) { indexPage.AppendLine(blurb); indexPage.AppendLine(); }
            indexPage.AppendLine("| Type | Summary |");
            indexPage.AppendLine("| --- | --- |");
            foreach (var t in byGroup[folder])
            {
                string page = Kebab(t.Name);
                pages.Add(page);
                File.WriteAllText(Path.Combine(dir, page + ".mdx"), Lf(renderer.RenderType(t)), new UTF8Encoding(false));
                indexPage.AppendLine($"| [{Escape(t.DisplayName)}](/docs/reference/{slug}/{page}) | {Escape(t.Kind)}. {Escape(renderer.Description(t.Node))} |");
            }
            indexPage.AppendLine();
            File.WriteAllText(Path.Combine(dir, "meta.json"), Lf(Json(new Dictionary<string, object> { ["title"] = title, ["pages"] = pages })), new UTF8Encoding(false));
            rootMeta.Add(slug);
        }
        File.WriteAllText(Path.Combine(outDir, "index.mdx"), Lf(indexPage.ToString()), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "meta.json"), Lf(Json(new Dictionary<string, object> { ["title"] = "API reference", ["description"] = "Generated from the C# sources", ["root"] = true, ["pages"] = rootMeta })), new UTF8Encoding(false));
        Console.WriteLine($"apigen: {types.Count} types in {groupOrder.Count} groups -> {outDir}");
        return 0;
    }

    private static bool IsPublic(SyntaxTokenList modifiers) => modifiers.Any(SyntaxKind.PublicKeyword);

    public static bool IsAccessible(SyntaxTokenList modifiers, bool parentIsInterfaceOrEnum)
    {
        if (parentIsInterfaceOrEnum) return true;
        if (modifiers.Any(SyntaxKind.PrivateKeyword)) return false; // includes `private protected`
        return modifiers.Any(SyntaxKind.PublicKeyword) || modifiers.Any(SyntaxKind.ProtectedKeyword);
    }

    public static string Kebab(string name)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                bool prevLowerOrDigit = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                bool acronymEnd = i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (prevLowerOrDigit || acronymEnd) sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    public static string Escape(string text)
    {
        // MDX: `<` opens JSX, `{` opens an expression; `*`/`_` would italicise identifiers like some_name.
        // A bare URL is autolinked by GFM up to the next `<`, which then reads as a tag even when escaped, so
        // URLs (and anything containing `://`) are wrapped in a code span instead of escaped.
        var sb = new StringBuilder(text.Length + 8);
        foreach (var part in Regex.Split(text, @"(\S*://\S*)"))
        {
            if (part.Contains("://")) { sb.Append('`').Append(part).Append('`'); continue; }
            foreach (char c in part)
            {
                if (c is '<' or '>' or '{' or '}' or '*' or '_' or '\\' or '[' or ']' or '|') sb.Append('\\');
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>Generated files use LF regardless of the host OS, so a regeneration on Windows is not a whole-file diff.</summary>
    private static string Lf(string text) => text.Replace("\r\n", "\n");

    private static string Json(Dictionary<string, object> values)
    {
        var sb = new StringBuilder("{\n");
        int i = 0;
        foreach (var (k, v) in values)
        {
            sb.Append("  \"").Append(k).Append("\": ");
            switch (v)
            {
                case string s: sb.Append('"').Append(s.Replace("\"", "\\\"")).Append('"'); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case List<string> list: sb.Append('[').Append(string.Join(", ", list.Select(x => "\"" + x + "\""))).Append(']'); break;
            }
            sb.Append(++i < values.Count ? ",\n" : "\n");
        }
        return sb.Append("}\n").ToString();
    }
}

public sealed class TypeInfo
{
    public required string Name;
    public required string DisplayName;
    public required string Kind;
    public required string Group;
    public required string File;
    public required string Namespace;
    public required BaseTypeDeclarationSyntax Node;
    public List<TypeInfo> NestedTypes = new();

    public static TypeInfo From(BaseTypeDeclarationSyntax decl, string group, string file)
    {
        string name = decl.Identifier.ValueText;
        string display = name;
        string kind = decl switch
        {
            ClassDeclarationSyntax => "Class",
            StructDeclarationSyntax => "Struct",
            InterfaceDeclarationSyntax => "Interface",
            EnumDeclarationSyntax => "Enum",
            RecordDeclarationSyntax r => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "Record struct" : "Record",
            _ => "Type",
        };
        if (decl is TypeDeclarationSyntax td && td.TypeParameterList != null)
            display = name + td.TypeParameterList.ToString();
        if (decl is ClassDeclarationSyntax c)
        {
            if (c.Modifiers.Any(SyntaxKind.StaticKeyword)) kind = "Static class";
            else if (c.Modifiers.Any(SyntaxKind.AbstractKeyword)) kind = "Abstract class";
        }
        string ns = "";
        for (SyntaxNode? p = decl.Parent; p != null; p = p.Parent)
            if (p is BaseNamespaceDeclarationSyntax n) { ns = n.Name.ToString(); break; }

        var info = new TypeInfo { Name = name, DisplayName = display, Kind = kind, Group = group, File = file, Namespace = ns, Node = decl };
        if (decl is TypeDeclarationSyntax t)
            foreach (var member in t.Members)
                if (member is BaseTypeDeclarationSyntax nested && nested.Modifiers.Any(SyntaxKind.PublicKeyword))
                    info.NestedTypes.Add(From(nested, group, file));
                else if (member is DelegateDeclarationSyntax d && d.Modifiers.Any(SyntaxKind.PublicKeyword))
                    info.NestedTypes.Add(new TypeInfo { Name = d.Identifier.ValueText, DisplayName = d.Identifier.ValueText, Kind = "Delegate", Group = group, File = file, Namespace = ns, Node = null!, Delegate = d });
        return info;
    }

    public DelegateDeclarationSyntax? Delegate;
}

public sealed class Renderer
{
    private readonly Dictionary<string, TypeInfo> _index;
    private readonly string? _sourceUrl;
    private TypeInfo _current = null!;
    private readonly HashSet<string> _currentMembers = new();

    public Renderer(Dictionary<string, TypeInfo> index, string? sourceUrl)
    {
        _index = index;
        _sourceUrl = sourceUrl;
    }

    public string RenderType(TypeInfo type)
    {
        _current = type;
        _currentMembers.Clear();
        if (type.Node is TypeDeclarationSyntax td)
            foreach (var m in td.Members)
                foreach (var name in MemberNames(m)) _currentMembers.Add(name);

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("title: " + Yaml(type.DisplayName));
        sb.AppendLine("description: " + Yaml(Description(type.Node)));
        sb.AppendLine("---");
        sb.AppendLine();
        string src = _sourceUrl != null ? $"[{type.File}]({_sourceUrl}/{type.File})" : $"`{type.File}`";
        string ns = type.Namespace.Length > 0 ? $"in namespace `{type.Namespace}`" : "in the global namespace";
        sb.AppendLine($"<div className=\"text-sm text-fd-muted-foreground\">{Program.Escape(type.Kind)} {ns} · {src}</div>");
        sb.AppendLine();
        sb.AppendLine("```csharp");
        sb.AppendLine(TypeHeader(type.Node));
        sb.AppendLine("```");
        sb.AppendLine();
        AppendDocBody(sb, type.Node, includeSummary: true);
        AppendMembers(sb, type, "##", "###");
        return sb.ToString();
    }

    private void AppendMembers(StringBuilder sb, TypeInfo type, string h2, string h3)
    {
        if (type.Node is EnumDeclarationSyntax e)
        {
            sb.AppendLine($"{h2} Members");
            sb.AppendLine();
            sb.AppendLine("| Name | Value | Description |");
            sb.AppendLine("| --- | --- | --- |");
            foreach (var m in e.Members)
            {
                string value = m.EqualsValue != null ? "`" + m.EqualsValue.Value.ToString() + "`" : "";
                sb.AppendLine($"| `{m.Identifier.ValueText}` | {value} | {InlineDoc(m)} |");
            }
            sb.AppendLine();
            return;
        }
        if (type.Node is not TypeDeclarationSyntax td) return;
        bool isInterface = td is InterfaceDeclarationSyntax;

        var ctors = new List<MemberDeclarationSyntax>();
        var fields = new List<MemberDeclarationSyntax>();
        var props = new List<MemberDeclarationSyntax>();
        var events = new List<MemberDeclarationSyntax>();
        var methods = new List<MemberDeclarationSyntax>();
        foreach (var m in td.Members)
        {
            if (m is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax) continue;
            if (!Program.IsAccessible(m.Modifiers, isInterface)) continue;
            switch (m)
            {
                case ConstructorDeclarationSyntax: ctors.Add(m); break;
                case FieldDeclarationSyntax: fields.Add(m); break;
                case PropertyDeclarationSyntax or IndexerDeclarationSyntax: props.Add(m); break;
                case EventFieldDeclarationSyntax or EventDeclarationSyntax: events.Add(m); break;
                case MethodDeclarationSyntax or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax: methods.Add(m); break;
            }
        }

        AppendMemberSection(sb, "Constructors", ctors, h2, h3);
        AppendMemberSection(sb, "Fields", fields, h2, h3);
        AppendMemberSection(sb, "Properties", props, h2, h3);
        AppendMemberSection(sb, "Events", events, h2, h3);
        AppendMemberSection(sb, "Methods", methods, h2, h3);

        if (type.NestedTypes.Count > 0)
        {
            sb.AppendLine($"{h2} Nested types");
            sb.AppendLine();
            foreach (var n in type.NestedTypes)
            {
                sb.AppendLine($"{h3} {Program.Escape(n.DisplayName)} [#{n.Name.ToLowerInvariant()}]");
                sb.AppendLine();
                sb.AppendLine("```csharp");
                sb.AppendLine(n.Delegate != null ? Signature(n.Delegate) : TypeHeader(n.Node));
                sb.AppendLine("```");
                sb.AppendLine();
                if (n.Delegate != null) AppendDocBody(sb, n.Delegate, includeSummary: true);
                else
                {
                    AppendDocBody(sb, n.Node, includeSummary: true);
                    var nestedSb = new StringBuilder();
                    var saved = _current;
                    _current = n;
                    AppendMembers(nestedSb, n, "####", "#####");
                    _current = saved;
                    sb.Append(nestedSb);
                }
            }
        }
    }

    private void AppendMemberSection(StringBuilder sb, string title, List<MemberDeclarationSyntax> members, string h2, string h3)
    {
        if (members.Count == 0) return;
        sb.AppendLine($"{h2} {title}");
        sb.AppendLine();
        // Overloads share one heading (and one anchor), in declaration order.
        var groups = new List<(string Name, List<MemberDeclarationSyntax> Members)>();
        foreach (var m in members)
        {
            string name = string.Join(", ", MemberNames(m));
            var g = groups.FirstOrDefault(x => x.Name == name);
            if (g.Members == null) { g = (name, new List<MemberDeclarationSyntax>()); groups.Add(g); }
            g.Members.Add(m);
        }
        foreach (var (name, list) in groups)
        {
            sb.AppendLine($"{h3} {Program.Escape(name)} [#{Anchor(name)}]");
            sb.AppendLine();
            sb.AppendLine("```csharp");
            foreach (var m in list) sb.AppendLine(Signature(m));
            sb.AppendLine("```");
            sb.AppendLine();
            // Docs: the first overload that has any; the others rarely repeat them.
            var documented = list.FirstOrDefault(HasDoc) ?? list[0];
            AppendDocBody(sb, documented, includeSummary: true);
        }
    }

    private static string Anchor(string name) => Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

    private static IEnumerable<string> MemberNames(MemberDeclarationSyntax m) => m switch
    {
        FieldDeclarationSyntax f => f.Declaration.Variables.Select(v => v.Identifier.ValueText),
        EventFieldDeclarationSyntax e => e.Declaration.Variables.Select(v => v.Identifier.ValueText),
        EventDeclarationSyntax e => new[] { e.Identifier.ValueText },
        PropertyDeclarationSyntax p => new[] { p.Identifier.ValueText },
        IndexerDeclarationSyntax => new[] { "this[]" },
        MethodDeclarationSyntax me => new[] { me.Identifier.ValueText },
        ConstructorDeclarationSyntax c => new[] { c.Identifier.ValueText },
        OperatorDeclarationSyntax o => new[] { "operator " + o.OperatorToken.ValueText },
        ConversionOperatorDeclarationSyntax c => new[] { c.ImplicitOrExplicitKeyword.ValueText + " operator " + c.Type },
        BaseTypeDeclarationSyntax t => new[] { t.Identifier.ValueText },
        DelegateDeclarationSyntax d => new[] { d.Identifier.ValueText },
        _ => Array.Empty<string>(),
    };

    // ---- signatures -----------------------------------------------------------------------------------

    private static string TypeHeader(BaseTypeDeclarationSyntax decl)
    {
        var sb = new StringBuilder();
        foreach (var list in decl.AttributeLists) sb.Append(list.ToString().Trim()).Append('\n');
        sb.Append(decl.Modifiers.ToString().Trim()).Append(' ');
        switch (decl)
        {
            case RecordDeclarationSyntax r:
                sb.Append("record ");
                if (!r.ClassOrStructKeyword.IsKind(SyntaxKind.None)) sb.Append(r.ClassOrStructKeyword.ValueText).Append(' ');
                break;
            case ClassDeclarationSyntax: sb.Append("class "); break;
            case StructDeclarationSyntax: sb.Append("struct "); break;
            case InterfaceDeclarationSyntax: sb.Append("interface "); break;
            case EnumDeclarationSyntax: sb.Append("enum "); break;
        }
        sb.Append(decl.Identifier.ValueText);
        if (decl is TypeDeclarationSyntax td)
        {
            if (td.TypeParameterList != null) sb.Append(td.TypeParameterList.ToString());
            if (td.BaseList != null) sb.Append(' ').Append(td.BaseList.ToString().Trim());
            foreach (var c in td.ConstraintClauses) sb.Append(' ').Append(c.ToString().Trim());
        }
        else if (decl.BaseList != null) sb.Append(' ').Append(decl.BaseList.ToString().Trim());
        return Regex.Replace(sb.ToString(), @"[ \t]+", " ").Trim();
    }

    private static string Signature(MemberDeclarationSyntax m)
    {
        var lists = List(m.AttributeLists.Where(l => !l.Attributes.All(a => StrippedMemberAttributes.Contains(a.Name.ToString().Split('.').Last()))));
        SyntaxNode node = m switch
        {
            MethodDeclarationSyntax me => me.WithAttributeLists(lists).WithBody(null).WithExpressionBody(null).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
            ConstructorDeclarationSyntax c => c.WithAttributeLists(lists).WithBody(null).WithExpressionBody(null).WithInitializer(null).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
            OperatorDeclarationSyntax o => o.WithAttributeLists(lists).WithBody(null).WithExpressionBody(null).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
            ConversionOperatorDeclarationSyntax c => c.WithAttributeLists(lists).WithBody(null).WithExpressionBody(null).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
            PropertyDeclarationSyntax p => CleanProperty(p).WithAttributeLists(lists),
            IndexerDeclarationSyntax i => i.WithAttributeLists(lists),
            EventDeclarationSyntax e => e.WithAttributeLists(lists).WithAccessorList(null).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
            DelegateDeclarationSyntax d => d.WithAttributeLists(lists),
            _ => m.WithAttributeLists(lists),
        };
        string text = node.WithoutLeadingTrivia().WithoutTrailingTrivia().NormalizeWhitespace().ToFullString().Trim();
        // NormalizeWhitespace puts each attribute list on its own line already; collapse stray blank lines.
        return Regex.Replace(text, @"\n\s*\n", "\n");
    }

    private static readonly HashSet<string> StrippedMemberAttributes = new() { "Tooltip", "Header", "SerializeField", "Space", "TextArea" };

    private static PropertyDeclarationSyntax CleanProperty(PropertyDeclarationSyntax p)
    {
        if (p.ExpressionBody != null || p.AccessorList == null)
        {
            var get = AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken));
            return p.WithExpressionBody(null).WithSemicolonToken(default).WithAccessorList(AccessorList(SingletonList(get)));
        }
        var accessors = new List<AccessorDeclarationSyntax>();
        foreach (var a in p.AccessorList.Accessors)
        {
            if (a.Modifiers.Any(SyntaxKind.PrivateKeyword) || a.Modifiers.Any(SyntaxKind.InternalKeyword)) continue;
            accessors.Add(a.WithBody(null).WithExpressionBody(null).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)));
        }
        return p.WithAccessorList(AccessorList(List(accessors)));
    }

    // ---- documentation ------------------------------------------------------------------------------

    private static DocumentationCommentTriviaSyntax? Doc(SyntaxNode node)
    {
        foreach (var trivia in node.GetLeadingTrivia())
        {
            if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                if (trivia.GetStructure() is DocumentationCommentTriviaSyntax d) return d;
        }
        return null;
    }

    private static bool HasDoc(SyntaxNode node) => Doc(node) != null || Tooltip(node) != null;

    private static string? Tooltip(SyntaxNode node)
    {
        if (node is not MemberDeclarationSyntax m) return null;
        foreach (var list in m.AttributeLists)
            foreach (var a in list.Attributes)
                if (a.Name.ToString().Split('.').Last() == "Tooltip" && a.ArgumentList?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax lit)
                    return lit.Token.ValueText;
        return null;
    }

    private static IEnumerable<XmlElementSyntax> Elements(DocumentationCommentTriviaSyntax doc, string name) =>
        doc.Content.OfType<XmlElementSyntax>().Where(e => e.StartTag.Name.LocalName.ValueText == name);

    /// <summary>Plain-text first sentence of the summary (for frontmatter and index tables).</summary>
    public string Description(SyntaxNode node)
    {
        string text = "";
        var doc = Doc(node);
        if (doc != null)
        {
            var summary = Elements(doc, "summary").FirstOrDefault();
            if (summary != null) text = RenderInline(summary.Content, plain: true);
            else text = RenderInline(doc.Content.Where(c => c is XmlTextSyntax), plain: true);
        }
        if (text.Length == 0) text = Tooltip(node) ?? "";
        text = Regex.Replace(text, @"\s+", " ").Trim();
        int end = text.IndexOf(". ", StringComparison.Ordinal);
        if (end > 0) text = text[..(end + 1)];
        if (text.Length > 240) text = text[..237] + "...";
        return text;
    }

    private string InlineDoc(SyntaxNode node)
    {
        var doc = Doc(node);
        if (doc == null) return Tooltip(node) is { } tip ? Program.Escape(tip) : "";
        var summary = Elements(doc, "summary").FirstOrDefault();
        string text = summary != null ? RenderInline(summary.Content, plain: false) : RenderInline(doc.Content.Where(c => c is XmlTextSyntax), plain: false);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private void AppendDocBody(StringBuilder sb, SyntaxNode node, bool includeSummary)
    {
        var doc = Doc(node);
        if (doc == null)
        {
            if (Tooltip(node) is { } tip) { sb.AppendLine(Program.Escape(tip)); sb.AppendLine(); }
            return;
        }
        var summary = Elements(doc, "summary").FirstOrDefault();
        if (includeSummary)
        {
            string body = summary != null ? RenderBlock(summary.Content) : RenderBlock(doc.Content.Where(IsLooseSummaryContent));
            if (body.Length > 0) { sb.AppendLine(body); sb.AppendLine(); }
        }
        foreach (var remarks in Elements(doc, "remarks"))
        {
            string body = RenderBlock(remarks.Content);
            if (body.Length > 0) { sb.AppendLine(body); sb.AppendLine(); }
        }
        foreach (var value in Elements(doc, "value"))
        {
            string body = RenderBlock(value.Content);
            if (body.Length > 0) { sb.AppendLine("**Value:** " + body); sb.AppendLine(); }
        }
        var typeParams = Elements(doc, "typeparam").ToList();
        var parameters = Elements(doc, "param").ToList();
        if (typeParams.Count + parameters.Count > 0)
        {
            sb.AppendLine("| Parameter | Description |");
            sb.AppendLine("| --- | --- |");
            foreach (var p in typeParams.Concat(parameters))
            {
                string name = p.StartTag.Attributes.OfType<XmlNameAttributeSyntax>().FirstOrDefault()?.Identifier.Identifier.ValueText ?? "";
                sb.AppendLine($"| `{name}` | {Regex.Replace(RenderInline(p.Content, plain: false), @"\s+", " ").Trim()} |");
            }
            sb.AppendLine();
        }
        foreach (var returns in Elements(doc, "returns"))
        {
            string body = Regex.Replace(RenderInline(returns.Content, plain: false), @"\s+", " ").Trim();
            if (body.Length > 0) { sb.AppendLine("**Returns:** " + body); sb.AppendLine(); }
        }
        foreach (var ex in Elements(doc, "exception"))
        {
            string cref = ex.StartTag.Attributes.OfType<XmlCrefAttributeSyntax>().FirstOrDefault()?.Cref.ToString() ?? "";
            string body = Regex.Replace(RenderInline(ex.Content, plain: false), @"\s+", " ").Trim();
            sb.AppendLine($"**Throws** `{cref}`: {body}"); sb.AppendLine();
        }
        foreach (var example in Elements(doc, "example"))
        {
            string body = RenderBlock(example.Content);
            if (body.Length > 0) { sb.AppendLine("**Example**"); sb.AppendLine(); sb.AppendLine(body); sb.AppendLine(); }
        }
    }

    /// <summary>Content of a doc comment written without a summary tag (plain `///` lines, as in the SpacetimeDB module).</summary>
    private static bool IsLooseSummaryContent(XmlNodeSyntax node)
    {
        if (node is XmlTextSyntax) return true;
        if (node is XmlElementSyntax e)
            return e.StartTag.Name.LocalName.ValueText is not ("param" or "typeparam" or "returns" or "remarks" or "exception" or "example" or "value");
        return node is XmlEmptyElementSyntax;
    }

    /// <summary>Block-level rendering: paragraphs, lists and code blocks become Markdown blocks.</summary>
    private string RenderBlock(IEnumerable<XmlNodeSyntax> nodes)
    {
        var sb = new StringBuilder();
        var inline = new List<XmlNodeSyntax>();
        void Flush()
        {
            if (inline.Count == 0) return;
            string text = Regex.Replace(RenderInline(inline, plain: false), @"\s+", " ").Trim();
            if (text.Length > 0) sb.Append(text).Append("\n\n");
            inline.Clear();
        }
        foreach (var node in nodes)
        {
            if (node is XmlElementSyntax e)
            {
                string name = e.StartTag.Name.LocalName.ValueText;
                switch (name)
                {
                    case "para":
                        Flush();
                        sb.Append(RenderBlock(e.Content)).Append("\n\n");
                        continue;
                    case "code":
                        Flush();
                        sb.Append("```csharp\n").Append(Dedent(RawText(e.Content))).Append("\n```\n\n");
                        continue;
                    case "list":
                        Flush();
                        string type = e.StartTag.Attributes.OfType<XmlTextAttributeSyntax>().FirstOrDefault(a => a.Name.LocalName.ValueText == "type")?.TextTokens.ToString() ?? "bullet";
                        int n = 0;
                        foreach (var item in e.Content.OfType<XmlElementSyntax>().Where(i => i.StartTag.Name.LocalName.ValueText is "item" or "listheader"))
                        {
                            var term = item.Content.OfType<XmlElementSyntax>().FirstOrDefault(x => x.StartTag.Name.LocalName.ValueText == "term");
                            var desc = item.Content.OfType<XmlElementSyntax>().FirstOrDefault(x => x.StartTag.Name.LocalName.ValueText == "description");
                            string text = desc != null || term != null
                                ? (term != null ? "**" + Regex.Replace(RenderInline(term.Content, false), @"\s+", " ").Trim() + "** " : "") + (desc != null ? Regex.Replace(RenderInline(desc.Content, false), @"\s+", " ").Trim() : "")
                                : Regex.Replace(RenderInline(item.Content, false), @"\s+", " ").Trim();
                            sb.Append(type == "number" ? $"{++n}. " : "- ").Append(text).Append('\n');
                        }
                        sb.Append('\n');
                        continue;
                }
            }
            inline.Add(node);
        }
        Flush();
        return sb.ToString().TrimEnd();
    }

    private static string RawText(IEnumerable<XmlNodeSyntax> nodes)
    {
        var sb = new StringBuilder();
        foreach (var node in nodes)
        {
            if (node is XmlTextSyntax t)
                foreach (var tok in t.TextTokens) sb.Append(tok.IsKind(SyntaxKind.XmlTextLiteralNewLineToken) ? "\n" : tok.ValueText);
            else sb.Append(node.ToString());
        }
        return sb.ToString().Trim('\n');
    }

    private static string Dedent(string text)
    {
        var lines = text.Split('\n');
        int indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart())).TrimEnd();
    }

    private string RenderInline(IEnumerable<XmlNodeSyntax> nodes, bool plain)
    {
        var sb = new StringBuilder();
        foreach (var node in nodes)
        {
            switch (node)
            {
                case XmlTextSyntax t:
                    foreach (var tok in t.TextTokens)
                    {
                        if (tok.IsKind(SyntaxKind.XmlTextLiteralNewLineToken)) sb.Append(' ');
                        else sb.Append(plain ? tok.ValueText : Program.Escape(tok.ValueText));
                    }
                    break;
                case XmlCDataSectionSyntax c:
                    sb.Append(plain ? c.TextTokens.ToString() : Program.Escape(c.TextTokens.ToString()));
                    break;
                case XmlEmptyElementSyntax e:
                    sb.Append(RenderEmpty(e, plain));
                    break;
                case XmlElementSyntax e:
                    string name = e.StartTag.Name.LocalName.ValueText;
                    string inner = RenderInline(e.Content, plain || name == "c");
                    switch (name)
                    {
                        case "c": sb.Append(plain ? inner : Code(inner)); break;
                        case "code": sb.Append(plain ? inner : Code(RawText(e.Content).Trim())); break;
                        case "see": case "seealso": sb.Append(RenderSee(e.StartTag.Attributes, inner, plain)); break;
                        case "i": case "em": sb.Append(plain ? inner : "*" + inner + "*"); break;
                        case "b": case "strong": sb.Append(plain ? inner : "**" + inner + "**"); break;
                        case "para": sb.Append(' ').Append(inner).Append(' '); break;
                        case "list":
                            // Inline context (a table cell): flatten the items.
                            foreach (var item in e.Content.OfType<XmlElementSyntax>())
                                sb.Append(' ').Append(Regex.Replace(RenderInline(item.Content, plain), @"\s+", " ").Trim()).Append(';');
                            break;
                        default: sb.Append(inner); break;
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Code(string text) => text.Contains('`') ? "`` " + text + " ``" : "`" + text + "`";

    private string RenderEmpty(XmlEmptyElementSyntax e, bool plain)
    {
        string name = e.Name.LocalName.ValueText;
        switch (name)
        {
            case "see":
            case "seealso":
                return RenderSee(e.Attributes, "", plain);
            case "paramref":
            case "typeparamref":
                string id = e.Attributes.OfType<XmlNameAttributeSyntax>().FirstOrDefault()?.Identifier.Identifier.ValueText ?? "";
                return plain ? id : Code(id);
            case "br":
                return " ";
            case "inheritdoc":
                return "";
        }
        return "";
    }

    private string RenderSee(SyntaxList<XmlAttributeSyntax> attributes, string inner, bool plain)
    {
        foreach (var a in attributes)
        {
            if (a is XmlCrefAttributeSyntax cref) return RenderCref(cref.Cref.ToString(), inner, plain);
            if (a is XmlTextAttributeSyntax text)
            {
                string value = text.TextTokens.ToString();
                if (text.Name.LocalName.ValueText == "langword") return plain ? value : Code(value);
                if (text.Name.LocalName.ValueText == "href") return plain ? (inner.Length > 0 ? inner : value) : $"[{(inner.Length > 0 ? inner : value)}]({value})";
            }
        }
        return inner;
    }

    private string RenderCref(string cref, string inner, bool plain)
    {
        // "NetworkBehaviour.WriteHandoverState", "PredictedBehaviour{TInput}", "int.MaxValue", "Simulate", "Foo(int)"
        string clean = Regex.Replace(cref, @"\(.*\)$", "");
        clean = Regex.Replace(clean, @"\{[^}]*\}", "");
        string display = inner.Length > 0 ? inner : Regex.Replace(cref, @"\(.*\)$", "").Replace('{', '<').Replace('}', '>');
        if (plain) return display;
        var parts = clean.Split('.');

        if (_index.TryGetValue(parts[0], out var type))
        {
            string url = "/docs/reference/" + GroupSlug(type.Group) + "/" + Program.Kebab(type.Name);
            if (parts.Length == 1) return Link(display, url);
            // Nested type or member of a known type.
            if (_index.ContainsKey(parts[0] + "." + parts[1]) && parts.Length == 2) return Link(display, url + "#" + parts[1].ToLowerInvariant());
            return Link(display, url + "#" + Anchor(parts[^1]));
        }
        if (parts.Length == 1 && _currentMembers.Contains(parts[0]))
            return Link(display, "#" + Anchor(parts[0]));
        return Code(display);
    }

    private static string Link(string display, string url) => $"[`{display}`]({url})";

    private static string GroupSlug(string folder)
    {
        foreach (var f in typeof(Program).GetField("Groups", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null) as (string Folder, string Title, string Slug, string Blurb)[] ?? Array.Empty<(string, string, string, string)>())
            if (f.Folder == folder) return f.Slug;
        return Program.Kebab(folder);
    }

    private static string Yaml(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
