using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using PhotoGeoEditor.Core;

namespace PhotoGeoEditor.WinForms;

/// <summary>
/// Главное окно. Слева — список фото, по центру — карта (Leaflet внутри WebView2), справа — редактор EXIF.
/// Вся логика (чтение/запись EXIF, состояние, настройки) — в PhotoGeoEditor.Core, здесь только интерфейс.
/// </summary>
public sealed class MainForm : Form
{
    private const string MapHost = "photogeo.local";
    private const string ProjectFilter = "Проект PhotoGeoEditor (*.pgeo.json)|*.pgeo.json|Все JSON (*.json)|*.json";
    private const string ImageFilter = "Фотографии|*.jpg;*.jpeg;*.png;*.webp;*.tif;*.tiff|Все файлы|*.*";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- логика (библиотека) ----
    private readonly SettingsService _settingsService = new();
    private readonly AppSettings _settings;
    private readonly IGeoMetadataService _metadata = new ExifGeoMetadataService();
    private readonly PhotoLibrary _library;
    private readonly ProjectStateService _stateService = new();

    // ---- элементы интерфейса ----
    private readonly MenuStrip _menu = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusCounts = new();
    private readonly ToolStripStatusLabel _statusMessage = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ListView _list = new();
    private readonly WebView2 _web = new();
    private readonly ToolTip _tip = new(); // полем, иначе сборщик мусора убьёт подсказки

    private readonly Label _lblFile = new();
    private readonly TextBox _tbLat = new();
    private readonly TextBox _tbLon = new();
    private readonly TextBox _tbAlt = new();
    private readonly TextBox _tbDir = new();
    private readonly TextBox _tbTime = new();
    private readonly TextBox _tbMethod = new();
    private readonly TextBox _tbDatum = new();
    private readonly TextBox _tbTaken = new() { ReadOnly = true };
    private readonly Button _btnApply = new() { Text = "Применить" };
    private readonly Button _btnRevert = new() { Text = "Вернуть исходное" };
    private readonly Button _btnClear = new() { Text = "Убрать GPS" };
    private readonly Button _btnCenter = new() { Text = "В центр карты" };

    private ToolStripMenuItem _miDark = null!;
    private ToolStripMenuItem _miDirection = null!;
    private ToolStripMenuItem _miSubfolders = null!;
    private ToolStripMenuItem _miCopies = null!;
    private ToolStripMenuItem _miOverwrite = null!;

    // ---- состояние окна ----
    private readonly Dictionary<Guid, ListViewItem> _rows = new();
    private readonly Dictionary<Guid, string?> _thumbs = new();
    private PhotoItem? _current;
    private bool _mapReady;
    private MapView? _lastView;
    private MapView? _pendingView;
    private string? _projectPath;

