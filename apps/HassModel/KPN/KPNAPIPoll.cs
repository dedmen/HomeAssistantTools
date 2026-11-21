using HomeAssistantNetDaemon.apps.HassModel.HelloWorld;
using HomeAssistantNetDaemon.apps.HassModel.Zyxel;
using KPNAPIPoll;
using Microsoft.Extensions.Configuration;
using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.Extensions.Scheduler;
using Org.BouncyCastle.Asn1.Ocsp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static KPNAPIPoll.KPNRequester;

namespace HomeAssistantNetDaemon.apps.HassModel.KPN
{
    [NetDaemonApp]
    class KPNAPIPoll
    {
        private readonly IHaContext _ha;
        private readonly IMqttEntityManager _entityManager;
        private readonly INetDaemonScheduler _scheduler;
        private readonly PrefixedLog _log;
        private KPNRequester _kr;
        private KPNRequester.ProductEntry _productEntry;

        public KPNAPIPoll(IHaContext ha, IMqttEntityManager entityManager, INetDaemonScheduler scheduler, IConfiguration cfg, ZyxelRouterService zyxel, ILogger<KPNAPIPoll> logger)
        {
            _ha = ha;
            _entityManager = entityManager;
            _scheduler = scheduler;
            _log = new PrefixedLog("[KPN]", logger);
            _kr = new KPNRequester(cfg, zyxel, logger);
            Task.Run(InitLoginAsync);
        }

        private async Task InitLoginAsync()
        {
            StartSetup();

            try
            {
                await _kr.DoLoginAsync();
            }
            catch (System.Net.WebException ex) // Resource temporarily unavailable
            {
                _log.Warning(ex.Message);
                //Thread.Sleep(30000);

                // try again
                _scheduler.RunIn(TimeSpan.FromMinutes(5), () => _ = InitLoginAsync());
                return;
            }
            catch (System.InvalidOperationException ex) // 2FA code not received
            {
                _log.Error(ex.Message);

                // try again
                _scheduler.RunIn(TimeSpan.FromMinutes(5), () => _ = InitLoginAsync());
                return;
            }

            await InitProductEntryAsync();
            _scheduler.RunEvery(TimeSpan.FromMinutes(10), () =>
            {
                if (string.IsNullOrEmpty(_productEntry.id))
                {
                    _ = InitProductEntryAsync();
                    return;
                }

                CheckLimits();
            });
        }

        private async Task InitProductEntryAsync()
        {
            try
            {
                _productEntry = _kr.GetProducts();
                CheckLimits();
            }
            catch (System.InvalidOperationException ex)
            {
                _log.Warning(ex.Message);
                //Thread.Sleep(30000);

                if (ex is System.Net.WebException webEx)
                {
                    if (webEx.Response is System.Net.HttpWebResponse resp)
                    {
                        if (resp.StatusCode == HttpStatusCode.Unauthorized)
                            await _kr.DoLoginAsync();
                    }
                }

                // try again
                _scheduler.RunIn(TimeSpan.FromMinutes(5), () => _ = InitProductEntryAsync());
            }
        }

        private async void CheckLimits()
        {
            ProductUsage usageData;

            try
            {
                usageData = _productEntry.GetUsage(_kr);
            }
            catch (System.InvalidOperationException _)
            {
                // Happens after midnight for a short while, no NL data subscriptíon present
                Thread.Sleep(TimeSpan.FromMinutes(1));
                return;
            }
            catch (System.NullReferenceException ex)
            {
                // uh?
                _log.Error(ex.Message);
                return;
            }

            var availableData = float.Parse(usageData.initial, NumberStyles.Any, CultureInfo.InvariantCulture);
            var usedData = float.Parse(usageData.usage, NumberStyles.Any, CultureInfo.InvariantCulture);

            var newState = new
            {
                data_used = usedData,
                data_avail = availableData
            };

            await _entityManager.SetStateAsync("sensor.kpn", JsonSerializer.Serialize(newState));

            var remainingData = availableData - usedData;

            _log.Info($"Avail {availableData} - Used {usedData} - Remaining GBs {remainingData}");

            const float desiredSpareData = 20;
            const float dataPerOrder = 2; // 2gb per bundle order

            if (remainingData < desiredSpareData)
            {
                var toRequest = (int)Math.Ceiling((desiredSpareData - remainingData) / dataPerOrder);
                _log.Info($"Less than {desiredSpareData}gb available, requesting {toRequest} extensions");

                try
                { 
                    for (int i = 0; i < toRequest; i++)
                    {
                        _productEntry.OrderSubscriptionProduct(_kr);
                        _log.Info($"Ordered {i + 1}/{toRequest}");
                        if (toRequest > i+1) // Delay if we do multiple
                            Thread.Sleep(8000 + Random.Shared.Next(4000));
                    }
                }
                catch (Exception ex)
                {
                    _log.Error($"Ordering threw exception, skipping all orders: {ex.Message}");
                }
            }

        }

        private async void StartSetup()
        {
            // This device will have five sensors. We tie all of the sensors together
            // by sharing the same `identifiers` list with each sensor.
            var identifiers = new[] { "kpn" };

            // It is important that all sensors share the same State Topic so that
            // we can update all values in one go.
            // You will see that in each sensor, the `value_template` defines how
            // we extract the sensor value from the multiple update.
            var stateTopic = "homeassistant/sensor/kpn/state";

            // First we define the device that will own all the sensors. This is passed
            // when we create the first of the sensors.
            var device = new
            {
                identifiers = identifiers,
                name = "KPN"
            };

            // Electricity 
            await _entityManager.CreateAsync("sensor.KPN_data_used", new EntityCreationOptions { Name = "Data Used", DeviceClass = "data_size", }, new
            {
                unit_of_measurement = "GiB",
                icon = "mdi:transmission-tower-export",
                state_class = "measurement",
                value_template = "{{ value_json.data_used }}",

                state_topic = stateTopic, // Note the override of the state topic
                device // Links the sensors together
            });

            await _entityManager.CreateAsync("sensor.KPN_data_avail", new EntityCreationOptions { Name = "Data Available", DeviceClass = "data_size", }, new
            {
                unit_of_measurement = "GiB",
                icon = "mdi:transmission-tower-import",
                state_class = "measurement",
                value_template = "{{ value_json.data_avail }}", // and value from state

                state_topic = stateTopic, // Note the override of the state topic
                device // Links the sensors together
            });

            //var newState = new
            //{
            //    data_used = 0,
            //    data_avail = 0
            //};
            //
            //await _entityManager.SetStateAsync("sensor.kpn", JsonSerializer.Serialize(newState));
        }



    }
}
