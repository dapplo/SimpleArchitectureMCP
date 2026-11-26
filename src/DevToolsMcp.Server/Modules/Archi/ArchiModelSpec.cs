using System.ComponentModel;
using System.Text.Json.Serialization;

namespace DevToolsMcp.Server.Modules.Archi;

// The compact JSON a model sends to archi_create_model. Ids are the caller's own short keys; the builder turns them
// into valid xs:ID identifiers. Layout is automatic unless x/y are given, so a model can describe a diagram in a few
// hundred characters.

[Description("ArchiMate model. Omit views for one view with all elements.")]
public sealed class ArchiModelSpec
{
    public string? Name { get; set; }

    public string? Doc { get; set; }

    public List<ElementSpec> Elements { get; set; } = [];

    public List<RelationshipSpec>? Relationships { get; set; }

    public List<ViewSpec>? Views { get; set; }
}

public sealed class ElementSpec
{
    [Description("Your key, referenced by relationships and views")]
    public string Id { get; set; } = "";

    [Description("ArchiMate type, e.g. BusinessActor, ApplicationComponent, Node, Goal, Grouping")]
    public string Type { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Doc { get; set; }
}

public sealed class RelationshipSpec
{
    [Description("Composition, Aggregation, Assignment, Realization, Serving, Access, Influence, Triggering, Flow, Specialization, Association")]
    public string Type { get; set; } = "";

    [Description("Element id")]
    public string Source { get; set; } = "";

    [Description("Element id")]
    public string Target { get; set; } = "";

    public string? Name { get; set; }

    [Description("Access only: Read, Write, ReadWrite or Access")]
    public string? Access { get; set; }

    [Description("Association only")]
    public bool? Directed { get; set; }
}

public sealed class ViewSpec
{
    public string Name { get; set; } = "";

    [Description("Element ids, auto-laid out; omit both lists for all")]
    public List<string>? Elements { get; set; }

    [Description("Nested or positioned nodes")]
    public List<NodeSpec>? Nodes { get; set; }

    [Description("Draw relationships between shown elements (default true)")]
    public bool? Connections { get; set; }
}

public sealed class NodeSpec
{
    [Description("Element id")]
    public string Ref { get; set; } = "";

    [Description("Relative to parent; omit for auto layout")]
    public int? X { get; set; }

    public int? Y { get; set; }

    public int? W { get; set; }

    public int? H { get; set; }

    [Description("Child nodes drawn inside this one")]
    public List<NodeSpec>? Nodes { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ArchiModelSpec))]
public sealed partial class ArchiJsonContext : JsonSerializerContext;
