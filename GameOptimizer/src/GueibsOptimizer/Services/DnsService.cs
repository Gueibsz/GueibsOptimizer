using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.Sockets;
using GueibsOptimizer.Core;

namespace GueibsOptimizer.Services;

/// <summary>Provedor de DNS com endereços oficiais (conferidos nas páginas dos provedores).</summary>
public sealed record DnsProvider(string Name, string[] IPv4, string[] IPv6, string Note)
{
    public bool IsAutomatic => IPv4.Length == 0 && IPv6.Length == 0;
    public string AddressesText => IsAutomatic ? "Fornecido pelo roteador (DHCP)" : string.Join(", ", IPv4.Concat(IPv6));
}

public sealed record DnsAdapter(string Id, int InterfaceIndex, string Name, string Description, string[] CurrentServers, bool IsAutomatic)
{
    public string CurrentText => IsAutomatic ? $"Automático (DHCP){(CurrentServers.Length > 0 ? ": " + string.Join(", ", CurrentServers) : string.Empty)}" : string.Join(", ", CurrentServers);
}

public sealed record DnsLatency(DnsProvider Provider, double? MedianMs, int Answered, int Sent)
{
    public string Text => MedianMs is { } ms ? $"{ms:0} ms ({Answered}/{Sent} respostas)" : "sem resposta";
}

/// <summary>
/// 9.4: troca de DNS por adaptador. Antes de mudar, guarda o que estava (servidores fixos ou "automático") para o desfazer.
/// Endereços passam por IPAddress.TryParse e o índice do adaptador é inteiro: nada de texto livre no comando.
/// </summary>
public sealed class DnsService(TweakContext ctx) : IUndoProvider
{
    public const string UndoPrefix = "dns:";

    public static readonly DnsProvider Automatic = new("Automático (DHCP)", [], [], "Usa o DNS que o roteador ou o provedor de internet entrega.");

    public static readonly IReadOnlyList<DnsProvider> Providers =
    [
        Automatic,
        new("Cloudflare", ["1.1.1.1", "1.0.0.1"], ["2606:4700:4700::1111", "2606:4700:4700::1001"], "Foco em velocidade e privacidade."),
        new("Google", ["8.8.8.8", "8.8.4.4"], ["2001:4860:4860::8888", "2001:4860:4860::8844"], "DNS público do Google."),
        new("Quad9", ["9.9.9.9", "149.112.112.112"], ["2620:fe::fe", "2620:fe::9"], "Bloqueia domínios maliciosos conhecidos."),
        new("AdGuard DNS", ["94.140.14.14", "94.140.15.15"], ["2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff"], "Bloqueia anúncios e rastreadores no nível do DNS.")
    ];

