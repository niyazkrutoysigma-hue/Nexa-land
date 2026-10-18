using MinecraftLauncher.Common;
using MinecraftLauncher.Models;

namespace MinecraftLauncher.Services;

/// <summary>Диалоги, реализованные поверх WPF-окон (вынесено, чтобы сервисы не зависели от UI).</summary>
public interface IDialogService
{
    /// <summary>Мастер создания/правки сборки. null — пользователь отменил.</summary>
    InstanceProfile? ShowInstanceEditor(InstanceProfile? existing);

    bool Confirm(string title, string message);

    string PromptText(string title, string message, string initial = "");

    string? PickFolder(string? initial = null);

    string[]? PickFiles(params (string Title, string Pattern)[] filters);

    /// <summary>Показать файл или папку в проводнике.</summary>
    void RevealInExplorer(string path);
}