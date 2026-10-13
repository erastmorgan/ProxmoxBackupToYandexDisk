using System.Text.Json.Serialization;

namespace ProxmoxBackupToYandexDisk.Core.Models;

public sealed class ProxmoxApiResponse<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

public sealed class ProxmoxTicketResponse
{
    [JsonPropertyName("ticket")]
    public string Ticket { get; set; } = string.Empty;

    [JsonPropertyName("CSRFPreventionToken")]
    public string CsrfPreventionToken { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;
}

public sealed class ProxmoxNode
{
    [JsonPropertyName("node")]
    public string Node { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("cpu")]
    public double Cpu { get; set; }

    [JsonPropertyName("maxcpu")]
    public int MaxCpu { get; set; }

    [JsonPropertyName("mem")]
    public long Mem { get; set; }

    [JsonPropertyName("maxmem")]
    public long MaxMem { get; set; }
}

public sealed class ProxmoxVm
{
    [JsonPropertyName("vmid")]
    public int VmId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("mem")]
    public long Mem { get; set; }

    [JsonPropertyName("maxmem")]
    public long MaxMem { get; set; }

    [JsonPropertyName("cpu")]
    public double Cpu { get; set; }

    [JsonPropertyName("maxcpu")]
    public int MaxCpu { get; set; }

    [JsonPropertyName("disk")]
    public long Disk { get; set; }

    [JsonPropertyName("maxdisk")]
    public long MaxDisk { get; set; }

    [JsonPropertyName("uptime")]
    public long Uptime { get; set; }
}

public sealed class ProxmoxBackupInfo
{
    [JsonPropertyName("volid")]
    public string VolId { get; set; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("ctime")]
    public long CTime { get; set; }

    [JsonPropertyName("vmid")]
    public int VmId { get; set; }

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = string.Empty;
}

public sealed class ProxmoxStorageContent
{
    [JsonPropertyName("volid")]
    public string VolId { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("ctime")]
    public long CTime { get; set; }
}

public sealed class ProxmoxVersion
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("release")]
    public string Release { get; set; } = string.Empty;

    [JsonPropertyName("repoid")]
    public string RepoId { get; set; } = string.Empty;
}

public sealed class ProxmoxStorage
{
    [JsonPropertyName("storage")]
    public string Storage { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("total")]
    public long Total { get; set; }

    [JsonPropertyName("used")]
    public long Used { get; set; }

    [JsonPropertyName("avail")]
    public long Avail { get; set; }

    [JsonPropertyName("enabled")]
    public int Enabled { get; set; } = 1;

    [JsonPropertyName("active")]
    public int Active { get; set; } = 1;
}

public enum ProxmoxVmType
{
    Qemu,
    Lxc
}
