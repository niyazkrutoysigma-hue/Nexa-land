using System.Collections.ObjectModel;
using System.Windows.Media;
using MinecraftLauncher.Common;
using MinecraftLauncher.Models;
using MinecraftLauncher.Services;

namespace MinecraftLauncher.ViewModels;

/// <summary>Карточка мода в каталоге (Modrinth / CurseForge).</summary>
public sealed class ModCard : ObservableObject
{
    private ImageSource? _thumbnail;
    private bool _installed;
    private bool _isBusy;
    private string _state = "";

    public required string ProjectId { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Author { get; init; } = "";
    public string Downloads { get; init; } = "";
    public string? ThumbnailUrl { get; init; }
    public string Source { get; init; } = "modrinth";
    public string? CurseModId { get; init; }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    public bool Installed
    {
        get => _installed;
        set => SetProperty(ref _installed, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    /// <summary>Подпись на кнопке: «Установить» / «Установлен» / «Обновить».</summary>
    public string State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }

    public bool HasUpdate => _state == "Обновить";

    public ImageSource? ThumbnailOrPlaceholder => _thumbnail ?? ThumbnailLoader.Placeholder;
}

/// <summary>Установленный мод в папке mods (меню дополнений сборки).</summary>
public sealed class InstalledMod : ObservableObject
{
    private bool _isBusy;

    public required string Path { get; init; }
    public required string FileName { get; init; }
    public long Size { get; init; }

    public bool Disabled =>
        FileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ||
        FileName.EndsWith(".jar.off", StringComparison.OrdinalIgnoreCase);

    public ModpackService.ModMarker? Marker { get; init; }

    public string Name
    {
        get
        {
            if (Marker is { Title.Length: > 0 } marker) return marker.Title;

            var name = System.IO.Path.GetFileNameWithoutExtension(FileName);
            foreach (var suffix in new[] { "-fabric", "-forge", "-neoforge", "-quilt", "-common", "-client", "-server" })
            {
                name = name.Replace(suffix, "", StringComparison.OrdinalIgnoreCase);
            }

            return name.Replace('-', ' ').Trim();
        }
    }

    public string VersionText => Marker is { VersionNumber.Length: > 0 } m
        ? m.VersionNumber
        : System.IO.Path.GetFileName(FileName);

    public string SizeText => DownloadStatus.Format(Size);
    public string StateText => Disabled ? "выключен" : "включён";
    public string SourceText => Marker?.Source == "curseforge" ? "CurseForge" : "Modrinth";

    public bool HasUpdate { get; init; }
    public string UpdateText => HasUpdate ? "есть обновление" : "";

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }
}

/// <summary>
/// Меню дополнений сборки: установленные моды + каталоги Modrinth и CurseForge.
/// Открывается плюсиком на карточке сборки.
/// </summary>
public sealed class SortOption
{
    public SortOption(string key, string title)
    {
        Key = key;
        Title = title;
    }

    public string Key { get; }
    public string Title { get; }

    public override string ToString() => Title;
}

public sealed class ModsManagerViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly Action _close;

    private string _search = "";
    private string _selectedCategory = "";
    private string _selectedSort = "downloads";
    private ModCard? _selectedCard;
    private InstalledMod? _selectedInstalled;
    private bool _isBusy;
    private string _status = "Готов";
    private long _totalHits;
    private bool _hasMore;
    private string _source = "installed";

