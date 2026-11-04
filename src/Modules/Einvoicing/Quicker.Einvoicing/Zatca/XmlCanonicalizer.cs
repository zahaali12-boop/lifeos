using System.Text;
using System.Xml.Linq;

namespace Quicker.Einvoicing.Zatca;

/// <summary>
/// Canonical XML (the W3C C14N form, which for a complete document is the same in versions 1.0 and 1.1): no XML
/// declaration, UTF-8, namespace declarations then attributes in canonical order, empty elements written open and
/// closed, text escaped the canonical way, comments dropped. ZATCA hashes the invoice in this form after the signature,
/// the extensions and the QR reference are taken out; the scheme recomputes it from the bytes it receives, so the form
/// here must be exact, and the tests hold it to known vectors.
/// </summary>
public static class XmlCanonicalizer
{
    public static byte[] Canonicalize(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var builder = new StringBuilder();
        if (document.Root is { } root)
        {
            Write(root, builder, new Dictionary<string, string>(StringComparer.Ordinal) { [string.Empty] = string.Empty });
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public static string CanonicalizeToString(XDocument document) => Encoding.UTF8.GetString(Canonicalize(document));

    private static void Write(XElement element, StringBuilder builder, Dictionary<string, string> inScope)
    {
        builder.Append('<').Append(QualifiedName(element.Name, element));
        var scope = new Dictionary<string, string>(inScope, StringComparer.Ordinal);
        var declarations = new List<(string Prefix, string Uri)>();
        var attributes = new List<XAttribute>();
        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
            {
                var prefix = attribute.Name.LocalName == "xmlns" ? string.Empty : attribute.Name.LocalName;
                if (!(inScope.TryGetValue(prefix, out var current) && string.Equals(current, attribute.Value, StringComparison.Ordinal)))
                {
                    declarations.Add((prefix, attribute.Value));
                }

                scope[prefix] = attribute.Value;
            }
            else
            {
                attributes.Add(attribute);
            }
        }

        foreach (var (prefix, uri) in declarations.OrderBy(static d => d.Prefix, StringComparer.Ordinal))
        {
            builder.Append(' ').Append(prefix.Length == 0 ? "xmlns" : "xmlns:" + prefix).Append("=\"").Append(EscapeAttribute(uri)).Append('"');
        }

        foreach (var attribute in attributes.OrderBy(static a => a.Name.NamespaceName, StringComparer.Ordinal).ThenBy(static a => a.Name.LocalName, StringComparer.Ordinal))
        {
            builder.Append(' ').Append(QualifiedName(attribute.Name, element)).Append("=\"").Append(EscapeAttribute(attribute.Value)).Append('"');
        }

        builder.Append('>');
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XElement child:
                    Write(child, builder, scope);
                    break;
                case XText text:
                    builder.Append(EscapeText(text.Value));
                    break;
                default:
                    break; // comments and processing instructions are not part of the canonical form used here
            }
        }

        builder.Append("</").Append(QualifiedName(element.Name, element)).Append('>');
    }

    private static string QualifiedName(XName name, XElement context)
    {
        if (name.NamespaceName.Length == 0)
        {
            return name.LocalName;
        }

        var prefix = context.GetPrefixOfNamespace(name.Namespace);
        return string.IsNullOrEmpty(prefix) ? name.LocalName : prefix + ":" + name.LocalName;
    }

    private static string EscapeText(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal).Replace("\r", "&#xD;", StringComparison.Ordinal);

    private static string EscapeAttribute(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal).Replace("\t", "&#x9;", StringComparison.Ordinal).Replace("\n", "&#xA;", StringComparison.Ordinal).Replace("\r", "&#xD;", StringComparison.Ordinal);
}
