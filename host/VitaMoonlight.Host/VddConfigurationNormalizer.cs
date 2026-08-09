using System.Xml.Linq;

namespace VitaMoonlight.Host;

/// <summary>
/// Normalizes only the virtual-display settings that Vita Moonlight owns.
/// User-selected GPU, cursor, EDID, color, and resolution settings are left
/// intact. Keeping this transformation pure makes repair behavior
/// deterministic and independently testable without touching a live driver.
/// </summary>
internal static class VddConfigurationNormalizer
{
    internal static string NormalizeForVitaRuntime(string configuration)
    {
        var document = XDocument.Parse(
            configuration,
            LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException(
            "The virtual display configuration has no root element.");

        var monitors = ConsolidateContainer(root, "monitors");
        SetExactScalar(monitors, "count", "1");

        var options = ConsolidateContainer(root, "options");
        SetExactScalar(options, "logging", "false");
        SetExactScalar(options, "debuglogging", "false");

        return document.ToString(SaveOptions.None);
    }

    internal static bool IsNormalizedForVitaRuntime(string configuration)
    {
        try
        {
            var root = XDocument.Parse(configuration).Root;
            if (root is null) return false;

            var monitors = FindChildren(root, "monitors").ToArray();
            var options = FindChildren(root, "options").ToArray();
            return monitors.Length == 1 &&
                   HasExactScalar(monitors[0], "count", "1") &&
                   options.Length == 1 &&
                   HasExactScalar(options[0], "logging", "false") &&
                   HasExactScalar(
                       options[0],
                       "debuglogging",
                       "false");
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    private static XElement ConsolidateContainer(
        XElement root,
        string canonicalName)
    {
        var matches = FindChildren(root, canonicalName).ToArray();
        if (matches.Length == 0)
        {
            var created = new XElement(canonicalName);
            root.Add(created);
            return created;
        }

        var primary = matches[0];
        primary.Name = canonicalName;
        foreach (var duplicate in matches.Skip(1))
        {
            // Preserve unrelated settings and comments from malformed or old
            // multi-container files before removing the ambiguous container.
            foreach (var node in duplicate.Nodes().ToArray())
            {
                node.Remove();
                primary.Add(node);
            }
            duplicate.Remove();
        }
        return primary;
    }

    private static void SetExactScalar(
        XElement container,
        string canonicalName,
        string value)
    {
        var matches = FindChildren(container, canonicalName).ToArray();
        XElement scalar;
        if (matches.Length == 0)
        {
            scalar = new XElement(canonicalName, value);
            container.AddFirst(scalar);
        }
        else
        {
            scalar = matches[0];
            scalar.Name = canonicalName;
            scalar.Value = value;
            foreach (var duplicate in matches.Skip(1)) duplicate.Remove();
        }
    }

    private static bool HasExactScalar(
        XElement container,
        string canonicalName,
        string value)
    {
        var matches = FindChildren(container, canonicalName).ToArray();
        return matches.Length == 1 &&
               matches[0].Name == canonicalName &&
               string.Equals(
                   matches[0].Value.Trim(),
                   value,
                   StringComparison.Ordinal);
    }

    private static IEnumerable<XElement> FindChildren(
        XElement parent,
        string localName) =>
        parent.Elements().Where(element =>
            string.Equals(
                element.Name.LocalName,
                localName,
                StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(element.Name.NamespaceName));
}
