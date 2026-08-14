using RingOrder.Epos.Domain;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// What prints where. These are the shapes real shops ask for: one printer, two
/// kitchen stations, a second copy for packing, and delivery dockets that only
/// the front printer should see.
/// </summary>
public class PrintRoutingTests
{
    private static PrintDevice Device(string id, string name, bool drawer = false) =>
        new() { Id = id, Name = name, HasCashDrawer = drawer };

    private static Dictionary<string, PrintDevice> Map(params PrintDevice[] devices) =>
        devices.ToDictionary(d => d.Id, StringComparer.Ordinal);

    private static CartLine Line(string name, string? printClass) =>
        new() { Name = name, PrintClass = printClass, Quantity = 1, BasePrice = 5m };

    private static PosOrder Order(
        ServiceType service = ServiceType.Collection,
        OrderChannel channel = OrderChannel.Counter) =>
        new() { ServiceType = service, Channel = channel };

    [Fact]
    public void One_printer_shop_sends_everything_to_it()
    {
        var till = Device("front", "Counter", drawer: true);
        var routes = PrintRouting.DefaultRoutes([till]);
        var order = Order();
        var lines = new[] { Line("Kung po", "kitchen"), Line("Coke", "bar") };

        var kitchen = PrintRouting.RouteKitchen(order, lines, routes, Map(till));

        Assert.Single(kitchen);
        Assert.Equal(2, kitchen[0].Lines.Count);
        Assert.Single(PrintRouting.Route(order, PrintDocument.Receipt, routes, Map(till)));
    }

    [Fact]
    public void Two_stations_each_get_only_their_own_dishes()
    {
        var wok = Device("wok", "Wok");
        var fryer = Device("fryer", "Fryer");
        var routes = new List<PrintRoute>
        {
            new() { Document = PrintDocument.Kitchen, PrintClass = "kitchen", DeviceId = wok.Id },
            new() { Document = PrintDocument.Kitchen, PrintClass = "fryer", DeviceId = fryer.Id, SortOrder = 1 },
        };

        var result = PrintRouting.RouteKitchen(
            Order(),
            [Line("Kung po", "kitchen"), Line("Spring rolls", "fryer"), Line("Chow mein", "kitchen")],
            routes,
            Map(wok, fryer));

        var toWok = result.Single(r => r.Target.Device.Id == "wok");
        var toFryer = result.Single(r => r.Target.Device.Id == "fryer");

        Assert.Equal(2, toWok.Lines.Count);
        Assert.Single(toFryer.Lines);
        Assert.Equal("Spring rolls", toFryer.Lines[0].Name);
    }

