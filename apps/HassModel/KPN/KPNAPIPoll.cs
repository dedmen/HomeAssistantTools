using HomeAssistantNetDaemon.apps.HassModel.HelloWorld;
using KPNAPIPoll;
using Microsoft.Extensions.Configuration;
using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.Extensions.Scheduler;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HomeAssistantNetDaemon.apps.HassModel.KPN
{
    [NetDaemonApp]
    [Focus]
    class KPNAPIPoll
    {
        private readonly IHaContext _ha;
        private readonly IMqttEntityManager _entityManager;
        private KPNRequester _kr;
        private KPNRequester.ProductEntry _productEntry;

        public KPNAPIPoll(IHaContext ha, IMqttEntityManager entityManager, INetDaemonScheduler scheduler, IConfiguration cfg)
        {
            _ha = ha;
            _entityManager = entityManager;
            _kr = new KPNRequester(cfg);
            _kr.DoLogin();
            _productEntry = _kr.GetProducts();

            //StartSetup();
            CheckLimits();
            scheduler.RunEvery(TimeSpan.FromMinutes(30), () =>
            {
                CheckLimits();
            });
        }

        private async void CheckLimits()
        {
            var usageData = _productEntry.GetUsage(_kr);

            var availableData = float.Parse(usageData.initial, NumberStyles.Any, CultureInfo.InvariantCulture);
            var usedData = float.Parse(usageData.usage, NumberStyles.Any, CultureInfo.InvariantCulture);

            var newState = new
            {
                data_used = usedData,
                data_avail = availableData
            };

            await _entityManager.SetStateAsync("sensor.kpn", JsonSerializer.Serialize(newState));

            Console.WriteLine($"Avail {availableData} - Used {usedData}");

            var remainingData = availableData - usedData;
            Console.WriteLine($"Remaining GBs {remainingData}");

            const float desiredSpareData = 12;
            const float dataPerOrder = 2; // 2gb per bundle order

            if (remainingData < desiredSpareData)
            {
                var toRequest = (int)Math.Ceiling((desiredSpareData - remainingData) / dataPerOrder);
                Console.WriteLine($"Less than {desiredSpareData}gb available, requesting {toRequest} extensions");

                for (int i = 0; i < toRequest; i++)
                {
                    _kr.OrderPacket();
                    if (toRequest > i+1) // Delay if we do multiple
                        Thread.Sleep(8000 + Random.Shared.Next(4000));
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
                state_class = "total_increasing",
                value_template = "{{ value_json.data_used }}",

                state_topic = stateTopic, // Note the override of the state topic
                device // Links the sensors together
            });

            await _entityManager.CreateAsync("sensor.KPN_data_avail", new EntityCreationOptions { Name = "Data Available", DeviceClass = "data_size", }, new
            {
                unit_of_measurement = "GiB",
                icon = "mdi:transmission-tower-import",
                state_class = "total_increasing",
                value_template = "{{ value_json.data_avail }}", // and value from state

                state_topic = stateTopic, // Note the override of the state topic
                device // Links the sensors together
            });

            var newState = new
            {
                data_used = 0,
                data_avail = 0
            };

            await _entityManager.SetStateAsync("sensor.kpn", JsonSerializer.Serialize(newState));
        }



    }
}
