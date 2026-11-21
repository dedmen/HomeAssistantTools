using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetDaemon.Extensions.Logging;
using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.Extensions.Tts;
using NetDaemon.Runtime;
using HomeAssistantNetDaemon;
using HomeAssistantNetDaemon.apps.HassModel.Zyxel;
// Add next line if using code generator
//using HomeAssistantGenerated;

#pragma warning disable CA1812


CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

try
{
    await Host.CreateDefaultBuilder(args)
        .UseNetDaemonAppSettings()
        //.UseNetDaemonDefaultLogging()
        .UseCustomLogging()
        .UseNetDaemonRuntime()
        .UseNetDaemonMqttEntityManagement()
        .ConfigureServices((_, services) =>
            services
                .AddAppsFromAssembly(Assembly.GetExecutingAssembly())
                .AddNetDaemonStateManager()
                .AddNetDaemonScheduler()
                .AddSingleton<ZyxelRouterService>()
                .AddHostedService(sp => sp.GetRequiredService<ZyxelRouterService>())
                // Add next line if using code generator
                // .AddHomeAssistantGenerated()
        )
        .Build()
        .RunAsync()
        .ConfigureAwait(false);
}
catch (Exception e)
{
    Console.WriteLine($"Failed to start host... {e}");
    throw;
}