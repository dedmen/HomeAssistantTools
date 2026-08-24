using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.HassModel.Entities;
using Org.BouncyCastle.Asn1.X509;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace HomeAssistantNetDaemon.apps.HassModel.BatteryControl
{
    [NetDaemonApp]
    [Focus]
    public class PowerMonitor : IAsyncInitializable
    {
        private readonly IHaContext _ha;
        private readonly IConfiguration _cfg;
        private readonly IMqttEntityManager _entityManager;

        private BatteryController _batteryController;


        // At this many watts off the target, we ignore all filters and rate limiting and try to push for instant correction
        // For Oven/Microwave turning on, or a sudden solar dip
        private static readonly float InstantCorrectionThreshold = 100; 

        AdaptivePowerFilter _filter = new AdaptivePowerFilter(0, 150);
        private ShellyEmulator _shellyEmulator;

        private float MeterTargetPower = -50;
        private float MinimumBatteryPower = 0;

        private float MaximumChargePower = 1200;
        private float MaximumDischargePower = 2400;

        private bool UseFilter = false;
        private bool UseFalseReadingDetection = false;
        private bool LogFalseReadingDetection = false;
        private int FalseReadingDetectionMaxIgnoreCount = 1;

        public readonly TokenBucketRateLimiter _rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1,                  // Maximum burst capacity
            QueueLimit = 1,                  // CRITICAL: 0 means drop new requests immediately if full
            ReplenishmentPeriod = TimeSpan.FromSeconds(2), // Refresh rate window
            TokensPerPeriod = 1,             // How many tokens refill per period
            AutoReplenishment = true
        });

        // Apply small power changes slowly, don't annoy the inverter for small adjustments. Large changes bypass the limiter
        public readonly DynamicMagnitudeRateLimiter _rateLimiterApply = new DynamicMagnitudeRateLimiter((int)InstantCorrectionThreshold, TimeSpan.FromSeconds(30));


        public class PowerEntity
        {
            public float PowerAtLastMeter;
            public float PowerLiveData;

            public enum PowerType
            {
                Grid,
                Battery,
                Load,
                Solar
            }

            public PowerType Type;

            public float GetDeltaSinceLastMeter()
            {
                return PowerLiveData - PowerAtLastMeter;
            }
        }

        public class PowerEntityGridMeter : PowerEntity
        {
            public int FalseReadingIgnoredCount = 0;
        }

        private readonly Dictionary<string, PowerEntity> _powerMeters = new();

        private readonly (string, PowerEntity.PowerType)[] _powerMetersNames =
        [
            ("sensor.solar_power", PowerEntity.PowerType.Solar),
            ("sensor.energy_grid_nrg_dongle_pro_power_delivered_nrg_dongle_pro_power_returned_net_power", PowerEntity.PowerType.Grid),
            ("sensor.schlafzimmer_indevolt_bk1600_a673ba40t800780s_battery_plug_total_power", PowerEntity.PowerType.Battery),
            ("sensor.living_room_indevolt_bk1600_sec_bk1600_battery_plug_sec_total_power", PowerEntity.PowerType.Battery),
            ("sensor.main_pc_power", PowerEntity.PowerType.Load),
            ("sensor.oven_power_2", PowerEntity.PowerType.Load)
        ];


        public PowerMonitor(IHaContext ha, IConfiguration cfg, IMqttEntityManager entityManager)
        {
            _ha = ha;
            _cfg = cfg;
            _entityManager = entityManager;

            _shellyEmulator = new ShellyEmulator();
            _batteryController = new BatteryController(ha, entityManager);
        }


        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            await _batteryController.InitializeAsync(cancellationToken);

            _entityManager.RemoveAsync("number.netdaemon_miniumum_battery_power");

            /// Generate Home Assistant entities for control and monitoring

            // Monitoring displays
            {
                await _entityManager.CreateAsync("sensor.battery_control_targetpower", new EntityCreationOptions { Name = "Power Monitor Wanted Target", DeviceClass = "power", }, new
                {
                    unit_of_measurement = "W",
                    icon = "mdi:power-settings",
                    state_class = "measurement",
                    state_topic = "homeassistant/sensor/battery_control_targetpower/state",
                    device = new { identifiers = new[] { "battery_control" }, name = "Battery Control", model = "SuperDuper Mk2", manufacturer = "Dedmen", sw_version = 1.00 }
                }).ConfigureAwait(false);

                await _entityManager.CreateAsync("sensor.battery_control_batterycommand", new EntityCreationOptions { Name = "Battery commanded Target", DeviceClass = "power", }, new
                {
                    unit_of_measurement = "W",
                    icon = "mdi:battery-charging-wireless",
                    state_class = "measurement",
                    state_topic = "homeassistant/sensor/battery_control_batterycommand/state",
                    device = new { identifiers = new[] { "battery_control" }, name = "Battery Control", model = "SuperDuper Mk2", manufacturer = "Dedmen", sw_version = 1.00 }
                }).ConfigureAwait(false);

                await _entityManager.CreateAsync("sensor.battery_control_deltatotarget", new EntityCreationOptions { Name = "Power Monitor Delta To Target", DeviceClass = "power", }, new
                {
                    unit_of_measurement = "W",
                    icon = "mdi:delta",
                    state_class = "measurement",
                    state_topic = "homeassistant/sensor/battery_control_deltatotarget/state",
                    device = new { identifiers = new[] { "battery_control" }, name = "Battery Control", model = "SuperDuper Mk2", manufacturer = "Dedmen", sw_version = 1.00 }
                }).ConfigureAwait(false);



            }


            // Control inputs
            {
                // Meter value target
                {
                    await _entityManager.CreateAsync("number.battery_control_meter_target", new EntityCreationOptions(Name: "MeterTarget", Persist: true), new
                    {
                        min = -100,
                        max = 50,
                        step = 1.0,
                        mode = "box", // Can be "box" or "slider"
                        unit_of_measurement = "W",
                        icon = "mdi:meter-electric-outline",

                        state_topic = "homeassistant/number/battery_control_meter_target/state",
                        device = new { identifiers = new[] { "battery_control" }, name = "Battery Control", model = "SuperDuper Mk2", manufacturer = "Dedmen", sw_version = 1.00 }
                    }).ConfigureAwait(false);

                    {
                        var commandSubscription = await _entityManager.PrepareCommandSubscriptionAsync("number.battery_control_meter_target").ConfigureAwait(false);

                        commandSubscription.Subscribe(async commandReceived =>
                        {
                            // Try parsing the incoming string into a valid double
                            if (float.TryParse(commandReceived, out float parsedValue))
                            {
                                await _entityManager.SetStateAsync("number.battery_control_meter_target", commandReceived).ConfigureAwait(false);
                                MeterTargetPower = parsedValue;
                            }
                        });

                        if (float.TryParse(_ha.GetState("number.battery_control_meter_target")?.State ?? "-50", out float parsedValue))
                        {
                            MeterTargetPower = parsedValue;
                        }
                    }
                }

                // Ability to force battery to charge or discharge
                {
                    await _entityManager.CreateAsync("number.battery_control_miniumum_battery_power", new EntityCreationOptions(Name: "Minimum Battery Power", Persist: true), new
                    {
                        min = -2400,
                        max = 1200,
                        step = 1.0,
                        mode = "box", // Can be "box" or "slider"
                        unit_of_measurement = "W",
                        icon = "mdi:battery-charging-medium",


                        state_topic = "homeassistant/number/battery_control_miniumum_battery_power/state",
                        device = new { identifiers = new[] { "battery_control" }, name = "Battery Control", model = "SuperDuper Mk2", manufacturer = "Dedmen", sw_version = 1.00 }
                    }).ConfigureAwait(false);

                    {
                        var commandSubscription = await _entityManager.PrepareCommandSubscriptionAsync("number.battery_control_miniumum_battery_power").ConfigureAwait(false);

                        commandSubscription.Subscribe(async commandReceived =>
                        {
                            // Try parsing the incoming string into a valid double
                            if (float.TryParse(commandReceived, out float parsedValue))
                            {
                                await _entityManager.SetStateAsync("number.battery_control_miniumum_battery_power", commandReceived).ConfigureAwait(false);
                                MinimumBatteryPower = parsedValue;

                                lock (_rateLimiter)
                                {
                                    OnUpdate();
                                }
                            }
                        });

                        if (float.TryParse(_ha.GetState("number.battery_control_miniumum_battery_power")?.State ?? "0", out float parsedValue))
                        {
                            MinimumBatteryPower = parsedValue;
                        }
                    }
                }

                // Maximum Charge Power
                {
                    await _entityManager.CreateAsync("number.battery_control_maximum_charge_power", new EntityCreationOptions(Name: "Maximum Charge Power", Persist: true), new
                    {
                        min = 0,
                        max = 1200,
                        step = 1.0,
                        mode = "box", // Can be "box" or "slider"
                        unit_of_measurement = "W",
                        icon = "mdi:battery-charging-100",

                        state_topic = "homeassistant/number/battery_control_maximum_charge_power/state",
                        device = new { identifiers = new[] { "battery_control" }, name = "Battery Control", model = "SuperDuper Mk2", manufacturer = "Dedmen", sw_version = 1.00 }
                    }).ConfigureAwait(false);

                    {
                        var commandSubscription = await _entityManager.PrepareCommandSubscriptionAsync("number.battery_control_maximum_charge_power").ConfigureAwait(false);

                        commandSubscription.Subscribe(async commandReceived =>
                        {
                            // Try parsing the incoming string into a valid double
                            if (float.TryParse(commandReceived, out float parsedValue))
                            {
                                await _entityManager.SetStateAsync("number.battery_control_maximum_charge_power", commandReceived).ConfigureAwait(false);
                                MaximumChargePower = parsedValue;

                                lock (_rateLimiter)
                                {
                                    OnUpdate();
                                }
                            }
                        });

                        if (float.TryParse(_ha.GetState("number.battery_control_maximum_charge_power")?.State ?? "1200", out float parsedValue))
                        {
                            MaximumChargePower = parsedValue;
                        }
                    }
                }

                // Maximum Discharge Power
                {
                    await _entityManager.CreateAsync("number.battery_control_maximum_discharge_power", new EntityCreationOptions(Name: "Maximum Discharge Power", Persist: true), new
                    {
                        min = 0,
                        max = 2400,
                        step = 1.0,
                        mode = "box", // Can be "box" or "slider"
                        unit_of_measurement = "W",
                        icon = "mdi:battery-charging-10",

                        state_topic = "homeassistant/number/battery_control_maximum_discharge_power/state",
                        device = new { identifiers = new[] { "battery_control" }, name = "Battery Control", model = "SuperDuper Mk2", manufacturer = "Dedmen", sw_version = 1.00 }
                    }).ConfigureAwait(false);

                    {
                        var commandSubscription = await _entityManager.PrepareCommandSubscriptionAsync("number.battery_control_maximum_discharge_power").ConfigureAwait(false);

                        commandSubscription.Subscribe(async commandReceived =>
                        {
                            // Try parsing the incoming string into a valid double
                            if (float.TryParse(commandReceived, out float parsedValue))
                            {
                                await _entityManager.SetStateAsync("number.battery_control_maximum_discharge_power", commandReceived).ConfigureAwait(false);
                                MaximumDischargePower = parsedValue;

                                lock (_rateLimiter)
                                {
                                    OnUpdate();
                                }
                            }
                        });

                        if (float.TryParse(_ha.GetState("number.battery_control_maximum_discharge_power")?.State ?? "2400", out float parsedValue))
                        {
                            MaximumDischargePower = parsedValue;
                        }
                    }
                }

                // Use PD Filter
                {
                    await _entityManager.CreateAsync("switch.battery_control_use_pd_filter", new EntityCreationOptions { DeviceClass = "switch", Name = "Use PD Filter", PayloadOn = "on", PayloadOff = "off" }, new
                    {
                        icon = "mdi:filter-cog",
                        state_topic = "homeassistant/switch/battery_control_use_pd_filter/state",
                        device = new { identifiers = new[] { "battery_control" }, name = "Battery Control", model = "SuperDuper Mk2", manufacturer = "Dedmen", sw_version = 1.00 }
                    }).ConfigureAwait(false);

                    var commandSubscription = await _entityManager.PrepareCommandSubscriptionAsync("switch.battery_control_use_pd_filter").ConfigureAwait(false);

                    commandSubscription.Subscribe(async commandReceived =>
                   {
                       await _entityManager.SetStateAsync("switch.battery_control_use_pd_filter", commandReceived).ConfigureAwait(false);
                       UseFilter = commandReceived == "on";
                   });

                   UseFilter = (_ha.GetState("number.battery_control_use_pd_filter")?.State ?? "off") == "on";
                }

                // Use False reading detection
                {
                    await _entityManager.CreateAsync("switch.battery_control_use_false_reading_detection", new EntityCreationOptions { DeviceClass = "switch", Name = "Use False reading detection", PayloadOn = "on", PayloadOff = "off" }, new
                    {
                        //icon = "mdi:filter-cog",
                        state_topic = "homeassistant/switch/battery_control_use_false_reading_detection/state",
                        device = new { identifiers = new[] { "battery_control" }, name = "Battery Control", model = "SuperDuper Mk2", manufacturer = "Dedmen", sw_version = 1.00 }
                    }).ConfigureAwait(false);

                    var commandSubscription = await _entityManager.PrepareCommandSubscriptionAsync("switch.battery_control_use_false_reading_detection").ConfigureAwait(false);

                    commandSubscription.Subscribe(async commandReceived =>
                    {
                        await _entityManager.SetStateAsync("switch.battery_control_use_false_reading_detection", commandReceived).ConfigureAwait(false);
                        UseFalseReadingDetection = commandReceived == "on";
                    });

                    UseFalseReadingDetection = (_ha.GetState("number.battery_control_use_false_reading_detection")?.State ?? "off") == "on";
                }

                // Log False reading detection
                {
                    await _entityManager.CreateAsync("switch.battery_control_log_false_reading_detection", new EntityCreationOptions { DeviceClass = "switch", Name = "Log False reading detection", PayloadOn = "on", PayloadOff = "off" }, new
                    {
                        //icon = "mdi:filter-cog",
                        state_topic = "homeassistant/switch/battery_control_log_false_reading_detection/state",
                        device = new { identifiers = new[] { "battery_control" }, name = "Battery Control", model = "SuperDuper Mk2", manufacturer = "Dedmen", sw_version = 1.00 }
                    }).ConfigureAwait(false);

                    var commandSubscription = await _entityManager.PrepareCommandSubscriptionAsync("switch.battery_control_log_false_reading_detection").ConfigureAwait(false);

                    commandSubscription.Subscribe(async commandReceived =>
                    {
                        await _entityManager.SetStateAsync("switch.battery_control_log_false_reading_detection", commandReceived).ConfigureAwait(false);
                        LogFalseReadingDetection = commandReceived == "on";
                    });

                    LogFalseReadingDetection = (_ha.GetState("number.battery_control_log_false_reading_detection")?.State ?? "off") == "on";
                }

                // False reading detection max ignore count
                {
                    await _entityManager.CreateAsync("number.battery_control_false_reading_detection_max_ignores", new EntityCreationOptions(Name: "False reading detection max ignore count", Persist: true), new
                    {
                        min = 1,
                        max = 6,
                        step = 1.0,
                        mode = "slider",
                        //icon = "mdi:battery-charging-10",

                        state_topic = "homeassistant/number/battery_control_false_reading_detection_max_ignores/state",
                        device = new { identifiers = new[] { "battery_control" }, name = "Battery Control", model = "SuperDuper Mk2", manufacturer = "Dedmen", sw_version = 1.00 }
                    }).ConfigureAwait(false);

                    {
                        var commandSubscription = await _entityManager.PrepareCommandSubscriptionAsync("number.battery_control_false_reading_detection_max_ignores").ConfigureAwait(false);

                        commandSubscription.Subscribe(async commandReceived =>
                        {
                            // Try parsing the incoming string into a valid double
                            if (int.TryParse(commandReceived, out var parsedValue))
                            {
                                await _entityManager.SetStateAsync("number.battery_control_false_reading_detection_max_ignores", commandReceived).ConfigureAwait(false);
                                FalseReadingDetectionMaxIgnoreCount = parsedValue;
                            }
                        });

                        if (int.TryParse(_ha.GetState("number.battery_control_false_reading_detection_max_ignores")?.State ?? "1", out var parsedValue))
                        {
                            FalseReadingDetectionMaxIgnoreCount = parsedValue;
                        }
                    }
                }

            }



            // Energy devices

            // Register them all
            foreach (var name in _powerMetersNames)
            {
                var state = _ha.GetState(name.Item1)?.State;
                if (state == null)
                    continue; // Missing device? //#TODO log message, or notification

                float.TryParse(state, out var currentState); // If it fails its zero, which is fine

                if (name.Item2 == PowerEntity.PowerType.Grid)
                    _powerMeters.Add(name.Item1, new PowerEntityGridMeter() { PowerAtLastMeter = currentState, PowerLiveData = currentState, Type = name.Item2 });
                else
                    _powerMeters.Add(name.Item1, new PowerEntity() { PowerAtLastMeter = currentState, PowerLiveData = currentState, Type = name.Item2 });
            }

            // Subscribe to their state updates
            _ha.StateChanges().Where(e => _powerMeters.ContainsKey(e.Entity.EntityId)).Subscribe(e =>
            {
                if (e.New?.State == null)
                    return;

                //Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} {e.Entity.EntityId}: {e.New.LastUpdated:HH:mm:ss.fff} / {e.New.State}");

                var updatedEntity = _powerMeters[e.Entity.EntityId];
                var newPowerState = float.Parse(e.New.State, CultureInfo.InvariantCulture);

                if (updatedEntity.Type == PowerEntity.PowerType.Grid)
                {
                    // There is a bug where meter shows wrong direction. If the fridge turns on and our power usage suddenly increases, the meter actually reports a surge in power export, wrong direction
                    // To fix this issue, we ignore one meter reading, if its a negative spike
                    // A sudden export spike is either a false positive, or a solar spike. We can deal with missing 10 seconds of solar, but we don't want to switch to charge mode, in the middle of night when the fridge turned on


                    // Try to detect the wrong data with heuristics
                    // If we expect (based on other devices) a certain power change, but the meter actually does the opposite. And the change, had it been applied correctly, would've caused a cross over zero

                    // The delta between old and new state, of all monitoring devices except the meter. What we expect the meter to tell us now with its update
                    var totalDeltaSinceLastMeter = _powerMeters.Select(x =>
                    {
                        if (x.Value.Type == PowerEntity.PowerType.Solar) // Solar is inverted, positive value means we are getting power, not consuming
                            return -x.Value.GetDeltaSinceLastMeter();
                        if (x.Value.Type == PowerEntity.PowerType.Grid)
                            return 0;
                        return x.Value.GetDeltaSinceLastMeter();
                    }).Sum();

                    // Delta between old and new state of meter. What the meter tells us has changed
                    var newStateMeterDelta = newPowerState - updatedEntity.PowerAtLastMeter;

                    if (Math.Abs(newPowerState) < 300) // These false readings only happen close to zero
                    {

                        // The expected delta is at least somewhat close to what the meter reported (If we expect no change, but the meter changed anyway. Something else has changed)

                        var expectedVsActual = Math.Abs(Math.Abs(newStateMeterDelta) - Math.Abs(totalDeltaSinceLastMeter));
                        var expectedVsActualPerc = expectedVsActual / ((Math.Abs(newStateMeterDelta) + Math.Abs(totalDeltaSinceLastMeter)) / 2);
                        //Console.WriteLine($"Exp {totalDeltaSinceLastMeter} Act {newStateMeterDelta} Delt {expectedVsActual} Perc {expectedVsActualPerc}");
                        // Are they close enough together, within tolerance?
                        // The actual meter change, went opposite of what we expected
                        if (expectedVsActualPerc < 0.5 &&  Math.Sign(totalDeltaSinceLastMeter) != Math.Sign(newStateMeterDelta))
                        {

                            if (LogFalseReadingDetection)
                                Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} Meter sign missmatch. ExpDelta {totalDeltaSinceLastMeter}, MeterDelta {newStateMeterDelta}, " +
                                        $"Expected {updatedEntity.PowerAtLastMeter}->{updatedEntity.PowerAtLastMeter + totalDeltaSinceLastMeter}. " +
                                        $"Observed {updatedEntity.PowerAtLastMeter}->{newPowerState}");

                            // The change, if applied correctly, would've crossed over zero
                            //if (Math.Sign(newPowerState) != Math.Sign(updatedEntity.PowerAtLastMeter + totalDeltaSinceLastMeter))
                            {
                                // The meter didn't do what expected, and if the expected one had applied, we would've crossed zero. This is the failure mode

                                if (LogFalseReadingDetection)
                                    Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} Meter false reading detected. ExpDelta {totalDeltaSinceLastMeter}, MeterDelta {newStateMeterDelta}, " +
                                        $"Expected {updatedEntity.PowerAtLastMeter}->{updatedEntity.PowerAtLastMeter + totalDeltaSinceLastMeter}. " +
                                        $"Observed {updatedEntity.PowerAtLastMeter}->{newPowerState}");




                                // We need to update the PowerAtLastMeter though, so that if the next reading still reports same, we don't fall into here again
                                //updatedEntity.PowerAtLastMeter = newPowerState;

                                var gridMeter = updatedEntity as PowerEntityGridMeter;
                                gridMeter.FalseReadingIgnoredCount++;
                                if (gridMeter.FalseReadingIgnoredCount >= FalseReadingDetectionMaxIgnoreCount)
                                {
                                    gridMeter.FalseReadingIgnoredCount = 0;
                                }
                                else
                                {
                                    return; // Ignore this update
                                }
                            }
                        }





                    }







                    // We were already in discharge
                    // A greater than 100w jump, into more grid export
                    if (updatedEntity.PowerAtLastMeter < 0 && newStateMeterDelta < -100)
                    {
                        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} Sudden grid meter spike from {updatedEntity.PowerAtLastMeter} to {newPowerState}, ignoring spike");
                        // We need to update the PowerAtLastMeter though, so that if the next reading still reports same, we don't fall into here again
                        updatedEntity.PowerAtLastMeter = newPowerState;

                        return; // Ignore this update
                    }
                }

                updatedEntity.PowerLiveData = newPowerState;

                if (updatedEntity.Type == PowerEntity.PowerType.Grid)
                {
                    // Everyone's live data is now synced up with the meter data
                    foreach (var item in _powerMeters)
                    {
                        item.Value.PowerAtLastMeter = item.Value.PowerLiveData;
                    }

                    OnUpdate(); // Meter updates, have actual live total consumption data, they are the most accurate data we get, they can bypass the rate limit
                }

                Task.Run(async () =>
                {
                    using RateLimitLease lease = await _rateLimiter.AcquireAsync(1);
                    lock (_rateLimiter)
                    {
                        OnUpdate();
                    }
                });
            });

        }

        private float ApplyLimitsPreFilter(float input)
        {
            var currentSolarPower = _powerMeters.Where(x => x.Value.Type == PowerEntity.PowerType.Solar).Select(x => x.Value.PowerLiveData).Sum();

            // If we charge, we charge from solar power. Never allow more charging than we have solar

            // If we want to charge, we only ever charge from Solar, so we can never charge at more power than we have solar
            // Protects against negative power spikes, when a big load falls away, overshooting us into a charge instead of putting us at zero
            if (input > 0 && input > currentSolarPower)
                input = Math.Clamp(currentSolarPower - 80, 0, 1200); // And leave some gap for the house base load

            return input;
        }

        private float ApplyLimitsPostFilter(float input)
        {
            // The battery has deadband between -50 and 50 watts. If we want to discharge, we need at least 50w, if we want to charge, we need at least 50w

            if (input > 0 && input < 50)
                input = 0; // We wanted to charge between 0-50w, the battery cannot do that. So lets leave it idle.

            if (input < 0 && input > -50)
                input = -50; // We wanted to discharge, lets bump it up to the minimum required discharge power, we waste some grid export, but thats better than importing

            input = MinimumBatteryPower switch
            {
                < 0 => Math.Min(input, MinimumBatteryPower), // Forced discharge
                > 0 => Math.Max(input, MinimumBatteryPower), // Forced charge
                _ => input                                   // No change if zero
            };

            // Power limits
            input = Math.Clamp(input, -MaximumDischargePower, MaximumChargePower);

            return input;
        }


        private void OnUpdate()
        {
            // Meter updates every 10 seconds
            var lastMeter = _powerMeters.First(x => x.Value.Type == PowerEntity.PowerType.Grid).Value.PowerLiveData;

            // To that we add the delta's that our fast updating data sources provide.
            var totalDeltaSinceLastMeter = _powerMeters.Select(x =>
            {
                if (x.Value.Type == PowerEntity.PowerType.Solar) // Solar is inverted, positive value means we are getting power, not consuming
                    return -x.Value.GetDeltaSinceLastMeter();
                if (x.Value.Type == PowerEntity.PowerType.Grid) // Grid meter is always current, except when we ignored a reading, which we did intentionally and don't want to respect it here
                    return 0;
                return x.Value.GetDeltaSinceLastMeter();
            }).Sum();

            var totalPlugLoad = _powerMeters.Select(x =>
            {
                if (x.Value.Type == PowerEntity.PowerType.Solar)
                    return 0;
                if (x.Value.Type == PowerEntity.PowerType.Grid)
                    return 0;
                return x.Value.PowerLiveData;
            }).Sum();

            var currentMeter = lastMeter + totalDeltaSinceLastMeter;

            var meterTarget = MeterTargetPower;

            var deltaToTarget = meterTarget - currentMeter;

            _entityManager.SetStateAsync("sensor.battery_control_deltatotarget", $"{deltaToTarget}");
            var currentBatteryLoad = _powerMeters.Where(x => x.Value.Type == PowerEntity.PowerType.Battery).Select(x => x.Value.PowerLiveData).Sum();

            // Give the batteries a new target, such that we get to our meterTarget
#if DEBUG
            Console.WriteLine($"{DateTimeOffset.Now: HH:mm:ss.fff} currentMeter {currentMeter} ({lastMeter} + {totalDeltaSinceLastMeter})");
            Console.WriteLine($"{DateTimeOffset.Now: HH:mm:ss.fff} deltaToTarget {deltaToTarget}");
#endif

            {
                using var rateLimitTicket = _rateLimiterApply.AttemptAcquire((int)Math.Abs(deltaToTarget));

#if DEBUG
                Console.WriteLine($"{DateTimeOffset.Now: HH:mm:ss.fff} Battery to {currentBatteryLoad + deltaToTarget} ({currentBatteryLoad} + {deltaToTarget}), Rate Limit {rateLimitTicket.IsAcquired}");
#endif

                _entityManager.SetStateAsync("sensor.battery_control_targetpower", $"{currentBatteryLoad + deltaToTarget}");

                var commandPower = (currentBatteryLoad + deltaToTarget);

                commandPower = ApplyLimitsPreFilter(commandPower);

                var commandPowerNonFilter = commandPower;

                if (UseFilter)
                {
                    commandPower = _filter.Filter(currentBatteryLoad + deltaToTarget); // We want to run the filter regularly, not just when we acquire a ticket, otherwise it updates too slowly
                }

                commandPower = ApplyLimitsPostFilter(commandPower);
                commandPowerNonFilter = ApplyLimitsPostFilter(commandPowerNonFilter);

                if (rateLimitTicket.IsAcquired)
                {
                    if (Math.Abs(deltaToTarget) > InstantCorrectionThreshold)
                    {
                        _batteryController.SetBatteryPower(commandPowerNonFilter, true);
                        _entityManager.SetStateAsync("sensor.battery_control_batterycommand", $"{commandPowerNonFilter}");
                    }
                    else
                    {
                        _batteryController.SetBatteryPower(commandPower);
                        _entityManager.SetStateAsync("sensor.battery_control_batterycommand", $"{commandPower}");
                    }

                    
                }
            }

            _shellyEmulator.SetPowerPhase(0, -_filter.Filter(deltaToTarget));
            _shellyEmulator.SetPowerPhase(1, -deltaToTarget);
            _shellyEmulator.SetPowerPhase(2, currentMeter);


            _shellyEmulator.SetDebugData(new
            {
                lastMeter,
                totalDeltaSinceLastMeter,
                currentMeter,
                deltaToTarget,
                currentBatteryLoad
            });
        }


    }
}
