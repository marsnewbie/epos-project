using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Online;

public sealed class OnlineOrderPollerOptions
{
    public string OrderServerUrl { get; set; } = "";
    public string PrintedUrl { get; set; } = "";
    public string CallbackUrl { get; set; } = "";
    public string ResId { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int IntervalSeconds { get; set; } = 4;
    public bool Enabled { get; set; }

    public static OnlineOrderPollerOptions FromSettings(AppSettings s) => new()
    {
        OrderServerUrl = s.OnlineOrderServerUrl,
        PrintedUrl = s.OnlinePrintedUrl,
        CallbackUrl = s.OnlineCallbackUrl,
        ResId = s.OnlineResId,
        Username = s.OnlineUsername,
        Password = s.OnlinePassword,
        IntervalSeconds = Math.Clamp(s.OnlinePollIntervalSeconds, 2, 60),
        Enabled = s.OnlinePollingEnabled,
    };
}

public interface IOnlineOrderPoller
{
    bool IsRunning { get; }
    string LastStatus { get; }
    event EventHandler<PosOrder>? OrderReceived;
    void Configure(OnlineOrderPollerOptions options);
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task PollOnceAsync(CancellationToken ct = default);
    Task AckPrintedAsync(string orderNumber, CancellationToken ct = default);
}

public sealed class WebsitePrintClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;

    public WebsitePrintClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<PosOrder?> GetNextOrderAsync(OnlineOrderPollerOptions opt, CancellationToken ct = default)
    {
        EnsureCredentials(opt);
        var url = AppendAuth(opt.OrderServerUrl, opt);
        using var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Online auth failed (401). Check a/u/p in Settings.");
        if (resp.StatusCode == HttpStatusCode.ServiceUnavailable)
            throw new InvalidOperationException("Print disabled on website (503).");
        if (resp.StatusCode == HttpStatusCode.NoContent)
            return null;
        resp.EnsureSuccessStatusCode();

        var media = resp.Content.Headers.ContentType?.MediaType ?? "";
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
            return null;

        // Preferred: EPOS JSON adapter
        if (media.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            body.TrimStart().StartsWith('{'))
        {
            var envelope = JsonSerializer.Deserialize<EposNextEnvelope>(body, JsonOpts)
                ?? throw new FormatException("Invalid EPOS JSON envelope.");
            if (envelope.Order is null)
                return null;
            return EposJsonOrderMapper.ToPosOrder(envelope.Order, body);
        }

        // Fallback: legacy Goodcom text (if Settings still point at gcanyorder/getorder)
        return GoodcomPayloadParser.Parse(body);
    }

    public async Task AckPrintedAsync(OnlineOrderPollerOptions opt, string orderNumber, CancellationToken ct = default)
    {
        EnsureCredentials(opt);
        var ackBase = string.IsNullOrWhiteSpace(opt.PrintedUrl) ? opt.CallbackUrl : opt.PrintedUrl;
        var url = AppendAuth(ackBase, opt);
        url += $"&o={Uri.EscapeDataString(orderNumber)}&ak=Accepted";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
    }

    private static void EnsureCredentials(OnlineOrderPollerOptions opt)
    {
        if (string.IsNullOrWhiteSpace(opt.OrderServerUrl))
            throw new InvalidOperationException("Order Server URL is empty.");
        if (string.IsNullOrWhiteSpace(opt.Username) || string.IsNullOrWhiteSpace(opt.Password))
            throw new InvalidOperationException("Online username/password empty. Copy a/u/p from website Admin → Print.");
    }

    private static string AppendAuth(string baseUrl, OnlineOrderPollerOptions opt)
    {
        var sep = baseUrl.Contains('?') ? "&" : "?";
        var sb = new StringBuilder(baseUrl);
        sb.Append(sep);
        if (!string.IsNullOrWhiteSpace(opt.ResId))
            sb.Append("a=").Append(Uri.EscapeDataString(opt.ResId)).Append('&');
        sb.Append("u=").Append(Uri.EscapeDataString(opt.Username));
        sb.Append("&p=").Append(Uri.EscapeDataString(opt.Password));
        return sb.ToString();
    }
}

internal sealed class EposNextEnvelope
{
    public EposPrintOrderDto? Order { get; set; }
}

