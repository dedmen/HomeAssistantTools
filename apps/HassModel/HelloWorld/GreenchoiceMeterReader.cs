// Use unique namespaces for your apps if you going to share with others to avoid
// conflicting names

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography.X509Certificates;
using HomeAssistantNetDaemon.apps.HassModel.HelloWorld;
using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.Extensions.Scheduler;
using System.Text.Json;
using System.Threading.Tasks;
using System;
using Microsoft.Extensions.Configuration;
using static HomeAssistantNetDaemon.apps.HassModel.HelloWorld.GreenchoiceAPI;

namespace HassModel;

/// <summary>
///     Hello world showcase using the new HassModel API
/// </summary>
[NetDaemonApp]
[Focus]
public class GreenchoiceMeterReader
{
    private readonly IHaContext _ha;
    private readonly IMqttEntityManager _entityManager;
    private GreenchoiceAPI _gc;

    public GreenchoiceMeterReader(IHaContext ha, IMqttEntityManager entityManager, INetDaemonScheduler scheduler, IConfiguration cfg)
    {
        _ha = ha;
        _entityManager = entityManager;
        _gc = new GreenchoiceAPI(cfg);

        _gc.FetchMeterReadings(DateTime.Now);

        //entityManager.RemoveAsync("sensor.car_charger_battery");
        //entityManager.RemoveAsync("sensor.car_charger_electricity_consumption_high");
        //entityManager.RemoveAsync("sensor.car_charger_electricity_consumption_low");
        //entityManager.RemoveAsync("sensor.car_charger_electricity_consumption_total");
        //entityManager.RemoveAsync("sensor.car_charger_progress");
        //entityManager.RemoveAsync("sensor.car_charger_mode");
        //entityManager.RemoveAsync("sensor.car_charger_temperature");
        //entityManager.RemoveAsync("sensor.car_charger_voltage");


        //scheduler.RunIn(TimeSpan.FromSeconds(0.2), () => StartSetup(DateTime.Now.Date.AddDays(-60)));
        //scheduler.RunIn(TimeSpan.FromSeconds(5), NewState);
        //scheduler.RunIn(TimeSpan.FromSeconds(1), Test);


        var next5AM = (DateTimeOffset.Now.Hour > 5 ? DateTimeOffset.Now.AddDays(1).Date : DateTimeOffset.Now.Date).AddHours(5);
        scheduler.RunEvery(TimeSpan.FromHours(24), next5AM.AddMinutes(5), () =>
        {
            if (DateTimeOffset.Now.Hour == 5) // Don't know if needed, want to protect against false triggers at wrong time
                Test();
        });
    }

    struct TotalCounters
    {
        public double elecHigh { get; set; }
        public double elecLow { get; set; }
        public double elecTotal { get; set; }

        public double elecCostsHigh { get; set; }
        public double elecCostsLow { get; set; }
        // There are also fixed costs that I don't record separately, they are included in the total
        public double elecCostsTotal { get; set; }
        //public float gasHigh { get; set; }
        //public float gasLow { get; set; } // There is no gas low it seems, so it doesn't matter
        public double gasTotal { get; set; }
        public double gasCostsTotal { get; set; }
    }

    private TotalCounters GetLastTotalCounters(GreenchoiceAPI.MeterReadings meterReadings, DateTime currentDate) // DateTime.Now.AddDays(-1)
    {
        try
        {
            var fileContents = File.ReadAllText("apps/GreenchoiceCounters.json");
            var counters = JsonSerializer.Deserialize<TotalCounters>(fileContents);
            return counters;
        }
        catch (Exception ex)
        {

        }

        var elecReading = meterReadings.GetReading(currentDate, GreenchoiceAPI.MeterReadings.ProductType.ProductTypeEnum.Stroom);
        var gasReading = meterReadings.GetReading(currentDate, GreenchoiceAPI.MeterReadings.ProductType.ProductTypeEnum.Gas);

        return new TotalCounters()
        {
            elecHigh = elecReading.normalConsumption.Value,
            elecLow = elecReading.offPeakConsumption.Value,
            elecTotal = elecReading.normalConsumption.Value + elecReading.offPeakConsumption.Value,
            elecCostsHigh = 0,
            elecCostsLow = 0,
            elecCostsTotal = 0,
            gasTotal = gasReading.gas.Value,
            gasCostsTotal = 0

        };
    }