    public ModsManagerViewModel(InstanceProfile instance, MainViewModel main, Action close)
    {
        _main = main;
        _close = close;
        Instance = instance;

        RefreshInstalledCommand = new RelayCommand(() => LoadInstalled());
        InstallCommand = new AsyncRelayCommand(() => InstallAsync(_selectedCard), () => _selectedCard is { Installed: false });
        UpdateCommand = new AsyncRelayCommand(() => UpdateAsync(_selectedCard), () => _selectedCard is { Installed: true });
        ToggleCommand = new RelayCommand(param => ToggleInstalled(param as InstalledMod));
        RemoveCommand = new RelayCommand(param => RemoveInstalled(param as InstalledMod));
        OpenModsFolderCommand = new RelayCommand(OpenModsFolder);
        ExportPackCommand = new AsyncRelayCommand(ExportPackAsync);
        ImportPackCommand = new RelayCommand(ImportPack);
        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync);
        LoadMoreCommand = new AsyncRelayCommand(() => LoadCatalogAsync(append: true), () => HasMore && !IsBusy);
        ResetCommand = new RelayCommand(() =>
        {
            Search = string.Empty;
            Category = AllCategories[0];
        });
        SourceCommand = new RelayCommand(param =>
        {
            Source = param?.ToString() ?? "installed";
            if (Source == "installed")
            {
                LoadInstalled();
            }
            else
            {
                _ = LoadCatalogAsync(false);
            }
        });

