using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Connascence.CSharp
{
    /// <summary>A C# file taking part in the analysis.</summary>
    public sealed record SourceFile
    {
        public required SyntaxTree Tree { get; init; }
        public required string RelativePath { get; init; }
        public required string Project { get; init; }
    }

    /// <summary>A call to a method or constructor declared in the analysed source.</summary>
    public sealed record CallSite
    {
        public required SyntaxNode Node { get; init; }
        public required ArgumentListSyntax Args { get; init; }
        public required IMethodSymbol Target { get; init; }
    }

    /// <summary>Everything a detector needs: syntax, semantics, settings and helpers to describe locations.</summary>
    public sealed class CSharpContext
    {
        readonly IReadOnlyDictionary<SyntaxTree, SourceFile> _files;
        readonly Dictionary<SyntaxTree, SemanticModel> _models = new Dictionary<SyntaxTree, SemanticModel>();
        Dictionary<IMethodSymbol, List<CallSite>>? _calls;

        public CSharpContext(Compilation compilation, IEnumerable<SourceFile> files, Settings? settings)
        {
            Compilation = compilation;
            Files = files.ToList();
            Settings = settings ?? new Settings();
            _files = Files.ToDictionary(f => f.Tree);
        }

        public Compilation Compilation { get; }
        public IReadOnlyList<SourceFile> Files { get; }
        public Settings Settings { get; }
        public List<string> Warnings { get; } = new List<string>();

        public IEnumerable<SyntaxNode> Roots => Files.Select(f => f.Tree.GetRoot());

        public SemanticModel Model(SyntaxTree tree)
        {
            if (!_models.TryGetValue(tree, out var m))
            {
                m = Compilation.GetSemanticModel(tree);
                _models[tree] = m;
            }
            return m;
        }

        public SourceFile? FileOf(SyntaxTree? tree)
        {
            return tree != null && _files.TryGetValue(tree, out var f) ? f : null;
        }

        /// <summary>Resolves a symbol even when overload resolution fails because references are missing.</summary>
        public ISymbol? SymbolOf(SyntaxNode? node)
        {
            if (node == null || FileOf(node.SyntaxTree) == null) return null;
            var info = Model(node.SyntaxTree).GetSymbolInfo(node);
            return info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        }

        public ITypeSymbol? TypeOf(ExpressionSyntax? expr)
        {
            if (expr == null || FileOf(expr.SyntaxTree) == null) return null;
            return Model(expr.SyntaxTree).GetTypeInfo(expr).Type;
        }

        public static bool IsInSource(ISymbol? s) => s != null && s.Locations.Any(l => l.IsInSource);

        /// <summary>All calls to source-declared methods and constructors, keyed by the original definition.</summary>
        public IReadOnlyDictionary<IMethodSymbol, List<CallSite>> Calls
        {
            get
            {
                if (_calls != null) return _calls;
                _calls = new Dictionary<IMethodSymbol, List<CallSite>>(SymbolEqualityComparer.Default);
                foreach (var root in Roots)
                {
                    foreach (var node in root.DescendantNodes())
                    {
                        ArgumentListSyntax? args = null;
                        if (node is InvocationExpressionSyntax inv) args = inv.ArgumentList;
                        else if (node is BaseObjectCreationExpressionSyntax oc) args = oc.ArgumentList;
                        else if (node is ConstructorInitializerSyntax ci) args = ci.ArgumentList;
                        else continue;
                        if (args == null) continue;

                        var sym = SymbolOf(node) as IMethodSymbol;
                        if (sym == null) continue;
                        if (sym.ReducedFrom != null) sym = sym.ReducedFrom;
                        sym = sym.OriginalDefinition;
                        if (!IsInSource(sym)) continue;

                        if (!_calls.TryGetValue(sym, out var list)) { list = new List<CallSite>(); _calls[sym] = list; }
                        list.Add(new CallSite { Node = node, Args = args, Target = sym });
                    }
                }
                return _calls;
            }
        }

        public IReadOnlyList<CallSite> CallsTo(IMethodSymbol? m)
        {
            return m != null && Calls.TryGetValue(m.OriginalDefinition, out var list) ? list : new List<CallSite>();
        }

        // ---------- Describing locations ----------

        public CodeLocation Locate(SyntaxNode node, string role)
        {
            return Locate(node.SyntaxTree, node.GetLocation().SourceSpan, node, role);
        }

        public CodeLocation Locate(SyntaxToken token, string role)
        {
            return Locate(token.SyntaxTree!, token.Span, token.Parent, role);
        }

        CodeLocation Locate(SyntaxTree tree, Microsoft.CodeAnalysis.Text.TextSpan span, SyntaxNode? node, string role)
        {
            var file = FileOf(tree);
            var pos = tree.GetLineSpan(span).StartLinePosition;
            var text = tree.GetText();
            var line = text.Lines[pos.Line].ToString().Trim();
            if (line.Length > 160) line = line.Substring(0, 157) + "...";
            return new CodeLocation
            {
                File = file != null ? file.RelativePath : tree.FilePath,
                Project = file != null ? file.Project : "",
                Line = pos.Line + 1,
                Column = pos.Character + 1,
                Type = TypeNameOf(node),
                Member = MemberNameOf(node),
                Snippet = line,
                Role = role,
            };
        }

        public static string? TypeNameOf(SyntaxNode? node)
        {
            var names = node?.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Select(t => t.Identifier.Text).Reverse().ToList();
            if (names == null || names.Count == 0)
                return node != null && node.AncestorsAndSelf().OfType<GlobalStatementSyntax>().Any() ? "<Program>" : null;
            return string.Join(".", names.ToArray());
        }

        public static string? MemberNameOf(SyntaxNode? node)
        {
            if (node == null) return null;
            foreach (var a in node.AncestorsAndSelf())
            {
                switch (a)
                {
                    case MethodDeclarationSyntax m: return m.Identifier.Text;
                    case ConstructorDeclarationSyntax _: return ".ctor";
                    case DestructorDeclarationSyntax _: return ".dtor";
                    case PropertyDeclarationSyntax p: return p.Identifier.Text;
                    case IndexerDeclarationSyntax _: return "this[]";
                    case EventDeclarationSyntax e: return e.Identifier.Text;
                    case OperatorDeclarationSyntax o: return "operator " + o.OperatorToken.Text;
                    case ConversionOperatorDeclarationSyntax _: return "operator conversion";
                    case FieldDeclarationSyntax f: return f.Declaration.Variables.First().Identifier.Text;
                    case EventFieldDeclarationSyntax ef: return ef.Declaration.Variables.First().Identifier.Text;
                    case EnumMemberDeclarationSyntax em: return em.Identifier.Text;
                    case GlobalStatementSyntax _: return "<Main>";
                    case BaseTypeDeclarationSyntax _: return null;
                }
            }
            return null;
        }

        /// <summary>Short display name like "OrderService.Ship".</summary>
        public static string Describe(CodeLocation l)
        {
            if (l.Type == null) return l.File;
            return l.Member == null ? l.Type : l.Type + "." + l.Member;
        }
    }

    public interface ICSharpDetector
    {
        IEnumerable<Finding> Detect(CSharpContext ctx);
    }
}
