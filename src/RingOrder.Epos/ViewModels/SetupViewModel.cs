using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RingOrder.Epos.Online;
using RingOrder.Epos.Services;

namespace RingOrder.Epos.ViewModels;

/// <summary>
/// The first thing a new installation shows: what shop is this?
/// <para>
/// Activation belongs here rather than buried in Settings. A till that has to be
/// signed into and navigated before it knows what it is has the order backwards
/// — and in practice nobody would ever go and find it, so the machine would run
/// unconnected for its whole life.
/// </para>
/// <para>
/// <b>Skipping is always offered, and it is not a grudging escape hatch.</b>
/// Install day is exactly when a shop's internet is most likely to be a phone
/// hotspot or nothing at all, and the person holding the screwdriver may not
/// have the code. A till that refused to open until it phoned home would be the
/// lock this whole design exists not to be.
/// </para>
/// <para>
/// The distinction that keeps both rules true: a shop that has been trading can
/// never be stopped, and a machine that has never traded loses nothing by being
/// asked once who it belongs to. Every card terminal and every till on the
/// market pairs on first boot; none of them is thought of as locked.
/// </para>
/// </summary>
public sealed partial class SetupViewModel : ViewModelBase
{
    private readonly AppServices _app;

    /// <summary>Raised when the till should get on with opening — connected or not.</summary>
    public event Action? Finished;

    [ObservableProperty] private string _code = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _connected;

    /// <summary>Shown so whoever is installing can see they have the right machine.</summary>
    [ObservableProperty] private string _shopName = "";

    public SetupViewModel(AppServices app)
    {
        _app = app;
        ShopName = app.GetSettings().ShopName;
    }

    [RelayCommand]
    private async Task Connect()
    {
        var code = (Code ?? "").Trim();
        if (code.Length == 0)
        {
            Message = UiText.Pick("Enter the code we sent you.", "请输入我们发给你的激活码。");
            return;
        }

        Busy = true;
        Message = UiText.Pick("Connecting…", "连接中…");

        try
        {
            var result = await _app.Entitlement.ActivateAsync(code);

            if (result.Outcome == RefreshOutcome.Fetched)
            {
                var settings = _app.GetSettings();
                settings.CloudActivationCode = "";
                _app.Settings.Save(settings);
                _app.ReloadSettings();

                Connected = true;
                Message = UiText.Pick($"Connected to {result.ShopId}.", $"已连接到 {result.ShopId}。");

                // Long enough to read, short enough that nobody wonders whether
                // they have to press something.
                await Task.Delay(1200);
                Finished?.Invoke();
                return;
            }

            Message = result.Outcome switch
            {
                RefreshOutcome.Rejected => UiText.Pick(
                    "That code was not recognised. Check it, or ask us for a new one — they expire after a week.",
                    "激活码无法识别。请核对，或向我们索取新的 —— 激活码一周后失效。"),
                RefreshOutcome.Unreachable => UiText.Pick(
                    "Could not reach us. Check the internet, or set this up later — the till works either way.",
                    "无法连接。请检查网络，或稍后再设置 —— 收银台照常可用。"),
                RefreshOutcome.ClientTooOld => UiText.Pick(
                    "This till needs updating first. It will update itself shortly.",
                    "此收银台需要先更新，稍后会自动完成。"),
                _ => result.Detail,
            };
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// Gets on with it. Says what it costs first, because a merchant who skipped
    /// without knowing would have been misled rather than informed.
    /// </summary>
    [RelayCommand]
    private void Skip() => Finished?.Invoke();
}