    public MainForm(string[] args)
    {
        _settings = _settingsService.Load();
        _library = new PhotoLibrary(_metadata);

        Text = "PhotoGeoEditor — геопозиция фотографий на карте";
        ClientSize = new Size(1340, 800);
        MinimumSize = new Size(1000, 600);
        StartPosition = FormStartPosition.CenterScreen;

        BuildMenu();
        BuildLayout();

        _library.Photos.CollectionChanged += OnPhotosChanged;
        FormClosing += OnFormClosing;

        Load += async (_, _) =>
        {
            try
            {
                ApplyTheme();
                await InitMapAsync();
                await HandleArgsAsync(args);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
    }

    // =================================================================== построение интерфейса

    private static ToolStripMenuItem Item(string text, Keys shortcut, Action action)
    {
        var item = new ToolStripMenuItem(text);
        if (shortcut != Keys.None) item.ShortcutKeys = shortcut;
        item.Click += (_, _) => action();
        return item;
    }

    private void BuildMenu()
    {
        var file = new ToolStripMenuItem("&Файл");
        file.DropDownItems.AddRange(new ToolStripItem[]
        {
            Item("Открыть папку с фото…", Keys.Control | Keys.O, () => _ = OpenFolderAsync()),
            Item("Добавить файлы…", Keys.Control | Keys.Shift | Keys.O, () => _ = AddFilesAsync()),
            new ToolStripSeparator(),
            Item("Сохранить состояние (проект)…", Keys.Control | Keys.S, () => SaveProject()),
            Item("Загрузить состояние (проект)…", Keys.Control | Keys.L, () => LoadProject(null)),
            new ToolStripSeparator(),
            Item("Записать изменения в EXIF", Keys.Control | Keys.E, WriteExif),
            Item("Экспорт в GeoJSON / CSV…", Keys.Control | Keys.Shift | Keys.E, ExportPhotos),
            new ToolStripSeparator(),
            Item("Выход", Keys.None, Close)
        });

        var edit = new ToolStripMenuItem("&Правка");
        edit.DropDownItems.AddRange(new ToolStripItem[]
        {
            Item("Вернуть исходные координаты", Keys.Control | Keys.R, RevertCurrent),
            Item("Убрать GPS у выбранного фото", Keys.None, ClearCurrentGps),
            Item("Убрать выбранное фото из списка", Keys.None, RemoveSelected),
            new ToolStripSeparator(),
            Item("Показать все фото на карте", Keys.Control | Keys.D0, () => PostToMap(new { type = "fit" }))
        });

        _miDark = new ToolStripMenuItem("Тёмная тема")
        {
            CheckOnClick = true,
            ShortcutKeys = Keys.Control | Keys.T,
            Checked = _settings.Theme == AppTheme.Dark
        };
        _miDark.CheckedChanged += (_, _) =>
        {
            _settings.Theme = _miDark.Checked ? AppTheme.Dark : AppTheme.Light;
            ApplyTheme();
            SaveSettings();
        };

        _miDirection = new ToolStripMenuItem("Показывать направление съёмки (стрелки)")
        {
            CheckOnClick = true,
            Checked = _settings.ShowDirectionArrows
        };
        _miDirection.CheckedChanged += (_, _) =>
        {
            _settings.ShowDirectionArrows = _miDirection.Checked;
            PostToMap(new { type = "showDirection", value = _miDirection.Checked });
            SaveSettings();
        };

        _miSubfolders = new ToolStripMenuItem("Искать фото во вложенных папках")
        {
            CheckOnClick = true,
            Checked = _settings.IncludeSubfolders
        };
        _miSubfolders.CheckedChanged += (_, _) =>
        {
            _settings.IncludeSubfolders = _miSubfolders.Checked;
            SaveSettings();
        };

        _miCopies = new ToolStripMenuItem("Режим записи: копии в подпапку geo_edited")
        {
            Checked = _settings.WriteMode == WriteMode.SaveCopies
        };
        _miOverwrite = new ToolStripMenuItem("Режим записи: перезаписывать оригиналы")
        {
            Checked = _settings.WriteMode == WriteMode.Overwrite
        };
        _miCopies.Click += (_, _) => SetWriteMode(WriteMode.SaveCopies);
        _miOverwrite.Click += (_, _) => SetWriteMode(WriteMode.Overwrite);

        var settings = new ToolStripMenuItem("&Настройки");
        settings.DropDownItems.AddRange(new ToolStripItem[]
        {
            _miDark, _miDirection, _miSubfolders, new ToolStripSeparator(), _miCopies, _miOverwrite
        });

        var help = new ToolStripMenuItem("&Справка");
        help.DropDownItems.AddRange(new ToolStripItem[]
        {
            Item("Справка по управлению", Keys.F1, ShowHelp),
            Item("О программе", Keys.None, ShowAbout)
        });

        _menu.Items.AddRange(new ToolStripItem[] { file, edit, settings, help });
    }

    private void BuildLayout()
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 310));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.MultiSelect = false;
        _list.GridLines = true;
        _list.Columns.Add("Файл", 115);
        _list.Columns.Add("Широта", 72);
        _list.Columns.Add("Долгота", 72);
        _list.Columns.Add("Азим.", 48);
        _list.Columns.Add("*", 22);
        _list.SelectedIndexChanged += (_, _) => OnSelectionChanged();
        _list.DoubleClick += (_, _) =>
        {
            if (_current is { HasPosition: true } p) PostToMap(new { type = "focus", id = p.Id });
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) RemoveSelected();
        };

        _web.Dock = DockStyle.Fill;

        table.Controls.Add(_list, 0, 0);
        table.Controls.Add(_web, 1, 0);
        table.Controls.Add(BuildEditorPanel(), 2, 0);

        _status.Items.Add(_statusCounts);
        _status.Items.Add(_statusMessage);

        // Порядок добавления важен для Dock: сначала Fill, потом нижняя и верхняя панели.
        Controls.Add(table);
        Controls.Add(_status);
        Controls.Add(_menu);
        MainMenuStrip = _menu;

        AcceptButton = _btnApply; // Enter в полях = «Применить»
        _btnApply.Click += (_, _) => ApplyEditor();
        _btnRevert.Click += (_, _) => RevertCurrent();
        _btnClear.Click += (_, _) => ClearCurrentGps();
        _btnCenter.Click += (_, _) => PlaceAtMapCenter();

        FillEditor(null);
        UpdateStatus();
    }

    private Control BuildEditorPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(8),
            AutoScroll = true
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var row = 0;

        void AddRow(string label, Control field)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 7, 0, 7)
            }, 0, row);
            field.Dock = DockStyle.Fill;
            panel.Controls.Add(field, 1, row);
            row++;
        }

        _lblFile.AutoSize = false;
        _lblFile.AutoEllipsis = true;
        _lblFile.Height = 24;
        _lblFile.Font = new Font(Font, FontStyle.Bold);
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(_lblFile, 0, row);
        panel.SetColumnSpan(_lblFile, 2);
        _lblFile.Dock = DockStyle.Fill;
        row++;

        AddRow("Широта", _tbLat);
        AddRow("Долгота", _tbLon);
        AddRow("Высота, м", _tbAlt);
        AddRow("Направление, °", _tbDir);
        AddRow("Время GPS (UTC)", _tbTime);
        AddRow("Способ", _tbMethod);
        AddRow("Система коорд.", _tbDatum);
        AddRow("Дата съёмки", _tbTaken);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 10, 0, 0)
        };
        foreach (var b in new[] { _btnApply, _btnRevert, _btnClear, _btnCenter })
        {
            b.Width = 270;
            b.Height = 30;
            buttons.Controls.Add(b);
        }
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(buttons, 0, row);
        panel.SetColumnSpan(buttons, 2);

        _tip.SetToolTip(_tbLat, "Градусы, например 55.753900 (минус — южное полушарие)");
        _tip.SetToolTip(_tbLon, "Градусы, например 37.620800 (минус — западное полушарие)");
        _tip.SetToolTip(_tbDir, "Азимут камеры 0..360, по часовой стрелке от севера");
        _tip.SetToolTip(_tbTime, "Формат: 2026-11-18 09:30:00 (UTC)");
        _tip.SetToolTip(_tbMethod, "GPS, WLAN, MANUAL ...");

        return panel;
    }

    // =================================================================== карта (WebView2)

    private async Task InitMapAsync()
    {
        try
        {
            // Папку данных браузера кладём во временную: рядом с exe писать не всегда можно.
            var env = await CoreWebView2Environment.CreateAsync(null,
                Path.Combine(Path.GetTempPath(), "PhotoGeoEditor_WebView2"));
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;      // правая кнопка нужна нам
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            // Виртуальный https-адрес: у запросов тайлов будет нормальный Referer (OpenStreetMap без него блокирует).
            core.SetVirtualHostNameToFolderMapping(MapHost,
                Path.Combine(AppContext.BaseDirectory, "Assets"),
                CoreWebView2HostResourceAccessKind.Allow);

            core.WebMessageReceived += OnWebMessage;
            core.Navigate($"https://{MapHost}/map.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Не удалось запустить встроенный браузер для карты (WebView2).\n" +
                "Установите «Microsoft Edge WebView2 Runtime» с сайта Microsoft и перезапустите программу.\n\n" + ex.Message,
                "Карта недоступна", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void PostToMap(object message)
    {
        if (!_mapReady || _web.CoreWebView2 is null) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var root = doc.RootElement;

        switch (root.GetProperty("type").GetString())
        {
            case "ready":
                _ = OnMapReadyAsync();
                break;

            case "select":
                if (Guid.TryParse(root.GetProperty("id").GetString(), out var selId)) SelectById(selId);
                break;

            case "moved": // маркер перетащили мышкой
                if (Guid.TryParse(root.GetProperty("id").GetString(), out var movedId))
                {
                    var photo = _library.Photos.FirstOrDefault(p => p.Id == movedId);
                    photo?.MoveTo(root.GetProperty("lat").GetDouble(), root.GetProperty("lng").GetDouble());
                    SetStatus(photo is null ? "" : $"{photo.FileName}: новая позиция {Fmt(photo.Latitude, "F6")}, {Fmt(photo.Longitude, "F6")}");
                }
                break;

            case "place": // правая кнопка мыши по карте
                if (_current is null)
                    SetStatus("Сначала выберите фото в списке слева.");
                else
                    _current.MoveTo(root.GetProperty("lat").GetDouble(), root.GetProperty("lng").GetDouble());
                break;

            case "view":
                _lastView = new MapView
                {
                    Latitude = root.GetProperty("lat").GetDouble(),
                    Longitude = root.GetProperty("lng").GetDouble(),
                    Zoom = (int)Math.Round(root.GetProperty("zoom").GetDouble())
                };
                break;

            case "key":
                HandleForwardedKey(
                    root.GetProperty("key").GetString() ?? "",
                    root.GetProperty("ctrl").GetBoolean(),
                    root.GetProperty("shift").GetBoolean());
                break;
        }
    }

    private async Task OnMapReadyAsync()
    {
        _mapReady = true;
        PostToMap(new
        {
            type = "init",
            tileUrl = _settings.TileUrl,
            dark = _settings.Theme == AppTheme.Dark,
            showDirection = _settings.ShowDirectionArrows
        });

        foreach (var p in _library.Photos.ToList()) await SendPhotoAsync(p);

        if (_pendingView is { } v)
        {
            PostToMap(new { type = "setView", lat = v.Latitude, lng = v.Longitude, zoom = v.Zoom });
            _pendingView = null;
        }
        else if (_library.Photos.Any(p => p.HasPosition))
        {
            PostToMap(new { type = "fit" });
        }

        if (_current is not null) PostToMap(new { type = "select", id = _current.Id });
    }

    private async Task<string?> GetThumbAsync(PhotoItem p)
    {
        if (_thumbs.TryGetValue(p.Id, out var cached)) return cached;

        var path = p.FilePath;
        var size = _settings.ThumbnailSize;
        var uri = await Task.Run(() => ThumbnailService.GetDataUri(path, size)); // в фоне, чтобы окно не зависало
        _thumbs[p.Id] = uri;
        return uri;
    }

    private async Task SendPhotoAsync(PhotoItem p)
    {
        if (!_mapReady) return;

        var thumb = await GetThumbAsync(p);
        if (!_rows.ContainsKey(p.Id)) return; // фото успели убрать из списка

        PostToMap(new
        {
            type = "upsert",
            photo = new
            {
                id = p.Id,
                name = p.FileName,
                lat = p.Latitude,
                lng = p.Longitude,
                dir = p.Direction,
                modified = p.IsModified,
                thumb
            }
        });
    }

    private void HandleForwardedKey(string key, bool ctrl, bool shift)
    {
        Keys keys;
        if (key.Equals("F1", StringComparison.OrdinalIgnoreCase)) keys = Keys.F1;
        else if (key.Length == 1 && ctrl) keys = (Keys)char.ToUpperInvariant(key[0]) | Keys.Control | (shift ? Keys.Shift : Keys.None);
        else return;

        FindShortcut(_menu.Items, keys)?.PerformClick();
    }

    private static ToolStripMenuItem? FindShortcut(ToolStripItemCollection items, Keys keys)
    {
        foreach (ToolStripItem item in items)
        {
            if (item is not ToolStripMenuItem menuItem) continue;
            if (menuItem.ShortcutKeys == keys) return menuItem;
            var inner = FindShortcut(menuItem.DropDownItems, keys);
            if (inner is not null) return inner;
        }
        return null;
    }

    // =================================================================== список фото

    private void OnPhotosChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                foreach (PhotoItem p in e.NewItems!)
                {
                    AddRow(p);
                    _ = SendPhotoAsync(p);
                }
                break;

            case NotifyCollectionChangedAction.Remove:
                foreach (PhotoItem p in e.OldItems!)
                {
                    RemoveRow(p);
                    PostToMap(new { type = "remove", id = p.Id });
                }
                break;

            case NotifyCollectionChangedAction.Reset:
                foreach (var row in _rows.Values) (row.Tag as PhotoItem)!.PropertyChanged -= OnPhotoChanged;
                _rows.Clear();
                _thumbs.Clear();
                _list.Items.Clear();
                PostToMap(new { type = "clear" });
                break;
        }
        UpdateStatus();
    }

    private void AddRow(PhotoItem p)
    {
        var row = new ListViewItem(p.FileName) { Tag = p };
        for (var i = 0; i < 4; i++) row.SubItems.Add("");
        _rows[p.Id] = row;
        UpdateRow(p);
        _list.Items.Add(row);
        p.PropertyChanged += OnPhotoChanged;
    }

    private void RemoveRow(PhotoItem p)
    {
        p.PropertyChanged -= OnPhotoChanged;
        if (_rows.Remove(p.Id, out var row)) _list.Items.Remove(row);
        _thumbs.Remove(p.Id);
    }

    private void UpdateRow(PhotoItem p)
    {
        if (!_rows.TryGetValue(p.Id, out var row)) return;
        row.SubItems[1].Text = Fmt(p.Latitude, "F5");
        row.SubItems[2].Text = Fmt(p.Longitude, "F5");
        row.SubItems[3].Text = Fmt(p.Direction, "F0");
        row.SubItems[4].Text = p.IsModified ? "*" : "";
    }

    private void OnPhotoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not PhotoItem p) return;
        UpdateRow(p);
        if (ReferenceEquals(p, _current)) FillEditor(p);
        _ = SendPhotoAsync(p);
        UpdateStatus();
    }

    private void OnSelectionChanged()
    {
        _current = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as PhotoItem : null;
        FillEditor(_current);
        PostToMap(new { type = "select", id = _current?.Id });
    }

    private void SelectById(Guid id)
    {
        if (!_rows.TryGetValue(id, out var row)) return;
        row.Selected = true;
        row.EnsureVisible();
    }

    // =================================================================== редактор справа

    private static string Fmt(double? value, string format)
        => value.HasValue ? value.Value.ToString(format, Inv) : "";

    private static string FmtTime(DateTime? t)
        => t.HasValue ? t.Value.ToString("yyyy-MM-dd HH:mm:ss", Inv) : "";

    private void FillEditor(PhotoItem? p)
    {
        _lblFile.Text = p?.FileName ?? "Фото не выбрано";
        _tbLat.Text = Fmt(p?.Latitude, "F6");
        _tbLon.Text = Fmt(p?.Longitude, "F6");
        _tbAlt.Text = Fmt(p?.Altitude, "F1");
        _tbDir.Text = Fmt(p?.Direction, "F1");
        _tbTime.Text = FmtTime(p?.GpsTimeUtc);
        _tbMethod.Text = p?.ProcessingMethod ?? "";
        _tbDatum.Text = p?.MapDatum ?? "";
        _tbTaken.Text = FmtTime(p?.DateTaken);

        var enabled = p is not null;
        foreach (var c in new Control[] { _tbLat, _tbLon, _tbAlt, _tbDir, _tbTime, _tbMethod, _tbDatum, _btnApply, _btnRevert, _btnClear, _btnCenter })
            c.Enabled = enabled;
    }

    private bool TryField(string text, string name, out double? value)
    {
        value = null;
        text = text.Trim();
        if (text.Length == 0) return true;
        if (double.TryParse(text.Replace(',', '.'), NumberStyles.Float, Inv, out var d))
        {
            value = d;
            return true;
        }
        Warn($"Поле «{name}»: не удалось разобрать число «{text}».");
        return false;
    }

    // Сравниваем с тем, что сейчас показано в поле, чтобы нажатие «Применить» без правок не помечало фото изменённым
    private static bool SameText(double? current, string format, string text)
        => Fmt(current, format) == text.Trim();

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void ApplyEditor()
    {
        var p = _current;
        if (p is null) return;

        if (!TryField(_tbLat.Text, "Широта", out var lat) || !TryField(_tbLon.Text, "Долгота", out var lon) ||
            !TryField(_tbAlt.Text, "Высота", out var alt) || !TryField(_tbDir.Text, "Направление", out var dir))
            return;

        if (lat.HasValue != lon.HasValue)
        {
            Warn("Укажите и широту, и долготу (или очистите оба поля).");
            return;
        }
        if (lat is { } la && !GeoMath.IsValidLatitude(la))
        {
            Warn("Широта должна быть в диапазоне от -90 до 90.");
            return;
        }
        if (lon is { } lo && !GeoMath.IsValidLongitude(lo))
        {
            Warn("Долгота должна быть в диапазоне от -180 до 180.");
            return;
        }

        DateTime? time = null;
        var timeText = _tbTime.Text.Trim();
        if (timeText.Length > 0)
        {
            if (!DateTime.TryParse(timeText, Inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
            {
                Warn("Время GPS: используйте формат 2026-11-18 09:30:00 (UTC).");
                return;
            }
            time = t;
        }

        if (!SameText(p.Latitude, "F6", _tbLat.Text) || !SameText(p.Longitude, "F6", _tbLon.Text))
        {
            p.Latitude = lat;
            p.Longitude = lon;
        }
        if (!SameText(p.Altitude, "F1", _tbAlt.Text)) p.Altitude = alt;
        if (!SameText(p.Direction, "F1", _tbDir.Text)) p.Direction = dir.HasValue ? GeoMath.NormalizeDirection(dir.Value) : null;
        if (FmtTime(p.GpsTimeUtc) != timeText) p.GpsTimeUtc = time;

        var method = NullIfBlank(_tbMethod.Text);
        if (method != p.ProcessingMethod) p.ProcessingMethod = method;
        var datum = NullIfBlank(_tbDatum.Text);
        if (datum != p.MapDatum) p.MapDatum = datum;

        FillEditor(p);
        PostToMap(new { type = "select", id = p.Id });
    }

    private void RevertCurrent() => _current?.Revert();

    private void ClearCurrentGps() => _current?.ClearGps();

    private void PlaceAtMapCenter()
    {
        if (_current is null || _lastView is null) return;
        _current.MoveTo(_lastView.Latitude, _lastView.Longitude);
        PostToMap(new { type = "select", id = _current.Id });
    }

    private void RemoveSelected()
    {
        if (_current is { } p) _library.Remove(p);
    }

    // =================================================================== файлы, проект, экспорт

    private async Task HandleArgsAsync(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.Equals("--dark", StringComparison.OrdinalIgnoreCase)) _miDark.Checked = true;
            else if (arg is "--help" or "-h" or "/?") ShowHelp();
            else if (Directory.Exists(arg))
                await LoadPathsAsync(PhotoLibrary.EnumerateImages(arg, _settings.IncludeSubfolders));
            else if (File.Exists(arg) && arg.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                LoadProject(arg);
            else if (File.Exists(arg))
                await LoadPathsAsync(new[] { arg });
        }
    }

    private async Task OpenFolderAsync()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Папка с фотографиями",
            SelectedPath = _settings.LastFolder ?? ""
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _settings.LastFolder = dialog.SelectedPath;
        SaveSettings();
        await LoadPathsAsync(PhotoLibrary.EnumerateImages(dialog.SelectedPath, _settings.IncludeSubfolders));
    }

    private async Task AddFilesAsync()
    {
        using var dialog = new OpenFileDialog { Filter = ImageFilter, Multiselect = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await LoadPathsAsync(dialog.FileNames);
    }

    private async Task LoadPathsAsync(IEnumerable<string> files)
    {
        var list = files.ToList();
        if (list.Count == 0)
        {
            SetStatus("Подходящих файлов не найдено.");
            return;
        }

        UseWaitCursor = true;
        SetStatus($"Читаю EXIF: {list.Count} файл(ов)…");
        try
        {
            // EXIF читаем в фоновом потоке
            var (items, errors) = await Task.Run(() =>
            {
                var ok = new List<PhotoItem>();
                var bad = new List<string>();
                foreach (var f in list)
                {
                    try { ok.Add(_library.ReadItem(f)); }
                    catch (Exception ex) { bad.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
                }
                return (ok, bad);
            });

            await Task.WhenAll(items.Select(GetThumbAsync)); // заранее готовим миниатюры

            var added = 0;
            foreach (var item in items)
                if (_library.Add(item)) added++;

            PostToMap(new { type = "fit" });
            SetStatus($"Добавлено фото: {added}, без координат: {items.Count(i => !i.HasPosition)}.");

            if (errors.Count > 0)
                MessageBox.Show(this, "Не удалось прочитать:\n" + string.Join("\n", errors.Take(15)),
                    "Предупреждение", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private bool SaveProject()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = ProjectFilter,
            FileName = _projectPath is null ? "project.pgeo.json" : Path.GetFileName(_projectPath),
            InitialDirectory = _projectPath is null ? "" : Path.GetDirectoryName(_projectPath) ?? ""
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;

        try
        {
            var state = _stateService.Capture(_library, _lastView, _current?.FilePath);
            _stateService.Save(dialog.FileName, state);
            _projectPath = dialog.FileName;
            Text = $"PhotoGeoEditor — {Path.GetFileName(_projectPath)}";
            SetStatus($"Состояние сохранено: {dialog.FileName}");
            return true;
        }
        catch (Exception ex)
        {
            Warn("Не удалось сохранить состояние: " + ex.Message);
            return false;
        }
    }

    private void LoadProject(string? path)
    {
        if (path is null)
        {
            using var dialog = new OpenFileDialog { Filter = ProjectFilter };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            path = dialog.FileName;
        }

        try
        {
            var state = _stateService.Load(path);
            Cursor = Cursors.WaitCursor;

            var problems = _stateService.Restore(_library, state);
            _projectPath = path;
            Text = $"PhotoGeoEditor — {Path.GetFileName(path)}";

            if (state.Map is { } view)
            {
                _lastView = view;
                _pendingView = view;
                if (_mapReady)
                {
                    PostToMap(new { type = "setView", lat = view.Latitude, lng = view.Longitude, zoom = view.Zoom });
                    _pendingView = null;
                }
            }

            if (state.SelectedPath is { } selected)
            {
                var item = _library.Photos.FirstOrDefault(p => string.Equals(p.FilePath, selected, StringComparison.OrdinalIgnoreCase));
                if (item is not null) SelectById(item.Id);
            }

            SetStatus($"Состояние загружено: {_library.Photos.Count} фото.");
            if (problems.Count > 0)
                Warn("Часть файлов не удалось открыть:\n" + string.Join("\n", problems.Take(15)));
        }
        catch (Exception ex)
        {
            Warn("Не удалось загрузить состояние: " + ex.Message);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void WriteExif()
    {
        var changed = _library.Photos.Count(p => p.IsModified);
        if (changed == 0)
        {
            MessageBox.Show(this, "Нет изменённых фотографий.", "Запись EXIF", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_settings.WriteMode == WriteMode.Overwrite &&
            MessageBox.Show(this, $"Будут перезаписаны файлы оригиналов: {changed} шт. Продолжить?",
                "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        SaveResult result;
        Cursor = Cursors.WaitCursor;
        try { result = _library.WriteChanges(_settings.WriteMode); }
        finally { Cursor = Cursors.Default; }

        var message = $"Записано файлов: {result.Saved}.";
        if (_settings.WriteMode == WriteMode.SaveCopies)
            message += $"\nКопии лежат в подпапке «{PhotoLibrary.OutputFolderName}» рядом с оригиналами.";
        if (result.Errors.Count > 0)
            message += "\n\nОшибки:\n" + string.Join("\n", result.Errors.Take(15));

        MessageBox.Show(this, message, "Запись EXIF", MessageBoxButtons.OK,
            result.Errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        UpdateStatus();
    }

    private void ExportPhotos()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "GeoJSON (*.geojson)|*.geojson|CSV (*.csv)|*.csv",
            FileName = "photos"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var exporter = Exporters.All[dialog.FilterIndex - 1];
            var path = Path.ChangeExtension(dialog.FileName, exporter.Extension);
            exporter.Export(_library.Photos, path);
            SetStatus($"Экспортировано: {path}");
        }
        catch (Exception ex)
        {
            Warn("Не удалось экспортировать: " + ex.Message);
        }
    }

    // =================================================================== настройки, тема, справка

    private void SetWriteMode(WriteMode mode)
    {
        _settings.WriteMode = mode;
        _miCopies.Checked = mode == WriteMode.SaveCopies;
        _miOverwrite.Checked = mode == WriteMode.Overwrite;
        SaveSettings();
        UpdateStatus();
    }

    private void SaveSettings()
    {
        try { _settingsService.Save(_settings); }
        catch { /* настройки — не критично */ }
    }

    private void ApplyTheme()
    {
        var dark = _settings.Theme == AppTheme.Dark;
        var back = dark ? Color.FromArgb(32, 34, 38) : SystemColors.Control;
        var fore = dark ? Color.Gainsboro : SystemColors.ControlText;
        var field = dark ? Color.FromArgb(45, 48, 54) : SystemColors.Window;

        Recolor(this, dark, back, fore, field);
        PostToMap(new { type = "theme", dark });
    }

    private static void Recolor(Control c, bool dark, Color back, Color fore, Color field)
    {
        if (c is WebView2) return;

        c.BackColor = c is TextBox or ListView ? field : back;
        c.ForeColor = fore;

        if (c is Button button)
        {
            button.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
            button.UseVisualStyleBackColor = !dark;
        }
        if (c is ToolStrip strip)
            foreach (ToolStripItem item in strip.Items) RecolorItem(item, back, fore);

        foreach (Control child in c.Controls) Recolor(child, dark, back, fore, field);
    }

    private static void RecolorItem(ToolStripItem item, Color back, Color fore)
    {
        item.BackColor = back;
        item.ForeColor = fore;
        if (item is ToolStripDropDownItem dropDown)
            foreach (ToolStripItem sub in dropDown.DropDownItems) RecolorItem(sub, back, fore);
    }

    private void ShowHelp()
    {
        using var form = new Form
        {
            Text = "Справка — PhotoGeoEditor",
            Width = 840,
            Height = 740,
            StartPosition = FormStartPosition.CenterParent,
            ShowInTaskbar = false,
            KeyPreview = true
        };
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 10f),
            BorderStyle = BorderStyle.None,
            Text = HelpText.Full.Replace("\r\n", "\n").Replace("\n", Environment.NewLine)
        };
        form.Controls.Add(box);
        form.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Escape or Keys.F1) form.Close();
        };
        form.Shown += (_, _) => box.Select(0, 0);
        form.ShowDialog(this);
    }

    private void ShowAbout()
        => MessageBox.Show(this,
            "PhotoGeoEditor\nПросмотр и редактор положения фотографий на карте по EXIF.\n\n" +
            "C# / .NET 10, WinForms + WebView2 (Leaflet, OpenStreetMap).\n" +
            "Карта © участники OpenStreetMap.",
            "О программе", MessageBoxButtons.OK, MessageBoxIcon.Information);

    // =================================================================== служебное

    private void SetStatus(string message) => _statusMessage.Text = message;

    private void UpdateStatus()
    {
        var all = _library.Photos.Count;
        var withGps = _library.Photos.Count(p => p.HasPosition);
        var modified = _library.Photos.Count(p => p.IsModified);
        var mode = _settings.WriteMode == WriteMode.SaveCopies ? "копии" : "перезапись";
        _statusCounts.Text = $"Фото: {all} | с координатами: {withGps} | изменено: {modified} | запись: {mode}";
    }

    private void Warn(string message)
        => MessageBox.Show(this, message, "PhotoGeoEditor", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_library.Photos.Any(p => p.IsModified))
        {
            var answer = MessageBox.Show(this,
                "Есть изменения, не записанные в файлы. Сохранить состояние приложения в файл проекта, " +
                "чтобы продолжить позже?",
                "Выход", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (answer == DialogResult.Cancel || (answer == DialogResult.Yes && !SaveProject()))
            {
                e.Cancel = true;
                return;
            }
        }
        SaveSettings();
    }
}