        LoadInstalled();
    }

    public InstanceProfile Instance { get; }
    public MainViewModel Main => _main;

    public ObservableCollection<ModCard> Catalog { get; } = [];
    public ObservableCollection<InstalledMod> InstalledMods { get; } = [];
    public IReadOnlyList<ModrinthService.ModCategory> AllCategories => ModrinthService.AllCategories;

    public string InstanceTitle => $"{Instance.Name} · {Instance.DisplayVersion}";

    public string ModsFolder => Instance.ModsDir;

    public bool CurseForgeReady => AppServices.CurseForge.IsConfigured;

    public string CurseForgeHint => CurseForgeReady
        ? "CurseForge подключён"
        : "Добавьте CurseForge API-ключ в настройках, чтобы открыть этот источник";

    public string Source
    {
        get => _source;
        set
        {
            if (SetProperty(ref _source, value))
            {
                OnPropertyChanged(nameof(IsInstalledTab));
                OnPropertyChanged(nameof(IsCatalogTab));
                OnPropertyChanged(nameof(IsModrinthTab));
                OnPropertyChanged(nameof(IsCurseForgeTab));
                SourceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsInstalledTab => Source == "installed";
    public bool IsCatalogTab => Source != "installed";
    public bool IsModrinthTab => Source == "modrinth";
    public bool IsCurseForgeTab => Source == "curseforge";

    public string Search
    {
        get => _search;
        set => SetProperty(ref _search, value);
    }

    public ModrinthService.ModCategory Category
    {
        get => AllCategories.FirstOrDefault(c => c.Key == _selectedCategory) ?? AllCategories[0];
        set
        {
            if (value is null) return;
            _selectedCategory = value.Key;
            OnPropertyChanged();
            _ = LoadCatalogAsync(false);
        }
    }

    public string Sort
    {
        get => _selectedSort;
        set
        {
            if (!SetProperty(ref _selectedSort, value)) return;
            _ = LoadCatalogAsync(false);
        }
    }

    public IReadOnlyList<SortOption> Sorts { get; } =
    [
        new("downloads", "Популярные"),
        new("newest", "Новые"),
        new("updated", "Обновлённые"),
        new("follows", "Избранные")
    ];

    public ModCard? SelectedCard
    {
        get => _selectedCard;
        set
        {
            if (SetProperty(ref _selectedCard, value)) UpdateCardCommands();
        }
    }

    public InstalledMod? SelectedInstalled
    {
        get => _selectedInstalled;
        set => SetProperty(ref _selectedInstalled, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value)) LoadMoreCommand.RaiseCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public long TotalHits
    {
        get => _totalHits;
        private set
        {
            if (SetProperty(ref _totalHits, value)) OnPropertyChanged(nameof(TotalText));
        }
    }

    public string TotalText => TotalHits > 0
        ? $"Найдено {TotalHits.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} модов"
        : "Каталог не загружен";

    public bool HasMore
    {
        get => _hasMore;
        private set
        {
            if (SetProperty(ref _hasMore, value)) LoadMoreCommand.RaiseCanExecuteChanged();
        }
    }

    public int CatalogCount => Catalog.Count;

    public string LoadedText => Catalog.Count == 0 ? "Пока ничего не показано" : $"Загружено {Catalog.Count}";

    public AsyncRelayCommand InstallCommand { get; }
    public AsyncRelayCommand UpdateCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand OpenModsFolderCommand { get; }
    public AsyncRelayCommand ExportPackCommand { get; }
    public RelayCommand ImportPackCommand { get; }
    public AsyncRelayCommand CheckUpdatesCommand { get; }
    public AsyncRelayCommand LoadMoreCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand SourceCommand { get; }
    public RelayCommand RefreshInstalledCommand { get; }

    public void Close() => _close();

    private void UpdateCardCommands()
    {
        InstallCommand.RaiseCanExecuteChanged();
        UpdateCommand.RaiseCanExecuteChanged();
    }

    // ---------------- Установленные ----------------

    public void LoadInstalled()
    {
        InstalledMods.Clear();
        SelectedInstalled = null;

        try
        {
            Instance.EnsureFolders();

            foreach (var path in Directory.GetFiles(Instance.ModsDir).OrderBy(System.IO.Path.GetFileName))
            {
                var fileName = System.IO.Path.GetFileName(path);
                if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                    !fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var marker = AppServices.Modpacks.ReadMarker(Instance.ModsDir, fileName);
                InstalledMods.Add(new InstalledMod
                {
                    Path = path,
                    FileName = fileName,
                    Size = new FileInfo(path).Length,
                    Marker = marker
                });
            }
        }
        catch (Exception ex)
        {
            LogService.Default.Warn($"Не удалось прочитать папку модов: {ex.Message}");
        }

        OnPropertyChanged(nameof(InstalledCountText));
        OnPropertyChanged(nameof(NoInstalledText));

        InstalledProjectIds = new HashSet<string>(
            InstalledMods.Where(m => m.Marker is { ProjectId.Length: > 0 } marker)
                .Select(m => m.Marker!.ProjectId),
            StringComparer.OrdinalIgnoreCase);
    }

    public int InstalledCount => InstalledMods.Count;

    public string InstalledCountText => $"Установлено: {InstalledMods.Count}";

    public string NoInstalledText => InstalledMods.Count == 0
        ? "В этой сборке ещё нет модов. Откройте вкладку Modrinth или CurseForge, выберите категорию и установите моды."
        : string.Empty;

    private void ToggleInstalled(InstalledMod? mod)
    {
        if (mod is null) return;

        try
        {
            if (mod.Disabled)
            {
                var restored = System.IO.Path.Combine(Instance.ModsDir,
                    mod.FileName.Replace(".disabled", "", StringComparison.OrdinalIgnoreCase));
                File.Move(mod.Path, restored, overwrite: true);
                ToastService.Default.Info("Мод включён", System.IO.Path.GetFileName(restored));
            }
            else
            {
                File.Move(mod.Path, mod.Path + ".disabled", overwrite: true);
                ToastService.Default.Info("Мод выключен", mod.FileName);
            }

            LoadInstalled();
        }
        catch (Exception ex)
        {
            ToastService.Default.Error("Не удалось переключить мод", ex.Message);
        }
    }

    private void RemoveInstalled(InstalledMod? mod)
    {
        if (mod is null) return;
        if (!AppServices.Dialogs.Confirm("Удалить мод", $"Удалить {mod.Name} из сборки?"))
            return;

        try
        {
            File.Delete(mod.Path);
            if (mod.Marker is not null)
            {
                var marker = System.IO.Path.Combine(Instance.ModsDir, ".nexa", mod.FileName + ".json");
                if (File.Exists(marker)) File.Delete(marker);
            }

            ToastService.Default.Success("Мод удалён", mod.Name);
            LoadInstalled();
        }
        catch (Exception ex)
        {
            ToastService.Default.Error("Не удалось удалить мод", ex.Message);
        }
    }

    private void OpenModsFolder()
    {
        Instance.EnsureFolders();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Instance.ModsDir)
        {
            UseShellExecute = true
        });
    }

    // ---------------- Каталог ----------------

    private CancellationTokenSource? _catalogCts;

    public async Task LoadCatalogAsync(bool append = false)
    {
        if (Source == "installed") return;

        _catalogCts?.Cancel();
        _catalogCts = new CancellationTokenSource();
        var ct = _catalogCts.Token;

        if (!append) Catalog.Clear();

        IsBusy = true;
        Status = Source == "curseforge" ? "CurseForge: загрузка…" : "Modrinth: загрузка…";

        try
        {
            var offset = append ? Catalog.Count : 0;
            var category = Category.ModrinthSlug;
            var loader = LoaderInfo.ModrinthId(Instance.Loader);
            var version = Instance.MinecraftVersion;

            if (Source == "curseforge")
            {
                await LoadCurseForgeAsync(category, offset, append, ct);
            }
            else
            {
                await LoadModrinthAsync(category, loader, version, offset, append, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // смена фильтра — просто игнорируем
        }
        catch (Exception ex)
        {
            Status = "Ошибка загрузки: " + ex.Message;
            LogService.Default.Error("Каталог модов не загружен", ex);
            ToastService.Default.Error("Каталог недоступен", ex.Message);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CatalogCount));
            OnPropertyChanged(nameof(LoadedText));
        }
    }

    private async Task LoadModrinthAsync(string category, string loader, string version, int offset,
        bool append, CancellationToken ct)
    {
        var page = await AppServices.Modrinth.SearchPageAsync(Search, version, loader,
            string.IsNullOrEmpty(category) ? null : category, offset, 100, Sort, ct);

        TotalHits = page.Total;
        HasMore = page.Offset + page.Hits.Count < page.Total && page.Hits.Count > 0;
        Status = HasMore
            ? $"Показано {Catalog.Count + page.Hits.Count} из {page.Total}"
            : $"Показаны все {page.Hits.Count}";

        foreach (var project in page.Hits)
        {
            Catalog.Add(BuildCard(project, "modrinth"));
        }

        _ = PrefetchThumbnails(ct);
    }

    private async Task LoadCurseForgeAsync(string category, int offset, bool append, CancellationToken ct)
    {
        if (!AppServices.CurseForge.IsConfigured)
        {
            Status = CurseForgeHint;
            ToastService.Default.Warn("CurseForge недоступен", CurseForgeHint);
            return;
        }

        int? categoryId = null;
        if (!string.IsNullOrEmpty(category) && CurseForgeService.CategoryMap.TryGetValue(category, out var cfCatId))
            categoryId = cfCatId;

        var mods = await AppServices.CurseForge.SearchAsync(Search, Instance.MinecraftVersion,
            LoaderInfo.ModrinthId(Instance.Loader), categoryId, offset, 50, ct);

        TotalHits = mods.Count;
        HasMore = mods.Count >= 50;
        Status = $"CurseForge: показано {Catalog.Count + mods.Count}";

        foreach (var mod in mods)
        {
            Catalog.Add(BuildCard(mod));
        }

        _ = PrefetchThumbnails(ct);
    }

    private ModCard BuildCard(ModProject project, string source)
    {
        var fileName = GuessInstalledFileName(project);
        var installed = InstalledProjectIds.Contains(project.Id) || IsInstalledByName(project.Title);

        return new ModCard
        {
            ProjectId = project.Id,
            Title = project.Title,
            Description = project.ShortDescription,
            Author = project.Author ?? "",
            Downloads = project.DownloadsText,
            ThumbnailUrl = project.IconUrl,
            Source = source,
            Installed = installed,
            State = installed ? "Установлен" : "Установить",
            Thumbnail = ThumbnailLoader.TryGet(project.IconUrl)
        };
    }

    private ModCard BuildCard(CurseForgeService.Mod mod)
    {
        return new ModCard
        {
            ProjectId = mod.Slug,
            CurseModId = mod.ModId,
            Title = mod.Name,
            Description = mod.Summary,
            Author = mod.AuthorText,
            Downloads = mod.DownloadsText,
            ThumbnailUrl = mod.ThumbnailUrl,
            Source = "curseforge",
            Installed = false,
            State = "Установить",
            Thumbnail = ThumbnailLoader.TryGet(mod.ThumbnailUrl)
        };
    }

    private static string GuessInstalledFileName(ModProject project) => project.Title.Replace(' ', '_');

    /// <summary>Идентификаторы проектов, установленных в папку mods (по метаданным .nexa).</summary>
    private HashSet<string> InstalledProjectIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private bool IsInstalledByName(string title)
    {
        var normalized = title.ToLowerInvariant().Replace(" ", "");
        if (normalized.Length < 3) return false;

        return InstalledMods.Any(m => m.FileName.ToLowerInvariant().Contains(normalized[..Math.Min(10, normalized.Length)]));
    }

    private async Task PrefetchThumbnails(CancellationToken ct)
    {
        var pending = Catalog.Where(c => c.Thumbnail is null && c.ThumbnailUrl is { Length: > 0 }).ToList();
        foreach (var card in pending)
        {
            ct.ThrowIfCancellationRequested();
            var image = await ThumbnailLoader.LoadAsync(card.ThumbnailUrl, 64, ct);
            if (image is not null) card.Thumbnail = image;
        }
    }

    // ---------------- Установка / обновление ----------------

    private async Task InstallAsync(ModCard? card)
    {
        if (card is null || card.Installed) return;

        card.IsBusy = true;
        IsBusy = true;
        Status = $"Установка {card.Title}…";

        var progress = new Progress<InstallProgress>(p => Status = p.Text);

        try
        {
            if (card.Source == "curseforge")
            {
                if (card.CurseModId is null || !long.TryParse(card.CurseModId, out var curseId))
                    throw new InvalidOperationException("Некорректный идентификатор CurseForge");

                var file = await AppServices.CurseForge.GetCompatibleFileAsync(curseId, Instance.MinecraftVersion, Instance.Loader)
                    ?? throw new InvalidOperationException($"Нет версии для {Instance.MinecraftVersion} / {Instance.LoaderName}");

                await AppServices.CurseForge.InstallAsync(file, Instance, progress);
                ToastService.Default.Success("Мод установлен", card.Title);
            }
            else
            {
                var files = await AppServices.Modrinth.GetCompatibleFilesAsync(card.ProjectId,
                    Instance.MinecraftVersion, Instance.Loader);

                var best = files.FirstOrDefault()
                    ?? throw new InvalidOperationException($"Нет совместимой версии для {Instance.DisplayVersion}");

                var info = await AppServices.Modrinth.GetFileInfoAsync(best.Id)
                    ?? throw new InvalidOperationException("Modrinth не вернул информацию о файле");

                var installed = await AppServices.Modrinth.InstallWithDependenciesAsync(info, Instance, progress);
                ToastService.Default.Success("Мод установлен",
                    installed.Count > 1 ? $"{card.Title} + зависимостей: {installed.Count - 1}" : card.Title);
            }

            card.Installed = true;
            card.State = "Установлен";
            LoadInstalled();
            UpdateCardCommands();
        }
        catch (OperationCanceledException)
        {
            // отмена
        }
        catch (Exception ex)
        {
            Status = "Ошибка: " + ex.Message;
            ToastService.Default.Error("Не удалось установить мод", ex.Message);
            LogService.Default.Error($"Установка мода {card.Title} не удалась", ex);
        }
        finally
        {
            card.IsBusy = false;
            IsBusy = false;
        }
    }

    private async Task UpdateAsync(ModCard? card)
    {
        if (card is null) return;

        card.IsBusy = true;
        Status = $"Обновление {card.Title}…";

        try
        {
            var installed = InstalledMods.FirstOrDefault(m =>
                m.Marker is { ProjectId.Length: > 0 } marker && marker.ProjectId == card.ProjectId);

            var latest = await AppServices.Modrinth.GetLatestCompatibleAsync(card.ProjectId, Instance);
            if (latest is null)
            {
                ToastService.Default.Warn("Нет обновления", "Не найдена более новая совместимая версия");
                return;
            }

            if (installed is not null)
            {
                File.Delete(installed.Path);
            }

            var progress = new Progress<InstallProgress>(p => Status = p.Text);
            await AppServices.Modrinth.InstallWithDependenciesAsync(latest, Instance, progress);

            card.State = "Установлен";
            ToastService.Default.Success("Мод обновлён", $"{card.Title} {latest.VersionNumber}");
            LoadInstalled();
        }
        catch (Exception ex)
        {
            ToastService.Default.Error("Не удалось обновить мод", ex.Message);
        }
        finally
        {
            card.IsBusy = false;
        }
    }

    private async Task CheckUpdatesAsync()
    {
        IsBusy = true;
        var progress = new Progress<InstallProgress>(p => Status = p.Text);

        try
        {
            var updates = await AppServices.Modrinth.CheckUpdatesAsync(Instance, progress);
            if (updates.Count == 0)
            {
                ToastService.Default.Success("Все моды актуальны");
                return;
            }

            var apply = AppServices.Dialogs.Confirm("Обновления модов",
                $"Найдено обновлений: {updates.Count}. Обновить все?");

            foreach (var update in updates)
            {
                if (!apply) break;

                try
                {
                    File.Delete(update.Installed.FileName.Length > 0
                        ? System.IO.Path.Combine(Instance.ModsDir, update.Installed.FileName)
                        : update.Installed.FileName);

                    var installedFiles = await AppServices.Modrinth.InstallWithDependenciesAsync(
                        update.Latest, Instance, progress);
                    LogService.Default.Info($"Обновлён мод {update.Installed.Title} ({installedFiles.Count} файлов)");
                }
                catch (Exception ex)
                {
                    LogService.Default.Warn($"Обновление {update.Installed.Title} не удалось: {ex.Message}");
                }
            }

            LoadInstalled();
            ToastService.Default.Success("Проверка завершена", $"Обновлений: {updates.Count}");
        }
        catch (Exception ex)
        {
            ToastService.Default.Error("Проверка обновлений не удалась", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------------- Модпаки ----------------

    private async Task ExportPackAsync()
    {
        IsBusy = true;
        try
        {
            var path = await AppServices.Modpacks.ExportAsync(Instance);
            ToastService.Default.Show("Модпак сохранён", path, LogLevel.Success,
                () => AppServices.Dialogs.RevealInExplorer(path), "Открыть");
        }
        catch (Exception ex)
        {
            ToastService.Default.Error("Экспорт не удался", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ImportPack()
    {
        var files = AppServices.Dialogs.PickFiles(new[] { ("Modpack Modrinth (*.mrpack)", "*.mrpack") });
        if (files is null || files.Length == 0) return;

        IsBusy = true;
        var progress = new Progress<InstallProgress>(p => Status = p.Text);

        Task.Run(async () =>
        {
            try
            {
                var result = await AppServices.Modpacks.ImportAsync(files[0], Instance, progress);
                LoadInstalled();
                ToastService.Default.Success("Модпак импортирован",
                    $"Установлено: {result.Installed}, пропущено: {result.Skipped}");
            }
            catch (Exception ex)
            {
                ToastService.Default.Error("Импорт не удался", ex.Message);
                LogService.Default.Error("Импорт модпака не удался", ex);
            }
            finally
            {
                IsBusy = false;
            }
        });
    }
}