public sealed class EposPrintOrderDto
{
    public int SchemaVersion { get; set; }
    public string Id { get; set; } = "";
    public string OrderNumber { get; set; } = "";
    public string OrderType { get; set; } = "collection";
    public string PaymentStatus { get; set; } = "";
    public string? PaymentMethod { get; set; }
    public string PaymentLabel { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string CustomerPhone { get; set; } = "";
    public string? CustomerEmail { get; set; }
    public string? DeliveryAddress { get; set; }
    public string? DeliveryPostcode { get; set; }
    public List<EposPrintLineDto> Lines { get; set; } = [];
    public List<EposFreeItemDto> FreeItems { get; set; } = [];
    public decimal Subtotal { get; set; }
    public decimal DeliveryFee { get; set; }
    public decimal BelowMinimumSurcharge { get; set; }
    public decimal DeliveryChargeTotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal Total { get; set; }
    public string? Notes { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? RequestedFor { get; set; }
    public string? FulfilmentLabel { get; set; }
    public string? FooterText { get; set; }
    public List<EposPromoDto> AppliedPromotions { get; set; } = [];
}

public sealed class EposPrintLineDto
{
    public string Id { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ItemTranslation { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal BasePrice { get; set; }
    public decimal LineTotal { get; set; }
    public string? Notes { get; set; }
    public List<EposSelectionDto> Selections { get; set; } = [];
}

public sealed class EposSelectionDto
{
    public string GroupId { get; set; } = "";
    public string GroupName { get; set; } = "";
    public List<EposChoiceDto> Choices { get; set; } = [];
}

public sealed class EposChoiceDto
{
    public string Label { get; set; } = "";
    public string? OptionTranslation { get; set; }
    public decimal PriceDelta { get; set; }
}

public sealed class EposFreeItemDto
{
    public string Label { get; set; } = "";
}

public sealed class EposPromoDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public decimal DiscountAmount { get; set; }
    public string? FreeItemLabel { get; set; }
    public bool FreeDelivery { get; set; }
}

public static class EposJsonOrderMapper
{
    public static PosOrder ToPosOrder(EposPrintOrderDto dto, string rawJson)
    {
        var order = new PosOrder
        {
            OrderNumber = string.IsNullOrWhiteSpace(dto.OrderNumber)
                ? $"ON-{DateTime.Now:HHmmss}"
                : dto.OrderNumber.Trim(),
            OnlineExternalId = string.IsNullOrWhiteSpace(dto.Id) ? dto.OrderNumber : dto.Id,
            OnlinePayload = rawJson,
            Source = PosOrderSource.Online,
            Status = PosOrderStatus.Open,
            OrderType = string.Equals(dto.OrderType, "delivery", StringComparison.OrdinalIgnoreCase)
                ? PosOrderType.Delivery
                : PosOrderType.Collection,
            CustomerName = EmptyToNull(dto.CustomerName),
            CustomerPhone = EmptyToNull(dto.CustomerPhone),
            DeliveryAddress = EmptyToNull(dto.DeliveryAddress),
            DeliveryPostcode = EmptyToNull(dto.DeliveryPostcode),
            Notes = EmptyToNull(dto.Notes),
            RequestedFor = EmptyToNull(dto.RequestedFor),
            FulfilmentLabel = EmptyToNull(dto.FulfilmentLabel),
            PaymentLabel = EmptyToNull(dto.PaymentLabel),
            TicketFooter = EmptyToNull(dto.FooterText),
            Subtotal = dto.Subtotal,
            DeliveryFee = dto.DeliveryChargeTotal > 0 ? dto.DeliveryChargeTotal : dto.DeliveryFee + dto.BelowMinimumSurcharge,
            BelowMinimumSurcharge = dto.BelowMinimumSurcharge,
            DiscountTotal = dto.DiscountTotal,
            Total = dto.Total,
            Lines = [],
        };

        if (DateTimeOffset.TryParse(dto.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created))
            order.CreatedAt = created;

        foreach (var line in dto.Lines)
        {
            var qty = Math.Max(1, line.Quantity);
            order.Lines.Add(new CartLine
            {
                Id = string.IsNullOrWhiteSpace(line.Id) ? Guid.NewGuid().ToString("N") : line.Id,
                ItemId = EmptyToNull(line.ItemId),
                Name = line.Name,
                ItemTranslation = EmptyToNull(line.ItemTranslation),
                BasePrice = line.BasePrice,
                Quantity = qty,
                LineTotal = line.LineTotal,
                Notes = EmptyToNull(line.Notes),
                Selections = line.Selections.Select(sel => new CartLineSelection
                {
                    GroupId = sel.GroupId,
                    GroupName = sel.GroupName,
                    Choices = sel.Choices.Select(c => new SelectedChoice
                    {
                        ChoiceId = c.Label,
                        Label = c.Label,
                        OptionTranslation = EmptyToNull(c.OptionTranslation),
                        PriceDelta = c.PriceDelta,
                    }).ToList(),
                }).ToList(),
            });
        }

        foreach (var free in dto.FreeItems)
        {
            if (string.IsNullOrWhiteSpace(free.Label)) continue;
            order.Lines.Add(new CartLine
            {
                Name = free.Label.Trim(),
                Quantity = 1,
                BasePrice = 0,
                LineTotal = 0,
                IsAdHoc = false,
            });
        }

        if (string.Equals(dto.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(dto.PaymentLabel))
        {
            order.Tenders.Add(new OrderTender
            {
                Type = TenderType.OnlinePaid,
                Amount = order.Total,
                Reference = dto.PaymentLabel ?? dto.PaymentMethod,
            });
            if (string.Equals(dto.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase))
                order.Status = PosOrderStatus.Paid;
        }

        // Keep website totals (promos / surcharges already applied server-side).
        if (order.Total <= 0)
            LinePricing.RecalculateOrder(order);

        return order;
    }

    private static string? EmptyToNull(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Parses Goodcom / GcAnyOrder wire text into a local POS order draft (fallback).</summary>
public static class GoodcomPayloadParser
{
    public static PosOrder Parse(string payload)
    {
        var raw = payload.Trim();
        if (raw.StartsWith('#')) raw = raw[1..];
        if (raw.EndsWith('#')) raw = raw[..^1];

        var parts = raw.Split('*');
        if (parts.Length < 6)
            throw new FormatException("Goodcom payload missing segments.");

        var orderId = parts[2].Trim();
        var deliveryType = parts[1].Trim();
        var itemsSeg = parts[3];
        var chargesSeg = parts.Length > 4 ? parts[4] : "";
        var detailsSeg = parts.Length > 5 ? parts[5] : "";
        var commentsSeg = parts.Length > 6 ? parts[6] : "";

        var order = new PosOrder
        {
            OrderNumber = string.IsNullOrWhiteSpace(orderId) ? $"ON-{DateTime.Now:HHmmss}" : orderId,
            OnlineExternalId = orderId,
            OnlinePayload = payload,
            Source = PosOrderSource.Online,
            Status = PosOrderStatus.Open,
            OrderType = deliveryType == "1" ? PosOrderType.Delivery : PosOrderType.Collection,
            Lines = ParseItems(itemsSeg),
        };

        var charges = chargesSeg.Split(';');
        if (charges.Length > 0 && decimal.TryParse(charges[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var fee))
            order.DeliveryFee = fee;
        if (charges.Length > 1 && decimal.TryParse(charges[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var disc))
            order.DiscountTotal = disc;

        var details = detailsSeg.Split(';');
        // fee;total;verifyType;name;addr;phone;paidType;payment;time;prev;
        if (details.Length > 3) order.CustomerName = EmptyToNull(details[3]);
        if (details.Length > 4)
        {
            var addr = details[4];
            if (!string.Equals(addr, "COLLECTION", StringComparison.OrdinalIgnoreCase))
                order.DeliveryAddress = EmptyToNull(addr);
        }
        if (details.Length > 5) order.CustomerPhone = EmptyToNull(details[5]);
        if (details.Length > 7) order.PaymentLabel = EmptyToNull(details[7]);
        if (details.Length > 8) order.RequestedFor = EmptyToNull(details[8]);

        var comments = commentsSeg.Split(';');
        if (comments.Length > 0)
        {
            var n = EmptyToNull(comments[0]);
            if (!string.Equals(n, "None", StringComparison.OrdinalIgnoreCase))
                order.Notes = n;
        }
        if (comments.Length > 1) order.TicketFooter = EmptyToNull(comments[1]);

        LinePricing.RecalculateOrder(order);
        if (details.Length > 1 &&
            decimal.TryParse(details[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var websiteTotal) &&
            websiteTotal > 0)
        {
            order.Total = websiteTotal;
        }

        if (details.Length > 7 && !string.IsNullOrWhiteSpace(details[7]))
        {
            order.Tenders.Add(new OrderTender
            {
                Type = TenderType.OnlinePaid,
                Amount = order.Total,
                Reference = details[7],
            });
            order.Status = PosOrderStatus.Paid;
        }

        return order;
    }

    private static List<CartLine> ParseItems(string itemsSeg)
    {
        var fields = itemsSeg.Split(';');
        var lines = new List<CartLine>();
        for (var i = 0; i + 5 < fields.Length; i += 6)
        {
            if (!int.TryParse(fields[i], out var qty) || qty <= 0)
            {
                if (string.IsNullOrWhiteSpace(fields[i])) break;
                continue;
            }

            var name = fields[i + 1];
            decimal.TryParse(fields[i + 2], NumberStyles.Any, CultureInfo.InvariantCulture, out var lineTotal);
            var translation = fields[i + 4];
            var optionsRaw = fields[i + 5];
            var (selections, notes) = ParseOptions(optionsRaw);

            var unit = qty > 0 ? lineTotal / qty : lineTotal;
            lines.Add(new CartLine
            {
                Name = name,
                ItemTranslation = EmptyToNull(translation),
                BasePrice = unit,
                Quantity = qty,
                Selections = selections,
                Notes = notes,
                LineTotal = lineTotal,
                IsAdHoc = false,
            });
        }
        return lines;
    }

    private static (List<CartLineSelection> Selections, string? Notes) ParseOptions(string optionsRaw)
    {
        var selections = new List<CartLineSelection>();
        string? notes = null;
        if (string.IsNullOrWhiteSpace(optionsRaw))
            return (selections, notes);

        var parts = optionsRaw.Split(',');
        var choices = new List<SelectedChoice>();
        for (var i = 0; i + 2 < parts.Length; i += 3)
        {
            var eng = parts[i].Trim();
            var zh = parts[i + 2].Trim();
            if (eng.StartsWith("NOTE:", StringComparison.OrdinalIgnoreCase))
            {
                notes = eng["NOTE:".Length..].Trim();
                continue;
            }
            if (string.IsNullOrWhiteSpace(eng) && string.IsNullOrWhiteSpace(zh))
                continue;
            var label = string.IsNullOrWhiteSpace(eng) ? zh : eng;
            choices.Add(new SelectedChoice
            {
                ChoiceId = label,
                Label = label,
                OptionTranslation = EmptyToNull(zh),
                PriceDelta = 0,
            });
        }

        if (choices.Count > 0)
        {
            selections.Add(new CartLineSelection
            {
                GroupId = "online",
                GroupName = "Options",
                Choices = choices,
            });
        }

        return (selections, notes);
    }

    private static string? EmptyToNull(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public sealed class OnlineOrderPoller : IOnlineOrderPoller
{
    private readonly WebsitePrintClient _client;
    private OnlineOrderPollerOptions _options = new();
    private CancellationTokenSource? _loopCts;
    private Task? _loop;

    public OnlineOrderPoller(WebsitePrintClient? client = null)
    {
        _client = client ?? new WebsitePrintClient();
    }

    public bool IsRunning { get; private set; }
    public string LastStatus { get; private set; } = "Idle";
    public event EventHandler<PosOrder>? OrderReceived;

    public void Configure(OnlineOrderPollerOptions options) => _options = options;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return Task.CompletedTask;
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsRunning = true;
        LastStatus = "Running";
        _loop = Task.Run(() => LoopAsync(_loopCts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!IsRunning) return;
        IsRunning = false;
        if (_loopCts is not null)
        {
            await _loopCts.CancelAsync();
            try { if (_loop is not null) await _loop; } catch { /* ignore */ }
            _loopCts.Dispose();
            _loopCts = null;
        }
        LastStatus = "Stopped";
    }

    public async Task PollOnceAsync(CancellationToken ct = default)
    {
        try
        {
            var order = await _client.GetNextOrderAsync(_options, ct);
            if (order is null)
            {
                LastStatus = $"No order @ {DateTime.Now:HH:mm:ss}";
                return;
            }

            LastStatus = $"Received {order.OrderNumber} @ {DateTime.Now:HH:mm:ss}";
            OrderReceived?.Invoke(this, order);
        }
        catch (Exception ex)
        {
            LastStatus = $"Error: {ex.Message}";
            throw;
        }
    }

    public Task AckPrintedAsync(string orderNumber, CancellationToken ct = default) =>
        _client.AckPrintedAsync(_options, orderNumber, ct);

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_options.Enabled)
                    await PollOnceAsync(ct);
                else
                    LastStatus = "Paused (disabled in Settings)";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LastStatus = $"Error: {ex.Message}";
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.IntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
