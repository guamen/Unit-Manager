using System.Globalization;
using System.Text;
using FileViewer.Models;

namespace FileViewer.Services;

internal sealed class NetworkDiagnosticLogger
{
    private const string CsvHeader =
        "Timestamp,InternetDownloadMbps,LocalDownloadMbps,LocalUploadMbps,GatewayIp,GatewayLatencyMs,GatewayLossPercent," +
        "InternetLatencyMs,JitterMs,InternetLossPercent,DnsLatencyMs,WifiSignalPercent,LinkSpeedMbps,AdapterName," +
        "ConnectionType,InterfaceErrorsDelta,TopProcess,TopProcessBytesPerSec,Status,Notes";

    private readonly object _fileLock = new();

    public string LogDirectory { get; }

    public NetworkDiagnosticLogger(string? logDirectory = null)
    {
        LogDirectory = string.IsNullOrWhiteSpace(logDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UnitManager",
                "NetworkLogs")
            : logDirectory;
    }

    public void EnsureLogDirectory() => Directory.CreateDirectory(LogDirectory);

    public string StartSession(DateTime startedAt)
    {
        EnsureLogDirectory();
        string timestamp = startedAt.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture);
        for (int index = 0; ; index++)
        {
            string suffix = index == 0 ? string.Empty : $"-{index}";
            string path = Path.Combine(LogDirectory, $"network-log-{timestamp}{suffix}.csv");
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 80 or 183)
            {
                continue;
            }
            using var writer = new StreamWriter(stream);
            writer.WriteLine(CsvHeader);
            return path;
        }
    }

    public void AppendSample(string path, NetworkSample sample)
    {
        lock (_fileLock)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            stream.Seek(0, SeekOrigin.End);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(FormatCsvRow(sample));
        }
    }

    public List<NetworkSample> LoadSamplesFromFile(string filePath)
    {
        var samples = new List<NetworkSample>();
        if (!File.Exists(filePath))
        {
            return samples;
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            return samples;
        }

        while (reader.ReadLine() is string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (TryParseCsvRow(line, out NetworkSample? sample) && sample is not null)
            {
                samples.Add(sample);
            }
        }

        return samples;
    }

    private static string FormatCsvRow(NetworkSample s)
    {
        static string Num(double? v) => v.HasValue ? v.Value.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;
        static string Int(int? v) => v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;

        return string.Join(',',
            Escape(s.Timestamp.ToString("o", CultureInfo.InvariantCulture)),
            Num(s.InternetDownloadMbps),
            s.LocalDownloadMbps.ToString("0.###", CultureInfo.InvariantCulture),
            s.LocalUploadMbps.ToString("0.###", CultureInfo.InvariantCulture),
            Escape(s.GatewayIp),
            Num(s.GatewayLatencyMs),
            s.GatewayLossPercent.ToString("0.#", CultureInfo.InvariantCulture),
            Num(s.InternetLatencyMs),
            Num(s.JitterMs),
            s.InternetLossPercent.ToString("0.#", CultureInfo.InvariantCulture),
            Num(s.DnsLatencyMs),
            Int(s.WifiSignalPercent),
            s.LinkSpeedMbps.ToString("0.#", CultureInfo.InvariantCulture),
            Escape(s.AdapterName),
            Escape(s.ConnectionType),
            s.InterfaceErrorsDelta.ToString(CultureInfo.InvariantCulture),
            Escape(s.TopProcess),
            Num(s.TopProcessBytesPerSec),
            Escape(s.Status.ToString()),
            Escape(s.Notes));
    }

    private static bool TryParseCsvRow(string line, out NetworkSample? sample)
    {
        sample = null;
        List<string> cols = ParseCsvLine(line);
        if (cols.Count < 19)
        {
            return false;
        }

        if (!DateTime.TryParse(cols[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime ts)
            && !DateTime.TryParse(cols[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out ts))
        {
            return false;
        }

        static double? ParseNullableDouble(string value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

        static double ParseDouble(string value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

        static int? ParseNullableInt(string value) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;

        static long ParseLong(string value) =>
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;

        Enum.TryParse(cols[18], ignoreCase: true, out NetworkHealthStatus status);
        string notes = cols.Count > 19 ? cols[19] : string.Empty;

        sample = new NetworkSample(
            Timestamp: ts.Kind == DateTimeKind.Utc ? ts.ToLocalTime() : ts,
            InternetDownloadMbps: ParseNullableDouble(cols[1]),
            LocalDownloadMbps: ParseDouble(cols[2]),
            LocalUploadMbps: ParseDouble(cols[3]),
            GatewayIp: cols[4],
            GatewayLatencyMs: ParseNullableDouble(cols[5]),
            GatewayLossPercent: ParseDouble(cols[6]),
            InternetLatencyMs: ParseNullableDouble(cols[7]),
            JitterMs: ParseNullableDouble(cols[8]),
            InternetLossPercent: ParseDouble(cols[9]),
            DnsLatencyMs: ParseNullableDouble(cols[10]),
            WifiSignalPercent: ParseNullableInt(cols[11]),
            LinkSpeedMbps: ParseDouble(cols[12]),
            AdapterName: cols[13],
            ConnectionType: cols[14],
            InterfaceErrorsDelta: ParseLong(cols[15]),
            TopProcess: cols[16],
            TopProcessBytesPerSec: ParseNullableDouble(cols[17]),
            Status: status,
            Notes: notes);
        return true;
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return value;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>(20);
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
