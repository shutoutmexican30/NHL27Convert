using System.Data;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Suite.Rosters;

namespace Suite.App.Pages;

/// <summary>
/// Full database editor over the open roster database: every table, every in-use record, every field. Integer and
/// fixed-width string cells are written in place (T3Db.SetInt / SetString), so the file size and layout never change and
/// only edited tables' section CRCs are recomputed at save. Records can be added (copy of a record, into the table's own
/// spare slots) or deleted for tables the teams model does not cache. Range checks come from the meta XML: a value that
/// does not fit the field's bits is refused and the cell keeps its old value.
/// </summary>
public sealed class DbEditorPanel : Grid
{
    static readonly SolidColorBrush Dim = new(Color.FromRgb(0x8c, 0x97, 0xa8));
    static readonly SolidColorBrush Soft = new(Color.FromRgb(0xb9, 0xc8, 0xd8));
    static readonly SolidColorBrush PanelBg = new(Color.FromRgb(0x1a, 0x22, 0x30));
    static readonly SolidColorBrush PanelAlt = new(Color.FromRgb(0x15, 0x1a, 0x22));
    static readonly SolidColorBrush Edge = new(Color.FromRgb(0x1d, 0x2b, 0x44));

    readonly ListBox _tables = new() { MinWidth = 320, MaxHeight = 820, Background = PanelBg, Foreground = Brushes.White };
    readonly TextBox _tableFilter = new() { MinWidth = 320, Margin = new Thickness(0, 0, 0, 6), ToolTip = "Filter tables by name" };
    readonly TextBox _rowFilter = new() { MinWidth = 300, ToolTip = "Show only records with a cell containing this text" };
    readonly ComboBox _bulkField = new() { MinWidth = 170, Margin = new Thickness(14, 0, 0, 0), ToolTip = "Field to set on every shown record" };
    readonly TextBox _bulkValue = new() { MinWidth = 90, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Value" };
    readonly Button _bulk = new() { Content = "Set on shown rows", Margin = new Thickness(6, 0, 0, 0) };
    readonly Button _add = new() { Content = "Add record (copy of selected)", Margin = new Thickness(0, 0, 0, 0) };
    readonly Button _delete = new() { Content = "Delete selected record", Margin = new Thickness(8, 0, 0, 0) };
    readonly Button _export = new() { Content = "Export table CSV…", Margin = new Thickness(8, 0, 0, 0) };
    readonly TextBlock _info = new() { Foreground = Soft, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = true,
        SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow,
        EnableRowVirtualization = true, EnableColumnVirtualization = true, MaxHeight = 840, MinHeight = 420, RowHeight = 28,
        Background = PanelBg, Foreground = Brushes.White, RowBackground = PanelAlt, AlternatingRowBackground = PanelBg,
        BorderBrush = Edge, HorizontalGridLinesBrush = Edge, VerticalGridLinesBrush = Edge, FrozenColumnCount = 1,
    };

    readonly Action<string> _status;
    readonly Action _edited;
    TeamEditor? _editor;
    T3Db.Table? _table;
    DataTable? _data;
    bool _loading;

    const string RecordColumn = "#";

    public DbEditorPanel(Action<string> status, Action edited)
    {
        _status = status;
        _edited = edited;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = "Tables", Foreground = Soft, Margin = new Thickness(0, 0, 0, 4) });
        left.Children.Add(_tableFilter);
        left.Children.Add(_tables);
        Children.Add(left);