    private void SetLastTotalCounters(TotalCounters counters)
    {
        File.WriteAllText("apps/GreenchoiceCounters.json", JsonSerializer.Serialize<TotalCounters>(counters));
    }

    class TempState
    {
        public class StateData
        {
            public string entity_id { get; set; }
            public string state { get; set; }
            public string last_updated { get; set; }
        }

        public string entity_id { get; set; }
        public StateData old_state { get; set; } = new();
        public StateData new_state { get; set; } = new();

        public TempState(string entityName, string state, DateTimeOffset time)
        {
            entity_id = entityName;
            new_state.entity_id = entityName;
            new_state.state = state;
            new_state.last_updated = time.ToString("O");
            old_state.entity_id = entityName;
        }

        public void Update(string newState, DateTimeOffset newTime)
        {
           old_state.state = new_state.state;
           old_state.last_updated = new_state.last_updated;

           new_state.state = newState;
           new_state.last_updated = newTime.ToString("O");
        }

    }


    private async void Test()
    {
        var meterReadings = await _gc.FetchMeterReadings(DateTimeOffset.Now.Date.AddDays(-1));

        // Seed code
        //for (int i = -34; i < -1; i++)
        //{
        //    await FetchDay(meterReadings, DateTimeOffset.Now.Date.AddDays(i));
        //    await Task.Delay(5000);
        //}

        await FetchDay(meterReadings, DateTimeOffset.Now.Date.AddDays(-1));
    }

