namespace RingOrder.Epos.Domain;

/// <summary>
/// How the customer receives the food. Independent of <see cref="OrderChannel"/>:
/// a phone call can be either collection or delivery, and a website order can be
/// either too, so the two must never be squashed into one list of buttons.
/// </summary>
public enum ServiceType
{
    /// <summary>Customer collects from the counter.</summary>
    Collection,

    /// <summary>Driver takes it to an address.</summary>
    Delivery,

    /// <summary>Eaten in, against a table number.</summary>
    EatIn,
}

/// <summary>
/// Where the order came from. Drives reporting, ticket banners and which
/// commission the owner is paying — never the fulfilment rules.
/// </summary>
public enum OrderChannel
{
    /// <summary>Taken at the counter with the customer present.</summary>
    Counter,

    /// <summary>Taken over the phone, usually from caller ID or the phone book.</summary>
    Phone,

    /// <summary>Pulled from the shop's own ordering website.</summary>
    Web,

    /// <summary>A third-party marketplace: Uber Eats, Deliveroo, Just Eat.</summary>
    Platform,
}

public enum PosOrderStatus
{
    Draft,
    Open,

    /// <summary>Sent to kitchen, not yet paid.</summary>
    Sent,

    /// <summary>Parked for later (name/phone label).</summary>
    Held,
    Paid,
    Completed,
    Cancelled,
    Voided,
}

public enum TenderType
{
    Cash,
    CardManual,
    CardIntegrated,

    /// <summary>Already paid away from the till — website or marketplace checkout.</summary>
    PrepaidOnline,
    Voucher,
    Other,
}

public enum StaffRole
{
    /// <summary>Takes orders and payments.</summary>
    Cashier,

    /// <summary>Adds voids, refunds, discounts, drawer and shift close.</summary>
    Supervisor,

    /// <summary>Everything, including settings and menu.</summary>
    Manager,
}

public enum ShiftStatus
{
    Open,
    Closed,
}

public enum PrintJobChannel
{
    Kitchen,
    Front,
}

public enum PrintJobStatus
{
    Pending,
    Claimed,
    Printed,
    Failed,
}

public enum OptionGroupType
{
    /// <summary>Pick exactly one.</summary>
    Single,

    /// <summary>Pick between min and max.</summary>
    Multi,
}

/// <summary>
/// Which station cooks or pours an item. Print routing matches on this rather
/// than on the category, so a shop can rearrange its menu without re-plumbing
/// its printers.
/// </summary>
public static class PrintClass
{
    public const string Kitchen = "kitchen";
    public const string Fryer = "fryer";
    public const string ColdPrep = "cold-prep";
    public const string Bar = "bar";
    public const string Dessert = "dessert";

    public static readonly IReadOnlyList<string> Known =
        [Kitchen, Fryer, ColdPrep, Bar, Dessert];
}

public sealed class QuickNoteDef
{
    public string En { get; set; } = "";
    public string Zh { get; set; } = "";
}

/// <summary>Industry quick kitchen notes (POS speed buttons).</summary>
public static class QuickKitchenNotes
{
    public static readonly IReadOnlyList<(string En, string Zh)> Defaults =
    [
        ("No onion", "不要葱"),
        ("No garlic", "不要蒜"),
        ("No coriander", "不要香菜"),
        ("No spicy", "不要辣"),
        ("Mild spicy", "少辣"),
        ("Extra spicy", "多辣"),
        ("Less oil", "少油"),
        ("Less salt", "少盐"),
        ("Sauce separate", "酱汁分开"),
        ("Urgent", "急单"),
        ("No cutlery", "不要餐具"),
        ("Cutlery please", "要餐具"),
        ("Well done", "煎透"),
        ("Soft", "嫩一点"),
    ];

    public static List<QuickNoteDef> CreateDefaultList() =>
        Defaults.Select(d => new QuickNoteDef { En = d.En, Zh = d.Zh }).ToList();
}

/// <summary>
/// Money crosses the SQLite boundary as whole pence. <c>decimal</c> is exact in
/// .NET so the domain keeps using it, but SQLite's <c>REAL</c> is binary
/// floating point and a till whose totals drift by a penny is unusable.
/// </summary>
public static class Money
{
    public static int ToPence(decimal amount) =>
        (int)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    public static decimal FromPence(long pence) => pence / 100m;

    public static decimal Round(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