        var right = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(new TextBlock { Text = "Find", Foreground = Soft, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        top.Children.Add(_rowFilter);
        top.Children.Add(_bulkField);
        top.Children.Add(_bulkValue);
        top.Children.Add(_bulk);
        right.Children.Add(top);
        right.Children.Add(_grid);
        var bottom = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(_add);
        bottom.Children.Add(_delete);
        bottom.Children.Add(_export);
        right.Children.Add(bottom);
        right.Children.Add(_info);
        SetColumn(right, 1);
        Children.Add(right);

        if (Application.Current.TryFindResource("Action") is Style action)
            _bulk.Style = _add.Style = _delete.Style = _export.Style = action;

        _tableFilter.TextChanged += (_, _) => FillTables();
        _tables.SelectionChanged += (_, e) => { e.Handled = true; if (_tables.SelectedItem is ListBoxItem { Tag: string name }) ShowTable(name); };
        _rowFilter.TextChanged += (_, _) => ApplyRowFilter();
        _grid.SelectionChanged += (_, e) => e.Handled = true;
        _bulk.Click += (_, _) => BulkSet();
        _add.Click += (_, _) => AddRecord();
        _delete.Click += (_, _) => DeleteRecord();
        _export.Click += (_, _) => Export();
        SetEnabled(false);
    }

    void SetEnabled(bool on) => _bulk.IsEnabled = _add.IsEnabled = _delete.IsEnabled = _export.IsEnabled = _bulkField.IsEnabled = _bulkValue.IsEnabled = on;

    /// <summary>Shows the database of a freshly opened editor (or clears the panel when null).</summary>
    public void Load(TeamEditor? editor)
    {
        _editor = editor;
        _table = null;
        _data = null;
        _grid.ItemsSource = null;
        _grid.Columns.Clear();
        _bulkField.Items.Clear();
        _info.Text = "";
        SetEnabled(false);
        FillTables();
    }

    T3Db? Db => _editor?.Db;

    void FillTables()
    {
        var keep = (_tables.SelectedItem as ListBoxItem)?.Tag as string;
        _tables.Items.Clear();
        if (Db is null) return;
        var f = _tableFilter.Text.Trim();
        foreach (var t in Db.Tables.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (f.Length > 0 && !t.Name.Contains(f, StringComparison.OrdinalIgnoreCase) && !t.Short.Contains(f, StringComparison.OrdinalIgnoreCase)) continue;
            var item = new ListBoxItem { Content = $"{t.Name}  ({t.InUse}/{t.Slots})", Tag = t.Name, ToolTip = $"{t.Short} · {t.Fields.Count} fields · {t.RecordBytes} bytes per record" };
            _tables.Items.Add(item);
            if (t.Name == keep) _tables.SelectedItem = item;
        }
    }

    static string TypeName(int type) => type switch { 0 => "string", 3 => "int", _ => $"type {type}" };

    void ShowTable(string name, int selectRecord = -1)
    {
        if (Db is null || !Db.Tables.TryGetValue(name, out var t)) return;
        _table = t;
        _loading = true;
        try
        {
            var dt = new DataTable(t.Name);
            dt.Columns.Add(RecordColumn, typeof(int));
            var fields = t.Fields.Values.OrderBy(f => f.BitOffset).ToList();
            foreach (var f in fields)
            {
                dt.Columns.Add(f.Name, f.Type == 3 ? typeof(long) : typeof(string));
            }
            for (int r = 0; r < t.InUse; r++)
            {
                var row = dt.NewRow();
                row[RecordColumn] = r;
                foreach (var f in fields)
                    row[f.Name] = f.Type switch
                    {
                        3 => (object)Db.GetInt(t, r, f.Name),
                        0 => Db.GetString(t, r, f.Name),
                        _ => RawHex(t, r, f),
                    };
                dt.Rows.Add(row);
            }
            dt.AcceptChanges();
            dt.Columns[RecordColumn]!.ReadOnly = true;
            foreach (var f in fields) dt.Columns[f.Name]!.ReadOnly = f.Type is not (0 or 3);
            dt.ColumnChanging += CellChanging;

            _grid.Columns.Clear();
            _grid.Columns.Add(new DataGridTextColumn { Header = new TextBlock { Text = RecordColumn }, Binding = new Binding($"[{RecordColumn}]"), IsReadOnly = true, Width = 56, SortMemberPath = RecordColumn });
            foreach (var f in fields)
            {
                var hi = f.Depth >= 63 ? long.MaxValue : f.RangeLow + (1L << f.Depth) - 1;
                var tip = f.Type == 3 ? $"{f.Name} ({f.Short}) · int · {f.Depth} bits · {f.RangeLow}..{hi}"
                    : f.Type == 0 ? $"{f.Name} ({f.Short}) · string · {f.Depth / 8} bytes (max {f.Depth / 8 - 1} characters)"
                    : $"{f.Name} ({f.Short}) · {TypeName(f.Type)} · {f.Depth} bits · read-only";
                _grid.Columns.Add(new DataGridTextColumn
                {
                    Header = new TextBlock { Text = f.Name, ToolTip = tip, Foreground = f.Type is 0 or 3 ? Brushes.White : Dim },
                    Binding = new Binding($"[{f.Name}]") { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus, ValidatesOnExceptions = true },
                    IsReadOnly = f.Type is not (0 or 3),
                    SortMemberPath = f.Name,
                });
            }
            _data = dt;
            _grid.ItemsSource = dt.DefaultView;

            _bulkField.Items.Clear();
            foreach (var f in fields.Where(f => f.Type is 0 or 3)) _bulkField.Items.Add(f.Name);
            if (_bulkField.Items.Count > 0) _bulkField.SelectedIndex = 0;
            SetEnabled(true);
            bool model = TeamEditor.ModelTables.Contains(t.Name);
            _add.IsEnabled = !model && t.InUse < t.Slots && t.InUse > 0;
            _delete.IsEnabled = !model && t.InUse > 0;
            _info.Text = $"{t.Name} ({t.Short}): {t.InUse} records in use of {t.Slots} slots, {t.Fields.Count} fields. " +
                "Edit a cell and leave it to write it. Hover a column header for its range." +
                (model ? " Records of this table can't be added or deleted here (the Roster and Strategy tabs track them by position)." : "") +
                (t.Name is "stockteamjerseys" or "stockteamequipmenttable"
                    ? " The game reads uniform rows from both nhl_game and initial_experience: make the same change in both game databases." : "");
            ApplyRowFilter();
            if (selectRecord >= 0 && selectRecord < dt.Rows.Count)
            {
                _grid.SelectedIndex = selectRecord;
                _grid.ScrollIntoView(_grid.SelectedItem);
            }
        }
        catch (Exception ex) { _status("Table not shown: " + ex.Message); }
        finally { _loading = false; }
    }

    string RawHex(T3Db.Table t, int record, T3Db.Field f)
    {
        int start = t.RecordsAt + record * t.RecordBytes + f.BitOffset / 8;
        int len = Math.Min((f.BitOffset % 8 + f.Depth + 7) / 8, t.RecordBytes - f.BitOffset / 8);
        return len <= 0 ? "" : Convert.ToHexString(Db!.Bytes, start, len);
    }

    void CellChanging(object sender, DataColumnChangeEventArgs e)
    {
        if (_loading || Db is null || _table is null || e.Column is null || e.Column.ColumnName == RecordColumn) return;
        int record = (int)e.Row[RecordColumn];
        var field = e.Column.ColumnName;
        var old = e.Row[e.Column];
        try
        {
            if (Equals(old, e.ProposedValue)) return;
            Write(record, field, e.ProposedValue);
            _status($"{_table.Name}[{record}].{field}: {old} → {e.ProposedValue}");
        }
        catch (Exception ex)
        {
            e.ProposedValue = old;
            _status("Not written: " + ex.Message);
        }
    }

    void Write(int record, string field, object? value)
    {
        var t = _table!;
        var f = t.Fields[field];
        if (f.Type == 3)
        {
            long v = value switch { long l => l, int i => i, string s => long.Parse(s.Trim()), null or DBNull => throw new FormatException($"{field} needs a number."), _ => Convert.ToInt64(value) };
            Db!.SetInt(t, record, field, v);
        }
        else if (f.Type == 0) Db!.SetString(t, record, field, value as string ?? "");
        else throw new InvalidOperationException($"{field} ({TypeName(f.Type)}) is read-only.");
        _edited();
    }

    void ApplyRowFilter()
    {
        if (_data is null) return;
        var q = _rowFilter.Text.Trim().Replace("'", "''").Replace("[", "[[]").Replace("*", "[*]").Replace("%", "[%]");
        try
        {
            _data.DefaultView.RowFilter = q.Length == 0 ? "" :
                string.Join(" OR ", _data.Columns.Cast<DataColumn>().Select(c => $"CONVERT([{c.ColumnName.Replace("]", "\\]")}], 'System.String') LIKE '%{q}%'"));
        }
        catch (Exception ex) { _status("Filter: " + ex.Message); }
        if (_table is not null) _info.Text = _info.Text.Split("  Showing")[0] + (q.Length == 0 ? "" : $"  Showing {_data.DefaultView.Count} of {_data.Rows.Count}.");
    }

    void BulkSet()
    {
        if (_data is null || _table is null || _bulkField.SelectedItem is not string field) return;
        var rows = _data.DefaultView.Cast<DataRowView>().Select(v => v.Row).ToList();
        if (rows.Count == 0) { _status("No records shown."); return; }
        if (MessageBox.Show($"Set {field} = '{_bulkValue.Text}' on {rows.Count} record(s) of {_table.Name}?", "Database editor",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        int n = 0;
        try
        {
            _loading = true;
            object value = _table.Fields[field].Type == 3 ? long.Parse(_bulkValue.Text.Trim()) : _bulkValue.Text;
            foreach (var row in rows)
            {
                Write((int)row[RecordColumn], field, value);
                row[field] = value;
                n++;
            }
            _status($"{_table.Name}.{field} set on {n} record(s).");
        }
        catch (Exception ex) { _status($"Stopped after {n} record(s): {ex.Message}"); }
        finally { _loading = false; _data.AcceptChanges(); }
    }

    int SelectedRecord => _grid.SelectedItem is DataRowView v ? (int)v.Row[RecordColumn] : -1;

    void AddRecord()
    {
        if (Db is null || _table is null) return;
        int src = SelectedRecord;
        if (src < 0) { _status("Select the record to copy first."); return; }
        if (MessageBox.Show($"Add a copy of {_table.Name}[{src}] right after it?\n\nRecords after it move down one slot. Only do this for " +
                "tables nothing points at by record number (keyed tables such as stockteamjerseys).", "Database editor",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            var t = Db.InsertRecord(_table, src + 1, src);
            _edited();
            _status($"{t.Name}: record {src + 1} added ({t.InUse}/{t.Slots}).");
            FillTables();
            ShowTable(t.Name, src + 1);
        }
        catch (Exception ex) { _status("Not added: " + ex.Message); }
    }

    void DeleteRecord()
    {
        if (Db is null || _table is null) return;
        int at = SelectedRecord;
        if (at < 0) { _status("Select the record to delete first."); return; }
        if (MessageBox.Show($"Delete {_table.Name}[{at}]?\n\nRecords after it move up one slot. Only do this for tables nothing points at " +
                "by record number.", "Database editor", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            var t = Db.DeleteRecord(_table, at);
            _edited();
            _status($"{t.Name}: record {at} deleted ({t.InUse}/{t.Slots}).");
            FillTables();
            ShowTable(t.Name, Math.Min(at, t.InUse - 1));
        }
        catch (Exception ex) { _status("Not deleted: " + ex.Message); }
    }

    void Export()
    {
        if (_data is null || _table is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = _table.Name + ".csv", Title = "Export table" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            static string Cell(object? v)
            {
                var s = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
            }
            var sb = new StringBuilder();
            var cols = _data.Columns.Cast<DataColumn>().Where(c => c.ColumnName != RecordColumn).ToList();
            sb.AppendLine(string.Join(",", cols.Select(c => Cell(c.ColumnName))));
            foreach (DataRow row in _data.Rows) sb.AppendLine(string.Join(",", cols.Select(c => Cell(row[c]))));
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(false));
            _status($"Exported {_data.Rows.Count} records to {dlg.FileName}");
        }
        catch (Exception ex) { _status("Not exported: " + ex.Message); }
    }
}