    private async Task FetchDay(MeterReadings meterReadings, DateTime fetchDate)
    {
        var lastTotalCounters = GetLastTotalCounters(meterReadings, fetchDate.Date);

        var fetchedData = await _gc.Fetch(fetchDate.Date, fetchDate.Date.AddDays(1)); // From yesterday 00:00 to today 00:00

        var elecData = fetchedData.entries.First(x => x.productType == "electricity");
        var gasData = fetchedData.entries.First(x => x.productType == "gas");


        TempState stateElecHigh = new TempState("sensor.greenchoice_electricity_consumption_high", lastTotalCounters.elecHigh.ToString(CultureInfo.InvariantCulture), DateTimeOffset.Now.Date.AddDays(-1));
        TempState stateElecLow = new TempState("sensor.greenchoice_electricity_consumption_low", lastTotalCounters.elecLow.ToString(CultureInfo.InvariantCulture), DateTimeOffset.Now.Date.AddDays(-1));
        TempState stateElecTotal = new TempState("sensor.greenchoice_electricity_consumption_total", lastTotalCounters.elecTotal.ToString(CultureInfo.InvariantCulture), DateTimeOffset.Now.Date.AddDays(-1));
        TempState stateGasTotal = new TempState("sensor.greenchoice_gas_consumption_total", lastTotalCounters.gasTotal.ToString(CultureInfo.InvariantCulture), DateTimeOffset.Now.Date.AddDays(-1));

        TempState stateCostElecHigh = new TempState("sensor.greenchoice_electricity_cost_high", lastTotalCounters.elecCostsHigh.ToString(CultureInfo.InvariantCulture), DateTimeOffset.Now.Date.AddDays(-1));
        TempState stateCostElecLow = new TempState("sensor.greenchoice_electricity_cost_low", lastTotalCounters.elecCostsLow.ToString(CultureInfo.InvariantCulture), DateTimeOffset.Now.Date.AddDays(-1));
        TempState stateCostElecTotal = new TempState("sensor.greenchoice_electricity_cost_total", lastTotalCounters.elecCostsTotal.ToString(CultureInfo.InvariantCulture), DateTimeOffset.Now.Date.AddDays(-1));
        TempState stateCostGasTotal = new TempState("sensor.greenchoice_gas_cost_total", lastTotalCounters.gasCostsTotal.ToString(CultureInfo.InvariantCulture), DateTimeOffset.Now.Date.AddDays(-1));

        double accumulatedElecHigh = lastTotalCounters.elecHigh;
        double accumulatedElecLow = lastTotalCounters.elecLow;
        double accumulatedElecTotal = lastTotalCounters.elecTotal;
        double accumulatedGasTotal = lastTotalCounters.gasTotal;

        double accumulatedCostElecHigh = lastTotalCounters.elecCostsHigh;
        double accumulatedCostElecLow = lastTotalCounters.elecCostsLow;
        double accumulatedCostElecTotal = lastTotalCounters.elecCostsTotal;
        double accumulatedCostGasTotal = lastTotalCounters.gasCostsTotal;

        foreach (var keyValuePair in elecData.values.Zip(gasData.values).OrderBy(x => x.First.Key))
        {
            // The time of the reading, is the time of when the change was detected.
            // So usage at 08:10 is logged in the 09 timespan. So we want to log into previous hour
            //!! The above is only for gas usage...
            // Using electricity at 08:10 shows up in 08:00 reading
            // Using gas at 08:10 shows up in 09:00 reading.. wow

            var measureTimeElec = keyValuePair.First.Key
                    .AddHours(1)
                    .AddSeconds(-1)
                ; // log it at end of the hour

            var measureTimeGas = keyValuePair.First.Key
                    .AddSeconds(-1)
                ; // log it at end of the previous hour

            var elecRead = keyValuePair.First.Value;
            var gasRead = keyValuePair.Second.Value;


            accumulatedElecHigh += elecRead.consumptionHigh;
            stateElecHigh.Update(accumulatedElecHigh.ToString("F", CultureInfo.InvariantCulture), measureTimeElec);
            _ha.SendEvent("state_changed", stateElecHigh);

            accumulatedElecLow += elecRead.consumptionLow;
            stateElecLow.Update(accumulatedElecLow.ToString("F", CultureInfo.InvariantCulture), measureTimeElec);
            _ha.SendEvent("state_changed", stateElecLow);

            accumulatedElecTotal += elecRead.consumptionTotal;
            stateElecTotal.Update(accumulatedElecTotal.ToString("F", CultureInfo.InvariantCulture), measureTimeElec);
            _ha.SendEvent("state_changed", stateElecTotal);

            accumulatedGasTotal += gasRead.consumptionTotal;
            stateGasTotal.Update(accumulatedGasTotal.ToString("F", CultureInfo.InvariantCulture), measureTimeGas);
            _ha.SendEvent("state_changed", stateGasTotal);


            accumulatedCostElecHigh += elecRead.costsHigh;
            stateCostElecHigh.Update(accumulatedCostElecHigh.ToString("F", CultureInfo.InvariantCulture), measureTimeElec);
            _ha.SendEvent("state_changed", stateCostElecHigh);

            accumulatedCostElecLow += elecRead.costsLow;
            stateCostElecLow.Update(accumulatedCostElecLow.ToString("F", CultureInfo.InvariantCulture), measureTimeElec);
            _ha.SendEvent("state_changed", stateCostElecLow);

            accumulatedCostElecTotal += elecRead.costsTotal;
            stateCostElecTotal.Update(accumulatedCostElecTotal.ToString("F", CultureInfo.InvariantCulture), measureTimeElec);
            _ha.SendEvent("state_changed", stateCostElecTotal);

            accumulatedCostGasTotal += gasRead.costsTotal;
            stateCostGasTotal.Update(accumulatedCostGasTotal.ToString("F", CultureInfo.InvariantCulture), measureTimeGas);
            _ha.SendEvent("state_changed", stateCostGasTotal);
        }

        {
            //#TODO this doesn't work at year transition because readings are only current year
            var elecBefore = meterReadings.GetReading(fetchDate.AddDays(-2), MeterReadings.ProductType.ProductTypeEnum.Stroom);
            var gasBefore = meterReadings.GetReading(fetchDate.AddDays(-2), MeterReadings.ProductType.ProductTypeEnum.Gas);

            var elecNow = meterReadings.GetReading(fetchDate.AddDays(-1), MeterReadings.ProductType.ProductTypeEnum.Stroom);
            var gasNow = meterReadings.GetReading(fetchDate.AddDays(-1), MeterReadings.ProductType.ProductTypeEnum.Gas);


            TempState stateMeterElecHigh = new TempState("sensor.greenchoice_electricity_meter_high", elecBefore.normalConsumption.Value.ToString(CultureInfo.InvariantCulture), DateTimeOffset.Now.Date.AddDays(-1));
            TempState stateMeterElecLow = new TempState("sensor.greenchoice_electricity_meter_low", elecBefore.offPeakConsumption.Value.ToString(CultureInfo.InvariantCulture), DateTimeOffset.Now.Date.AddDays(-1));
            TempState stateMeterGasTotal = new TempState("sensor.greenchoice_gas_meter_total", gasNow.gas.Value.ToString(CultureInfo.InvariantCulture), DateTimeOffset.Now.Date.AddDays(-1));


            stateMeterElecHigh.Update(elecNow.normalConsumption.Value.ToString("F", CultureInfo.InvariantCulture), fetchDate.AddDays(1).AddMinutes(-1));
            _ha.SendEvent("state_changed", stateMeterElecHigh);


            stateMeterElecLow.Update(elecNow.offPeakConsumption.Value.ToString("F", CultureInfo.InvariantCulture), fetchDate.AddDays(1).AddMinutes(-1));
            _ha.SendEvent("state_changed", stateMeterElecLow);

            stateMeterGasTotal.Update(gasNow.gas.Value.ToString("F", CultureInfo.InvariantCulture), fetchDate.AddDays(1).AddMinutes(-1));
            _ha.SendEvent("state_changed", stateMeterGasTotal);
        }








        // We processed the whole day. Calculate day usage just for checks with total values

        double dayUsageElecHigh = accumulatedElecHigh - lastTotalCounters.elecHigh;
        double dayUsageElecLow = accumulatedElecLow - lastTotalCounters.elecLow;
        double dayUsageElecTotal = accumulatedElecTotal - lastTotalCounters.elecTotal;
        double dayUsageGasTotal = accumulatedGasTotal - lastTotalCounters.gasTotal;

        double dayUsageCostElecHigh = accumulatedCostElecHigh - lastTotalCounters.elecCostsHigh;
        double dayUsageCostElecLow = accumulatedCostElecLow - lastTotalCounters.elecCostsLow;
        double dayUsageCostElecTotal = accumulatedCostElecTotal - lastTotalCounters.elecCostsTotal;
        double dayUsageCostGasTotal = accumulatedCostGasTotal - lastTotalCounters.gasCostsTotal;

        var usageDay = new
        {
            dayUsageElecHigh,
            dayUsageElecLow,
            dayUsageElecTotal,
            dayUsageGasTotal,
            dayUsageCostElecHigh,
            dayUsageCostElecLow,
            dayUsageCostElecTotal,
            dayUsageCostGasTotal
        };

        var usageDeltas = new
        {
            deltaElecHigh = Math.Abs(dayUsageElecHigh - elecData.total.consumptionHigh),
            deltaElecLow = Math.Abs(dayUsageElecLow - elecData.total.consumptionLow),
            deltaElecTotal = Math.Abs(dayUsageElecTotal - elecData.total.consumptionTotal),
        };

        var newTotals = new TotalCounters()
        {
            elecHigh = accumulatedElecHigh,
            elecLow = accumulatedElecLow,
            elecTotal = accumulatedElecTotal,
            gasTotal = accumulatedGasTotal,
            elecCostsHigh = accumulatedCostElecHigh,
            elecCostsLow = accumulatedCostElecLow,
            elecCostsTotal = accumulatedCostElecTotal,
            gasCostsTotal = accumulatedCostGasTotal
        };

        SetLastTotalCounters(newTotals);

        _ha.CallService("notify", "persistent_notification", data: new {message = $"EnergyData {fetchDate:D} {JsonSerializer.Serialize(usageDay)} {JsonSerializer.Serialize(usageDeltas)}", title = "Greenchoice Meters"});
    }

