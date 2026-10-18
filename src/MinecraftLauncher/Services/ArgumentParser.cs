using System.Text.Json;
using MinecraftLauncher.Models;

namespace MinecraftLauncher.Services;

/// <summary>Разбор строк arguments в version.json (1.13+).</summary>
public static class ArgumentParser
{
    public static ArgumentsDef Parse(JsonElement? arguments)
    {
        var result = new ArgumentsDef();
        if (arguments is null || arguments.Value.ValueKind != JsonValueKind.Object) return result;

        var root = arguments.Value;
        result.Game = ParseSection(root, "game");
        result.Jvm = ParseSection(root, "jvm");
        return result;
    }

    private static List<ArgumentElement> ParseSection(JsonElement root, string name)
    {
        var list = new List<ArgumentElement>();
        if (!root.TryGetProperty(name, out var section) || section.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in section.EnumerateArray())
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.String:
                    list.Add(new ArgumentElement { Value = [item.GetString() ?? string.Empty] });
                    break;

                case JsonValueKind.Object:
                {
                    var element = new ArgumentElement();
                    if (item.TryGetProperty("rules", out var rules) &&
                        rules.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var rule in rules.EnumerateArray())
                        {
                            element.Rules.Add(JsonSerializer.Deserialize<Rule>(rule.GetRawText()) ?? new Rule());
                        }
                    }

                    if (item.TryGetProperty("value", out var value))
                    {
                        switch (value.ValueKind)
                        {
                            case JsonValueKind.String:
                                element.Value.Add(value.GetString() ?? string.Empty);
                                break;
                            case JsonValueKind.Array:
                                foreach (var v in value.EnumerateArray())
                                    element.Value.Add(v.GetString() ?? string.Empty);
                                break;
                        }
                    }

                    list.Add(element);
                    break;
                }
            }
        }

        return list;
    }
}