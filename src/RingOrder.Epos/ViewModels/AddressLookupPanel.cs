using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;
using RingOrder.Epos.Services;

namespace RingOrder.Epos.ViewModels;

/// <summary>
/// The "Find" button next to a postcode box, and the list it produces.
/// <para>
/// Shared by the till and the phone book because they are the same job done in
/// two places, and two copies would drift apart the first time one of them was
/// fixed.
/// </para>
/// <para>
/// It never blocks the address fields. Whatever this returns, the member of staff
/// can ignore it and keep typing — a lookup that stops an order being taken has
/// cost the shop a sale to save a few keystrokes.
/// </para>
/// </summary>
public partial class AddressLookupPanel : ObservableObject
{
    private readonly AddressLookupService _service;
    private readonly Func<string> _readPostcode;
    private readonly Action<AddressCandidate> _onPicked;
    private CancellationTokenSource? _inFlight;

    public AddressLookupPanel(
        AddressLookupService service,
        Func<string> readPostcode,
        Action<AddressCandidate> onPicked)
    {
        _service = service;
        _readPostcode = readPostcode;
        _onPicked = onPicked;
    }

    public ObservableCollection<AddressCandidate> Candidates { get; } = [];

    /// <summary>
    /// What was chosen, and where the provider said it is.
    /// <para>
    /// Kept so the address is stored as a *found* place with its coordinates
    /// rather than as loose text. Without this the delivery-zone work would have
    /// to look every saved address up again — paying a second time for something
    /// the till already knew.
    /// </para>
    /// </summary>
    public AddressCandidate? LastPicked { get; private set; }

    public double? LastLatitude { get; private set; }
    public double? LastLongitude { get; private set; }

    /// <summary>
    /// True when <paramref name="street"/> is still what was picked. Someone who
    /// edits the box afterwards has overridden the provider, and their text must
    /// not be filed under the provider's coordinates.
    /// </summary>
    public bool StillMatches(string? street) =>
        LastPicked is not null &&
        string.Equals(LastPicked.StreetLine, (street ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;

    /// <summary>Drives the results panel's visibility; empty means nothing is in the way.</summary>
    public bool HasCandidates => Candidates.Count > 0;

    public bool HasStatus => Status.Length > 0;

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    [ObservableProperty] private string _lblFind = "Find";
    [ObservableProperty] private string _lblSearching = "Searching…";

    /// <summary>Called from the host's own label refresh, so the panel follows the shop's language.</summary>
    public void RefreshUiLabels()
    {
        LblFind = UiText.FindAddress;
        LblSearching = UiText.SearchingAddress;
    }

    [RelayCommand]
    private async Task FindAsync()
    {
        // A second press replaces the first: someone correcting a typo should not
        // be shown the answer to the postcode they already fixed.
        _inFlight?.Cancel();

        var cts = new CancellationTokenSource();
        _inFlight = cts;

        Candidates.Clear();
        OnPropertyChanged(nameof(HasCandidates));

        IsBusy = true;
        Status = LblSearching;

        try
        {
            var result = await _service.FindAsync(_readPostcode(), cts.Token);
            if (cts.IsCancellationRequested) return;

            foreach (var candidate in result.Candidates) Candidates.Add(candidate);

            LastLatitude = result.Latitude;
            LastLongitude = result.Longitude;
            Status = result.Message;

            // One address is not a choice; fill it in and let them carry on.
            if (Candidates.Count == 1) Pick(Candidates[0]);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search; the newer one owns the status line.
        }
        finally
        {
            // Each call disposes its own source once its await has unwound —
            // never the superseding call's, and never one the HTTP client is
            // still holding a cancellation registration on.
            if (ReferenceEquals(_inFlight, cts))
            {
                IsBusy = false;
                _inFlight = null;
            }

            cts.Dispose();
            OnPropertyChanged(nameof(HasCandidates));
        }
    }

    /// <summary>
    /// Bound to the results list. Touching a row is the pick — no second
    /// "confirm" button, because the list only appears when someone asked for it
    /// and one tap is what a member of staff on the phone has time for.
    /// </summary>
    [ObservableProperty] private AddressCandidate? _selectedCandidate;

    /// <summary>Picking empties the list, which re-enters this setter. Once is enough.</summary>
    private bool _picking;

    partial void OnSelectedCandidateChanged(AddressCandidate? value)
    {
        if (_picking || value is null) return;
        Pick(value);
    }

    public void Pick(AddressCandidate? candidate)
    {
        if (candidate is null) return;

        _picking = true;
        try
        {
            LastPicked = candidate;
            _onPicked(candidate);
            SelectedCandidate = null;
            Candidates.Clear();
            OnPropertyChanged(nameof(HasCandidates));
            Status = candidate.Display;
        }
        finally
        {
            _picking = false;
        }
    }

    public void Reset()
    {
        _inFlight?.Cancel();
        _inFlight = null;

        LastPicked = null;
        LastLatitude = null;
        LastLongitude = null;

        _picking = true;
        try
        {
            SelectedCandidate = null;
            Candidates.Clear();
        }
        finally
        {
            _picking = false;
        }

        OnPropertyChanged(nameof(HasCandidates));
        Status = "";
        IsBusy = false;
    }
}
