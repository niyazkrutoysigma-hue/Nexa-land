using System.Text;
using System.Windows;
using System.Windows.Threading;
using MinecraftLauncher.Common;
using MinecraftLauncher.Services;
using MinecraftLauncher.ViewModels;
using MinecraftLauncher.Views;

namespace MinecraftLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppServices.Initialize();
        AppServices.Dialogs = new WpfDialogService();

        SettingsService.Default.Load();

        // Аккаунты и сборки нужны и обычному запуску, и CLI-режимам: без загрузки
        // список сборок был бы пустым и Save() затирал бы instances.json целиком.
        AppServices.Accounts.Load();
        AppServices.Instances.Load();

        // Диагностический режим: NexaLand.exe --selftest [версия]
        var args = e.Args;
        if (args.Contains("--selftest"))
        {
            RunSelfTestAsync(args);
            return;
        }

        // Режим командной строки: --install [версия] / --launch [версия]
        if (args.Contains("--install") || args.Contains("--launch"))
        {
            RunCliAsync(args);
            return;
        }

        // Необработанные исключения — в лог, а не в диалог Fatal
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                LogService.Default.Error("Необработанная ошибка в фоновом потоке", ex);
        };

        LogService.Default.Info($"{NexaBrand.Title} — запуск");

        NexaBrand.EnsureIcon();
        ThemeManager.Default.Apply(SettingsService.Default.Theme, SettingsService.Default.AccentColor);

        // иконки сборок
        foreach (var instance in AppServices.Instances.Instances)
        {
            if (!File.Exists(instance.IconFile)) AppServices.Icons.GenerateInstanceIcon(instance);
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        _ = StartupTasksAsync(window);
    }

    private static async Task StartupTasksAsync(MainWindow window)
    {
        // поиск Java в фоне — не блокируем интерфейс
        await Task.Run(() => AppServices.Java.Refresh());

        if (!SettingsService.Default.AutoUpdateLauncher) return;

        var update = await AppServices.Updates.CheckAsync();
        if (update is { IsNewer: true })
        {
            LogService.Default.Success($"Доступно обновление {NexaBrand.Title} {update.Version}");
            ToastService.Default.Show($"Доступно обновление {update.Version}",
                "Откройте настройки, чтобы скачать новую сборку", LogLevel.Success);
        }
    }

    private async void RunSelfTestAsync(string[] args)
    {
        var index = Array.FindIndex(args, a => a == "--selftest");
        var version = index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--")
            ? args[index + 1]
            : null;

        var service = new SelfTestService();
        LogService.Default.Info("Самопроверка запущена");

        bool ok;
        try
        {
            var task = service.RunAsync(version);
            var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromMinutes(3)));
            ok = completed == task && task.Result;
        }
        catch (Exception ex)
        {
            LogService.Default.Error("Самопроверка упала", ex);
            ok = false;
        }

        var path = service.WriteReport();
        LogService.Default.Info($"Отчёт самопроверки: {path}");

        MessageBox.Show(service.BuildReport,
            ok ? "Самопроверка пройдена" : "Самопроверка: есть замечания",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        Shutdown();
    }

    /// <summary>
    /// Консольные режимы: скачать версию заранее или сразу запустить её.
    /// Полезно для «прогрева» кэша и для автоматизации.
    /// </summary>
    private async void RunCliAsync(string[] args)
    {
        var launch = args.Contains("--launch");
        var flagIndex = Array.FindIndex(args, a => a == (launch ? "--launch" : "--install"));
        var versionId = flagIndex >= 0 && flagIndex + 1 < args.Length && !args[flagIndex + 1].StartsWith("--")
            ? args[flagIndex + 1]
            : null;

        var log = new StringBuilder();
        void Say(string text)
        {
            log.AppendLine(text);
            LogService.Default.Info(text);
        }

        try
        {
            var manifest = await AppServices.Mojang.GetManifestAsync(true);
            var summary = versionId is null
                ? MojangApi.Releases(manifest).First()
                : MojangApi.Find(manifest, versionId) ?? throw new FileNotFoundException($"Версия {versionId} не найдена");

            var instance = AppServices.Instances.Instances
                .FirstOrDefault(i => i.MinecraftVersion == summary.Id && i.Loader == LoaderType.None)
                ?? new InstanceProfile
                {
                    Name = summary.Id,
                    MinecraftVersion = summary.Id,
                    VersionType = summary.Type,
                    ReleaseTime = summary.ReleaseTime,
                    RamMb = SettingsService.Default.DefaultRamMb
                };

            if (AppServices.Instances.Instances.Contains(instance))
                Say($"Используем существующую сборку «{instance.Name}»");
            else
            {
                AppServices.Instances.Add(instance);
                AppServices.Icons.GenerateInstanceIcon(instance);
                Say($"Создана сборка «{instance.Name}»");
            }

            instance.VersionUrl = summary.Url;
            instance.VersionJsonId = summary.Id;
            AppServices.Instances.Save();

            var progress = new Progress<InstallProgress>(p =>
            {
                if (p.Percent % 5 < 1) Say(p.Text);
            });

            Say("Скачиваем файлы версии…");
            await AppServices.Setup.EnsureReadyAsync(instance, progress);
            AppServices.Instances.Save();
            Say("Файлы на месте");

            if (launch)
            {
                var account = AppServices.Accounts.Active;
                if (account is null)
                {
                    account = new Account { Type = "Offline", DisplayName = Environment.UserName };
                    AppServices.Accounts.Add(account);
                    AppServices.Accounts.Active = account;
                    Say($"Создан оффлайн-аккаунт «{account.DisplayName}»");
                }

                var version = await AppServices.Launch.ResolveVersionAsync(instance);
                var config = AppServices.Launch.Prepare(instance, version, account);
                Say($"Запуск: {config.JavaPath}");
                Say($"Classpath: {config.Classpath.Count} элементов");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var code = await AppServices.Launch.LaunchAsync(config);
                        Say($"Игра завершилась с кодом {code}");
                    }
                    catch (Exception ex)
                    {
                        Say("Не удалось запустить игру: " + ex.Message);
                        LogService.Default.Error("Не удалось запустить игру", ex);
                    }
                });

                Say("Процесс игры запущен");
            }
        }
        catch (Exception ex)
        {
            Say("Ошибка: " + ex.Message);
            LogService.Default.Error("CLI-режим завершился ошибкой", ex);
            MessageBox.Show(log.ToString(), $"{NexaBrand.Title} — ошибка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        File.WriteAllText(Path.Combine(AppPaths.Root, "cli.log"), log.ToString());
        if (!launch)
        {
            MessageBox.Show(log.ToString(), $"{NexaBrand.Title} — установка завершена",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogService.Default.Error("Ошибка в интерфейсе", e.Exception);
        ToastService.Default.Error("Что-то пошло не так", e.Exception.Message);

        e.Handled = true;
    }
}