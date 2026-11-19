using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.ViewModels;

public sealed record PcFieldRow(string Label, string Value);

public sealed partial class PcSectionViewModel(PcSection section) : ObservableObject
{
    public PcSection Section { get; } = section;
    public string Title => Section.Title;

    [ObservableProperty] private IReadOnlyList<PcFieldRow> _rows = [];

    public void Refresh(bool includeSerials) =>
        Rows = Section.Fields.Select(f => new PcFieldRow(f.Label, f.IsSerial && !includeSerials ? PcDetailsLogic.HiddenSerial : f.Value)).ToList();
}

/// <summary>Fase 8.6: abas do Meu PC, diagrama esquemático e exportação (.txt/.html/.json, seriais fora por padrão).</summary>
public sealed partial class HardwareViewModel
{
    private PcDetails? _details;

    public ObservableCollection<PcSectionViewModel> PcSections { get; } = [];
    public ObservableCollection<SlotDetail> DiagramSlots { get; } = [];
    public ObservableCollection<string> DiagramGpus { get; } = [];
    public ObservableCollection<string> DiagramDisks { get; } = [];

    [ObservableProperty] private bool _includeSerials;
    [ObservableProperty] private string _diagramCpu = "—";
    [ObservableProperty] private string _diagramChipset = "—";
    [ObservableProperty] private string _diagramMemory = "—";
    [ObservableProperty] private string _exportStatus = string.Empty;

    partial void OnIncludeSerialsChanged(bool value)
    {
        foreach (var s in PcSections) s.Refresh(value);
    }

    private async Task LoadDetailsAsync()
    {
        try
        {
            _details = await _info.CollectDetailsAsync(_summary);
            var d = _details;
            PcSections.Clear();
            foreach (var section in PcDetailsLogic.Sections(d))
            {
                var vm = new PcSectionViewModel(section);
                vm.Refresh(IncludeSerials);
                PcSections.Add(vm);
            }
            DiagramCpu = d.Summary.Cpu is { } c ? $"{c.Name}\n{c.Cores} núcleos / {c.Threads} threads" : "CPU não identificada";
            DiagramChipset = d.Chipset;
            DiagramMemory = d.Summary.Memory.Count > 0
                ? $"{SystemSummary.Gb(d.Summary.TotalMemoryBytes)} · {d.Slots.Occupied} de {d.Slots.Total} slots"
                : "Memória não identificada";
            DiagramSlots.Clear();
            foreach (var s in d.Slots.Slots) DiagramSlots.Add(s);
            DiagramGpus.Clear();
            foreach (var g in d.Summary.Gpus) DiagramGpus.Add(g.VramBytes is { } v ? $"{g.DisplayName}\n{SystemSummary.Gb(v)} VRAM" : g.DisplayName);
            if (DiagramGpus.Count == 0) DiagramGpus.Add("GPU não identificada");
            DiagramDisks.Clear();
            foreach (var disk in d.Disks) DiagramDisks.Add($"{disk.Model}\n{GueibsOptimizer.Core.LocalizationService.Tr(disk.Kind)} · {SystemSummary.Gb(disk.SizeBytes)} · {GueibsOptimizer.Core.LocalizationService.Tr(disk.Health)}");
            if (DiagramDisks.Count == 0) DiagramDisks.Add("Disco não identificado");
        }
        catch (Exception ex)
        {
            _logger.Error("Falha ao coletar os detalhes do PC", ex);
        }
    }

    [RelayCommand]
    private void ExportReport(string? format)
    {
        if (_details is null) { _dialogs.Info("Exportar relatório", "Aguarde a leitura das informações do PC."); return; }
        format = (format ?? "txt").ToLowerInvariant();
        string filter = format switch
        {
            "html" => "Página HTML (*.html)|*.html",
            "json" => "JSON (*.json)|*.json",
            _ => "Texto (*.txt)|*.txt"
        };
        var path = _dialogs.PickSaveFile(filter, $"Relatorio-PC-{DateTime.Now:yyyyMMdd}.{format}");
        if (path is null) return;
        var sections = PcDetailsLogic.Sections(_details);
        var now = DateTime.Now;
        string content = format switch
        {
            "html" => PcDetailsLogic.ToHtml(sections, IncludeSerials, now),
            "json" => PcDetailsLogic.ToJson(sections, IncludeSerials, now),
            _ => PcDetailsLogic.ToText(sections, IncludeSerials, now)
        };
        try
        {
            File.WriteAllText(path, content, new System.Text.UTF8Encoding(true));
            ExportStatus = $"Relatório salvo em {path}";
            _logger.Info($"Relatório do PC exportado ({format}, seriais {(IncludeSerials ? "incluídos" : "omitidos")}): {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Error("Exportar relatório", $"Não foi possível salvar o arquivo: {ex.Message}");
        }
    }

    [RelayCommand] private void OpenCheckupTab() => SelectedTab = 2;
}
