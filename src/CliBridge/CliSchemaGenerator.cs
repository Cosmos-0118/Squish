using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ClopWindows.CliBridge;

internal static class SchemaCommandBuilder
{
    public static Command Create(RootCommand root)
    {
        var command = new Command("schema", "Export a JSON schema describing the clop CLI surface.");
        var output = new Option<FileInfo?>(new[] { "-o", "--output" }, "Write schema to this file instead of stdout.");
        var pretty = new Option<bool>("--pretty", () => true, "Pretty-print the generated JSON.");
        command.AddOption(output);
        command.AddOption(pretty);

        command.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(output);
            var schema = CliSchemaGenerator.Generate(root);
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = context.ParseResult.GetValueForOption(pretty)
            };

            var json = JsonSerializer.Serialize(schema, options);
            if (target is null)
            {
                Console.WriteLine(json);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target.FullName)!);
                await File.WriteAllTextAsync(target.FullName, json).ConfigureAwait(false);
                if (!context.ParseResult.GetValueForOption(pretty))
                {
                    Console.WriteLine($"[info] Schema written to {target.FullName}.");
                }
            }

            context.ExitCode = 0;
        });

        return command;
    }
}

internal static class CliSchemaGenerator
{
    public static CliSchema Generate(RootCommand root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var commands = root.Subcommands.Select(BuildCommand).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        return new CliSchema(root.Name, root.Description ?? string.Empty, commands);
    }

    private static CliSchemaCommand BuildCommand(Command command)
    {
        var options = command.Options.Select(BuildOption).OrderBy(o => o.Name, StringComparer.Ordinal).ToList();
        var arguments = command.Arguments.Select(BuildArgument).ToList();
        var children = command.Subcommands.Select(BuildCommand).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        return new CliSchemaCommand(command.Name, command.Description ?? string.Empty, options, arguments, children);
    }

    private static CliSchemaOption BuildOption(Option option)
    {
        var aliases = option.Aliases.Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
        var valueType = option.ValueType?.Name ?? "void";
        var allowsMultiple = option.Arity.MaximumNumberOfValues is > 1 or int.MaxValue;
        return new CliSchemaOption(option.Name, aliases, option.Description ?? string.Empty, option.IsRequired, valueType, allowsMultiple);
    }

    private static CliSchemaArgument BuildArgument(Argument argument)
    {
        var arity = FormatArity(argument.Arity);
        var valueType = argument.ValueType?.Name ?? "string";
        return new CliSchemaArgument(argument.Name, argument.Description ?? string.Empty, argument.Arity.MinimumNumberOfValues > 0, valueType, arity);
    }

    private static string FormatArity(ArgumentArity arity)
    {
        if (arity.MinimumNumberOfValues == arity.MaximumNumberOfValues)
        {
            return arity.MinimumNumberOfValues.ToString();
        }

        var max = arity.MaximumNumberOfValues == int.MaxValue ? "*" : arity.MaximumNumberOfValues.ToString();
        return $"{arity.MinimumNumberOfValues}..{max}";
    }
}

internal sealed record CliSchema(string Name, string Description, IReadOnlyList<CliSchemaCommand> Commands);

internal sealed record CliSchemaCommand(
    string Name,
    string Description,
    IReadOnlyList<CliSchemaOption> Options,
    IReadOnlyList<CliSchemaArgument> Arguments,
    IReadOnlyList<CliSchemaCommand> Subcommands);

internal sealed record CliSchemaOption(
    string Name,
    IReadOnlyList<string> Aliases,
    string Description,
    bool IsRequired,
    string ValueType,
    bool AllowsMultiple);

internal sealed record CliSchemaArgument(
    string Name,
    string Description,
    bool IsRequired,
    string ValueType,
    string Arity);
