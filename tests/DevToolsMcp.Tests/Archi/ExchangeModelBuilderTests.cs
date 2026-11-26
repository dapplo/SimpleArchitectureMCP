using System.Xml.Linq;
using DevToolsMcp.Server.Modules.Archi;

namespace DevToolsMcp.Tests.Archi;

public class ExchangeModelBuilderTests
{
    private static readonly XNamespace A = ExchangeModelBuilder.Namespace;
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    internal static ArchiModelSpec Sample() => new()
    {
        Name = "Shop",
        Elements =
        [
            new() { Id = "customer", Type = "BusinessActor", Name = "Customer" },
            new() { Id = "order", Type = "business process", Name = "Order <goods>" },
            new() { Id = "shop", Type = "ApplicationComponent", Name = "Web shop", Doc = "Public site" },
            new() { Id = "db", Type = "dataobject", Name = "Orders" },
            new() { Id = "srv", Type = "Node", Name = "Server" },
        ],
        Relationships =
        [
            new() { Type = "Assignment", Source = "customer", Target = "order" },
            new() { Type = "serving", Source = "shop", Target = "order", Name = "supports" },
            new() { Type = "Access", Source = "shop", Target = "db", Access = "readwrite" },
            new() { Type = "Realization", Source = "srv", Target = "shop" },
        ],
    };

    [Fact]
    public void Default_view_shows_all_elements_and_connections()
    {
        var result = ExchangeModelBuilder.Build(Sample());
        var doc = XDocument.Parse(result.Xml);

        Assert.Equal("Shop", doc.Root!.Element(A + "name")!.Value);
        Assert.Equal(["Shop"], result.Views);
        var view = doc.Descendants(A + "view").Single();
        Assert.Equal("Diagram", (string?)view.Attribute(Xsi + "type"));
        Assert.Equal(5, view.Elements(A + "node").Count());
        Assert.Equal(4, view.Elements(A + "connection").Count());

        var order = doc.Descendants(A + "element").Single(e => (string?)e.Attribute("identifier") == "id-order");
        Assert.Equal("BusinessProcess", (string?)order.Attribute(Xsi + "type"));
        Assert.Equal("Order <goods>", order.Element(A + "name")!.Value);
        Assert.Equal("ReadWrite", (string?)doc.Descendants(A + "relationship").Single(r => (string?)r.Attribute(Xsi + "type") == "Access").Attribute("accessType"));
    }

    [Fact]
    public void Identifiers_are_unique_and_valid_ids()
    {
        var doc = XDocument.Parse(ExchangeModelBuilder.Build(Sample()).Xml);
        var ids = doc.Descendants().Select(e => (string?)e.Attribute("identifier")).Where(i => i is not null).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Matches("^[A-Za-z_][A-Za-z0-9._-]*$", id));
        Assert.Equal("id-a_b_c", ExchangeModelBuilder.Xid("a b/c"));
    }

    [Fact]
    public void Auto_layout_puts_layers_in_rows_top_down()
    {
        var doc = XDocument.Parse(ExchangeModelBuilder.Build(Sample()).Xml);
        int Y(string element) => (int)doc.Descendants(A + "node").Single(n => (string?)n.Attribute("elementRef") == element).Attribute("y")!;

        Assert.Equal(Y("id-customer"), Y("id-order"));
        Assert.True(Y("id-order") < Y("id-shop"));
        Assert.Equal(Y("id-shop"), Y("id-db"));
        Assert.True(Y("id-shop") < Y("id-srv"));
    }

    [Fact]
    public void Nested_nodes_use_absolute_coordinates_and_skip_nesting_connections()
    {
        var spec = Sample();
        spec.Views =
        [
            new()
            {
                Name = "Deployment",
                Nodes = [new() { Ref = "srv", X = 100, Y = 50, Nodes = [new() { Ref = "shop" }, new() { Ref = "db", X = 10, Y = 100 }] }],
            },
        ];
        var doc = XDocument.Parse(ExchangeModelBuilder.Build(spec).Xml);
        var srv = doc.Descendants(A + "node").Single(n => (string?)n.Attribute("elementRef") == "id-srv");
        var children = srv.Elements(A + "node").ToList();

        Assert.Equal(2, children.Count);
        Assert.Equal(100 + ExchangeModelBuilder.Padding, (int)children[0].Attribute("x")!);
        Assert.Equal(150, (int)children[1].Attribute("y")!);
        Assert.True((int)srv.Attribute("h")! >= 100 + ExchangeModelBuilder.NodeHeight, "parent grows to fit its children");

        // srv realizes shop (nested: no line), shop accesses db (siblings: line).
        var connection = Assert.Single(doc.Descendants(A + "connection"));
        Assert.Equal("Access", (string?)doc.Descendants(A + "relationship")
            .Single(r => (string?)r.Attribute("identifier") == (string?)connection.Attribute("relationshipRef")).Attribute(Xsi + "type"));
    }

    [Fact]
    public void Connections_can_be_turned_off()
    {
        var spec = Sample();
        spec.Views = [new() { Name = "Plain", Elements = ["shop", "db"], Connections = false }];
        var doc = XDocument.Parse(ExchangeModelBuilder.Build(spec).Xml);
        Assert.Empty(doc.Descendants(A + "connection"));
        Assert.Equal(2, doc.Descendants(A + "node").Count());
    }

    [Fact]
    public void Errors_are_collected_into_one_message()
    {
        var spec = new ArchiModelSpec
        {
            Elements = [new() { Id = "a", Type = "Banana", Name = "A" }, new() { Id = "b", Type = "Goal", Name = "B" }, new() { Id = "b", Type = "Goal", Name = "B2" }],
            Relationships = [new() { Type = "Serving", Source = "b", Target = "zzz" }, new() { Type = "Loves", Source = "b", Target = "b" }],
            Views = [new() { Name = "V", Elements = ["nope"] }],
        };
        var ex = Assert.Throws<ArgumentException>(() => ExchangeModelBuilder.Build(spec));
        Assert.Contains("unknown type 'Banana'", ex.Message);
        Assert.Contains("'b': duplicate id", ex.Message);
        Assert.Contains("unknown target", ex.Message);
        Assert.Contains("unknown type 'Loves'", ex.Message);
        Assert.Contains("unknown element id(s) nope", ex.Message);
    }
}