    /// <summary>Valida e normaliza uma lista de IPs digitados (separados por vírgula, ponto e vírgula ou espaço).</summary>
    public static (string[] Valid, string[] Invalid) ParseAddresses(string text)
    {
        var parts = (text ?? string.Empty).Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var valid = new List<string>();
        var invalid = new List<string>();
        foreach (var p in parts)
        {
            if (IPAddress.TryParse(p, out var ip) && ip.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 &&
                !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any) &&
                // IPAddress.TryParse aceita formas curtas ("1" = 0.0.0.1); exige a forma completa.
                (ip.AddressFamily == AddressFamily.InterNetwork ? p.Count(c => c == '.') == 3 : p.Contains(':')))
                valid.Add(ip.ToString());
            else invalid.Add(p);
        }
        return (valid.Distinct().ToArray(), invalid.ToArray());
    }

    // ---------- Adaptadores ----------

    public Task<IReadOnlyList<DnsAdapter>> ListAdaptersAsync() => Task.Run<IReadOnlyList<DnsAdapter>>(ListAdapters);

    public IReadOnlyList<DnsAdapter> ListAdapters()
    {
        var list = new List<DnsAdapter>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT SettingID, InterfaceIndex, Description, DNSServerSearchOrder FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");
            var names = AdapterNames();
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    string id = mo["SettingID"]?.ToString() ?? string.Empty;
                    int index = Convert.ToInt32(mo["InterfaceIndex"] ?? 0);
                    var servers = mo["DNSServerSearchOrder"] as string[] ?? [];
                    var (v4, v6) = ReadStatic(id);
                    list.Add(new DnsAdapter(id, index, names.GetValueOrDefault(id) ?? mo["Description"]?.ToString() ?? id,
                        mo["Description"]?.ToString() ?? string.Empty, servers, v4.Length == 0 && v6.Length == 0));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            ctx.Logger.Warn($"DNS: não foi possível listar os adaptadores: {ex.Message}");
        }
        return list;
    }

    private static Dictionary<string, string> AdapterNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = new ManagementObjectSearcher("SELECT GUID, NetConnectionID FROM Win32_NetworkAdapter WHERE NetEnabled = TRUE");
            foreach (ManagementObject mo in s.Get())
                using (mo)
                    if (mo["GUID"]?.ToString() is { } g && mo["NetConnectionID"]?.ToString() is { } n) map[g] = n;
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException) { }
        return map;
    }

    private const string V4Root = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";
    private const string V6Root = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\";

    /// <summary>Servidores fixos (NameServer) do adaptador; vazio = automático.</summary>
    public (string[] V4, string[] V6) ReadStatic(string adapterId)
    {
        static string[] Split(string? s) => (s ?? string.Empty).Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        var v4 = ctx.Registry.Read(RegHive.LocalMachine, V4Root + adapterId, "NameServer")?.Text;
        var v6 = ctx.Registry.Read(RegHive.LocalMachine, V6Root + adapterId, "NameServer")?.Text;
        return (Split(v4), Split(v6));
    }

    // ---------- Aplicar / desfazer ----------

    /// <summary>Script com índice inteiro e IPs já validados (aspas simples do PowerShell).</summary>
    public static string BuildScript(int interfaceIndex, IReadOnlyList<string> servers) =>
        servers.Count == 0
            ? $"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ResetServerAddresses -ErrorAction Stop; Clear-DnsClientCache"
            : $"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ServerAddresses @({string.Join(",", servers.Select(Shell.PsQuote))}) -ErrorAction Stop; Clear-DnsClientCache";

    /// <summary>Aplica servidores (lista vazia = automático). Retorna null se deu certo, senão a mensagem de erro.</summary>
    public async Task<string?> ApplyAsync(DnsAdapter adapter, IReadOnlyList<string> servers)
    {
        var (valid, invalid) = ParseAddresses(string.Join(",", servers));
        if (invalid.Length > 0) return $"Endereço inválido: {string.Join(", ", invalid)}";

        string undoId = UndoPrefix + adapter.Id;
        if (ctx.Backup.ForTweak(undoId).Count == 0 && !ctx.DryRun)
        {
            var (v4, v6) = ReadStatic(adapter.Id);
            ctx.SaveCustomBackup(undoId, "dns-servers", string.Join(",", v4.Concat(v6)), $"DNS de {adapter.Name}: {adapter.CurrentText}");
        }
        var r = await ctx.Shell.RunPowerShellMutatingAsync($"DNS de {adapter.Name} → {(valid.Length == 0 ? "automático" : string.Join(", ", valid))}",
            BuildScript(adapter.InterfaceIndex, valid), TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (!r.Success) return $"Não foi possível alterar o DNS: {(r.StdErr + r.StdOut).Trim()}";
        ctx.Logger.Info(r.Simulated ? $"DNS simulado para {adapter.Name}." : $"DNS de {adapter.Name} alterado.");
        return null;
    }

    public bool CanUndo(string id) => id.StartsWith(UndoPrefix, StringComparison.Ordinal);

    public string DisplayName(string id) => $"DNS do adaptador ({id[UndoPrefix.Length..]})";

    public async Task UndoAsync(string id)
    {
        var backup = ctx.Backup.ForTweak(id).FirstOrDefault(b => b.CustomKey == "dns-servers");
        string adapterId = id[UndoPrefix.Length..];
        var adapter = ListAdapters().FirstOrDefault(a => a.Id.Equals(adapterId, StringComparison.OrdinalIgnoreCase));
        if (adapter is null) { ctx.Logger.Warn($"DNS: adaptador {adapterId} não encontrado para desfazer."); return; }
        var previous = (backup?.CustomValue ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
        var r = await ctx.Shell.RunPowerShellMutatingAsync($"Restaurar DNS de {adapter.Name}", BuildScript(adapter.InterfaceIndex, ParseAddresses(string.Join(",", previous)).Valid),
            TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (r.Success && !ctx.DryRun) ctx.Backup.RemoveTweak(id);
    }

    // ---------- Latência (somente medida) ----------

    /// <summary>Consulta DNS mínima (tipo A, recursão desejada) montada aqui; não depende de biblioteca externa.</summary>
    public static byte[] BuildQuery(ushort id, string domain)
    {
        var bytes = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        foreach (var label in domain.Trim('.').Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        bytes.AddRange([0x00, 0x00, 0x01, 0x00, 0x01]);
        return bytes.ToArray();
    }

    public static bool IsAnswerTo(byte[] response, ushort id) =>
        response.Length >= 12 && response[0] == (byte)(id >> 8) && response[1] == (byte)id && (response[2] & 0x80) != 0;

    public static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return null;
        var s = values.Order().ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    public static readonly string[] TestDomains = ["www.microsoft.com", "www.wikipedia.org", "store.steampowered.com", "www.cloudflare.com"];

    public async Task<DnsLatency> MeasureAsync(DnsProvider provider, CancellationToken ct = default)
    {
        if (provider.IsAutomatic) return new DnsLatency(provider, null, 0, 0);
        var times = new List<double>();
        int sent = 0;
        var server = IPAddress.Parse(provider.IPv4[0]);
        foreach (var domain in TestDomains)
        {
            ct.ThrowIfCancellationRequested();
            sent++;
            ushort id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            try
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                var sw = Stopwatch.StartNew();
                await udp.SendAsync(BuildQuery(id, domain), new IPEndPoint(server, 53), ct).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var result = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                sw.Stop();
                if (IsAnswerTo(result.Buffer, id)) times.Add(sw.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (SocketException) { }
        }
        return new DnsLatency(provider, Median(times), times.Count, sent);
    }
}
