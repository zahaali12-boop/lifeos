using System.Text;

namespace Quicker.Web.Imports;

/// <summary>RFC 4180 rows: commas between fields, quotes around a field with commas, quotes or line breaks, a doubled quote inside.</summary>
public static class Csv
{
    public static IReadOnlyList<IReadOnlyList<string>> Rows(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var start = csv.Length > 0 && csv[0] == '﻿' ? 1 : 0;
        for (var i = start; i < csv.Length; i++)
        {
            var c = csv[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < csv.Length && csv[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
