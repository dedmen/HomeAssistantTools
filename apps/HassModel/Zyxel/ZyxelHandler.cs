using Microsoft.Extensions.Configuration;
using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.HassModel.Integration;
using Renci.SshNet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;


namespace HomeAssistantNetDaemon.apps.HassModel.Zyxel
{
    [NetDaemonApp]
    [Focus]
    public class ZyxelHandler
    {
        private readonly IHaContext _ha;
        private readonly IConfiguration _cfg;
        private readonly IMqttEntityManager _entityManager;

        public ZyxelHandler(IHaContext ha, IConfiguration cfg, IMqttEntityManager entityManager)
        {
            _ha = ha;
            _cfg = cfg;
            _entityManager = entityManager;

            Console.WriteLine("ZyxelStartup");

            Task.Run(async () =>
            {
                await entityManager.CreateAsync("binary_sensor.zyxel_lte_connected", options: new EntityCreationOptions { DeviceClass = "connectivity", Name = "LTE Connected", PayloadOn = "on", PayloadOff = "off" });

                await entityManager.CreateAsync("switch.zyxel_lte_enabled", options: new EntityCreationOptions { DeviceClass = "switch", Name = "LTE Enabled", PayloadOn = "on", PayloadOff = "off" });
                (await entityManager.PrepareCommandSubscriptionAsync("switch.zyxel_lte_enabled").ConfigureAwait(false))
                .Subscribe(new Action<string>(async state =>
                {
                    if (state == "on")
                        SetLTEOn(true);
                    else if (state == "off")
                        SetLTEOn(false);
                }));
            });

            Task.Run(async () =>
            {
                bool isOn = await CheckAPNStatus();
                await _entityManager.SetStateAsync("binary_sensor.zyxel_lte_connected", isOn ? "on" : "off").ConfigureAwait(false);
                var switchState = ha.GetState("switch.zyxel_lte_enabled");
                bool isWantedOn = switchState?.State == "on";
                if (isWantedOn != isOn)
                    SetLTEOn(isWantedOn);
            });
        }

        private async void SetLTEOn(bool isOn)
        {
            using var client = new SshClient(_cfg["Zyxel:Host"], _cfg["Zyxel:Username"], _cfg["Zyxel:Password"]);
            client.Connect();

            using ShellStream shell = client.CreateShellStream("shell", 128, 128, 128, 128, 1024);

            shell.DataReceived += (object? sender, Renci.SshNet.Common.ShellDataEventArgs e) =>
            {
                Console.WriteLine(System.Text.Encoding.Default.GetString(e.Data));
            };

            shell.Expect(new Regex(@"[$>]"));

            if (!isOn)
            {
                shell.WriteLine("cfg cellwan_mapn edit --Index 1 --AP_Enable 0");
            }
            else
            {
                shell.WriteLine("cfg cellwan_mapn edit --Index 1 --AP_Enable 1");
                //using SshCommand cmd = client.RunCommand("cfg cellwan_mapn edit --Index 1 --AP_Enable 1");
                //Console.WriteLine(cmd.Result); // "Hello World!\n"
            }

            var result = shell.Expect(new Regex(@"Command Successful."), TimeSpan.FromSeconds(2));

            if (result != null)
                await _entityManager.SetStateAsync("switch.zyxel_lte_enabled", isOn ? "on" : "off").ConfigureAwait(false);
        }

        private async Task<bool> CheckAPNStatus()
        {
            using var client = new SshClient(_cfg["Zyxel:Host"], _cfg["Zyxel:Username"], _cfg["Zyxel:Password"]);
            client.Connect();

            using ShellStream shell = client.CreateShellStream("shell", 128, 128, 128, 128, 1024);

            shell.DataReceived += (object? sender, Renci.SshNet.Common.ShellDataEventArgs e) =>
            {
                Console.WriteLine(System.Text.Encoding.Default.GetString(e.Data));
            };

            shell.Expect(new Regex(@"[$>]"));

            shell.WriteLine("cfg cellwan_mapn get"); //#TODO we want to know if connected too , not just if enabled

            var result = shell.Expect(new Regex(@"1\s*(Enabled|Disabled)"), TimeSpan.FromSeconds(2));

            if (result == null)
                return false;
            if (result.EndsWith("Enabled"))
                return true;
            return false;

            return false;
        }

    }
}
