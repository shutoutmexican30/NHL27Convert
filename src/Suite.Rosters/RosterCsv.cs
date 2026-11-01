using Microsoft.VisualBasic.FileIO;

namespace Suite.Rosters;

/// <summary>
/// Stopgap reader: builds a <see cref="RosterDb"/> from a CSV dump of a roster database (exhibitionplayerbiotable.csv,
/// as written by the Worker's db-dump). Replaced by the suite's own T3DB reader when that lands.
/// </summary>
public static class RosterCsv
{
    public static RosterDb Load(string name, string dumpDir)
    {
        var path = Path.Combine(dumpDir, "exhibitionplayerbiotable.csv");
        using var parser = new TextFieldParser(path) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");
        var header = parser.ReadFields()!.Select(h => h.Trim('﻿')).ToArray();
        int Col(string n) => Array.IndexOf(header, n);
        int cGame = Col("game_id"), cFirst = Col("firstname"), cLast = Col("lastname"), cY = Col("year"), cM = Col("month"), cD = Col("day"),
            cTeam = Col("team"), cJersey = Col("jersey"), cPos = Col("position"), cPortrait = Col("hasportrait"), cHead = Col("headid");
        var players = new List<DbPlayer>();
        int row = 0;
        while (!parser.EndOfData)
        {
            var f = parser.ReadFields()!;
            int I(int c) => int.TryParse(f[c], out var v) ? v : -1;
            players.Add(new DbPlayer(row++, f[cGame], f[cFirst], f[cLast], RosterSync.BioDate(I(cY), I(cM), I(cD)),
                I(cTeam), I(cJersey), I(cPos), I(cPortrait) == 1, I(cHead)));
        }
        return new RosterDb(name, dumpDir, players);
    }
}
