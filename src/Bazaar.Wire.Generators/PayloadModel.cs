using Microsoft.CodeAnalysis;

namespace Bazaar.Wire.Generators;

/// <summary>One <c>[WirePayload]</c> record and the message types it travels under.</summary>
internal sealed class PayloadInfo
{
    public PayloadInfo(INamedTypeSymbol type, IReadOnlyList<string> messageTypes)
    {
        Type = type;
        MessageTypes = messageTypes;
    }

    public INamedTypeSymbol Type { get; }

    public IReadOnlyList<string> MessageTypes { get; }
}

internal static class PayloadModel
{
    /// <summary>Finds every payload record in this assembly and in referenced Bazaar assemblies, in a stable order.</summary>
    public static List<PayloadInfo> Discover(Compilation compilation, List<DiagnosticInfo> diagnostics, System.Threading.CancellationToken ct)
    {
        var marker = compilation.GetTypeByMetadataName(WireCodecGenerator.PayloadAttributeMetadataName);
        var result = new List<PayloadInfo>();
        if (marker is null)
        {
            diagnostics.Add(new DiagnosticInfo(Descriptors.NoContracts, WireCodecGenerator.PayloadAttributeMetadataName));
            return result;
        }

        var assemblies = new List<IAssemblySymbol> { compilation.Assembly };
        foreach (var reference in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (reference.Name.StartsWith("Bazaar.", StringComparison.Ordinal)) assemblies.Add(reference);
        }

        foreach (var assembly in assemblies) Walk(assembly.GlobalNamespace, marker, result, ct);

        result.Sort((a, b) => string.CompareOrdinal(a.Type.ToDisplayString(), b.Type.ToDisplayString()));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var payload in result)
        {
            foreach (var messageType in payload.MessageTypes)
            {
                if (!seen.Add(messageType)) diagnostics.Add(new DiagnosticInfo(Descriptors.DuplicateMessageType, messageType));
            }
        }

        return result;
    }

    private static void Walk(INamespaceSymbol ns, INamedTypeSymbol marker, List<PayloadInfo> into, System.Threading.CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var type in ns.GetTypeMembers()) Visit(type, marker, into);
        foreach (var child in ns.GetNamespaceMembers()) Walk(child, marker, into, ct);
    }

    private static void Visit(INamedTypeSymbol type, INamedTypeSymbol marker, List<PayloadInfo> into)
    {
        if (type.DeclaredAccessibility == Accessibility.Public)
        {
            var messageTypes = new List<string>();
            var marked = false;
            foreach (var attribute in type.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, marker)) continue;
                marked = true;
                if (attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is string messageType)
                {
                    messageTypes.Add(messageType);
                }
            }

            if (marked) into.Add(new PayloadInfo(type, messageTypes));
        }

        foreach (var nested in type.GetTypeMembers()) Visit(nested, marker, into);
    }
}
