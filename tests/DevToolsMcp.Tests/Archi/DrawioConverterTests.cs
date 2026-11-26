using System.Xml.Linq;
using DevToolsMcp.Server.Modules.Archi;

namespace DevToolsMcp.Tests.Archi;

public class DrawioConverterTests
{
    private static XDocument Convert(ArchiModelSpec spec, params string[] views) =>
        XDocument.Parse(DrawioConverter.Convert(ExchangeModelBuilder.Build(spec).Xml, views).Xml);

    private static XElement Vertex(XDocument doc, string label) =>
        doc.Descendants("mxCell").Single(c => (string?)c.Attribute("vertex") == "1" && (string?)c.Attribute("value") == label);

    [Fact]
    public void Elements_become_archimate3_shapes_with_layer_colours()
    {
        var doc = Convert(ExchangeModelBuilderTests.Sample());
        var page = Assert.Single(doc.Root!.Elements("diagram"));
        Assert.Equal("Shop", (string?)page.Attribute("name"));

        var actor = (string)Vertex(doc, "Customer").Attribute("style")!;
        Assert.Contains("shape=mxgraph.archimate3.application;appType=actor;archiType=square;", actor);
        Assert.Contains("fillColor=#ffffb5", actor);
        Assert.Contains("appType=comp;", (string)Vertex(doc, "Web shop").Attribute("style")!);
        Assert.Contains("appType=node;", (string)Vertex(doc, "Server").Attribute("style")!);
        Assert.Contains("fillColor=#c9e7b7", (string)Vertex(doc, "Server").Attribute("style")!);

        // Names are HTML-encoded because draw.io labels are HTML (html=1).
        Assert.NotNull(Vertex(doc, "Order &lt;goods&gt;"));
    }

    [Fact]
    public void Connections_become_edges_with_archimate_arrows()
    {
        var doc = Convert(ExchangeModelBuilderTests.Sample());
        var edges = doc.Descendants("mxCell").Where(c => (string?)c.Attribute("edge") == "1").ToList();
        Assert.Equal(4, edges.Count);

        var cells = doc.Descendants("mxCell").Select(c => (string?)c.Attribute("id")).ToHashSet();
        Assert.All(edges, e => Assert.Contains((string?)e.Attribute("source"), cells));
        Assert.All(edges, e => Assert.Contains((string?)e.Attribute("target"), cells));

        var styles = edges.Select(e => (string)e.Attribute("style")!).ToList();
        Assert.Contains(styles, s => s.Contains("startArrow=oval", StringComparison.Ordinal));                          // assignment
        Assert.Contains(styles, s => s.Contains("dashPattern=1 4;startArrow=open", StringComparison.Ordinal)
                                     && s.Contains("endArrow=open", StringComparison.Ordinal));                         // read/write access
        Assert.Contains(edges, e => (string?)e.Attribute("value") == "supports");
    }

    [Fact]
    public void Nested_nodes_get_parent_relative_geometry()
    {
        var spec = ExchangeModelBuilderTests.Sample();
        spec.Views = [new() { Name = "Deployment", Nodes = [new() { Ref = "srv", X = 100, Y = 50, Nodes = [new() { Ref = "db", X = 10, Y = 30 }] }] }];
        var doc = Convert(spec);

        var server = Vertex(doc, "Server");
        var db = Vertex(doc, "Orders");
        Assert.Equal((string?)server.Attribute("id"), (string?)db.Attribute("parent"));
        Assert.Equal("10", (string?)db.Element("mxGeometry")!.Attribute("x"));
        Assert.Equal("30", (string?)db.Element("mxGeometry")!.Attribute("y"));
        Assert.Contains("verticalAlign=top", (string)server.Attribute("style")!); // label above the nested children
    }

    [Fact]
    public void Selects_views_by_name_and_rejects_unknown_ones()
    {
        var spec = ExchangeModelBuilderTests.Sample();
        spec.Views = [new() { Name = "One" }, new() { Name = "Two", Elements = ["db"] }];
        Assert.Equal(["Two"], DrawioConverter.Convert(ExchangeModelBuilder.Build(spec).Xml, ["two"]).Pages);
        Assert.Equal(["One", "Two"], DrawioConverter.Convert(ExchangeModelBuilder.Build(spec).Xml).Pages);
        var ex = Assert.Throws<ArgumentException>(() => DrawioConverter.Convert(ExchangeModelBuilder.Build(spec).Xml, ["Three"]));
        Assert.Contains("Views: One, Two", ex.Message);
        Assert.Throws<ArgumentException>(() => DrawioConverter.Convert("<model/>"));
    }

    [Fact]
    public void Refuses_models_with_a_DTD()
    {
        // Billion laughs: each entity expands the previous one ten times.
        const string bomb = """
            <?xml version="1.0"?>
            <!DOCTYPE model [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;"><!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">]>
            <model xmlns="http://www.opengroup.org/xsd/archimate/3.0/"><name>&c;</name></model>
            """;
        var ex = Assert.Throws<ArgumentException>(() => DrawioConverter.Convert(bomb));
        Assert.Contains("DTD", ex.Message);
    }
}
