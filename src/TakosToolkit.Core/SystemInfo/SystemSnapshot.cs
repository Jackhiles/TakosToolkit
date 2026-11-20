using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace TakosToolkit.Core.SystemInfo;

public sealed record InfoItem(string Label, string Value);

public sealed record InfoSection(string Title, IReadOnlyList<InfoItem> Items);

/// <summary>Collects a one-page summary of the machine, suitable for pasting into a ticket.</summary>
public static class SystemSnapshot
{
    public static Task<IReadOnlyList<InfoSection>> CollectAsync() => Task.Run(Collect);

    public static IReadOnlyList<InfoSection> Collect()
    {
        var cs = QueryFirst("SELECT Manufacturer, Model, Domain, PartOfDomain, TotalPhysicalMemory FROM Win32_ComputerSystem");
        var bios = QueryFirst("SELECT SerialNumber, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
        var os = QueryFirst("SELECT Caption, OSArchitecture, InstallDate, LastBootUpTime, FreePhysicalMemory FROM Win32_OperatingSystem");

        // Custom-built PCs often report placeholders here; the motherboard is more useful.
        string manufacturer = Str(cs, "Manufacturer");
        string model = Str(cs, "Model");
        string serial = Str(bios, "SerialNumber");
        if (IsPlaceholder(model) || IsPlaceholder(serial))
        {
            var board = QueryFirst("SELECT Manufacturer, Product, SerialNumber FROM Win32_BaseBoard");
            if (IsPlaceholder(model) && !IsPlaceholder(Str(board, "Product")))
                model = $"{Str(board, "Manufacturer")} {Str(board, "Product")} (motherboard)";
            if (IsPlaceholder(serial))
                serial = IsPlaceholder(Str(board, "SerialNumber")) ? "Not set by manufacturer" : Str(board, "SerialNumber") + " (motherboard)";
        }

        return
        [
            Section("Device",
                ("Computer name", Environment.MachineName),
                ("Manufacturer", manufacturer),
                ("Model", model),
                ("Serial number", serial),
                ("BIOS", JoinNonEmpty(" · ", Str(bios, "SMBIOSBIOSVersion"), FormatDate(WmiDate(bios, "ReleaseDate"), dateOnly: true))),
                (Bool(cs, "PartOfDomain") ? "Domain" : "Workgroup", Str(cs, "Domain"))),

            Section("Operating system",
                ("Edition", Str(os, "Caption")),
                ("Version", WindowsVersion()),
                ("Architecture", Str(os, "OSArchitecture")),
                ("Installed", FormatDate(WmiDate(os, "InstallDate"))),
                ("Last boot", FormatDate(WmiDate(os, "LastBootUpTime"))),
                ("Uptime", FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64))),
                ("Pending reboot", PendingReboot())),

            Section("User",
                ("Signed in as", $@"{Environment.UserDomainName}\{Environment.UserName}"),
                ("Running as admin", IsAdministrator() ? "Yes" : "No")),

            Section("Hardware", HardwareItems(cs, os)),

            Section("Storage", StorageItems()),

            Section("Network", NetworkItems()),
        ];
    }

    public static string ToTicketText(IReadOnlyList<InfoSection> sections)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== System Snapshot — Takos Toolkit ===");
        sb.AppendLine("Collected: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

        int width = sections.SelectMany(s => s.Items).Select(i => i.Label.Length).DefaultIfEmpty(0).Max();
        foreach (var section in sections)
        {
            sb.AppendLine();
            sb.AppendLine($"[{section.Title}]");
            foreach (var item in section.Items)
            {
                var lines = item.Value.Split('\n');
                sb.AppendLine($"  {item.Label.PadRight(width)} : {lines[0]}");
                foreach (var line in lines.Skip(1))
                    sb.AppendLine($"  {new string(' ', width)}   {line}");
            }
        }
        return sb.ToString();
    }

    private static InfoSection Section(string title, params (string Label, string Value)[] items) =>
        Section(title, items.AsEnumerable());

    private static InfoSection Section(string title, IEnumerable<(string Label, string Value)> items) =>
        new(title, items.Select(i => new InfoItem(i.Label, string.IsNullOrWhiteSpace(i.Value) ? "—" : i.Value.Trim())).ToList());

    private static IEnumerable<(string, string)> HardwareItems(ManagementBaseObject? cs, ManagementBaseObject? os)
    {
        foreach (var cpu in Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
        {
            yield return ("Processor", Str(cpu, "Name"));
            yield return ("Cores / threads", $"{Str(cpu, "NumberOfCores")} / {Str(cpu, "NumberOfLogicalProcessors")}");
        }

        long totalRam = Long(cs, "TotalPhysicalMemory");
        long freeRam = Long(os, "FreePhysicalMemory") * 1024; // reported in KB
        if (totalRam > 0)
            yield return ("Memory", $"{ByteSize.Format(totalRam)} total · {ByteSize.Format(freeRam)} free ({Percent(freeRam, totalRam)})");

        // Skip virtual adapters (Remote Display, Basic Display, Hyper-V, etc.) when real hardware exists.
        var gpus = Query("SELECT Name, PNPDeviceID, DriverVersion FROM Win32_VideoController");
        var physical = gpus.Where(g => Str(g, "PNPDeviceID").StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var gpu in physical.Count > 0 ? physical : gpus)
            yield return ("Graphics", JoinNonEmpty(" · ", Str(gpu, "Name"), Str(gpu, "DriverVersion") is { Length: > 0 } v ? "driver " + v : ""));
    }

    private static IEnumerable<(string, string)> StorageItems()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network))
                continue;

            string label = drive.Name.TrimEnd('\\');
            if (!string.IsNullOrWhiteSpace(drive.VolumeLabel))
                label += $" ({drive.VolumeLabel})";

            string value = $"{ByteSize.Format(drive.AvailableFreeSpace)} free of {ByteSize.Format(drive.TotalSize)} " +
                           $"({Percent(drive.AvailableFreeSpace, drive.TotalSize)} free) · {drive.DriveFormat}";
            if (drive.DriveType != DriveType.Fixed)
                value += " · " + drive.DriveType;
            yield return (label, value);
        }
    }

    private static IEnumerable<(string, string)> NetworkItems()
    {
        NetworkInterface[] adapters;
        try { adapters = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { yield break; }

        foreach (var nic in adapters)
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            var props = nic.GetIPProperties();
            var lines = new List<string>();

            var ipv4 = props.UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => $"{a.Address}/{a.PrefixLength}");
            lines.Add("IP " + JoinNonEmpty(", ", ipv4.ToArray()));

            var gateways = props.GatewayAddresses
                .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(g => g.Address.ToString()).ToArray();
            if (gateways.Length > 0)
                lines.Add("Gateway " + string.Join(", ", gateways));

            var dns = props.DnsAddresses
                .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
                .Select(d => d.ToString()).ToArray();
            if (dns.Length > 0)
                lines.Add("DNS " + string.Join(", ", dns));

            if (!string.IsNullOrEmpty(props.DnsSuffix))
                lines.Add("Suffix " + props.DnsSuffix);

            var mac = nic.GetPhysicalAddress().GetAddressBytes();
            if (mac.Length > 0)
                lines.Add("MAC " + string.Join("-", mac.Select(b => b.ToString("X2"))));

            if (nic.Speed > 0)
                lines.Add("Link " + FormatLinkSpeed(nic.Speed));

            yield return (nic.Name, string.Join("\n", lines));
        }
    }

    private static string WindowsVersion()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        if (key is null)
            return Environment.OSVersion.VersionString;

        string display = key.GetValue("DisplayVersion") as string ?? key.GetValue("ReleaseId") as string ?? "";
        string build = key.GetValue("CurrentBuild") as string ?? "";
        object? ubr = key.GetValue("UBR");
        if (ubr is not null)
            build += "." + ubr;
        return JoinNonEmpty(" · ", display, string.IsNullOrEmpty(build) ? "" : "Build " + build);
    }

    private static string PendingReboot()
    {
        var reasons = new List<string>();
        if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
            reasons.Add("Windows servicing");
        if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            reasons.Add("Windows Update");
        using (var sm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
        {
            if (sm?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 })
                reasons.Add("pending file renames");
        }
        return reasons.Count == 0 ? "No" : "Yes — " + string.Join(", ", reasons);

        static bool KeyExists(string path)
        {
            using var k = Registry.LocalMachine.OpenSubKey(path);
            return k is not null;
        }
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // ---- WMI helpers -------------------------------------------------------------------------

    private static List<ManagementBaseObject> Query(string wql)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            return searcher.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }

    private static ManagementBaseObject? QueryFirst(string wql) => Query(wql).FirstOrDefault();

    private static object? Prop(ManagementBaseObject? obj, string name)
    {
        try { return obj?[name]; }
        catch (ManagementException) { return null; }
    }

    private static string Str(ManagementBaseObject? obj, string name) => Prop(obj, name)?.ToString()?.Trim() ?? "";

    private static bool Bool(ManagementBaseObject? obj, string name) => Prop(obj, name) is true;

    private static long Long(ManagementBaseObject? obj, string name) =>
        Prop(obj, name) is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0;

    private static DateTime? WmiDate(ManagementBaseObject? obj, string name)
    {
        string s = Str(obj, name);
        if (s.Length == 0)
            return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // ---- Formatting --------------------------------------------------------------------------

    internal static bool IsPlaceholder(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim('0', ' ').Length == 0)
            return true;
        string v = value.Trim().ToLowerInvariant();
        return v is "system product name" or "system serial number" or "default string" or "none" or "n/a"
            || v.Contains("to be filled") || v.Contains("o.e.m");
    }

    private static string FormatDate(DateTime? date, bool dateOnly = false) =>
        date is null ? "" : date.Value.ToString(dateOnly ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    internal static string FormatUptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m"
        : t.TotalHours >= 1 ? $"{t.Hours}h {t.Minutes}m"
        : $"{t.Minutes}m";

    private static string FormatLinkSpeed(long bitsPerSecond) =>
        bitsPerSecond >= 1_000_000_000 ? $"{bitsPerSecond / 1_000_000_000.0:0.#} Gbps" : $"{bitsPerSecond / 1_000_000} Mbps";

    private static string Percent(long part, long whole) =>
        whole <= 0 ? "?" : (part / (double)whole).ToString("0%", CultureInfo.InvariantCulture);

    private static string JoinNonEmpty(string separator, params string[] parts) =>
        string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
