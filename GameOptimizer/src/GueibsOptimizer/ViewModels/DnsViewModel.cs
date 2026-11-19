using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.ViewModels;

public sealed partial class DnsLatencyRow(DnsProvider provider) : ObservableObject
{
    public DnsProvider Provider { get; } = provider;
    public string Name => Provider.Name;
    [ObservableProperty] private string _result = "—";
    [ObservableProperty] private bool _isFastest;
}

/// <summary>9.4: DNS por adaptador (com desfazer), teste de latência medido e ajustes de IPv6 (net.*).</summary>
public sealed partial class DnsViewModel(DnsService dns, CategoryTweaksViewModel network, IDialogService dialogs, Logger logger) : ObservableObject
{
    public const string Prefix = "net.";
    public const string CustomName = "Personalizado";

    public static bool IsNetwork(ITweak t) => t.Id.StartsWith(Prefix, StringComparison.Ordinal);

    public CategoryTweaksViewModel Network { get; } = network;
    public ObservableCollection<DnsAdapter> Adapters { get; } = [];
    public IReadOnlyList<string> ProviderNames { get; } = DnsService.Providers.Select(p => p.Name).Append(CustomName).ToList();
    public ObservableCollection<DnsLatencyRow> Latency { get; } = new(DnsService.Providers.Where(p => !p.IsAutomatic).Select(p => new DnsLatencyRow(p)));

    [ObservableProperty] private DnsAdapter? _selectedAdapter;
    [ObservableProperty] private string _selectedProvider = DnsService.Providers[1].Name;
    [ObservableProperty] private string _customServers = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isTesting;

    public bool IsCustom => SelectedProvider == CustomName;
    public string ProviderAddresses => DnsService.Providers.FirstOrDefault(p => p.Name == SelectedProvider) is { } p
        ? $"{LocalizationService.Tr(p.AddressesText)} · {LocalizationService.Tr(p.Note)}" : "Digite um ou mais IPs (IPv4 ou IPv6), separados por vírgula.";

    partial void OnSelectedProviderChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustom));
        OnPropertyChanged(nameof(ProviderAddresses));
    }

    public async Task RefreshAsync()
    {
        var keep = SelectedAdapter?.Id;
        var list = await dns.ListAdaptersAsync();
        Adapters.Clear();
        foreach (var a in list) Adapters.Add(a);
        SelectedAdapter = Adapters.FirstOrDefault(a => a.Id == keep) ?? Adapters.FirstOrDefault();
        if (Adapters.Count == 0) Status = "Nenhum adaptador de rede ativo encontrado.";
        await Network.RefreshAsync();
    }

    /// <summary>Servidores escolhidos (vazio = automático) ou o erro de validação.</summary>
    public (string[] Servers, string? Error) ChosenServers()
    {
        if (IsCustom)
        {
            var (valid, invalid) = DnsService.ParseAddresses(CustomServers);
            if (invalid.Length > 0) return ([], $"Endereço inválido: {string.Join(", ", invalid)}");
            if (valid.Length == 0) return ([], "Digite ao menos um endereço de DNS.");
            return (valid, null);
        }
        var p = DnsService.Providers.First(x => x.Name == SelectedProvider);
        return (p.IPv4.Concat(p.IPv6).ToArray(), null);
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (SelectedAdapter is not { } adapter) { Status = "Escolha um adaptador."; return; }
        var (servers, error) = ChosenServers();
        if (error is not null) { Status = error; return; }
        string target = servers.Length == 0 ? "automático (DHCP)" : string.Join(", ", servers);
        if (!dialogs.Confirm("Alterar DNS", $"Adaptador: {adapter.Name}\nDNS atual: {adapter.CurrentText}\nNovo DNS: {target}\n\nO valor atual fica guardado e pode ser desfeito na aba Restauração. Continuar?"))
            return;
        IsBusy = true;
        try
        {
            var err = await dns.ApplyAsync(adapter, servers);
            Status = err ?? "DNS alterado. Pode ser desfeito na aba Restauração.";
            if (err is not null) dialogs.Error("DNS", err);
            await RefreshAsync();
            WeakReferenceMessenger.Default.Send(new TweaksChangedMessage());
        }
        catch (Exception ex)
        {
            logger.Error("Falha ao alterar o DNS", ex);
            Status = $"Falha ao alterar o DNS: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task TestLatencyAsync()
    {
        if (IsTesting) return;
        IsTesting = true;
        try
        {
            foreach (var row in Latency) { row.Result = "medindo..."; row.IsFastest = false; }
            var results = await Task.WhenAll(Latency.Select(async row =>
            {
                var r = await dns.MeasureAsync(row.Provider);
                row.Result = r.Text;
                return (row, r.MedianMs);
            }));
            if (results.Where(r => r.MedianMs is not null).OrderBy(r => r.MedianMs).FirstOrDefault() is { row: { } best })
                best.IsFastest = true;
            logger.Info("Latência de DNS: " + string.Join("; ", Latency.Select(r => $"{r.Name} {r.Result}")));
        }
        finally { IsTesting = false; }
    }
}