    [Fact]
    public void A_dish_can_print_in_two_places()
    {
        // Wok cooks it, packing bench checks it off. Both are correct.
        var wok = Device("wok", "Wok");
        var packing = Device("pack", "Packing");
        var routes = new List<PrintRoute>
        {
            new() { Document = PrintDocument.Kitchen, PrintClass = "kitchen", DeviceId = wok.Id },
            new() { Document = PrintDocument.Kitchen, DeviceId = packing.Id, SortOrder = 1 },
        };

        var result = PrintRouting.RouteKitchen(
            Order(), [Line("Kung po", "kitchen")], routes, Map(wok, packing));

        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Single(r.Lines));
    }

    [Fact]
    public void A_rule_can_be_limited_to_one_service_type()
    {
        var front = Device("front", "Counter");
        var routes = new List<PrintRoute>
        {
            new()
            {
                Document = PrintDocument.Receipt,
                ServiceType = ServiceType.Delivery,
                DeviceId = front.Id,
            },
        };

        Assert.Empty(PrintRouting.Route(Order(ServiceType.Collection), PrintDocument.Receipt, routes, Map(front)));
        Assert.Single(PrintRouting.Route(Order(ServiceType.Delivery), PrintDocument.Receipt, routes, Map(front)));
    }

    [Fact]
    public void A_rule_can_be_limited_to_one_channel()
    {
        var front = Device("front", "Counter");
        var routes = new List<PrintRoute>
        {
            new() { Document = PrintDocument.Kitchen, Channel = OrderChannel.Web, DeviceId = front.Id },
        };

        Assert.Empty(PrintRouting.RouteKitchen(
            Order(channel: OrderChannel.Counter), [Line("A", "kitchen")], routes, Map(front)));
        Assert.Single(PrintRouting.RouteKitchen(
            Order(channel: OrderChannel.Web), [Line("A", "kitchen")], routes, Map(front)));
    }

    [Fact]
    public void A_switched_off_printer_is_not_routed_to()
    {
        var off = Device("wok", "Wok");
        off.IsEnabled = false;
        var routes = new List<PrintRoute>
        {
            new() { Document = PrintDocument.Kitchen, DeviceId = off.Id },
        };

        Assert.Empty(PrintRouting.RouteKitchen(Order(), [Line("A", "kitchen")], routes, Map(off)));
    }

    [Fact]
    public void A_disabled_rule_does_not_fire()
    {
        var front = Device("front", "Counter");
        var routes = new List<PrintRoute>
        {
            new() { Document = PrintDocument.Receipt, DeviceId = front.Id, IsEnabled = false },
        };

        Assert.Empty(PrintRouting.Route(Order(), PrintDocument.Receipt, routes, Map(front)));
    }

    [Fact]
    public void Copies_and_fallback_travel_with_the_target()
    {
        var wok = Device("wok", "Wok");
        var front = Device("front", "Counter");
        var routes = new List<PrintRoute>
        {
            new()
            {
                Document = PrintDocument.Kitchen,
                DeviceId = wok.Id,
                Copies = 2,
                FallbackDeviceId = front.Id,
            },
        };

        var target = PrintRouting.RouteKitchen(
            Order(), [Line("A", "kitchen")], routes, Map(wok, front)).Single().Target;

        Assert.Equal(2, target.Copies);
        Assert.Equal("front", target.FallbackDeviceId);
    }

    [Fact]
    public void A_dish_that_follows_its_category_still_routes()
    {
        // Blank on the dish means "wherever the category goes". If that reaches
        // an order line as null, every station rule stops matching and the
        // kitchen gets nothing — which is exactly what happened once.
        var starters = new Category { Id = "starters", Name = "Starters", PrintClass = "fryer" };
        var dish = new MenuItem
        {
            Id = "spring-rolls",
            CategoryId = starters.Id,
            Name = "Spring rolls",
            PrintClass = null,
            CategoryPrintClass = starters.PrintClass,
        };

        Assert.Equal("fryer", dish.EffectivePrintClass);

        var fryer = Device("fryer", "Fryer");
        var routes = new List<PrintRoute>
        {
            new() { Document = PrintDocument.Kitchen, PrintClass = "fryer", DeviceId = fryer.Id },
        };

        var line = Line(dish.Name, dish.EffectivePrintClass);
        Assert.Single(PrintRouting.RouteKitchen(Order(), [line], routes, Map(fryer)));
    }

    [Fact]
    public void A_dish_may_override_its_category()
    {
        var dish = new MenuItem
        {
            Name = "Salt and chilli squid",
            PrintClass = "fryer",
            CategoryPrintClass = "kitchen",
        };

        Assert.Equal("fryer", dish.EffectivePrintClass);
    }

    [Fact]
    public void With_nothing_set_anywhere_a_dish_still_reaches_the_kitchen()
    {
        Assert.Equal("kitchen", new MenuItem { Name = "Unclassified" }.EffectivePrintClass);
    }

    [Fact]
    public void Defaults_put_the_drawer_printer_on_receipts_and_the_other_on_the_kitchen()
    {
        var front = Device("front", "Counter", drawer: true);
        var kitchen = Device("kitchen", "Kitchen");

        var routes = PrintRouting.DefaultRoutes([front, kitchen]);

        var receipt = routes.Single(r => r.Document == PrintDocument.Receipt);
        var cook = routes.Single(r => r.Document == PrintDocument.Kitchen);

        Assert.Equal("front", receipt.DeviceId);
        Assert.Equal("kitchen", cook.DeviceId);
        Assert.Equal("front", cook.FallbackDeviceId);
    }
}
