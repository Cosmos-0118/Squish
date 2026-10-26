using System;
using System.CommandLine;
using Squish.Core.Security;
using Squish.Core.Settings;

namespace Squish.CliBridge;

/// <summary>
/// <c>squish automation-token</c> prints the bearer token required by the local HTTP automation API
/// (<c>Authorization: Bearer &lt;token&gt;</c>). The token is created on first use and can be rotated.
/// </summary>
internal static class AutomationTokenCommandBuilder
{
    public static Command Create()
    {
        var command = new Command("automation-token", "Print (or rotate) the access token for the local HTTP automation API.");
        var rotate = new Option<bool>("--rotate") { Description = "Generate a new token and invalidate the old one." };
        command.Options.Add(rotate);

        command.SetAction(parse =>
        {
            SettingsHost.EnsureInitialized();
            var token = SettingsHost.Get(SettingsRegistry.AutomationAccessToken);
            if (parse.GetValue(rotate) || string.IsNullOrWhiteSpace(token))
            {
                token = AutomationRequestGuard.GenerateToken();
                SettingsHost.Set(SettingsRegistry.AutomationAccessToken, token);
            }

            Console.WriteLine(token);
            return 0;
        });

        return command;
    }
}
