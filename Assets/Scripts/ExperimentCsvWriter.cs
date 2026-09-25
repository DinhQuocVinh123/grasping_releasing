using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Ghi 1 file CSV, moi dong ghi xuong dia NGAY (flush) -- neu ung dung bi tat
/// giua chung thi nhung luot da xong van con nguyen trong file.
///
/// File nam trong Application.persistentDataPath/Experiments/. Tren Quest la:
///   /sdcard/Android/data/&lt;ten goi ung dung&gt;/files/Experiments/
/// Lay ve may tinh bang: adb pull /sdcard/Android/data/&lt;ten goi&gt;/files/Experiments .
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
                // Dau phay trong chu se lam lech cot -> thay bang ';'
                return Convert.ToString(value, CultureInfo.InvariantCulture).Replace(',', ';').Replace('\n', ' ');
        }
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
    }
}
