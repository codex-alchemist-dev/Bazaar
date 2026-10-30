using Bazaar.Graph.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bazaar.Graph.Extraction;

/// <summary>Extracts types, members, usings, base types and call sites from one C# file with Roslyn's syntax tree. No compilation or references are needed.</summary>
public static class CSharpExtractor
{
    public static FileFragment Extract(string relativePath, string sha256, string text)
    {
        var builder = new FragmentBuilder(relativePath, sha256);
        var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
        builder.AddFileNode();

        foreach (var directive in root.Usings)
        {
            var name = directive.Name?.ToString();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var target = builder.AddNode(NodeKind.Namespace, name, builder.FileId, 0, "ns:" + name);
            builder.AddEdge(builder.FileId, target, Relation.Imports, Confidence.Extracted);
        }

        VisitMembers(root.Members, builder, builder.FileId, qualifier: null);
        return builder.Build();
    }

    private static void VisitMembers(SyntaxList<MemberDeclarationSyntax> members, FragmentBuilder builder, string parentId, string? qualifier)
    {
        foreach (var member in members)
        {
            switch (member)
            {
                case NamespaceDeclarationSyntax ns:
                    VisitMembers(ns.Members, builder, parentId, qualifier);
                    break;
                case FileScopedNamespaceDeclarationSyntax fileNs:
                    VisitMembers(fileNs.Members, builder, parentId, qualifier);
                    break;
                case BaseTypeDeclarationSyntax type:
                    VisitType(type, builder, parentId, qualifier);
                    break;
                case MethodDeclarationSyntax or ConstructorDeclarationSyntax or PropertyDeclarationSyntax:
                    VisitMember(member, builder, parentId, qualifier);
                    break;
            }
        }
    }

    private static void VisitType(BaseTypeDeclarationSyntax type, FragmentBuilder builder, string parentId, string? qualifier)
    {
        var name = type.Identifier.ValueText;
        var qualified = Qualify(qualifier, name);
        var id = builder.AddSymbol(NodeKind.Type, name, qualified, type.GetLocation());
        builder.AddEdge(parentId, id, Relation.Contains, Confidence.Extracted);

        if (type.BaseList is not null)
        {
            foreach (var baseType in type.BaseList.Types)
            {
                var baseName = SimpleName(baseType.Type);
                if (baseName is not null)
                {
                    builder.AddRef(id, Relation.Inherits, baseName, byPath: false);
                }
            }
        }

        VisitMembers(MembersOf(type), builder, id, qualified);
    }

    private static SyntaxList<MemberDeclarationSyntax> MembersOf(BaseTypeDeclarationSyntax type) => type switch
    {
        TypeDeclarationSyntax t => t.Members,
        EnumDeclarationSyntax => default,
        _ => default,
    };

    private static void VisitMember(MemberDeclarationSyntax member, FragmentBuilder builder, string parentId, string? qualifier)
    {
        var name = member switch
        {
            MethodDeclarationSyntax m => m.Identifier.ValueText,
            ConstructorDeclarationSyntax c => c.Identifier.ValueText,
            PropertyDeclarationSyntax p => p.Identifier.ValueText,
            _ => string.Empty,
        };

        var qualified = Qualify(qualifier, name);
        var id = builder.AddSymbol(NodeKind.Member, name, qualified, member.GetLocation());
        builder.AddEdge(parentId, id, Relation.Contains, Confidence.Extracted);

        foreach (var invocation in member.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var callee = SimpleName(invocation.Expression);
            if (callee is not null)
            {
                builder.AddRef(id, Relation.Calls, callee, byPath: false);
            }
        }
    }

    /// <summary>Returns the last identifier of a name, so <c>Foo.Bar&lt;T&gt;</c> gives <c>Bar</c>.</summary>
    private static string? SimpleName(SyntaxNode? node) => node switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        QualifiedNameSyntax qualified => SimpleName(qualified.Right),
        MemberAccessExpressionSyntax access => SimpleName(access.Name),
        SimpleBaseTypeSyntax baseType => SimpleName(baseType.Type),
        _ => null,
    };

    private static string Qualify(string? qualifier, string name) =>
        string.IsNullOrEmpty(qualifier) ? name : qualifier + "." + name;
}
