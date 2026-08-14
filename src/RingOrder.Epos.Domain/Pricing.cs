namespace RingOrder.Epos.Domain;

/// <summary>
/// Server-style line pricing — never trust a hand-edited line total.
/// Mirrors website calculateLineTotal / repriceOrderLines spirit.
/// </summary>
public static class LinePricing
{
    public static decimal CalculateUnitPrice(decimal basePrice, IEnumerable<CartLineSelection> selections)
    {
        var deltas = selections.SelectMany(s => s.Choices).Sum(c => c.PriceDelta);
        return basePrice + deltas;
    }

    public static decimal CalculateLineTotal(decimal basePrice, int quantity, IEnumerable<CartLineSelection> selections)
    {
        var qty = Math.Clamp(quantity, 1, 99);
        return RoundMoney(CalculateUnitPrice(basePrice, selections) * qty);
    }

    public static void RecalculateLine(CartLine line)
    {
        line.Quantity = Math.Clamp(line.Quantity, 1, 99);
        if (line.IsAdHoc)
        {
            line.LineTotal = RoundMoney(line.BasePrice * line.Quantity);
            return;
        }

        line.LineTotal = CalculateLineTotal(line.BasePrice, line.Quantity, line.Selections);
    }

    public static void RecalculateOrder(PosOrder order)
    {
        foreach (var line in order.Lines)
            RecalculateLine(line);

        order.Subtotal = RoundMoney(order.Lines.Sum(l => l.LineTotal));
        order.Total = RoundMoney(order.Subtotal + order.DeliveryFee - order.DiscountTotal);
        if (order.Total < 0) order.Total = 0;
        order.UpdatedAt = DateTimeOffset.Now;
    }

    public static CartLine BuildMenuLine(
        MenuItem item,
        int quantity,
        IReadOnlyDictionary<string, IReadOnlyList<string>> selectedChoiceIdsByGroup,
        string? notes = null)
    {
        var selections = new List<CartLineSelection>();
        var visible = GetVisibleOptionGroups(item, selectedChoiceIdsByGroup);

        foreach (var group in visible.OrderBy(g => g.SortOrder))
        {
            if (!selectedChoiceIdsByGroup.TryGetValue(group.Id, out var choiceIds) || choiceIds.Count == 0)
                continue;

            var chosen = group.Choices
                .Where(c => choiceIds.Contains(c.Id) && c.IsAvailable)
                .Select(c => new SelectedChoice
                {
                    ChoiceId = c.Id,
                    Label = c.Label,
                    OptionTranslation = c.OptionTranslation,
                    PriceDelta = c.PriceDelta,
                })
                .ToList();

            if (chosen.Count == 0) continue;

            selections.Add(new CartLineSelection
            {
                GroupId = group.Id,
                GroupName = group.Name,
                Choices = chosen,
            });
        }

        var line = new CartLine
        {
            ItemId = item.Id,
            Name = item.Name,
            ItemTranslation = item.ItemTranslation,
            BasePrice = item.BasePrice,
            Quantity = Math.Clamp(quantity, 1, 99),
            Selections = selections,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            IsAdHoc = false,
        };
        RecalculateLine(line);
        return line;
    }

    public static CartLine BuildAdHocLine(string name, decimal unitPrice, int quantity, string? kitchenTranslation = null, string? notes = null)
    {
        var line = new CartLine
        {
            ItemId = null,
            Name = string.IsNullOrWhiteSpace(name) ? "Ad-hoc" : name.Trim(),
            ItemTranslation = string.IsNullOrWhiteSpace(kitchenTranslation) ? null : kitchenTranslation.Trim(),
            BasePrice = unitPrice,
            Quantity = Math.Clamp(quantity, 1, 99),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            IsAdHoc = true,
        };
        RecalculateLine(line);
        return line;
    }

    public static IReadOnlyList<OptionGroup> GetVisibleOptionGroups(
        MenuItem item,
        IReadOnlyDictionary<string, IReadOnlyList<string>> selectedChoiceIdsByGroup)
    {
        return item.OptionGroups
            .Where(g =>
            {
                if (g.ShowWhen is null) return true;
                if (!selectedChoiceIdsByGroup.TryGetValue(g.ShowWhen.GroupId, out var ids))
                    return false;
                return ids.Any(id => g.ShowWhen.ChoiceIds.Contains(id));
            })
            .OrderBy(g => g.SortOrder)
            .ToList();
    }

    public static string? ValidateSelections(
        MenuItem item,
        IReadOnlyDictionary<string, IReadOnlyList<string>> selectedChoiceIdsByGroup)
    {
        foreach (var group in GetVisibleOptionGroups(item, selectedChoiceIdsByGroup))
        {
            selectedChoiceIdsByGroup.TryGetValue(group.Id, out var ids);
            ids ??= Array.Empty<string>();
            var count = ids.Count;

            if (group.Type is OptionGroupType.Single)
            {
                if (group.Required && count != 1)
                    return $"Select one option for {group.Name}";
                if (!group.Required && count > 1)
                    return $"Select at most one option for {group.Name}";
            }
            else
            {
                var min = group.MinSelections ?? (group.Required ? 1 : 0);
                var max = group.MaxSelections ?? group.Choices.Count;
                if (count < min) return $"Select at least {min} for {group.Name}";
                if (count > max) return $"Select at most {max} for {group.Name}";
            }
        }

        return null;
    }

    public static Dictionary<string, IReadOnlyList<string>> DefaultSelections(MenuItem item)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var group in item.OptionGroups.OrderBy(g => g.SortOrder))
        {
            var defaults = group.Choices.Where(c => c.IsDefault && c.IsAvailable).Select(c => c.Id).ToList();
            if (defaults.Count == 0 && group.Required && group.Type is OptionGroupType.Single)
            {
                var first = group.Choices.FirstOrDefault(c => c.IsAvailable);
                if (first is not null) defaults.Add(first.Id);
            }

            if (defaults.Count > 0)
                map[group.Id] = defaults;
        }

        // Re-evaluate showWhen after defaults
        var visible = GetVisibleOptionGroups(item, map);
        var pruned = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var g in visible)
        {
            if (map.TryGetValue(g.Id, out var ids))
                pruned[g.Id] = ids;
        }
        return pruned;
    }

    public static decimal RoundMoney(decimal value) => Money.Round(value);
}
