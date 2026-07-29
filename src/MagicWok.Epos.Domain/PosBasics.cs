namespace MagicWok.Epos.Domain;

public enum PosOrderType
{
    Collection,
    Delivery,
    WalkIn,
    EatIn,
}

public enum PosOrderSource
{
    Pos,
    Online,
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
    OnlinePaid,
    Other,
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
    Radio,
    Checkbox,
    Select,
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