    private async void StartSetup(DateTime startDate)
    {
        // This device will have five sensors. We tie all of the sensors together
        // by sharing the same `identifiers` list with each sensor.
        var identifiers = new[] {"greenchoice"};

        // It is important that all sensors share the same State Topic so that
        // we can update all values in one go.
        // You will see that in each sensor, the `value_template` defines how
        // we extract the sensor value from the multiple update.
        var stateTopic = "homeassistant/sensor/greenchoice/state";

        // First we define the device that will own all the sensors. This is passed
        // when we create the first of the sensors.
        var device = new
        {
            identifiers = identifiers,
            name = "Greenchoice meters"
        };

        // Electricity 
        await _entityManager.CreateAsync("sensor.greenchoice_electricity_consumption_high", new EntityCreationOptions {Name = "Electricity High", DeviceClass = "energy",}, new
        {
            unit_of_measurement = "kWh",
            icon = "mdi:weather-sunset-up",
            state_class = "total_increasing",
            value_template = "{{ value_json.electricity_consumption_high }}",

            state_topic = stateTopic, // Note the override of the state topic
            device // Links the sensors together
        });

        await _entityManager.CreateAsync("sensor.greenchoice_electricity_consumption_low", new EntityCreationOptions { Name = "Electricity Low", DeviceClass = "energy", }, new
        {
            unit_of_measurement = "kWh",
            icon = "mdi:weather-sunset-down",
            state_class = "total_increasing",
            value_template = "{{ value_json.electricity_consumption_low }}", // and value from state

            state_topic = stateTopic, // Note the override of the state topic
            device // Links the sensors together
        });

        await _entityManager.CreateAsync("sensor.greenchoice_electricity_consumption_total", new EntityCreationOptions { Name = "Electricity Total", DeviceClass = "energy", }, new
        {
            unit_of_measurement = "kWh",
            icon = "mdi:transmission-tower-export",
            state_class = "total_increasing",
            value_template = "{{ value_json.electricity_consumption_total }}", // and value from state

            state_topic = stateTopic, // Note the override of the state topic
            device // Links the sensors together
        });

        // Gas
        await _entityManager.CreateAsync("sensor.greenchoice_gas_consumption_total", new EntityCreationOptions { Name = "Gas Total", DeviceClass = "energy", }, new
        {
            unit_of_measurement = "m\u00b3",
            icon = "mdi:fire",
            state_class = "total_increasing",
            value_template = "{{ value_json.gas_consumption_total }}", // and value from state

            state_topic = stateTopic, // Note the override of the state topic
            device // Links the sensors together
        });

        // Costs

        await _entityManager.CreateAsync("sensor.greenchoice_electricity_cost_high", new EntityCreationOptions { Name = "Electricity Cost High", DeviceClass = "monetary", }, new
        {
            unit_of_measurement = "€",
            icon = "mdi:currency-eur",
            state_class = "total",
            value_template = "{{ value_json.electricity_cost_high }}", // and value from state
            state_topic = stateTopic, // Note the override of the state topic
            device // Links the sensors together
        });
        await _entityManager.CreateAsync("sensor.greenchoice_electricity_cost_low", new EntityCreationOptions { Name = "Electricity Cost Low", DeviceClass = "monetary", }, new
        {
            unit_of_measurement = "€",
            icon = "mdi:currency-eur",
            state_class = "total",
            value_template = "{{ value_json.electricity_cost_low }}", // and value from state
            state_topic = stateTopic, // Note the override of the state topic
            device // Links the sensors together
        });
        await _entityManager.CreateAsync("sensor.greenchoice_electricity_cost_total", new EntityCreationOptions { Name = "Electricity Cost Total", DeviceClass = "monetary", }, new
        {
            unit_of_measurement = "€",
            icon = "mdi:currency-eur",
            state_class = "total",
            value_template = "{{ value_json.electricity_cost_total }}", // and value from state
            state_topic = stateTopic, // Note the override of the state topic
            device // Links the sensors together
        });


        await _entityManager.CreateAsync("sensor.greenchoice_gas_cost_total", new EntityCreationOptions { Name = "Gas Cost Total", DeviceClass = "monetary", }, new
        {
            unit_of_measurement = "€",
            icon = "mdi:currency-eur",
            state_class = "total",
            value_template = "{{ value_json.gas_cost_total }}", // and value from state
            state_topic = stateTopic, // Note the override of the state topic
            device // Links the sensors together
        });


        // Meter Readings

        await _entityManager.CreateAsync("sensor.greenchoice_electricity_meter_high", new EntityCreationOptions { Name = "Electricity Meter High", DeviceClass = "energy", }, new
        {
            unit_of_measurement = "kWh",
            //icon = "mdi:weather-sunset-up",
            state_class = "total_increasing",
            value_template = "{{  value_json.electricity_meter_high }}",

            state_topic = stateTopic, // Note the override of the state topic
            device // Links the sensors together
        });

        await _entityManager.CreateAsync("sensor.greenchoice_electricity_meter_low", new EntityCreationOptions { Name = "Electricity Meter Low", DeviceClass = "energy", }, new
        {
            unit_of_measurement = "kWh",
            //icon = "mdi:weather-sunset-down",
            state_class = "total_increasing",
            value_template = "{{ value_json.electricity_meter_low }}", // and value from state

            state_topic = stateTopic, // Note the override of the state topic
            device // Links the sensors together
        });


        await _entityManager.CreateAsync("sensor.greenchoice_gas_meter_total", new EntityCreationOptions { Name = "Gas Meter Total", DeviceClass = "energy", }, new
        {
            unit_of_measurement = "m\u00b3",
            icon = "mdi:fire",
            state_class = "total_increasing",
            value_template = "{{ value_json.gas_meter_total }}", // and value from state

            state_topic = stateTopic, // Note the override of the state topic
            device // Links the sensors together
        });


        // Default values are meter readings from last available day

        var meterReadings = await _gc.FetchMeterReadings(startDate);
        var elecReading = meterReadings.GetReading(startDate, GreenchoiceAPI.MeterReadings.ProductType.ProductTypeEnum.Stroom);
        var gasReading = meterReadings.GetReading(startDate, GreenchoiceAPI.MeterReadings.ProductType.ProductTypeEnum.Gas);

        var newState = new
        {
            electricity_consumption_high = elecReading.normalConsumption.Value,
            electricity_consumption_low = elecReading.offPeakConsumption.Value,
            electricity_consumption_total = elecReading.normalConsumption.Value + elecReading.offPeakConsumption.Value,
            gas_consumption_total = gasReading.gas.Value,
            electricity_cost_high = 0,
            electricity_cost_low = 0,
            electricity_cost_total = 0,
            gas_cost_total = 0
        };

        await _entityManager.SetStateAsync("sensor.greenchoice", JsonSerializer.Serialize(newState));
    }

}