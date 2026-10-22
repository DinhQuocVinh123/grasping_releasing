using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Writes one CSV file and flushes every row to disk IMMEDIATELY, so finished trials survive if the app
/// is closed halfway through.
///
/// Files go to Application.persistentDataPath/Experiments/. On the Quest that is:
///   /sdcard/Android/data/&lt;package name&gt;/files/Experiments/
/// Copy them to the PC with: adb pull /sdcard/Android/data/&lt;package name&gt;/files/Experiments .
/// </summary>
public sealed class ExperimentCsvWriter : IDisposable
{
    private StreamWriter _writer;

    public string FilePath { get; }

    public ExperimentCsvWriter(string fileName, params string[] header)
    {
        string folder = Path.Combine(Application.persistentDataPath, "Experiments");
        Directory.CreateDirectory(folder);
        FilePath = Path.Combine(folder, fileName);
        _writer = new StreamWriter(FilePath, false, new UTF8Encoding(false));
        WriteRow(header);
    }

    public void WriteRow(params object[] values)
    {
        if (_writer == null) return;

        var sb = new StringBuilder();
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Format(values[i]));
        }
        _writer.WriteLine(sb.ToString());
        _writer.Flush();
    }

    private static string Format(object value)
    {
        switch (value)
        {
            case null: return "";
            case float f: return f.ToString("0.#####", CultureInfo.InvariantCulture);
            case double d: return d.ToString("0.#####", CultureInfo.InvariantCulture);
            case bool b: return b ? "1" : "0";
            default:
                // A comma inside text would shift the columns -> replace it with ';'
                return Convert.ToString(value, CultureInfo.InvariantCulture).Replace(',', ';').Replace('\n', ' ');
        }
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
    }
}
