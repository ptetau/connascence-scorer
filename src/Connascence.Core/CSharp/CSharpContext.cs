using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
    /// <remarks>Equality is by identity: a context stands for one compilation, and its caches aren't values.</remarks>
    public sealed record CSharpContext
    {
        public bool Equals(CSharpContext? other) => ReferenceEquals(this, other);

        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

        readonly ImmutableDictionary<SyntaxTree, SourceFile> _files;
        readonly ImmutableDictionary<SyntaxTree, SemanticModel> _models;
        readonly Lazy<CallIndex> _calls;

        public CSharpContext(Compilation compilation, IEnumerable<SourceFile> files, Settings? settings)
        {
            Compilation = compilation;
            Files = files.ToImmutableArray();
            Settings = settings ?? new Settings();
            _files = Files.GroupBy(f => f.Tree).ToImmutableDictionary(g => g.Key, g => g.First());
            _models = Files.GroupBy(f => f.Tree).ToImmutableDictionary(g => g.Key, g => compilation.GetSemanticModel(g.Key));
            _calls = new Lazy<CallIndex>(FindCalls);
        }

        public Compilation Compilation { get; }
        public ImmutableArray<SourceFile> Files { get; }
        public Settings Settings { get; }

        public IEnumerable<SyntaxNode> Roots => Files.Select(f => f.Tree.GetRoot());

        /// <summary>The semantic model for a tree: shared for the analysed files, made fresh for any other tree.</summary>
        public SemanticModel Model(SyntaxTree tree) =>
            _models.TryGetValue(tree, out var m) ? m : Compilation.GetSemanticModel(tree);

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

        /// <summary>All calls to source-declared methods and constructors, keyed by the original definition. Found on first use.</summary>
        /// <remarks>In the order each method is first called, so findings built from it come out in a stable order.</remarks>
        public ImmutableArray<(IMethodSymbol Method, ImmutableArray<CallSite> Sites)> Calls => _calls.Value.InOrder;

        /// <summary>The calls to each method, in first-seen order, and the same calls looked up by method.</summary>
        sealed record CallIndex(
            ImmutableArray<(IMethodSymbol Method, ImmutableArray<CallSite> Sites)> InOrder,
            ImmutableDictionary<IMethodSymbol, ImmutableArray<CallSite>> ByMethod);

        CallIndex FindCalls()
        {
            var inOrder = Roots.SelectMany(root => root.DescendantNodes())
                .Select(CallAt)
                .OfType<CallSite>()
                .GroupBy(c => c.Target, SameMethod)
                .Select(g => (Method: g.Key, Sites: g.ToImmutableArray()))
                .ToImmutableArray();
            return new CallIndex(inOrder, inOrder.GroupBy(x => x.Method, SameMethod).ToImmutableDictionary(g => g.Key, g => g.First().Sites, SameMethod));
        }

        static readonly IEqualityComparer<IMethodSymbol> SameMethod = SymbolEqualityComparer.Default;

        /// <summary>The call this node makes to a method or constructor declared in the analysed source, if it is one.</summary>
        CallSite? CallAt(SyntaxNode node)
        {
            var args = node switch
            {
                InvocationExpressionSyntax inv => inv.ArgumentList,
                BaseObjectCreationExpressionSyntax oc => oc.ArgumentList,
                ConstructorInitializerSyntax ci => ci.ArgumentList,
                _ => null,
            };
            if (args == null || !(SymbolOf(node) is IMethodSymbol sym)) return null;
            var target = (sym.ReducedFrom ?? sym).OriginalDefinition;
            return IsInSource(target) ? new CallSite { Node = node, Args = args, Target = target } : null;
        }

        public IReadOnlyList<CallSite> CallsTo(IMethodSymbol? m)
        {
            return m != null && _calls.Value.ByMethod.TryGetValue(m.OriginalDefinition, out var list) ? list : ImmutableArray<CallSite>.Empty;
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
            var full = text.Lines[pos.Line].ToString().Trim();
            var line = full.Length > 160 ? full.Substring(0, 157) + "..." : full;
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
            var names = node?.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Select(t => t.Identifier.Text).Reverse().ToImmutableArray();
            if (names == null || names.Value.Length == 0)
                return node != null && node.AncestorsAndSelf().OfType<GlobalStatementSyntax>().Any() ? "<Program>" : null;
            return string.Join(".", names.Value);
        }

        public static string? MemberNameOf(SyntaxNode? node) =>
            node?.AncestorsAndSelf().Select(MemberStep).FirstOrDefault(step => step.Found).Name;

        /// <summary>The member a node names, if it is a member; or found with no name, if it is a type (stop looking there).</summary>
        static (bool Found, string? Name) MemberStep(SyntaxNode a) => a switch
        {
            MethodDeclarationSyntax m => (true, m.Identifier.Text),
            ConstructorDeclarationSyntax _ => (true, ".ctor"),
            DestructorDeclarationSyntax _ => (true, ".dtor"),
            PropertyDeclarationSyntax p => (true, p.Identifier.Text),
            IndexerDeclarationSyntax _ => (true, "this[]"),
            EventDeclarationSyntax e => (true, e.Identifier.Text),
            OperatorDeclarationSyntax o => (true, "operator " + o.OperatorToken.Text),
            ConversionOperatorDeclarationSyntax _ => (true, "operator conversion"),
            FieldDeclarationSyntax f => (true, f.Declaration.Variables.First().Identifier.Text),
            EventFieldDeclarationSyntax ef => (true, ef.Declaration.Variables.First().Identifier.Text),
            EnumMemberDeclarationSyntax em => (true, em.Identifier.Text),
            GlobalStatementSyntax _ => (true, "<Main>"),
            BaseTypeDeclarationSyntax _ => (true, null),
            _ => (false, null),
        };

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
